using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using ScreenOffTray;

namespace OledPrototype
{
    // One invisible dispatch control, one gate and one hook pair for the entire tray lifetime.
    // All state changes run on the UI thread; CCD work never runs inside a hook or timer.
    internal sealed class OledSession : Control
    {
        private readonly SessionFlow flow = new SessionFlow();
        private readonly InputGate gate = new InputGate();
        private readonly TransitionDeadline deadline = new TransitionDeadline();
        private readonly RecoveryHandoff handoff = new RecoveryHandoff();
        private readonly SerialDisplayWork displayWork = new SerialDisplayWork();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly PhysicalActivityClock physicalActivity;
        private uint physicalInputVersion;
        private readonly HashSet<int> swallowed = new HashSet<int>();
        private readonly ExitNotifications exitNotifications = new ExitNotifications();
        private readonly ExitWaitNotice exitWaitNotice = new ExitWaitNotice();
        private readonly RecoveryRetryIntent retryIntent = new RecoveryRetryIntent();
        private readonly RuntimeMaintenance runtimeFiles = new RuntimeMaintenance(Program.Runtime, Program.Log);
        private readonly List<Task> pendingMaintenance = new List<Task>();
        private RuntimeMaintenance.OwnedSnapshot ownedRecoverySnapshot;
        private readonly RecoveryStopBarrier stopBarrier = new RecoveryStopBarrier();
        private RecoveryReaders recoveryReaders;
        private RecoveryReaders.Lease guardianReader;
        private int pendingRecoveryCreations, outstandingRecoveryReaders;
        private string lastExitState;
        private readonly Timer heartbeat = new Timer();
        private Native.HookProc keyboardProc, mouseProc;
        private IntPtr keyboardHook, mouseHook;
        private bool hotkeyHeld, inputReady, disposed, rescue;
        private readonly HotkeyRegistration hotkeys;
        private readonly HotkeyInputState hotkeyInput = new HotkeyInputState();
        private readonly bool hooksAvailable;
        private DisplayBackend backend;
        private IRecoveryExecutor guardian;
        private bool guardianArmed, localRecoveryTried, recoveryUnavailable;
        private string recoverySnapshot;
        private PreparationAttempt preparation;
        private long preparationId, displayVersion;
        private long transitionTime;
        private bool automaticRequest;
        private readonly Func<bool> displayReady;
        internal event Action Changed;
        internal event Action<string> Problem;
        internal event Action ExitReady;
        internal event Action<AutoOffPauseReason> AutomaticProtectionObserved;
        // Settings-only observation. A subscriber queues the key snapshot for its
        // focused capture box; it cannot change hook blocking or session decisions.
        internal event Action<int, uint, bool> SettingsKey;
        internal bool Supported { get; private set; }
        internal string UnavailableReason { get; private set; }
        internal bool IsBusy { get { return flow.Busy; } }
        internal bool CanStartSession { get { return !disposed && !flow.Busy && stopBarrier.Pending == 0 && !stopBarrier.Unconfirmed && System.Threading.Volatile.Read(ref outstandingRecoveryReaders) == 0; } }
        internal bool ExitRetryAvailable { get { return !disposed && exitNotifications.Failed; } }
        internal Task PendingMaintenance { get { lock (pendingMaintenance) return Task.WhenAll(pendingMaintenance.ToArray()); } }
        internal HotkeyBinding Hotkey { get { return hotkeys.Hotkey; } }
        internal string StateText { get {
            switch (flow.Phase) {
                case SessionPhase.Preparing: return "准备关屏 / Preparing";
                case SessionPhase.SwitchingOff: return "正在关屏 / Switching off";
                case SessionPhase.Off: return "屏幕已关闭 / Display off";
                case SessionPhase.Restoring: return "正在恢复显示 / Restoring";
                case SessionPhase.Draining: return flow.ExitRequested ? "退出中 · 等待松开键鼠 / Exiting · release wake input" : "等待键鼠松开 / Release wake input";
                default:
                    if (flow.ExitRequested) return exitNotifications.Failed ? "退出未完成 · 再点退出重试 / Exit incomplete · retry Exit" : "正在退出 / Exiting";
                    if (stopBarrier.Unconfirmed) return "恢复进程退出未确认 · 关屏停用 / Recovery exit unconfirmed · display off disabled";
                    if (stopBarrier.Pending != 0 || System.Threading.Volatile.Read(ref outstandingRecoveryReaders) != 0) return "正在释放恢复保护 / Finishing recovery guard";
                    return "运行中 / Running";
            }
        } }
        private bool HasHeldInput { get { return swallowed.Count != 0 || hotkeyHeld; } }

        internal OledSession() : this(delegate { return true; }) { }

        internal OledSession(Func<bool> readiness) : this(readiness, HotkeyBinding.Default) { }

