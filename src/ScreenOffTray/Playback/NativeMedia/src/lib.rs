use std::ffi::c_void;
use std::os::windows::io::{AsRawHandle, BorrowedHandle, OwnedHandle};
use std::ptr::{null_mut, write};
use std::sync::{Arc, Mutex};
use windows::Media::Control::{
    GlobalSystemMediaTransportControlsSessionManager as Manager,
    GlobalSystemMediaTransportControlsSessionPlaybackStatus as Playback,
};
use windows::Win32::System::WinRT::{RO_INIT_MULTITHREADED, RoInitialize, RoUninitialize};
use windows::Win32::{Foundation::HANDLE, System::Threading::SetEvent};
use windows::core::{Error, HRESULT, HSTRING, Result};
use windows_future::{AsyncOperationCompletedHandler, IAsyncOperation};

const INVALID_ARGUMENT: i32 = 0x80070057_u32 as i32;
const INVALID_DATA: i32 = 0x8007000d_u32 as i32;
const MAX_SESSIONS: u32 = 128;
const MAX_ID: usize = 1024;

struct Runtime;
impl Drop for Runtime {
    fn drop(&mut self) {
        // SAFETY: Each Runtime owns one successful initialization on this same thread.
        unsafe { RoUninitialize() }
    }
}

enum Source {
    Pending(IAsyncOperation<Manager>),
    Ready(Manager),
}

struct Completion(Arc<Mutex<Option<OwnedHandle>>>);
impl Drop for Completion {
    fn drop(&mut self) {
        self.0.lock().unwrap().take();
    }
}

// Drop the WinRT objects before balancing initialization.
pub struct Reader {
    source: Source,
    rows: Vec<(HSTRING, i32)>,
    _completion: Completion,
    _runtime: Runtime,
}

impl Reader {
    fn open(event: BorrowedHandle<'_>) -> Result<Self> {
        // SAFETY: The audio worker opens, reads and closes on the same MTA thread.
        unsafe { RoInitialize(RO_INIT_MULTITHREADED)? };
        let runtime = Runtime;
        let signal = Completion(Arc::new(Mutex::new(Some(
            event.try_clone_to_owned().map_err(|error| {
                Error::from_hresult(HRESULT::from_win32(error.raw_os_error().unwrap_or(6) as u32))
            })?,
        ))));
        let pending = Manager::RequestAsync()?;
        let event = Arc::clone(&signal.0);
        pending.SetCompleted(&AsyncOperationCompletedHandler::new(move |_, _| {
            if let Some(owned) = event.lock().unwrap().take() {
                // SAFETY: This callback takes ownership of the duplicated event.
                unsafe { SetEvent(HANDLE(owned.as_raw_handle()))? };
            }
            Ok(())
        }))?;
        Ok(Self {
            source: Source::Pending(pending),
            rows: Vec::new(),
            _completion: signal,
            _runtime: runtime,
        })
    }

    fn finish(&mut self) -> Result<()> {
        if let Source::Pending(operation) = &self.source {
            // The managed worker calls this only after the completion event.
            self.source = Source::Ready(operation.GetResults()?);
        }
        Ok(())
    }

    fn sample(&mut self) -> Result<i32> {
        self.rows.clear();
        let Source::Ready(manager) = &self.source else {
            return Err(Error::from_hresult(HRESULT(INVALID_DATA)));
        };
        let sessions = manager.GetSessions()?;
        let count = sessions.Size()?;
        if count > MAX_SESSIONS {
            return Err(Error::from_hresult(HRESULT(INVALID_DATA)));
        }
        for index in 0..count {
            let session = sessions.GetAt(index)?;
            let id = session.SourceAppUserModelId()?;
            if id.is_empty() || id.len() > MAX_ID {
                return Err(Error::from_hresult(HRESULT(INVALID_DATA)));
            }
            let status = match session.GetPlaybackInfo()?.PlaybackStatus()? {
                Playback::Playing => 1,
                Playback::Paused => 2,
                _ => 3,
            };
            self.rows.push((id, status));
        }
        Ok(count as i32)
    }
}

#[repr(C)]
pub struct Row {
    id: *const u16,
    length: i32,
    state: i32,
}

// The audio worker owns the reader and copies each row before the next sample.
#[unsafe(no_mangle)]
pub unsafe extern "system" fn media_open(event: *mut c_void, out: *mut *mut Reader) -> i32 {
    if out.is_null() || event.is_null() {
        return INVALID_ARGUMENT;
    }
    // SAFETY: The caller supplies a writable IntPtr and a live SafeWaitHandle.
    unsafe { write(out, null_mut()) };
    match Reader::open(unsafe { BorrowedHandle::borrow_raw(event) }) {
        Ok(reader) => {
            // SAFETY: Same output address; ownership transfers to media_close.
            unsafe { write(out, Box::into_raw(Box::new(reader))) };
            0
        }
        Err(error) => error.code().0,
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn media_finish(reader: *mut Reader) -> i32 {
    // SAFETY: The worker owns this live reader exclusively.
    let Some(reader) = (unsafe { reader.as_mut() }) else {
        return INVALID_ARGUMENT;
    };
    match reader.finish() {
        Ok(()) => 0,
        Err(error) => error.code().0,
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn media_sample(reader: *mut Reader) -> i32 {
    // SAFETY: The worker never shares or reuses a closed reader.
    let Some(reader) = (unsafe { reader.as_mut() }) else {
        return INVALID_ARGUMENT;
    };
    match reader.sample() {
        Ok(count) => count,
        Err(error) => {
            reader.rows.clear();
            error.code().0
        }
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn media_row(reader: *const Reader, index: usize, out: *mut Row) -> i32 {
    // SAFETY: The worker holds the live reader throughout this call.
    let Some(reader) = (unsafe { reader.as_ref() }) else {
        return INVALID_ARGUMENT;
    };
    let Some((id, state)) = reader.rows.get(index) else {
        return INVALID_ARGUMENT;
    };
    if out.is_null() {
        return INVALID_ARGUMENT;
    }
    // SAFETY: out is a writable Row. The borrowed ID lasts until sample or close.
    unsafe {
        write(
            out,
            Row {
                id: id.as_ptr(),
                length: id.len() as i32,
                state: *state,
            },
        )
    };
    0
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn media_close(reader: *mut Reader) {
    if !reader.is_null() {
        // SAFETY: This is the worker's one close for a pointer returned by open.
        drop(unsafe { Box::from_raw(reader) });
    }
}
