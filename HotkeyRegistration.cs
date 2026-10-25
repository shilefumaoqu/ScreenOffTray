using System;
using System.Collections.Generic;

namespace ScreenOffTray
{
    internal interface IHotkeyRegistrar
    {
        bool TryRegister(int id, HotkeyBinding binding, out string error);
        bool Unregister(int id);
    }

    // UI-thread transaction state, independent of HWNDs, hooks, settings and display work.
    internal sealed class HotkeyRegistration : IDisposable
    {
        private readonly IHotkeyRegistrar registrar;
        private readonly Dictionary<int, HotkeyBinding> reservations = new Dictionary<int, HotkeyBinding>();
        private int activeId, nextId = 0x5101;
        private bool disposed;
        internal HotkeyBinding Hotkey { get; private set; }
        internal bool Registered { get { return activeId != 0; } }
        internal bool Changing { get; private set; }
        internal bool CleanupPending { get { return reservations.Count > (Registered ? 1 : 0); } }

        internal HotkeyRegistration(IHotkeyRegistrar registrar, HotkeyBinding initial)
        {
            if (registrar == null) throw new ArgumentNullException("registrar");
            if (initial == null) throw new ArgumentNullException("initial");
            this.registrar = registrar; Hotkey = initial;
        }

        internal bool Initialize(out string error)
        {
            return TryChange(Hotkey, delegate { }, false, false, out error);
        }

        // persist must save atomically and restore its own in-memory settings on
        // exception. Action cannot undo arbitrary external writes by its caller.
        // The previous registration stays alive throughout persist; it cannot be
        // stolen between unregister and a failed save's rollback.
        internal bool TryChange(HotkeyBinding binding, Action persist, bool busy, bool heldInput, out string error)
        {
            error = null;
            if (disposed) { error = "会话已关闭 / Session is closed."; return false; }
            if (busy || heldInput || Changing) {
                error = "请先完成恢复并松开按键 / Finish recovery and release held input first."; return false;
            }
            if (binding == null || persist == null) {
                error = "快捷键或保存操作无效 / Invalid hotkey or save operation."; return false;
            }
            Changing = true;
            int candidate = 0;
            try {
                foreach (KeyValuePair<int, HotkeyBinding> item in reservations)
                    if (item.Value.SameAs(binding)) { candidate = item.Key; break; }
                if (candidate == 0) {
                    candidate = AllocateId();
                    if (candidate == 0) { error = "快捷键注册资源不足 / Hotkey registration resources exhausted."; return false; }
                    string registrationError;
                    if (!registrar.TryRegister(candidate, binding, out registrationError)) {
                        error = "快捷键被占用或系统拒绝注册 / Hotkey is in use or registration was rejected." +
                            (String.IsNullOrEmpty(registrationError) ? "" : " " + registrationError);
                        candidate = 0; return false;
                    }
                    reservations.Add(candidate, binding);
                }
                persist();
                // Commit the recognized binding before retiring the old native ID.
                // A failed unregister leaves only an inert reservation: WM_HOTKEY
                // never triggers this application, and hooks match Hotkey only.
                Hotkey = binding; activeId = candidate;
                CleanupInactive();
                return true;
            } catch (Exception ex) {
                if (candidate != 0 && candidate != activeId) TryRelease(candidate);
                error = "快捷键保存或注册失败，原快捷键已保留 / Hotkey save or registration failed; previous binding retained. " + ex.Message;
                return false;
            } finally { Changing = false; }
        }

        private int AllocateId()
        {
            for (int i = 0; i <= 0xBFFF - 0x5101; i++) {
                int id = nextId;
                nextId = nextId == 0xBFFF ? 0x5101 : nextId + 1;
                if (!reservations.ContainsKey(id)) return id;
            }
            return 0;
        }
        private void TryRelease(int id)
        {
            try { if (registrar.Unregister(id)) reservations.Remove(id); }
            catch { /* Retain ownership for retry; active binding is unchanged. */ }
        }
        private void CleanupInactive()
        {
            List<int> ids = new List<int>(reservations.Keys);
            foreach (int id in ids) if (id != activeId) TryRelease(id);
        }
        public void Dispose()
        {
            if (Changing) throw new InvalidOperationException("快捷键正在保存 / Hotkey save is in progress.");
            disposed = true; activeId = 0;
            CleanupInactive();
        }
    }
}