        internal OledSession(Func<bool> readiness, HotkeyBinding binding)
        {
            if (readiness == null) throw new ArgumentNullException("readiness");
            if (binding == null) throw new ArgumentNullException("binding");
            displayReady = readiness;
            physicalActivity = new PhysicalActivityClock(delegate { return clock.ElapsedMilliseconds; });
            Visible = false;
            IntPtr window = Handle;
            hotkeys = new HotkeyRegistration(new WindowHotkeyRegistrar(window), binding);
            string hotkeyError;
            hotkeys.Initialize(out hotkeyError);
            keyboardProc = Keyboard; mouseProc = Mouse;
            IntPtr module = Native.GetModuleHandle(null);
            keyboardHook = Native.SetWindowsHookEx(13, keyboardProc, module, 0);
            mouseHook = Native.SetWindowsHookEx(14, mouseProc, module, 0);
            hooksAvailable = keyboardHook != IntPtr.Zero && mouseHook != IntPtr.Zero;
            Supported = hotkeys.Registered && hooksAvailable;
            UnavailableReason = hooksAvailable ? hotkeyError :
                "键鼠监听安装失败，关屏已停用 / Input hooks could not be installed; display off is disabled.";
            // A registration conflict is recoverable from settings, while an
            // incomplete hook pair must remain disabled for this lifetime.
            if (!hooksAvailable) ReleaseHooks();
            heartbeat.Interval = 100;
            heartbeat.Tick += delegate { Tick(); };
            heartbeat.Start();
        }

        internal bool TryChangeHotkey(HotkeyBinding binding, Action persist, out string error)
        {
            if (disposed || !hooksAvailable) {
                error = "键鼠监听不可用或会话已关闭 / Input hooks are unavailable or session is closed.";
                return false;
            }
            if (!hotkeys.TryChange(binding, persist, flow.Busy, HasHeldInput, out error)) return false;
            Supported = hotkeys.Registered && hooksAvailable;
            UnavailableReason = null;
            if (hotkeys.CleanupPending)
                Report("新快捷键已保存并生效，但旧注册清理失败，旧组合可能仍被本程序占用 / The new hotkey is saved and active, but cleanup of an old registration failed; its combination may still be reserved by this app.");
            return true;
        }

        private sealed class WindowHotkeyRegistrar : IHotkeyRegistrar
        {
            private readonly IntPtr window;
            internal WindowHotkeyRegistrar(IntPtr window) { this.window = window; }
            public bool TryRegister(int id, HotkeyBinding binding, out string error)
            {
                bool registered = Native.RegisterHotKey(window, id, binding.Modifiers | 0x4000U, (uint)binding.Key);
                error = registered ? null : "Win32=" + Marshal.GetLastWin32Error();
                return registered;
            }
            public bool Unregister(int id) { return Native.UnregisterHotKey(window, id); }
        }

        internal uint EffectiveIdle(uint systemIdle) { return flow.EffectiveIdle(systemIdle, clock.ElapsedMilliseconds); }
        internal uint PhysicalIdleMilliseconds { get { return physicalActivity.IdleMilliseconds; } }
        internal uint PhysicalInputVersion { get { return physicalInputVersion; } }

        internal bool TryBegin(bool automatic)
        {
            if (hotkeys.Changing || !CanStartSession) return false;
            // The physical hotkey enters here directly. It must obey the same
            // first-run display check as menu and automatic requests.
            if (!displayReady()) return false;
            bool displayRequired = false;
            if (automatic) {
                uint state;
                bool stateKnown = ScreenOffTray.NativeMethods.TryGetSystemExecutionState(out state);
                displayRequired = !stateKnown || (state & ScreenOffTray.NativeMethods.ES_DISPLAY_REQUIRED) != 0;
                if (displayRequired) NotifyAutomaticProtection(stateKnown ? AutoOffPauseReason.DisplayRequired : AutoOffPauseReason.StateUnknown);
            }
            if (!flow.TryBegin(Supported && inputReady && !rescue, displayRequired, automatic)) return false;
            automaticRequest = automatic;
            handoff.Reset(); localRecoveryTried = recoveryUnavailable = false;
            recoverySnapshot = null;
            PreparationAttempt attempt = new PreparationAttempt(++preparationId, clock.ElapsedMilliseconds, automatic);
            preparation = attempt;
            Notify();
            // Selection is queried afresh per session; no stored list index or arbitrary physical target.
            Task.Run(delegate {
                IRecoveryExecutor preparedGuardian = null;
                RuntimeMaintenance.OwnedSnapshot snapshotOwner = null;
                RecoveryReaders readers = null;
                RecoveryReaders.Lease reader = null;
                bool executorAttempted = false;
                try {
                    if (!attempt.Pending) return;
                    List<DisplayTarget> targets = DisplayBackend.ListTargets();
                    List<DisplayTarget> accepted = targets.FindAll(delegate(DisplayTarget t) {
                        return t.Available && DisplayBackend.IsKnownVirtual(t.Technology, t.AdapterPath) && IsMttTarget(t.AdapterPath);
                    });
                    if (accepted.Count != 1) throw new InvalidOperationException("需要唯一可用的独立 MttVDD 虚拟屏幕。不会改用其他驱动或系统关屏命令。");
                    DisplayTarget target = accepted[0];
                    DisplayBackend prepared = new DisplayBackend();
                    prepared.Prepare(target.DevicePath);
                    if (!attempt.Pending) return;
                    snapshotOwner = runtimeFiles.CreateSnapshot(prepared.SaveRecovery);
                    string snapshot = snapshotOwner.Path;
                    if (!attempt.Pending) return;
                    // Guardian's ready.WaitOne(5000) belongs on this worker, never the LL-hook/UI thread.
                    readers = new RecoveryReaders(); reader = RegisterReader(readers);
                    executorAttempted = true;
                    System.Threading.Interlocked.Increment(ref pendingRecoveryCreations);
                    try { preparedGuardian = CreateExecutor(snapshot); }
                    finally { System.Threading.Interlocked.Decrement(ref pendingRecoveryCreations); }
                    if (!attempt.Pending) return;
                    IRecoveryExecutor readyGuardian = preparedGuardian;
                    RuntimeMaintenance.OwnedSnapshot readySnapshot = snapshotOwner;
                    RecoveryReaders readyReaders = readers; RecoveryReaders.Lease readyReader = reader;
                    if (Post(delegate { Prepared(attempt, prepared, readyGuardian, snapshot, readySnapshot, readyReaders, readyReader); })) {
                        preparedGuardian = null; snapshotOwner = null;
                    }
                } catch (Exception ex) {
                    if (executorAttempted && preparedGuardian == null && reader != null) ConfirmFailedCreation(reader, ex);
                    string message = ex.Message;
                    Post(delegate { PreparationFailed(attempt, message); });
                } finally {
                    // A failed/disposed dispatcher never takes ownership; stop the unarmed child here.
                    if (preparedGuardian != null) StopAndArchive(preparedGuardian, snapshotOwner, readers, reader, true);
                    else if (snapshotOwner != null) ArchiveSnapshot(snapshotOwner, readers, !executorAttempted || reader != null);
                }
            });
            return true;
        }

        private static IRecoveryExecutor CreateExecutor(string snapshot)
        {
            // Pure snapshot parsing before a new child can be armed; Recover itself still verifies the real topology.
            using (FileStream stream = new FileStream(snapshot, FileMode.Open, FileAccess.Read, FileShare.Read))
                DisplayBackend.ReadSnapshot(stream);
            return new GuardianExecutor(new Guardian(snapshot, 0));
        }

        private void Prepared(PreparationAttempt attempt, DisplayBackend prepared, IRecoveryExecutor preparedGuardian, string snapshot, RuntimeMaintenance.OwnedSnapshot snapshotOwner, RecoveryReaders readers, RecoveryReaders.Lease reader)
        {
            if (disposed || !attempt.IsCurrent(preparation) || attempt.Expired(clock.ElapsedMilliseconds) || flow.EndRequested) {
                StopAndArchive(preparedGuardian, snapshotOwner, readers, reader, true);
                if (!disposed && ReferenceEquals(attempt, preparation)) CancelPreparation(attempt, null);
                return;
            }
            try {
                backend = prepared;
                // Normal mode is unlimited. The copied watchdog still has its independent 8s heartbeat guard.
                guardian = preparedGuardian;
                guardianArmed = false;
                recoverySnapshot = snapshot;
                ownedRecoverySnapshot = snapshotOwner;
                recoveryReaders = readers; guardianReader = reader;
                // Recheck automatic requests before installing our own DISPLAY_REQUIRED hold.
                // Manual menu/hotkey requests retain the old explicit user override.
                if (automaticRequest) {
                    uint state;
                    bool stateKnown = ScreenOffTray.NativeMethods.TryGetSystemExecutionState(out state);
                    if (!stateKnown || (state & ScreenOffTray.NativeMethods.ES_DISPLAY_REQUIRED) != 0) {
                        NotifyAutomaticProtection(stateKnown ? AutoOffPauseReason.DisplayRequired : AutoOffPauseReason.StateUnknown);
                        throw new InvalidOperationException("有应用请求保持显示开启，或无法核实该状态，已取消自动关屏。");
                    }
                }
                if (!flow.BeginSwitch()) throw new InvalidOperationException("关屏准备已取消。");
                transitionTime = clock.ElapsedMilliseconds;
                gate.StartOff(transitionTime, null);
                if (Native.SetThreadExecutionState(0x80000003) == 0)
                    throw new InvalidOperationException("无法保持后台运行，未执行关屏。");
                guardian.Arm();
                guardianArmed = true;
                if (!guardian.IsAlive) throw new InvalidOperationException("恢复进程已退出，未执行关屏。");
                deadline.Start(clock.ElapsedMilliseconds);
                long operation = ++displayVersion;
                Notify();
                displayWork.Enqueue(delegate {
                    // Commit on the worker immediately before native work, not while it is still queued.
                    if (!attempt.Commit()) { Post(delegate { CancelPreparation(attempt, null); }); return; }
                    Post(delegate { if (ReferenceEquals(preparation, attempt)) preparation = null; });
                    try { prepared.TurnOff(); Post(delegate {
                        if (rescue || operation != displayVersion) return;
                        deadline.Complete(); flow.MarkOff();
                        gate.StartOff(clock.ElapsedMilliseconds, null);
                        Program.Log("virtual_only_configuration_verified; normal_unlimited");
                        Notify();
                        if (flow.EndRequested) Wake();
                    }); } catch (Exception ex) {
                        string message = ex.Message;
                        Post(delegate {
                            if (rescue || operation != displayVersion) return;
                            deadline.Complete(); flow.MarkOff();
                            Report("关屏未完成，正在恢复：" + message); Wake();
                        });
                    }
                });
            } catch (Exception ex) {
                bool neverApplied = attempt.Cancel() || attempt.Cancelled;
                StopAndArchive(guardian, ownedRecoverySnapshot, recoveryReaders, guardianReader, neverApplied);
                guardian = null; ownedRecoverySnapshot = null; recoveryReaders = null; guardianReader = null;
                guardianArmed = false; recoverySnapshot = null;
                attempt.Cancel(); preparation = null;
                Native.SetThreadExecutionState(0x80000000);
                gate.Cancel(); deadline.Complete();
                flow.Complete(clock.ElapsedMilliseconds, HasHeldInput);
                Report(ex.Message); Notify();
            }
        }

        private void PreparationFailed(PreparationAttempt attempt, string message)
        {
            if (disposed || !attempt.IsCurrent(preparation)) return;
            attempt.Cancel(); preparation = null;
            flow.Complete(clock.ElapsedMilliseconds, HasHeldInput);
            Report("未执行关屏：" + message); Notify();
        }

        private bool CancelPreparation(PreparationAttempt attempt, string message)
        {
            if (disposed || !ReferenceEquals(attempt, preparation) ||
                (flow.Phase != SessionPhase.Preparing && flow.Phase != SessionPhase.SwitchingOff)) return false;
            // If the native worker won Commit, only the normal restore path can end the session.
            if (!attempt.Cancel() && !attempt.Cancelled) return false;
            preparation = null;
            if (flow.Phase == SessionPhase.SwitchingOff) {
                ++displayVersion; deadline.Complete(); gate.Cancel();
                Native.SetThreadExecutionState(0x80000000);
                StopAndArchive(guardian, ownedRecoverySnapshot, recoveryReaders, guardianReader, true);
                guardian = null; ownedRecoverySnapshot = null; recoveryReaders = null; guardianReader = null;
                guardianArmed = false; recoverySnapshot = null;
            }
            flow.Complete(clock.ElapsedMilliseconds, HasHeldInput);
            if (message != null) Report(message);
            Notify();
            return true;
        }

        internal void RequestEnd(bool exit)
        {
            if (exit && !flow.ExitRequested) Program.Log("exit_requested; phase=" + flow.Phase + "; held_count=" + HeldInputCount);
            flow.RequestEnd(exit);
            if (preparation != null && CancelPreparation(preparation, null)) return;
            if (!flow.Busy && HasHeldInput) flow.Complete(clock.ElapsedMilliseconds, true);
            Wake();
            if (recoveryUnavailable) Rescue("请求重试恢复 / Retry recovery requested", true);
            Notify();
        }

        private void Wake()
        {
            if (rescue || !flow.BeginRestore()) return;
            StartRestore();
        }

        private void StartRestore()
        {
            long operation = ++displayVersion;
            deadline.Start(clock.ElapsedMilliseconds); Notify();
            displayWork.Enqueue(delegate {
                try {
                    backend.Restore();
                    Post(delegate {
                        if (rescue || operation != displayVersion) return;
                        deadline.Complete();
                        gate.MarkDisplayOn(clock.ElapsedMilliseconds);
                        Native.SetThreadExecutionState(0x80000000);
                        StopAndArchive(guardian, ownedRecoverySnapshot, recoveryReaders, guardianReader, true);
                        guardian = null; ownedRecoverySnapshot = null; recoverySnapshot = null; recoveryReaders = null; guardianReader = null;
                        guardianArmed = false; handoff.Reset(); recoveryUnavailable = false;
                        flow.Complete(clock.ElapsedMilliseconds, HasHeldInput);
                        Program.Log("original_configuration_restored_and_verified"); Notify();
                    });
                } catch (Exception ex) {
                    string message = ex.Message;
                    Post(delegate { if (!rescue && operation == displayVersion) Rescue("恢复检查未通过：" + message, false); });
                }
            });
        }

        private void Tick()
        {
            if (!inputReady) inputReady = AllInputsReleased();
            long now = clock.ElapsedMilliseconds;
            if (preparation != null && preparation.Expired(now))
                CancelPreparation(preparation, "准备超过8秒，已取消；未执行关屏 / Preparation timed out; display off cancelled.");
            if (deadline.TryExpire(now)) Rescue("显示操作超过8秒 / Display operation timed out", false);
            if (handoff.PendingExpired(now)) ReplacementFailed(handoff.Version, "替代恢复进程准备超过8秒 / Recovery executor startup timed out");
            bool alive = guardian != null && guardian.IsAlive;
            if (alive) guardian.Pulse();
            if (!alive && rescue) {
                rescue = false; handoff.LostExecutor(now); recoveryUnavailable = true;
                gate.Cancel(); StartLocalRecoveryOnce();
                if (handoff.Failures >= 3) Report("恢复进程反复退出，自动重试已暂停；恢复尚未确认，材料已保留。可用实体按键请求有限重试。 / Recovery processes exited repeatedly. Automatic retries are paused; restoration is unverified and recovery data is preserved. Physical input can request a bounded retry.");
                Rescue("恢复执行者已退出，重新建立救援 / Recovery executor exited", false);
            } else if (!alive && flow.Phase == SessionPhase.Off) {
                    Report("独立恢复进程已退出，正在尝试恢复显示。"); Wake();
            } else if (!alive && flow.Phase == SessionPhase.SwitchingOff) {
                flow.RequestEnd(false);
            }
            if (recoveryUnavailable && !handoff.Pending && !rescue)
                Rescue("重试替代恢复进程 / Retrying recovery executor", false);
            gate.Tick(flow.Phase == SessionPhase.SwitchingOff ? transitionTime : clock.ElapsedMilliseconds);
            if (flow.Drain(clock.ElapsedMilliseconds, HasHeldInput)) Notify();
            else if (!disposed && flow.ReadyToExit && stopBarrier.Pending == 0 && exitNotifications.ShouldRetry(now)) Notify();
            if (!disposed && exitWaitNotice.Observe(flow.ExitRequested, flow.Phase == SessionPhase.Draining, HeldInputCount, now)) {
                Program.Log("exit_waiting_for_physical_release; held_count=" + HeldInputCount);
                Report(ExitWaitNotice.Describe(swallowed, hotkeyHeld, Hotkey.Key), false);
                Notify();
            }
        }

        private void Rescue(string message, bool explicitRetry)
        {
            if (disposed || rescue || !flow.NeedsRecovery || recoverySnapshot == null) return;
            if (stopBarrier.Unconfirmed || stopBarrier.Pending != 0 || System.Threading.Volatile.Read(ref pendingRecoveryCreations) != 0) {
                recoveryUnavailable = true; return;
            }
            if (guardianArmed && handoff.TryTransfer(guardian, true)) {
                deadline.Complete();
                rescue = true; ++displayVersion;
                Program.Log("watchdog_handoff_live_armed: " + message);
                Report(message + "；恢复进程将先停止本程序再恢复显示 / Recovery will stop this app before restoring.");
                return;
            }
            if (guardian != null) {
                IRecoveryExecutor old = guardian; RecoveryReaders.Lease oldReader = guardianReader;
                guardian = null; guardianReader = null; guardianArmed = false;
                StopExecutor(old, oldReader); recoveryUnavailable = true; deadline.Complete();
                return;
            }
            RecoveryReaders readers = recoveryReaders;
            if (readers == null) { recoveryUnavailable = true; return; }
            long token;
            if (!handoff.Begin(clock.ElapsedMilliseconds, explicitRetry, out token)) return;
            deadline.Complete();
            string snapshot = recoverySnapshot;
            RecoveryReaders.Lease reader = RegisterReader(readers);
            System.Threading.Interlocked.Increment(ref pendingRecoveryCreations);
            Task.Run(delegate {
                IRecoveryExecutor replacement = null;
                try {
                    replacement = CreateExecutor(snapshot);
                    IRecoveryExecutor ready = replacement;
                    if (Post(delegate { ReplacementReady(token, ready, reader, message); })) replacement = null;
                } catch (Exception ex) {
                    ConfirmFailedCreation(reader, ex);
                    string problem = ex.Message;
                    Post(delegate { ReplacementFailed(token, problem); });
                } finally {
                    if (replacement != null) StopExecutor(replacement, reader);
                    System.Threading.Interlocked.Decrement(ref pendingRecoveryCreations);
                }
            });
        }

        private void ReplacementReady(long token, IRecoveryExecutor replacement, RecoveryReaders.Lease reader, string message)
        {
            if (disposed || !handoff.IsCurrent(token) || !flow.NeedsRecovery) { StopExecutor(replacement, reader); return; }
            if (!handoff.TryTransfer(replacement, false)) {
                StopExecutor(replacement, reader); ReplacementFailed(token, "替代恢复进程未存活并armed / Replacement executor not live and armed"); return;
            }
            if (guardian != null) StopExecutor(guardian, guardianReader);
            guardian = replacement; guardianReader = reader; guardianArmed = true;
            rescue = true; ++displayVersion; deadline.Complete();
            Program.Log("replacement_watchdog_live_armed_handoff: " + message);
            Report("替代恢复进程已就绪，将先停止本程序再恢复 / Replacement recovery will stop this app before restoring.");
        }

        private void ReplacementFailed(long token, string message)
        {
            if (disposed || !handoff.Fail(token, clock.ElapsedMilliseconds)) return;
            recoveryUnavailable = true;
            // Keep consumed holds paired, but do not trap all subsequent physical input on a false handoff.
            gate.Cancel();
            Report("恢复执行者建立失败，未标记恢复成功 / Recovery executor unavailable; restoration NOT verified: " + message);
            StartLocalRecoveryOnce();
        }
        private void StartLocalRecoveryOnce()
        {
            if (!localRecoveryTried && flow.BeginRecovery()) {
                localRecoveryTried = true;
                // Queued after any late APPLY/Restore, never parallel with it.
                StartRestore();
            }
        }

        private void NotifyAutomaticProtection(AutoOffPauseReason reason)
        {
            Action<AutoOffPauseReason> observer = AutomaticProtectionObserved;
            if (observer == null) return;
            try { observer(reason); }
            catch (Exception ex) { Program.Log("automatic_protection_observer_failed: " + ex.Message); }
        }
        private void ObservePhysicalActivity(InputKind kind, bool injected)
        {
            // Reuse the existing physical/injected classification; observing the
            // idle clock never changes wake, swallow or pairing decisions.
            physicalActivity.Observe(injected);
            if (!injected) physicalInputVersion = unchecked(physicalInputVersion + 1);
            PreparationAttempt attempt = preparation;
            if (attempt != null && attempt.ObservePhysicalActivity(kind, injected)) {
                // No native call has won yet: the returning user's input still belongs to the visible app.
                gate.Cancel();
                Post(delegate { CancelPreparation(attempt, null); });
            }
            if (retryIntent.Observe(kind, injected, recoveryUnavailable && !rescue && !handoff.Pending)) {
                long operation = displayVersion, retryVersion = handoff.Version;
                Post(delegate {
                    if (operation == displayVersion && !rescue &&
                        handoff.AllowsRetry(retryVersion, flow.NeedsRecovery, recoveryUnavailable))
                        Rescue("实体操作请求重试恢复 / Physical input requested recovery retry", true);
                });
            }
        }
        private void Notify()
        {
            if (flow.ExitRequested) {
                string state = flow.Phase + "; held_count=" + HeldInputCount + "; recovery_stopping=" + stopBarrier.Pending + "; recovery_stop_unconfirmed=" + stopBarrier.Unconfirmed;
                if (state != lastExitState) { lastExitState = state; Program.Log("exit_state; phase=" + state); }
            }
            bool wasFailed = exitNotifications.Failed;
            exitNotifications.Dispatch(Changed, delegate { return !disposed && flow.ReadyToExit && stopBarrier.Pending == 0; }, ExitReady, Program.Log, clock.ElapsedMilliseconds);
            if (!disposed && !wasFailed && exitNotifications.Failed) {
                Report("退出收尾未完成，请从托盘再次点击退出重试。 / Exit cleanup did not complete. Choose Exit from the tray to retry.");
                ExitNotifications.InvokeAll(Changed, Program.Log, "exit_failure_status_failed");
            }
        }
        internal void RetryExit()
        {
            if (disposed || !flow.ReadyToExit || !exitNotifications.Failed) return;
            Program.Log("exit_cleanup_retry_requested"); exitNotifications.Retry(); Notify();
        }
        internal void TrackRecoveryStop(Task<bool> confirmation)
        { ObserveStop(delegate { return confirmation; }); }
        private RecoveryReaders.Lease RegisterReader(RecoveryReaders readers)
        {
            RecoveryReaders.Lease lease = readers.Register();
            System.Threading.Interlocked.Increment(ref outstandingRecoveryReaders);
            lease.Completion.ContinueWith(delegate(Task<bool> done) {
                bool confirmed = done.Status == TaskStatus.RanToCompletion && done.Result;
                if (!confirmed) stopBarrier.RetainUnconfirmed();
                if (done.IsFaulted) { Exception observed = done.Exception; }
                System.Threading.Interlocked.Decrement(ref outstandingRecoveryReaders);
                Post(Notify);
            }, TaskScheduler.Default);
            return lease;
        }
        private Task<bool> ObserveStop(Func<Task<bool>> request)
        {
            return stopBarrier.Observe(request, delegate(bool success) {
                Post(delegate {
                    if (!success) Report("恢复进程退出未确认，本次关屏已停用；显示恢复记录已保留，可正常退出。 / Recovery process exit could not be confirmed. Display off is disabled for this run; recovery data was preserved and normal exit remains available.");
                    Notify();
                });
            }, Program.Log);
        }
        private Task<bool> StopExecutor(IRecoveryExecutor executor, RecoveryReaders.Lease reader)
        {
            Task<bool> confirmed = executor == null ? ObserveStop(delegate { return Task.FromResult(false); }) : ObserveStop(executor.StopAsync);
            if (reader != null) reader.ConfirmWith(confirmed);
            return confirmed;
        }
        private void ConfirmFailedCreation(RecoveryReaders.Lease reader, Exception error)
        {
            GuardianStartException startup = error as GuardianStartException;
            Task<bool> stopped = startup == null ? Task.FromResult(false) : startup.StopConfirmation;
            reader.ConfirmWith(ObserveStop(delegate { return stopped; }));
        }
        private void StopAndArchive(IRecoveryExecutor executor, RuntimeMaintenance.OwnedSnapshot snapshot, RecoveryReaders readers, RecoveryReaders.Lease reader, bool safeToArchive)
        {
            if (executor != null || reader != null) StopExecutor(executor, reader);
            ArchiveSnapshot(snapshot, readers, safeToArchive);
        }
        private void ArchiveSnapshot(RuntimeMaintenance.OwnedSnapshot snapshot, RecoveryReaders readers, bool safeToArchive)
        {
            if (snapshot == null || !safeToArchive) return;
            Task<bool> readersStopped = readers == null ? Task.FromResult(true) : readers.Seal();
            ObserveArchive(runtimeFiles.ArchiveCompletedAsync(snapshot, readersStopped, true));
        }
        private void ObserveArchive(Task<bool> task)
        {
            lock (pendingMaintenance) pendingMaintenance.Add(task);
            task.ContinueWith(delegate(Task<bool> finished) {
                lock (pendingMaintenance) pendingMaintenance.Remove(task);
                if (finished.IsFaulted) Program.Log("snapshot_maintenance_task_failed: " + finished.Exception);
            }, TaskScheduler.Default);
        }
        private int HeldInputCount { get { return swallowed.Count + (hotkeyHeld ? 1 : 0); } }
        private void Report(string message, bool logMessage = true)
        {
            if (logMessage) Program.Log(message);
            Action<string> handler = Problem;
            if (handler == null) return;
            foreach (Action<string> subscriber in handler.GetInvocationList()) {
                try { subscriber(message); } catch (Exception ex) { Program.Log("session_problem_notification_failed: " + ex); }
            }
        }
        private bool Post(Action action)
        {
            if (disposed || IsDisposed || !IsHandleCreated) return false;
            try { BeginInvoke(action); return true; }
            catch (InvalidOperationException) { return false; }
        }
        private static bool AllInputsReleased()
        {
            for (int k = 1; k <= 255; k++) if ((Native.GetAsyncKeyState(k) & 0x8000) != 0) return false;
            return true;
        }
        internal static bool IsMttHardware(string[] ids)
        {
            if (ids == null) return false;
            foreach (string id in ids)
                if (String.Equals(id, @"Root\MttVDD", StringComparison.OrdinalIgnoreCase) ||
                    String.Equals(id, "MttVDD", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
        private static bool IsMttTarget(string adapterPath)
        {
            string instance = DisplayBackend.RootInstancePath(adapterPath);
            if (instance == null) return false;
            try {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\" + instance))
                    return key != null && IsMttHardware(key.GetValue("HardwareID") as string[]);
            } catch { return false; }
        }
        private IntPtr Decide(InputKind kind, int id, bool injected, IntPtr hook, int code, IntPtr w, IntPtr l)
        {
            InputDecision d = gate.Handle(kind, id, injected,
                flow.Phase == SessionPhase.SwitchingOff ? transitionTime : clock.ElapsedMilliseconds);
            if (!injected) {
                if ((kind == InputKind.KeyDown || kind == InputKind.ButtonDown) && d.Block) swallowed.Add(id);
                if (kind == InputKind.KeyUp || kind == InputKind.ButtonUp) swallowed.Remove(id);
            }
            if (d.RequestWake) BeginInvoke(new Action(Wake));
            return d.Block ? new IntPtr(1) : Native.CallNextHookEx(hook, code, w, l);
        }
        private IntPtr Keyboard(int code, IntPtr w, IntPtr l)
        {
            if (code < 0) return Native.CallNextHookEx(keyboardHook, code, w, l);
            try {
                Native.KeyboardData e = (Native.KeyboardData)Marshal.PtrToStructure(l, typeof(Native.KeyboardData));
                bool injected = (e.flags & 0x12) != 0;
                bool up = w.ToInt32() == 0x101 || w.ToInt32() == 0x105;
                Action<int, uint, bool> observer = SettingsKey;
                if (!injected && observer != null) {
                    try { observer((int)e.vkCode, up ? 0U : CurrentModifiers(), up); }
                    catch { /* UI observation must never affect input/session handling. */ }
                }
                hotkeyInput.Observe((int)e.vkCode, up, injected);
                ObservePhysicalActivity(up ? InputKind.KeyUp : InputKind.KeyDown, injected);
                if (!injected && e.vkCode == Hotkey.Key) {
                    if (hotkeyHeld) { if (up) hotkeyHeld = false; return new IntPtr(1); }
                    if (Supported && inputReady && !hotkeys.Changing && gate.CanStartHotkey(Hotkey.Key) &&
                        hotkeyInput.Matches(Hotkey, (int)e.vkCode, up, injected, CurrentModifiers())) {
                        hotkeyHeld = true;
                        BeginInvoke(new Action(delegate { TryBegin(false); })); return new IntPtr(1);
                    }
                }
                return Decide(up ? InputKind.KeyUp : InputKind.KeyDown, (int)e.vkCode, injected, keyboardHook, code, w, l);
            } catch { if (flow.Busy) BeginInvoke(new Action(Wake)); return Native.CallNextHookEx(keyboardHook, code, w, l); }
        }
        private static uint CurrentModifiers()
        {
            uint modifiers = 0;
            if ((Native.GetAsyncKeyState(0x11) & 0x8000) != 0) modifiers |= 2;
            if ((Native.GetAsyncKeyState(0x12) & 0x8000) != 0) modifiers |= 1;
            if ((Native.GetAsyncKeyState(0x10) & 0x8000) != 0) modifiers |= 4;
            if ((Native.GetAsyncKeyState(0x5B) & 0x8000) != 0 || (Native.GetAsyncKeyState(0x5C) & 0x8000) != 0) modifiers |= 8;
            return modifiers;
        }
        private IntPtr Mouse(int code, IntPtr w, IntPtr l)
        {
            if (code < 0) return Native.CallNextHookEx(mouseHook, code, w, l);
            try {
                Native.MouseData e = (Native.MouseData)Marshal.PtrToStructure(l, typeof(Native.MouseData));
                int msg = w.ToInt32(), id = 0; InputKind kind = InputKind.Move;
                switch (msg) {
                    case 0x201: kind = InputKind.ButtonDown; id = 257; break;
                    case 0x202: kind = InputKind.ButtonUp; id = 257; break;
                    case 0x204: kind = InputKind.ButtonDown; id = 258; break;
                    case 0x205: kind = InputKind.ButtonUp; id = 258; break;
                    case 0x207: kind = InputKind.ButtonDown; id = 259; break;
                    case 0x208: kind = InputKind.ButtonUp; id = 259; break;
                    case 0x20B: kind = InputKind.ButtonDown; id = ((e.mouseData >> 16) & 1) != 0 ? 260 : 261; break;
                    case 0x20C: kind = InputKind.ButtonUp; id = ((e.mouseData >> 16) & 1) != 0 ? 260 : 261; break;
                    case 0x20A: case 0x20E: kind = InputKind.Wheel; break;
                }
                ObservePhysicalActivity(kind, (e.flags & 3) != 0);
                return Decide(kind, id, (e.flags & 3) != 0, mouseHook, code, w, l);
            } catch { if (flow.Busy) BeginInvoke(new Action(Wake)); return Native.CallNextHookEx(mouseHook, code, w, l); }
        }
        private void ReleaseHooks()
        {
            if (keyboardHook != IntPtr.Zero) Native.UnhookWindowsHookEx(keyboardHook);
            if (mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(mouseHook);
            keyboardHook = mouseHook = IntPtr.Zero;
            hotkeys.Dispose();
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing && !disposed) {
                if (flow.Busy) throw new InvalidOperationException("必须先完成显示恢复及输入配对释放再销毁会话。");
                disposed = true; heartbeat.Stop(); heartbeat.Dispose(); ReleaseHooks();
                Native.SetThreadExecutionState(0x80000000);
            }
            base.Dispose(disposing);
        }
    }
}
