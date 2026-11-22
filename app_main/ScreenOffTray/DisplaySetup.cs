using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Diagnostics;
using System.Threading;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Win32;
using OledPrototype;
using ScreenOffTray.Properties;

namespace ScreenOffTray
{
    internal sealed class DisplaySetupResult
    {
        internal string Profile = "", Zh, En, Diagnostics = "";
        internal bool MatchingLive, PhysicalOnly, Compatible;
        internal bool VirtualModeMissing, ClonedDisplay, MetricsUnavailable;
        internal uint PhysicalWidth, PhysicalHeight, PhysicalDpi, VirtualWidth, VirtualHeight, VirtualDpi;
        internal string RequiredResolution = "";
        internal bool PreparationBlocked { get { return VirtualModeMissing || MetricsUnavailable; } }
        internal DateTime CheckedUtc = DateTime.UtcNow;
        internal string Detail(bool english) { return english ? En : Zh; }
    }

    // Probe and automatic native work run on workers. IsReady is a cheap
    // cached gate, safe to call from the session's input/hotkey dispatch.
    internal sealed class DisplaySetupState : IDisposable
    {
        private readonly Action<Action> post;
        private readonly Action<Action> completionPost;
        private bool checking;
        private volatile bool disposed;
        private readonly object autoSync = new object();
        private bool workerOccupied;
        private readonly IAutoSetupClock autoClock;
        private readonly IAutoSetupScheduler autoScheduler;
        private volatile bool preparing;
        private AutoSetupAttempt attempt;
        private readonly IDisplayEvidenceStore evidence;
        private readonly Func<IAutoSetupPlatform> autoPlatform;
        private readonly Func<DisplaySetupResult> readOnlyProbe;
        private bool recoveryUnresolved;
        private bool failedWithVerifiedOriginal;
        private bool autoAttemptFailed, unsupportedPreparation;
        private volatile bool modeAdapting;
        private int modeCancelled;
        private string modeFailure = "";
        internal bool Preparing { get { return preparing || modeAdapting; } }
        internal bool CanPrepare { get { lock (autoSync) return !disposed && !checking && !Preparing && !workerOccupied && !queryWorker.Occupied && !recoveryUnresolved && (Result == null || !Result.PreparationBlocked); } }
        // An old failed read must not veto a new explicit attempt. Each attempt
        // obtains its own live requirement before any mode or CCD operation.
        internal bool CanPrepareDisplay { get { lock (autoSync) return !disposed && !checking && !Preparing && !workerOccupied && !queryWorker.Occupied && !recoveryUnresolved; } }
        internal bool WaitingForPreviousQuery { get { lock (autoSync) return (workerOccupied && !preparing) || (queryTimedOut && queryWorker.Occupied); } }
        // Integration may archive RecoveryPath only at SafeToArchive, and only
        // after GuardianStopConfirmation has completed with true.
        internal AutoSetupOutcome LastAutoOutcome { get; private set; }
        private readonly List<Task> pendingMaintenance = new List<Task>();
        // The exit owner may await this with its own bounded budget. It never
        // includes a blocked preflight worker or delays cancellation/Preparing.
        internal Task PendingMaintenance
        {
            get {
                lock (autoSync) {
                    pendingMaintenance.RemoveAll(delegate(Task task) { return task.IsCompleted; });
                    if (pendingMaintenance.Count == 0) return Task.FromResult(0);
                    if (pendingMaintenance.Count == 1) return pendingMaintenance[0];
                    return Task.WhenAll(pendingMaintenance.ToArray());
                }
            }
        }
        private void TrackMaintenance(Task maintenance)
        {
            if (maintenance == null || maintenance.IsCompleted) return;
            lock (autoSync) {
                if (maintenance.IsCompleted || pendingMaintenance.Contains(maintenance)) return;
                pendingMaintenance.Add(maintenance);
            }
            maintenance.ContinueWith(delegate(Task completed) {
                lock (autoSync) pendingMaintenance.Remove(completed);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        internal string LastPreparationError { get; private set; }
        internal string Diagnostics { get; private set; }
        internal string Detail(bool english)
        {
            if (modeAdapting) return english ? "Adapting the virtual display. Administrator confirmation may be needed and the screen may flicker. Cancellation or exit waits for the device operation to finish." : "正在适配虚拟屏，可能需要管理员确认、画面可能闪动。取消或退出会等待设备操作完成。";
            if (preparing) return english ? "Preparing safely. Please wait; cancellation also waits for recovery." : "正在安全准备，请稍候；取消后也会等待恢复完成。";
            if(queryTimedOut && Result!=null)return Result.Detail(english);
            if (WaitingForPreviousQuery) return english ? "Preparation cancelled. Waiting for the previous query to return before retrying. You may exit normally." : "准备已取消，等待上一次查询返回后才能重试；现在可以正常退出。";
            if (recoveryUnresolved) return english ? "Display recovery is unverified. Display off stays disabled; retain the details." : "显示恢复尚未确认，关屏保持停用。请展开并保留详情。";
            if (IsReady) return english ? "Ready and enabled. Only the physical display is active." : "准备完成，已启用。当前仅实体屏输出。";
            if (checking) return english ? "Checking your displays. Please wait." : "正在检查显示配置，请稍候。";
            if (!String.IsNullOrEmpty(modeFailure)) return (english ? "Display adaptation did not complete; display off stays disabled.\r\n" : "显示适配未完成，关屏保持停用。\r\n") + modeFailure;
            if (autoAttemptFailed && LastAutoOutcome != null && !LastAutoOutcome.Success)
                return english ? LastAutoOutcome.En : LastAutoOutcome.Zh;
            if (Result != null && (Result.VirtualModeMissing || Result.ClonedDisplay || Result.MetricsUnavailable)) {
                string restored = failedWithVerifiedOriginal ? (english ? "The previous layout was verified restored.\r\n" : "上次操作的原布局已确认恢复。\r\n") : "";
                return restored + Result.Detail(english);
            }
            if (unsupportedPreparation && Result != null && (LastPreparationError ?? "").IndexOf("DPI", StringComparison.OrdinalIgnoreCase) >= 0) return Result.Detail(english);
            if (unsupportedPreparation) return failedWithVerifiedOriginal ?
                (english ? "Automatic preparation is unsupported for this setup. The original layout was verified restored; display off stays disabled." : "当前配置不支持自动准备。原布局已确认恢复，关屏保持停用。") :
                (english ? "Automatic preparation is unsupported for this setup. Display off stays disabled." : "当前配置不支持自动准备，关屏保持停用。");
            if (failedWithVerifiedOriginal) return english ? "Preparation did not complete. The original layout was verified restored; display off stays disabled." : "本次准备未完成，已确认原布局恢复。关屏保持停用。";
            return english ? "Automatic display preparation is needed before enabling display off." : "需要先自动准备显示配置，完成后即可启用关屏。";
        }
        private int generation;
        private int settledGeneration = -1;
        private volatile bool ready;
        private long validUntil;
        private readonly SingleReadOnlyWorker<DisplaySetupResult> queryWorker = new SingleReadOnlyWorker<DisplaySetupResult>();
        private IDisposable queryDeadline;
        private long queryId, queryStarted;
        private bool queryTimedOut, queryAwaiting;
        private string observedProfile, completedProfile;
        private DateTime observedUtc;
        internal DisplaySetupResult Result { get; private set; }
        internal event Action Changed;
        // Subscribe immediately after construction. Runs once per returned
        // attempt on a worker, before completion publication. Register only;
        // never wait for stopping or synchronously access UI from this callback.
        internal event Action<Task<bool>> RecoveryStopConfirmationAvailable;
        internal bool HasVerifiedProfile { get { return ready && !disposed && !Preparing && !recoveryUnresolved && autoClock.Milliseconds < validUntil; } }
        internal bool IsReady { get { return HasVerifiedProfile && !checking; } }
        internal bool Checking { get { return checking; } }
        // Invalidation and an in-flight refresh are not evidence of a failed check.
        internal bool HasSettledResult { get { return !disposed && !checking && !Preparing && Result != null && settledGeneration == generation; } }
        internal bool CanComplete { get; private set; }
        internal static IAutoSetupPlatform CreateDefaultAutoPlatform() { return new DpiMatchedAutoSetup(new AutoSetupNative()); }
        internal DisplaySetupState(Action<Action> post) : this(post, new SettingsDisplayEvidenceStore(), CreateDefaultAutoPlatform) { }
        // Only these lifecycle callbacks may bypass the tray's exiting filter.
        // The dispatcher must still marshal to the UI and reject a disposed host.
        internal DisplaySetupState(Action<Action> post, Action<Action> completionPost) : this(post)
        { this.completionPost = completionPost; }
        internal DisplaySetupState(Action<Action> post, IDisplayEvidenceStore evidence, Func<IAutoSetupPlatform> autoPlatform)
            : this(post, evidence, autoPlatform, DisplaySetupProbe.Probe) { }
        internal DisplaySetupState(Action<Action> post, IDisplayEvidenceStore evidence, Func<IAutoSetupPlatform> autoPlatform, Func<DisplaySetupResult> readOnlyProbe)
            : this(post, evidence, autoPlatform, readOnlyProbe, new AutoSetupClock(), new AutoSetupScheduler()) { }
        internal DisplaySetupState(Action<Action> post, IDisplayEvidenceStore evidence, Func<IAutoSetupPlatform> autoPlatform,
            Func<DisplaySetupResult> readOnlyProbe, IAutoSetupClock autoClock, IAutoSetupScheduler autoScheduler)
        {
            this.post = post;
            this.completionPost = post;
            this.evidence = evidence; this.autoPlatform = autoPlatform;
            this.readOnlyProbe = readOnlyProbe;
            this.autoClock = autoClock; this.autoScheduler = autoScheduler;
            Diagnostics = ""; LastPreparationError = "";
            try {
                DisplayEvidence saved = evidence.Load();
                observedProfile = saved.Observed;
                completedProfile = saved.Completed;
                DateTime.TryParse(saved.Checked, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out observedUtc);
                if(observedUtc > DateTime.UtcNow) { observedProfile = completedProfile = ""; observedUtc = DateTime.MinValue; }
            } catch (Exception ex) {
                observedProfile = completedProfile = "";
                Result = new DisplaySetupResult { Zh = "设置无法读取，请保留配置文件并按帮助修复；关屏已停用。\r\n" + ex.Message,
                    En = "Settings could not be read. Preserve the configuration and follow the local help to repair it; display off is disabled.\r\n" + ex.Message };
                Diagnostics = ex.ToString();
                settledGeneration = generation;
            }
        }
        internal DisplaySetupState(Action<Action> post, IDisplayEvidenceStore evidence, Func<IAutoSetupPlatform> autoPlatform,
            Func<DisplaySetupResult> readOnlyProbe, IAutoSetupClock autoClock, IAutoSetupScheduler autoScheduler, Action<Action> completionPost)
            : this(post, evidence, autoPlatform, readOnlyProbe, autoClock, autoScheduler)
        { this.completionPost = completionPost; }
        internal void Invalidate()
        {
            if (disposed) return;
            CancelPreparation();
            ++generation; ready = false; CanComplete = false;
            if (Changed != null) Changed();
        }
        internal void Recheck()
        {
            if (disposed || checking || Preparing || WaitingForPreviousQuery || queryWorker.Occupied || recoveryUnresolved) return;
            modeFailure = "";
            checking = true; queryTimedOut = false; queryAwaiting = true; int token = generation; long id = ++queryId;
            queryStarted = autoClock.Milliseconds;
            queryDeadline = autoScheduler.Schedule(delegate { post(delegate { if(queryAwaiting) ExpireQuery(id); }); }, 30000);
            if (Changed != null) Changed();
            if (!queryWorker.TryRead(readOnlyProbe, delegate(DisplaySetupResult value, Exception failure) {
                post(async delegate {
                    if (disposed || id != queryId) return;
                    queryAwaiting = false;
                    if (queryDeadline != null) { queryDeadline.Dispose(); queryDeadline = null; }
                    if (queryTimedOut || autoClock.Milliseconds - queryStarted >= 30000) {
                        ExpireQuery(id); if (Changed != null) Changed(); return;
                    }
                    if (token != generation) { checking=false; if(Changed!=null)Changed(); return; }
                    checking = false;
                    if (token != generation) { if (Changed != null) Changed(); return; }
                    DisplaySetupResult result = value;
                    if (failure != null || result == null) result = new DisplaySetupResult {
                        Zh = "无法核实显示配置；关屏保持暂停。",
                        En = "Display check failed; display off stays paused.",
                        Diagnostics = failure == null ? "No display result" : failure.ToString() };
                    Result = result;
                    settledGeneration = token;
                    Diagnostics = String.IsNullOrEmpty(result.Diagnostics) ? result.Zh + "\r\n" + result.En : result.Diagnostics;
                    if (result.MatchingLive) {
                        observedProfile = result.Profile; observedUtc = result.CheckedUtc;
                        // Extended mode is setup only, never the final operating baseline.
                        completedProfile = "";
                        if (!await SaveEvidenceAsync()) { ready = false; CanComplete = false; if (Changed != null) Changed(); return; }
                    } else if (!result.PhysicalOnly || result.Profile != observedProfile) {
                        completedProfile = "";
                        if (!result.PhysicalOnly) observedProfile = "";
                        if (!await SaveEvidenceAsync()) { ready = false; CanComplete = false; if (Changed != null) Changed(); return; }
                    }
                    if(disposed || id!=queryId || token!=generation) {ready=false;return;}
                    CanComplete = !autoAttemptFailed && DisplayReadinessPolicy.CanComplete(result, observedProfile, observedUtc, DateTime.UtcNow);
                    ready = CanComplete && result.Profile == completedProfile;
                    validUntil = autoClock.Milliseconds + 35000;
                    if (Changed != null) Changed();

                });
            })) {
                checking = false;
                if (queryDeadline != null) { queryDeadline.Dispose(); queryDeadline = null; }
                if (Changed != null) Changed();
            }
        }
        private void ExpireQuery(long id)
        {
            if (disposed || id != queryId || queryTimedOut || (!queryAwaiting && autoClock.Milliseconds-queryStarted<30000)) return;
            queryAwaiting = false; queryTimedOut = true; checking = false; ready = false; CanComplete = false;
            if (queryDeadline != null) { queryDeadline.Dispose(); queryDeadline = null; }
            Result = new DisplaySetupResult { Zh = "显示检查超过30秒，关屏保持暂停。原查询结束前不会重复启动。", En = "Display check timed out after 30 seconds; display off remains paused. Waiting for the original query.", Diagnostics = "read_only_query_timeout" };
            Diagnostics = Result.Diagnostics; settledGeneration = generation;
            if (Changed != null) Changed();
        }
        internal bool Complete()
        {
            if (evidence is SettingsDisplayEvidenceStore || disposed || Preparing || WaitingForPreviousQuery || recoveryUnresolved || checking || !CanComplete || Result == null || autoClock.Milliseconds - queryStarted >= 30000) return false;
            string oldCompleted = completedProfile;
            completedProfile = Result.Profile;
            if (!SaveEvidence()) { completedProfile = oldCompleted; ready = false; return false; }
            ready = true; validUntil = autoClock.Milliseconds + 35000;
            if (Changed != null) Changed();
            return true;
        }
        private async Task<bool> SaveEvidenceAsync()
        {
            var value=new DisplayEvidence {Observed=observedProfile,Checked=observedUtc.ToString("o",CultureInfo.InvariantCulture),Completed=completedProfile};
            int token=generation;bool previousChecking=checking;checking=true;
            try { await SaveEvidenceValueAsync(evidence,value);return !disposed && token==generation; }
            catch(Exception ex) {Diagnostics=ex.ToString();ready=false;return false;}
            finally {checking=previousChecking;}
        }
        private static Task SaveEvidenceValueAsync(IDisplayEvidenceStore store,DisplayEvidence value)
        {
            var real=store as SettingsDisplayEvidenceStore;
            if(real!=null)return real.SaveAsync(value);
            try {store.Save(value);return Task.FromResult(0);}catch(Exception ex){var failed=new TaskCompletionSource<bool>();failed.SetException(ex);return failed.Task;}
        }
        private bool SaveEvidence()
        {
            try {
                evidence.Save(new DisplayEvidence { Observed = observedProfile, Checked = observedUtc.ToString("o", CultureInfo.InvariantCulture), Completed = completedProfile }); return true;
            } catch (Exception ex) { Diagnostics = ex.ToString(); ready = false; return false; }
        }
        // The explicit UI entry always does a fresh mode check, including inactive
        // VDDs. Its lifetime is separate from the old 30s setup/CCD guardian.
        internal bool PrepareDisplay() { return PrepareDisplay(delegate { return new VirtualModeNative(); }); }
        internal bool PrepareDisplay(Func<IVirtualModePlatform> platform)
        {
            Task<bool> work=PrepareDisplayAsync(platform);
            work.ContinueWith(delegate(Task<bool> done) {if(done.IsFaulted) OledPrototype.Program.Log("mode_prepare_dispatch_failed: "+done.Exception);},TaskScheduler.Default);
            return work.IsCompleted ? work.GetAwaiter().GetResult() : true;
        }
        private async Task<bool> PrepareDisplayAsync(Func<IVirtualModePlatform> platform)
        {
            int token;
            lock (autoSync) {
                if (!CanPrepareDisplay) return false;
                ready = false; CanComplete = false; modeFailure = "";
                observedProfile = completedProfile = "";
                token = ++generation; settledGeneration = -1;
                modeCancelled = 0; modeAdapting = true;
                LastPreparationError = ""; LastAutoOutcome = null;
            }
            if(!await SaveEvidenceAsync()) {
                lock(autoSync) {modeAdapting=false;modeFailure=Diagnostics;}
                if(Changed!=null)Changed();return false;
            }
            string notificationFailure = "";
            try { if (Changed != null) Changed(); }
            catch (Exception ex) {
                // A presentation subscriber must not strand the gate before its
                // worker exists. Keep the error with this operation's diagnostics.
                notificationFailure = "\r\nmode_setup_notify_failed: " + ex;
                lock (autoSync) Diagnostics += notificationFailure;
            }
            Task.Run(delegate { return VirtualModeAdaptation.Run(platform(), delegate { return disposed || Volatile.Read(ref modeCancelled) != 0 || generation != token; }); })
                .ContinueWith(delegate(Task<VirtualModeAdaptationResult> work) {
                    var result = work.IsFaulted ? new VirtualModeAdaptationResult { Error = work.Exception.GetBaseException().Message, Diagnostics = work.Exception.ToString() } : work.Result;
                    result.Diagnostics = notificationFailure + result.Diagnostics;
                    Action finish = delegate {
                        lock (autoSync) {
                            modeAdapting = false;
                            if (disposed) return;
                            ready = false; CanComplete = false; Result = null;
                            Diagnostics = result.Diagnostics; settledGeneration = generation;
                            if (!result.Success || Volatile.Read(ref modeCancelled) != 0 || generation != token) {
                                modeFailure = LastPreparationError = String.IsNullOrEmpty(result.Error) ? "适配已取消或显示状态变化，请重新准备 / Adaptation cancelled or display changed; prepare again." : result.Error;
                                Result = new DisplaySetupResult { Zh = modeFailure, En = modeFailure };
                                if (Changed != null) Changed(); return;
                            }
                            // Construct AutoSetupNative and PlanCheck only AFTER
                            // reload/readback. No pre-reload LUID snapshot exists.
                            if (!AutoPrepare(result.Requirement, result.Verify)) {
                                modeFailure = LastPreparationError = "未能开始受保护显示准备，请重新检查 / Could not start protected display preparation; check again.";
                                Result = new DisplaySetupResult { Zh = modeFailure, En = modeFailure };
                                if (Changed != null) Changed();
                            }
                        }
                    };
                    try { completionPost(finish); }
                    catch (Exception ex) {
                        lock (autoSync) { modeAdapting = false; ready = false; CanComplete = false; modeFailure = LastPreparationError = "完成通知失败 / Completion dispatch failed: " + ex.Message; Diagnostics += "\r\n" + result.Diagnostics + "\r\n" + ex; }
                    }
                }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            return true;
        }
        internal bool AutoPrepare() { return AutoPrepare(null, null); }
        private bool AutoPrepare(VirtualModeRequirement requirement, Action verifyMode)
        {
            AutoSetupAttempt current;
            DisplayEvidence previous;
            lock (autoSync) {
                if (!CanPrepare) return false;
                try { previous = evidence.Load(); }
                catch (Exception ex) { LastPreparationError = ex.Message; Diagnostics = ex.ToString(); return false; }
                current = new AutoSetupAttempt(generation, autoClock, autoScheduler);
                current.PreflightCancelled += delegate { EndPreflight(current); };
                attempt = current; workerOccupied = true; preparing = true; ready = false; CanComplete = false;
                LastAutoOutcome = null;
                LastPreparationError = ""; Diagnostics = ""; failedWithVerifiedOriginal = false; unsupportedPreparation = false;
            }
            NotifyAuto(current);
            Task<AutoSetupOutcome> worker;
            try {
                current.StartDeadline();
                worker = Task.Run(delegate {
                AutoSetupOutcome outcome;
                try {
                    current.Check();
                    IAutoSetupPlatform native = autoPlatform();
                    if (requirement != null) native = new ModeCheckedAutoSetup(native, requirement, verifyMode);
                    outcome = DisplayAutoSetup.Run(native, current, post,
                        delegate(DisplaySetupResult live, DisplaySetupResult final) {
                            if(!InvokeAutoUi(current,delegate {if(!AutoCurrent(current))throw new OperationCanceledException();},false))return false;
                            SaveEvidenceValueAsync(evidence,new DisplayEvidence {Observed=live.Profile,Checked=live.CheckedUtc.ToString("o",CultureInfo.InvariantCulture),Completed=final.Profile}).GetAwaiter().GetResult();
                            return true;
                        },
                        delegate { return InvokeAutoUi(current, delegate {
                            if (!AutoCurrent(current) || !current.TryCommit()) throw new OperationCanceledException();
                        }, false); },
                        delegate { if(!InvokeAutoUi(current,delegate{},true))return false;SaveEvidenceValueAsync(evidence,previous).GetAwaiter().GetResult();return true; });
                } catch (Exception ex) { outcome = new AutoSetupOutcome { Error = ex.Message, Diagnostics = ex.ToString(), RecoveryUnresolved = current.Guarded,
                    GuardianStopConfirmation = Task.FromResult(!current.Guarded),
                    Zh = "准备未完成，关屏保持停用，请保留诊断记录。", En = "Preparation did not complete; display off remains disabled. Retain diagnostics." }; }
                return outcome;
                });
            } catch (Exception ex) {
                current.Cancel(); current.Dispose();
                lock (autoSync) { workerOccupied = false; Diagnostics += "\r\nworker_dispatch_failed: " + ex; }
                NotifyAuto(current); return false;
            }
            // Release the slot only after the Task actually returns. Cancellation
            // does not abort a native query, nor permit a second blocked worker.
            worker.ContinueWith(delegate(Task<AutoSetupOutcome> task) {
                AutoSetupOutcome finished = task.Result;
                current.Dispose();
                PublishRecoveryStopConfirmation(current, finished);
                lock (autoSync) {
                    TrackMaintenance(finished.ArchiveConfirmation);
                    workerOccupied = false; LastAutoOutcome = finished;
                }
                try { completionPost(delegate { FinishAuto(current, finished, true); }); }
                catch (Exception ex) {
                    // No UI dispatcher is available. Recovery has already run;
                    // settle memory only, never publish ready or save settings.
                    finished.Diagnostics += "\r\nauto_setup_dispatch_failed: " + ex;
                    FinishAuto(current, finished, false);
                }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            return true;
        }
        private void PublishRecoveryStopConfirmation(AutoSetupAttempt current, AutoSetupOutcome finished)
        {
            Task<bool> stopping = finished.GuardianStopConfirmation ?? Task.FromResult(false);
            finished.GuardianStopConfirmation = stopping;
            Action<Task<bool>> available = RecoveryStopConfirmationAvailable;
            Exception failure = null;
            if (available != null) foreach (Action<Task<bool>> subscriber in available.GetInvocationList()) {
                try { subscriber(stopping); }
                catch (Exception ex) { failure = failure == null ? ex : new AggregateException(failure, ex); }
            }
            if (failure == null) return;
            // An exception registering the shared barrier must not leave any
            // successful completion capable of enabling display off. Exit and
            // preflight cancellation still do not wait for the stop task.
            finished.Success = false; finished.RecoveryUnresolved = true;
            finished.Error = "恢复保护停止确认接线失败 / Recovery stop confirmation registration failed.";
            finished.Diagnostics += "\r\nrecovery_stop_registration_failed: " + failure;
            finished.Zh = "恢复保护停止确认接线失败，关屏保持停用。";
            finished.En = "Recovery stop confirmation registration failed; display off remains disabled.";
            lock (autoSync) {
                recoveryUnresolved = true; ready = false; CanComplete = false;
                if (!disposed && ReferenceEquals(attempt, current) && current.Generation == generation) {
                    LastPreparationError = finished.Error; Diagnostics = finished.Diagnostics;
                }
            }
        }
        private void EndPreflight(AutoSetupAttempt current)
        {
            lock (autoSync) {
                if (disposed || !ReferenceEquals(attempt, current) || !current.PreflightWasCancelled || !preparing) return;
                // This transition is independent of the blocked worker and of
                // UI dispatch. A guarded attempt can never enter this branch.
                preparing = false; ready = false; CanComplete = false; autoAttemptFailed = true;
                LastPreparationError = current.DeadlineExpired ? "准备超过安全时限 / Preparation exceeded its safety deadline." : "准备已取消 / Preparation cancelled.";
                Diagnostics = LastPreparationError;
                Result = new DisplaySetupResult { Zh = "准备已取消，关屏保持停用。", En = "Preparation cancelled; display off remains disabled." };
            }
            NotifyAuto(current);
        }
        private void NotifyAuto(AutoSetupAttempt current)
        {
            try { completionPost(delegate {
                lock (autoSync) {
                    if (!disposed && ReferenceEquals(attempt, current) && current.Generation == generation && Changed != null) Changed();
                }
            }); } catch (Exception ex) {
                lock (autoSync) {
                    if (!disposed && ReferenceEquals(attempt, current) && current.Generation == generation)
                        Diagnostics += "\r\nauto_setup_notify_failed: " + ex;
                }
            }
        }
        private void FinishAuto(AutoSetupAttempt current, AutoSetupOutcome finished, bool dispatched)
        {
            lock (autoSync) {
                if (!ReferenceEquals(attempt, current)) return;
                attempt = null;
                // Early cancellation has already settled UI state. Late query
                // results must not overwrite it or revive a new generation.
                if (current.PreflightWasCancelled) {
                    if (dispatched && !disposed && current.Generation == generation && Changed != null) Changed();
                    return;
                }
                preparing = false;
                if (!disposed) {
                    recoveryUnresolved = finished.RecoveryUnresolved;
                    if (current.Generation != generation) {
                        ready = false; CanComplete = false;
                        if (dispatched && Changed != null) Changed(); // lifecycle only; retain current-generation content
                        return;
                    }
                    settledGeneration = current.Generation;
                    failedWithVerifiedOriginal = !finished.Success && finished.OriginalVerified;
                    autoAttemptFailed = !finished.Success || !dispatched; unsupportedPreparation = finished.Unsupported;
                    Diagnostics = finished.Diagnostics; LastPreparationError = finished.Error ?? "";
                    ready = false; CanComplete = false;
                    if (finished.Success && dispatched && current.Generation == generation) {
                        Result = finished.Final; observedProfile = finished.Live.Profile; observedUtc = finished.Live.CheckedUtc;
                        completedProfile = finished.Final.Profile; ready = true; CanComplete = true;
                        validUntil = autoClock.Milliseconds + 35000;
                        Result.Zh = "准备完成，已启用。当前仅实体屏输出。";
                        Result.En = "Preparation complete and enabled. Only the physical display is active.";
                    } else {
                        Result = new DisplaySetupResult { Zh = finished.Zh ?? "显示状态已变化，关屏保持停用，请重新检查。",
                            En = finished.En ?? "Display state changed; display off remains disabled. Please recheck." };
                    }
                    if (dispatched && current.Generation == generation && Changed != null) Changed();
                }
            }
        }
        private bool AutoCurrent(AutoSetupAttempt current)
        { lock (autoSync) return !disposed && ReferenceEquals(attempt, current) && current.Generation == generation && !current.Cancelled; }
        private bool InvokeAutoUi(AutoSetupAttempt current, Action action, bool recovery)
        {
            var completion = new TaskCompletionSource<bool>();
            int claimed = 0;
            post(delegate {
                if (Interlocked.CompareExchange(ref claimed, 1, 0) != 0) return;
                try { lock (autoSync) { if (!recovery && !AutoCurrent(current)) throw new OperationCanceledException(); action(); } completion.TrySetResult(true); }
                catch (Exception ex) { completion.TrySetException(ex); }
            });
            while (!completion.Task.Wait(100)) {
                if ((!recovery && current.Cancelled) || current.ElapsedMilliseconds > (recovery ? 42000 : 30000)) {
                    // Expire a queued callback. An executing settings write must finish
                    // before recovery/rollback; watchdog is the independent upper bound.
                    if (Interlocked.CompareExchange(ref claimed, 2, 0) == 0) return false;
                }
            }
            return completion.Task.GetAwaiter().GetResult();
        }
        internal void CancelPreparation()
        {
            if (modeAdapting) Interlocked.Exchange(ref modeCancelled, 1);
            AutoSetupAttempt current; lock (autoSync) current = attempt;
            if (current != null) {
                current.Cancel();
                // A deadline callback may already have won cancellation but not
                // yet acquired autoSync. Synchronous exit must still settle now.
                if (current.PreflightWasCancelled) EndPreflight(current);
            }
        }
        public void Dispose()
        {
            CancelPreparation();
            lock (autoSync) { disposed = true; ready = false; ++generation; }
            if (queryDeadline != null) { queryDeadline.Dispose(); queryDeadline = null; }
            queryWorker.Dispose();
            // Do not wait for, stop the clock of, or dispose state still read by
            // the worker. Its completion owns disposal of the deadline timer.
        }
    }

    internal sealed class DisplayEvidence
    {
        internal string Observed, Checked, Completed;
    }
    internal interface IDisplayEvidenceStore
    {
        DisplayEvidence Load();
        void Save(DisplayEvidence value);
    }
    internal sealed class SettingsDisplayEvidenceStore : IDisplayEvidenceStore
    {
        public DisplayEvidence Load()
        {
            return new DisplayEvidence { Observed = Settings.Default.DisplayProfile,
                Checked = Settings.Default.DisplayProfileCheckedUtc, Completed = Settings.Default.CompletedDisplayProfile };
        }
        private static void Assign(DisplayEvidence value)
        {
            Settings.Default.DisplayProfile = value.Observed;
            Settings.Default.DisplayProfileCheckedUtc = value.Checked;
            Settings.Default.CompletedDisplayProfile = value.Completed;
        }
        public void Save(DisplayEvidence value) { SaveAsync(value).GetAwaiter().GetResult(); }
        internal Task SaveAsync(DisplayEvidence value)
        {return SerializedSettingsWriter.Default.UpdateAsync(delegate {Assign(value);});}

    }
    internal interface IAutoSetupClock { long Milliseconds { get; } }
    internal interface IAutoSetupScheduler { IDisposable Schedule(Action callback, int milliseconds); }
    internal sealed class AutoSetupClock : IAutoSetupClock
    {
        private readonly Stopwatch clock = Stopwatch.StartNew();
        public long Milliseconds { get { return clock.ElapsedMilliseconds; } }
    }
    internal sealed class AutoSetupScheduler : IAutoSetupScheduler
    {
        public IDisposable Schedule(Action callback, int milliseconds)
        { return new Timer(delegate { callback(); }, null, milliseconds, Timeout.Infinite); }
    }
    internal sealed class AutoSetupAttempt : IDisposable
    {
        private readonly object sync = new object();
        private readonly IAutoSetupClock clock;
        private readonly IAutoSetupScheduler scheduler;
        private readonly long started;
        private IDisposable deadline;
        private bool cancelled, committed, guarded, preflightCancelled, expired, deadlineStarted, disposed;
        internal event Action PreflightCancelled;
        internal readonly int Generation;
        internal AutoSetupAttempt(int generation) : this(generation, new AutoSetupClock(), new AutoSetupScheduler()) { }
        internal AutoSetupAttempt(int generation, IAutoSetupClock clock, IAutoSetupScheduler scheduler)
        { Generation = generation; this.clock = clock; this.scheduler = scheduler; started = clock.Milliseconds; }
        internal long ElapsedMilliseconds { get { return clock.Milliseconds - started; } }
        internal bool Cancelled { get { lock (sync) return cancelled; } }
        internal bool Guarded { get { lock (sync) return guarded; } }
        internal bool PreflightWasCancelled { get { lock (sync) return preflightCancelled; } }
        internal bool DeadlineExpired { get { lock (sync) return expired; } }
        internal void StartDeadline()
        {
            lock (sync) { if (deadlineStarted || disposed) return; deadlineStarted = true; }
            IDisposable scheduled = scheduler.Schedule(OnDeadline, (int)Math.Max(0, 30000 - ElapsedMilliseconds));
            lock (sync) { if (disposed) scheduled.Dispose(); else deadline = scheduled; }
        }
        private void OnDeadline()
        {
            // The clock is authoritative, even if a scheduler wakes early.
            long remaining = 30000 - ElapsedMilliseconds;
            if (remaining > 0) {
                lock (sync) {
                    if (disposed || committed || cancelled) return;
                    if (deadline != null) deadline.Dispose();
                    deadline = scheduler.Schedule(OnDeadline, (int)remaining);
                }
                return;
            }
            CancelCore(true);
        }
        internal void Cancel() { CancelCore(false); }
        private void CancelCore(bool timeout)
        {
            Action notify = null;
            lock (sync) {
                if (committed || cancelled) return;
                expired = timeout; cancelled = true;
                if (!guarded) { preflightCancelled = true; notify = PreflightCancelled; }
            }
            if (notify != null) notify();
        }
        internal bool TryEnterGuarded()
        {
            lock (sync) {
                if (!cancelled && ElapsedMilliseconds < 30000) { guarded = true; return true; }
            }
            CancelCore(ElapsedMilliseconds >= 30000); return false;
        }
        internal bool TryCommit() { lock (sync) { if (cancelled || ElapsedMilliseconds >= 30000) return false; committed = true; return true; } }
        internal void Check()
        {
            if (ElapsedMilliseconds >= 30000) CancelCore(true);
            if (DeadlineExpired) throw new TimeoutException("准备超过安全时限 / Preparation exceeded its safety deadline.");
            if (Cancelled) throw new OperationCanceledException("准备已取消 / Preparation cancelled.");
        }
        public void Dispose() { lock (sync) { disposed = true; if (deadline != null) deadline.Dispose(); } }
    }
    internal sealed class AutoSetupPlan
    {
        internal DisplayBackend.Snapshot Original, Extended, PhysicalOnly;
        internal DisplayBackend.Plan VirtualOnly;
        internal string Summary;
    }
    internal sealed class AutoSetupOutcome
    {
        internal bool Success, RecoveryUnresolved, OriginalVerified, Unsupported;
        internal DisplaySetupResult Live, Final;
        internal string Zh, En, Error, Diagnostics;
        internal string RecoveryPath;
        internal bool SafeToArchive;
        internal Task<bool> GuardianStopConfirmation = Task.FromResult(true);
        internal Task<bool> ArchiveConfirmation = Task.FromResult(false);
    }
    internal interface IAutoSetupGuardian
    {
        bool IsAlive { get; }
        void Arm();
        void Pulse();
        void Stop();
        Task<bool> StopAsync();
        void Relinquish();
    }
    internal sealed class AutoSetupUnsupportedException : InvalidOperationException
    {
        internal AutoSetupUnsupportedException(string message) : base(message) { }
        internal AutoSetupUnsupportedException(string message, Exception inner) : base(message, inner) { }
    }
    internal interface IAutoSetupPlatform
    {
        AutoSetupPlan PlanCheck();
        DisplayBackend.Snapshot Capture();
        void Validate(DisplayBackend.Snapshot snapshot);
        void Apply(DisplayBackend.Snapshot expected, DisplayBackend.Snapshot desired, Action beforeApply);
        DisplaySetupResult Probe();
        string SaveRecovery(DisplayBackend.Snapshot original);
        IAutoSetupGuardian StartGuardian(string path);
        // A null path is a successful summary for the bounded log, not a sidecar.
        void Record(string path, string detail);
    }
    // Optional, fakeable completion hook. Own the token at creation time; never
    // reconstruct ownership from the string path returned by SaveRecovery.
    internal interface IAutoSetupRecoveryCompletion
    {
        Task<bool> CompleteRecoveryAsync(Task<bool> guardianStopped, bool safeToArchive);
    }

    // No UI, hooks, power API or virtual-only APPLY. The only production APPLYs
    // are extended, physical-only, and the retained original recovery snapshot.
    internal static class DisplayAutoSetup
    {
        internal static AutoSetupOutcome Run(IAutoSetupPlatform platform, AutoSetupAttempt attempt,
            Action<Action> post, Func<DisplaySetupResult, DisplaySetupResult, bool> save,
            Func<bool> commit, Func<bool> rollback)
        {
            var outcome = new AutoSetupOutcome();
            AutoSetupPlan plan = null;
            IAutoSetupGuardian guardian = null;
            Timer heartbeat = null;
            object guardLock = new object();
            bool closed = false, applied = false, saved = false, restored = false;
            int pulseQueued = 0;
            string recoveryPath = null;
            try {
                attempt.StartDeadline();
                attempt.Check(); plan = platform.PlanCheck(); attempt.Check();
                DisplayBackend.VerifySnapshot(plan.Original, platform.Capture());
                attempt.Check();
                recoveryPath = platform.SaveRecovery(plan.Original);
                // This atomic transition races cancellation/deadline. Only its
                // winner may start the guardian and proceed towards Arm/APPLY.
                if (!attempt.TryEnterGuarded()) attempt.Check();
                // Unknown startup failures provide no child-exit proof. A typed
                // failure carries the exact stop task even without a guardian.
                outcome.GuardianStopConfirmation = Task.FromResult(false);
                try { guardian = platform.StartGuardian(recoveryPath); } // Constructor waits for ready on worker.
                catch (GuardianStartException start) {
                    outcome.GuardianStopConfirmation = start.StopConfirmation ?? Task.FromResult(false);
                    throw;
                }
                heartbeat = new Timer(delegate {
                    if (Interlocked.CompareExchange(ref pulseQueued, 1, 0) != 0) return;
                    try { post(delegate {
                        try { lock (guardLock) { if (!closed && guardian != null) guardian.Pulse(); } }
                        finally { Interlocked.Exchange(ref pulseQueued, 0); }
                    }); } catch { Interlocked.Exchange(ref pulseQueued, 0); }
                }, null, 500, 500);
                attempt.Check();
                if (!guardian.IsAlive) throw new InvalidOperationException("恢复保护不可用 / Recovery guardian unavailable.");
                guardian.Arm(); // ready -> arm -> validate/recheck -> first APPLY.
                attempt.Check();
                RequireGuardian(guardian);
                platform.Apply(plan.Original, plan.Extended, delegate { attempt.Check(); RequireGuardian(guardian); applied = true; });
                var extended = platform.Capture(); DisplayAutoCcd.VerifyExtended(plan, extended);
                attempt.Check(); RequireGuardian(guardian);
                outcome.Live = ReadSettledProof(delegate { return ReadProof(platform); },
                    delegate(DisplaySetupResult r) { return r != null && r.MatchingLive && !String.IsNullOrEmpty(r.Profile); },
                    delegate { attempt.Check(); RequireGuardian(guardian); }, delegate { return attempt.ElapsedMilliseconds; },
                    delegate(int delay) { Thread.Sleep(delay); }, delegate(DisplaySetupResult value) { outcome.Live = value; });
                if (!outcome.Live.MatchingLive || String.IsNullOrEmpty(outcome.Live.Profile))
                    throw new AutoSetupUnsupportedException("不支持此配置：未能核实虚拟屏独立且一致的分辨率和缩放；不会降低实体屏模式。 / Unsupported: independent virtual resolution and DPI could not be verified; physical mode is unchanged.");
                DisplayAutoCcd.VerifyExtended(plan, platform.Capture());
                attempt.Check(); RequireGuardian(guardian);
                platform.Apply(extended, plan.PhysicalOnly, delegate { attempt.Check(); RequireGuardian(guardian); });
                DisplayBackend.VerifySnapshot(plan.PhysicalOnly, platform.Capture());
                attempt.Check(); RequireGuardian(guardian);
                outcome.Final = ReadSettledProof(delegate { return ReadProof(platform); },
                    delegate(DisplaySetupResult r) { return DisplayReadinessPolicy.CanComplete(r, outcome.Live.Profile, outcome.Live.CheckedUtc, DateTime.UtcNow); },
                    delegate { attempt.Check(); RequireGuardian(guardian); }, delegate { return attempt.ElapsedMilliseconds; },
                    delegate(int delay) { Thread.Sleep(delay); }, delegate(DisplaySetupResult value) { outcome.Final = value; });
                if (!DisplayReadinessPolicy.CanComplete(outcome.Final, outcome.Live.Profile, outcome.Live.CheckedUtc, DateTime.UtcNow))
                    throw new AutoSetupUnsupportedException("不支持此配置：仅实体屏下的严格兼容校验或配置身份不一致 / Unsupported: strict physical-only compatibility or profile identity did not match.");
                DisplayBackend.VerifySnapshot(plan.PhysicalOnly, platform.Capture());
                attempt.Check(); RequireGuardian(guardian);
                // Save is on the UI thread while guardian is still armed.
                // Failure restores the layout before waiting for UI rollback.
                saved = true;
                if (!save(outcome.Live, outcome.Final)) throw new OperationCanceledException("设置提交已取消 / Settings submission cancelled.");
                attempt.Check(); RequireGuardian(guardian);
                DisplayBackend.VerifySnapshot(plan.PhysicalOnly, platform.Capture());
                if (!commit()) throw new OperationCanceledException("准备提交已取消 / Preparation commit cancelled.");
                outcome.Success = true;
                outcome.Diagnostics = "strict_extend_and_physical_only_verified; real_MatchingLive; UI_settings_committed; recovery=" + recoveryPath;
            } catch (Exception ex) {
                outcome.Error = ex.Message; outcome.Diagnostics = ex.ToString();
                outcome.Unsupported = ex is AutoSetupUnsupportedException;
                bool settingsRestored = true;
                try {
                    if (applied && plan != null) {
                        var current = platform.Capture();
                        try { DisplayBackend.VerifySnapshot(plan.Original, current); restored = true; }
                        catch {
                            DisplayAutoCcd.CheckRecoveryScope(plan, current);
                            // Apply rechecks current twice, including after strict VALIDATE.
                            platform.Apply(current, plan.Original, delegate {
                                if (attempt.ElapsedMilliseconds >= 42000) throw new TimeoutException("Local recovery deadline exceeded; guardian retains ownership.");
                            });
                            DisplayBackend.VerifySnapshot(plan.Original, platform.Capture()); restored = true;
                        }
                    }
                } catch (Exception recovery) { outcome.Diagnostics += "\r\noriginal_recovery_failed: " + recovery; }
                if (saved) {
                    try { if (!rollback()) throw new InvalidOperationException("设置回退回调未完成 / Settings rollback callback did not complete."); }
                    catch (Exception error) { settingsRestored = false; outcome.Diagnostics += "\r\nsettings_rollback_failed: " + error; }
                }
                outcome.RecoveryUnresolved = applied && !restored;
                outcome.OriginalVerified = restored;
                if (outcome.RecoveryUnresolved) {
                    outcome.Zh = "准备未完成，关屏保持停用。原布局尚未核实恢复，已保留恢复材料，独立保护将接管。";
                    outcome.En = "Preparation failed; display off remains disabled. Original layout recovery is unverified. Recovery evidence is retained and the independent guardian will take over.";
                } else if (restored) {
                    outcome.Zh = "准备未完成，已严格回读确认原布局。关屏保持停用。";
                    outcome.En = "Preparation did not complete. The original layout has been strictly verified; display off remains disabled.";
                } else {
                    outcome.Zh = "此配置暂不支持自动准备，未执行显示切换；关屏保持停用。";
                    outcome.En = "Automatic preparation is unsupported for this configuration. No display switch was applied; display off remains disabled.";
                }
                if (!settingsRestored) { outcome.Zh += " 设置回退未确认，请保留诊断记录。"; outcome.En += " Settings rollback is unverified; retain diagnostics."; }
                if (outcome.Live != null && !outcome.Live.MatchingLive) {
                    string measured = MeasurementSummary(outcome.Live, false);
                    string measuredEn = MeasurementSummary(outcome.Live, true);
                    outcome.Zh += "\r\n" + measured + "\r\n请在显示设置中匹配虚拟屏分辨率和缩放，再重新准备。";
                    outcome.En += "\r\n" + measuredEn + "\r\nMatch the virtual display resolution and scale to the physical display in Windows Display Settings, then prepare again. Every attempt measures the current setup.";
                }
                if (outcome.Live == null && ex is AutoSetupUnsupportedException && ex.Message.IndexOf("DPI", StringComparison.OrdinalIgnoreCase) >= 0) {
                    outcome.Zh += " 虚拟屏缩放未通过实测匹配。请查看诊断或在 Windows 显示设置中核对后重新准备。";
                    outcome.En += " Virtual DPI matching was not verified. See diagnostics or check Windows Display Settings, then prepare again.";
                }
                // Native error details belong only in Diagnostics, not the main UI.
            } finally {
                if (heartbeat != null) heartbeat.Dispose();
                lock (guardLock) {
                    closed = true;
                    if (guardian != null) {
                        if (!applied || restored || outcome.Success) {
                            try { outcome.GuardianStopConfirmation = guardian.StopAsync(); }
                            catch (Exception ex) { outcome.GuardianStopConfirmation = Task.FromResult(false); outcome.Diagnostics += "\r\nguardian_stop_failed: " + ex; }
                        }
                        else { outcome.GuardianStopConfirmation = Task.FromResult(false); guardian.Relinquish(); } // Do not disarm an unverified recovery.
                    }
                }
                outcome.Diagnostics += "\r\n" + (plan == null ? "preflight_incomplete" : plan.Summary) + "\r\nrecovery=" + recoveryPath;
                if (outcome.Live != null) outcome.Diagnostics += "\r\nextended_readback=" + MeasurementSummary(outcome.Live, true) + "\r\n" + outcome.Live.Diagnostics;
                if (outcome.Final != null) outcome.Diagnostics += "\r\nfinal_readback=" + outcome.Final.Diagnostics;
                outcome.RecoveryPath = recoveryPath;
                outcome.SafeToArchive = !applied || restored || outcome.Success;
                // Integration cleanup belongs here or in the caller: archive
                // only safe outcomes AND confirmed guardian exit. Never delete
                // evidence for a relinquished or unconfirmed guardian.
                if (recoveryPath != null) {
                    try { platform.Record(outcome.Success ? null : recoveryPath, outcome.Diagnostics); }
                    catch (Exception ex) { outcome.Diagnostics += "\r\nevidence_record_failed: " + ex; }
                }
                var completion = platform as IAutoSetupRecoveryCompletion;
                if (completion != null) {
                    try { outcome.ArchiveConfirmation = completion.CompleteRecoveryAsync(outcome.GuardianStopConfirmation, outcome.SafeToArchive); }
                    catch (Exception ex) { outcome.Diagnostics += "\r\nsnapshot_completion_failed: " + ex; }
                }
            }
            return outcome;
        }
        private static void RequireGuardian(IAutoSetupGuardian guardian)
        { if (!guardian.IsAlive) throw new InvalidOperationException("恢复保护已退出 / Recovery guardian exited."); }
        private static DisplaySetupResult ReadProof(IAutoSetupPlatform platform)
        {
            try { return platform.Probe(); }
            catch (Exception ex) { throw new AutoSetupUnsupportedException("此配置无法自动取得可靠显示读数 / Reliable automatic display measurement is unsupported for this setup.", ex); }
        }
        // SetDisplayConfig returning is not a guarantee that monitor/DPI
        // enumeration has settled. Re-read only; never re-APPLY, infer DPI or
        // extend the outer 30s cancellation/recovery contract. Fake time in tests.
        internal static DisplaySetupResult ReadSettledProof(Func<DisplaySetupResult> probe, Func<DisplaySetupResult,bool> accepts,
            Action check, Func<long> milliseconds, Action<int> wait, Action<DisplaySetupResult> observed = null)
        {
            long started = milliseconds(); DisplaySetupResult last = null; Exception failure = null;
            string acceptedProfile = null;
            for (int n = 0; n < 21; n++) {
                check();
                if (milliseconds() - started >= 3000) break;
                try {
                    var result = probe();
                    // Keep actual measurements even when cancellation, a slow
                    // native call or a later enumeration error rejects the proof.
                    if (result != null) { last = result; if (observed != null) observed(result); }
                    check(); failure = null;
                    if (milliseconds() - started > 3000) break;
                    if (result != null && accepts(result)) {
                        if (acceptedProfile == result.Profile) return result;
                        acceptedProfile = result.Profile;
                    } else acceptedProfile = null;
                } catch (AutoSetupUnsupportedException ex) { failure = ex; acceptedProfile = null; }
                if (milliseconds() - started >= 3000 || n == 20) break;
                wait((int)Math.Max(0,Math.Min(150,3000 - (milliseconds() - started))));
            }
            check();
            if (failure != null) throw failure;
            if (last == null) throw new AutoSetupUnsupportedException("稳定校验未取得可用显示读数，请重试 / No usable display measurements arrived within the settling budget; retry.");
            if (last != null && accepts(last)) throw new AutoSetupUnsupportedException("显示读数尚未稳定，请重试 / Display measurements did not settle; retry.");
            return last;
        }
        internal static string MeasurementSummary(DisplaySetupResult result, bool english)
        {
            if (result == null || result.PhysicalDpi == 0 || result.VirtualDpi == 0)
                return english ? "Independent virtual display measurements are not available yet." : "尚未取得虚拟屏的可靠独立读数。";
            return (english ? "Physical: " : "实体屏：") + result.PhysicalWidth + " × " + result.PhysicalHeight + " / " +
                (result.PhysicalDpi * 100.0 / 96).ToString("0.#",CultureInfo.InvariantCulture) + "%" +
                (english ? "; virtual: " : "；虚拟屏：") + result.VirtualWidth + " × " + result.VirtualHeight + " / " +
                (result.VirtualDpi * 100.0 / 96).ToString("0.#",CultureInfo.InvariantCulture) + "%";
        }
    }

    // Pure CCD assembly. Keep original mode arrays intact so physical source,
    // target timing and optional desktop-image indices retain their exact meaning.
    // Only the new virtual source index is allocated; its target/desktop halves
    // are INVALID for strict Windows mode selection (never ALLOW_CHANGES).
    internal static class DisplayAutoCcd
    {
        internal static AutoSetupPlan Build(DisplayBackend.Snapshot original, DisplayBackend.Plan virtualOnly)
        {
            DisplayBackend.ValidateSnapshot(original);
            if (virtualOnly == null || virtualOnly.Paths == null || virtualOnly.Paths.Length != 1 ||
                virtualOnly.Modes == null || virtualOnly.Modes.Length != 1 || virtualOnly.AllowModeChanges)
                throw new InvalidOperationException("虚拟计划不完整或放宽模式 / Invalid or flexible virtual plan.");
            int real = -1;
            for (int i = 0; i < original.Paths.Length; i++) {
                if (DisplayBackend.IsPhysical(original.Paths[i].Target.Technology, original.AdapterPaths[i])) real = i;
                else if (!SameRoute(original.Paths[i], virtualOnly.Paths[0]) ||
                    !SameText(original.DevicePaths[i], virtualOnly.DevicePath) || !SameText(original.AdapterPaths[i], virtualOnly.AdapterPath))
                    throw new InvalidOperationException("原布局含其他输出 / Original layout contains another output.");
            }
            if (real < 0) throw new InvalidOperationException("实体屏未启用 / Physical display is not active.");
            var physical = Single(original, real);
            var physicalPath = physical.Paths[0];
            var virtualPath = virtualOnly.Paths[0];
            if ((physicalPath.Flags & 1) == 0 || SameTarget(physicalPath, virtualPath) ||
                (SameLuid(physicalPath.Source.Adapter, virtualPath.Source.Adapter) && physicalPath.Source.Id == virtualPath.Source.Id))
                throw new InvalidOperationException("无法构造独立显示源 / Independent display sources unavailable.");
            var source = Source(physical, physicalPath);
            long right = (long)source.X + source.Width;
            if (right > Int32.MaxValue || right < Int32.MinValue || original.Modes.Length >= DisplayBackend.MaxModes)
                throw new InvalidOperationException("显示坐标或模式数量超限 / Display coordinates or mode count exceed limits.");
            var virtualSource = source; virtualSource.X = (int)right;
            var modes = new DisplayBackend.ModeInfo[original.Modes.Length + 1];
            Array.Copy(original.Modes, modes, original.Modes.Length);
            uint index = (uint)original.Modes.Length;
            virtualPath.Flags = (virtualPath.Flags & DisplayBackend.VirtualMode) | DisplayBackend.Active;
            virtualPath.Source.ModeIndex = (virtualPath.Flags & DisplayBackend.VirtualMode) != 0 ? (index << 16) | 0xffffU : index;
            virtualPath.Source.Status = 0;
            virtualPath.Target.ModeIndex = DisplayBackend.Invalid;
            modes[index] = new DisplayBackend.ModeInfo { InfoType = 1, Adapter = virtualPath.Source.Adapter,
                Id = virtualPath.Source.Id, Source = virtualSource };
            var extended = new DisplayBackend.Snapshot {
                Paths = new DisplayBackend.PathInfo[] { physicalPath, virtualPath }, Modes = modes,
                DevicePaths = new string[] { physical.DevicePaths[0], virtualOnly.DevicePath },
                AdapterPaths = new string[] { physical.AdapterPaths[0], virtualOnly.AdapterPath } };
            return new AutoSetupPlan { Original = original, PhysicalOnly = physical, Extended = extended, VirtualOnly = virtualOnly,
                Summary = "physical=" + source.Width + "x" + source.Height + "@(" + source.X + "," + source.Y + "); rotation=" + physicalPath.Target.Rotation +
                    "; refresh=" + physicalPath.Target.Refresh.Numerator + "/" + physicalPath.Target.Refresh.Denominator +
                    "; virtual_right=(" + virtualSource.X + "," + virtualSource.Y + "); physical_active_in_all_APPLYs=true; flags=USE_SUPPLIED|VIRTUAL_AWARE, no_ALLOW_CHANGES, no_database_write" };
        }
        internal static DisplayBackend.Snapshot Single(DisplayBackend.Snapshot value, int index)
        {
            return new DisplayBackend.Snapshot { Paths = new DisplayBackend.PathInfo[] { value.Paths[index] },
                Modes = (DisplayBackend.ModeInfo[])value.Modes.Clone(), DevicePaths = new string[] { value.DevicePaths[index] },
                AdapterPaths = new string[] { value.AdapterPaths[index] } };
        }
        internal static DisplayBackend.SourceMode Source(DisplayBackend.Snapshot value, DisplayBackend.PathInfo path)
        {
            uint index = DisplayBackend.SourceIndex(path);
            if (index >= value.Modes.Length || value.Modes[index].InfoType != 1 || value.Modes[index].Id != path.Source.Id ||
                !SameLuid(value.Modes[index].Adapter, path.Source.Adapter)) throw new InvalidOperationException("显示源索引错误 / Invalid source mode index.");
            return value.Modes[index].Source;
        }
        internal static void VerifyExtended(AutoSetupPlan plan, DisplayBackend.Snapshot actual)
        {
            DisplayBackend.ValidateSnapshot(actual);
            if (actual.Paths.Length != 2) throw new InvalidOperationException("未回读到独立扩展 / Independent extended layout not verified.");
            int real = -1, virtualIndex = -1;
            for (int i = 0; i < actual.Paths.Length; i++) {
                if (SameRoute(actual.Paths[i], plan.PhysicalOnly.Paths[0]) && SameText(actual.DevicePaths[i], plan.PhysicalOnly.DevicePaths[0]) &&
                    SameText(actual.AdapterPaths[i], plan.PhysicalOnly.AdapterPaths[0])) real = i;
                if (SameRoute(actual.Paths[i], plan.Extended.Paths[1]) && SameText(actual.DevicePaths[i], plan.VirtualOnly.DevicePath) &&
                    SameText(actual.AdapterPaths[i], plan.VirtualOnly.AdapterPath)) virtualIndex = i;
            }
            if (real < 0 || virtualIndex < 0 || real == virtualIndex) throw new InvalidOperationException("扩展路径身份改变 / Extended path identity changed.");
            DisplayBackend.VerifySnapshot(plan.PhysicalOnly, Single(actual, real));
            var wanted = Source(plan.Extended, plan.Extended.Paths[1]);
            var got = Source(actual, actual.Paths[virtualIndex]);
            if (wanted.Width != got.Width || wanted.Height != got.Height || wanted.X != got.X || wanted.Y != got.Y || wanted.PixelFormat != got.PixelFormat ||
                actual.Paths[virtualIndex].Target.Rotation != plan.Extended.Paths[1].Target.Rotation ||
                actual.Paths[virtualIndex].Target.Scaling != plan.Extended.Paths[1].Target.Scaling)
                throw new InvalidOperationException("虚拟屏未精确扩展至实体屏右侧 / Virtual display was not extended exactly to the physical display's right.");
        }
        internal static void CheckRecoveryScope(AutoSetupPlan plan, DisplayBackend.Snapshot current)
        {
            try { DisplayBackend.VerifySnapshot(plan.Original, current); return; } catch (InvalidOperationException) { }
            try { DisplayBackend.VerifySnapshot(plan.PhysicalOnly, current); return; } catch (InvalidOperationException) { }
            // Refuse to overwrite third-party outputs, physical mode changes, or
            // a virtual layout that cannot be attributed to this transaction.
            VerifyExtended(plan, current);
        }
        internal static bool SameText(string a, string b) { return String.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        internal static bool SameLuid(DisplayBackend.Luid a, DisplayBackend.Luid b) { return a.Low == b.Low && a.High == b.High; }
        internal static bool SameTarget(DisplayBackend.PathInfo a, DisplayBackend.PathInfo b)
        { return SameLuid(a.Target.Adapter, b.Target.Adapter) && a.Target.Id == b.Target.Id; }
        internal static bool SameRoute(DisplayBackend.PathInfo a, DisplayBackend.PathInfo b)
        { return SameTarget(a, b) && SameLuid(a.Source.Adapter, b.Source.Adapter) && a.Source.Id == b.Source.Id; }
    }

    // This wrapper owns only a newly created preparation guardian. On failed
    // recovery it deliberately leaves the existing watchdog armed, with heartbeat
    // disabled. That watchdog owns the last-resort recovery and parent termination.
    internal sealed class AutoSetupGuardian : IAutoSetupGuardian
    {
        private readonly Guardian guardian;
        internal AutoSetupGuardian(string path) { guardian = new Guardian(path, 45); }
        public bool IsAlive { get { return guardian.IsAlive; } }
        public void Arm() { guardian.Arm(); }
        public void Pulse() { guardian.Pulse(); }
        public void Stop() { try { guardian.Stop(); } catch (Exception ex) { OledPrototype.Program.Log("auto_guardian_stop_failed: " + ex); } }
        public Task<bool> StopAsync() { return guardian.StopAsync(); }
        public void Relinquish() { guardian.DisableHeartbeat(); }
    }

    internal sealed class AutoSetupNative : IAutoSetupPlatform, IAutoSetupRecoveryCompletion
    {
        private RuntimeMaintenance maintenance;
        private RuntimeMaintenance.OwnedSnapshot ownedSnapshot;
        public AutoSetupPlan PlanCheck()
        {
            var original = Capture();
            DisplayBackend.ValidateSnapshot(original);
            var all = Routes(Query(DisplayBackend.QueryAll));
            var physical = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var virtuals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var route in all) {
                if (!route.Device.Available) continue;
                if (DisplaySetupProbe.IsExactMtt(route.Device.AdapterPath)) virtuals.Add(route.Device.DevicePath);
                else if (DisplayBackend.IsPhysical(route.Device.Technology, route.Device.AdapterPath)) physical.Add(route.Device.DevicePath);
            }
            if (physical.Count != 1 || virtuals.Count != 1)
                throw new AutoSetupUnsupportedException("仅支持一个实体屏和唯一精确 Root\\MttVDD / One physical display and exactly one verified Root\\MttVDD required.");
            string device = null; foreach (string item in virtuals) device = item;
            // Reuse the existing selection and source-mode policy without invoking
            // TurnOff, Prepare's flexible validation, or any physical-off entry.
            var virtualOnly = DisplayBackend.BuildPlan(original, all, device);
            var plan = DisplayAutoCcd.Build(original, virtualOnly);
            foreach (var path in plan.Original.Paths)
                if (!DisplayAutoCcd.SameTarget(path, plan.PhysicalOnly.Paths[0]) && !DisplayAutoCcd.SameTarget(path, plan.Extended.Paths[1]))
                    throw new InvalidOperationException("存在其他活动输出 / Another active output exists.");
            string source = SourceName(plan.Extended.Paths[1]);
            var dimensions = DisplayAutoCcd.Source(plan.PhysicalOnly, plan.PhysicalOnly.Paths[0]);
            if (!ListsResolution(source, dimensions.Width, dimensions.Height))
                throw new AutoSetupUnsupportedException("此配置不支持自动准备：Windows 未列出精确虚拟分辨率 / Unsupported: Windows does not list the exact virtual resolution.");
            Validate(plan.Original); Validate(plan.Extended); Validate(plan.PhysicalOnly);
            ValidateCompatibilityPlan(virtualOnly);
            DisplayBackend.VerifySnapshot(original, Capture());
            plan.Summary += "; listed_virtual_source=" + source + "; strict_original_extend_physical_virtual_validate=passed; APPLY_not_run_by_preflight";
            return plan;
        }
        public DisplayBackend.Snapshot Capture()
        {
            var snapshot = Query(DisplayBackend.QueryActive);
            var routes = Routes(snapshot);
            snapshot.DevicePaths = new string[routes.Count]; snapshot.AdapterPaths = new string[routes.Count];
            for (int i = 0; i < routes.Count; i++) { snapshot.DevicePaths[i] = routes[i].Device.DevicePath; snapshot.AdapterPaths[i] = routes[i].Device.AdapterPath; }
            return snapshot;
        }
        public void Validate(DisplayBackend.Snapshot snapshot)
        { StrictSet(snapshot.Paths, snapshot.Modes, false); }
        public void Apply(DisplayBackend.Snapshot expected, DisplayBackend.Snapshot desired, Action beforeApply)
        {
            // Verify before AND after VALIDATE to avoid overwriting an intervening
            // unrelated change. CCD has no compare-and-swap; this is the final
            // immediately-adjacent readback before each explicit APPLY.
            DisplayBackend.VerifySnapshot(expected, Capture());
            RequirePhysical(desired);
            DisplayBackend.VerifySnapshot(Physical(expected), Physical(desired));
            Validate(desired);
            DisplayBackend.VerifySnapshot(expected, Capture());
            beforeApply();
            StrictSet(desired.Paths, desired.Modes, true);
        }
        private static DisplayBackend.Snapshot Physical(DisplayBackend.Snapshot snapshot)
        {
            for (int i = 0; i < snapshot.Paths.Length; i++)
                if (DisplayBackend.IsPhysical(snapshot.Paths[i].Target.Technology, snapshot.AdapterPaths[i])) return DisplayAutoCcd.Single(snapshot, i);
            throw new InvalidOperationException("实体屏必须保持启用 / Physical display must remain active.");
        }
        private static void RequirePhysical(DisplayBackend.Snapshot snapshot)
        {
            if (snapshot.Paths.Length < 1 || snapshot.Paths.Length > 2) throw new InvalidOperationException("活动输出数量超限 / Invalid active output count.");
            var real = Physical(snapshot);
            DisplayBackend.ValidateSnapshot(real);
            foreach (var path in snapshot.Paths) if ((path.Flags & DisplayBackend.Active) == 0) throw new InvalidOperationException("Inactive APPLY path refused.");
        }
        public DisplaySetupResult Probe() { return DisplaySetupProbe.Probe(true); }
        internal static string VirtualSource(string device)
        {
            var native = new AutoSetupNative(); var baseline = native.Capture();
            var routes = Routes(Query(DisplayBackend.QueryAll));
            string source = SelectedVirtualSource(baseline, routes, device, SourceName);
            DisplayBackend.VerifySnapshot(baseline, native.Capture());
            return source;
        }
        internal static string SelectedVirtualSource(DisplayBackend.Snapshot baseline, List<DisplayBackend.Route> routes, string device, Func<DisplayBackend.PathInfo, string> sourceName)
        {
            // Check target identity across ALL available candidates before source
            // filtering, so a shared/proxy route cannot hide another target.
            DisplayBackend.Route target = null;
            var eligible = new List<DisplayBackend.Route>();
            foreach (var route in routes) {
                if (!route.Device.Available || !DisplayAutoCcd.SameText(route.Device.DevicePath, device)) continue;
                if (target != null && !DisplayAutoCcd.SameTarget(target.Path, route.Path))
                    throw new InvalidOperationException("DevicePath 对应多个不同 target，拒绝歧义选择 / DevicePath maps to different targets; ambiguous selection refused.");
                target = route;
                var path = route.Path;
                if (path.Target.Available == 0 || path.Target.Technology != route.Device.Technology)
                    throw new InvalidOperationException("虚拟路径身份与可用性不一致 / Virtual route identity or availability is inconsistent.");
                if (!DisplayAutoCcd.SameLuid(path.Source.Adapter, path.Target.Adapter)) continue;
                bool shared = false;
                for (int i = 0; i < baseline.Paths.Length; i++)
                    if (DisplayBackend.IsPhysical(baseline.Paths[i].Target.Technology, baseline.AdapterPaths[i]) &&
                        DisplayAutoCcd.SameLuid(baseline.Paths[i].Source.Adapter, path.Source.Adapter) && baseline.Paths[i].Source.Id == path.Source.Id) shared = true;
                if (!shared) eligible.Add(route);
            }
            if (target == null) throw new VirtualModeNotReadyException("虚拟显示路由尚未就绪 / Virtual display route is not ready.");
            if (eligible.Count == 0) throw new InvalidOperationException("无法核实独立虚拟显示源 / Cannot verify an independent virtual source.");
            // Keep BuildPlan's active/OS-priority policy within independent sources
            // only. Enumerate one selected source; never the clone's physical GDI.
            var plan = DisplayBackend.BuildPlan(baseline, eligible, device);
            return sourceName(plan.Paths[0]);
        }
        internal static void ValidateCompatibility(string device)
        {
            var native = new AutoSetupNative(); var baseline = native.Capture();
            var plan = DisplayBackend.BuildPlan(baseline, Routes(Query(DisplayBackend.QueryAll)), device);
            ValidateCompatibilityPlan(plan); DisplayBackend.VerifySnapshot(baseline, native.Capture());
        }
        private static void ValidateCompatibilityPlan(DisplayBackend.Plan plan)
        { StrictSet(plan.Paths, plan.Modes, false); }
        private static void StrictSet(DisplayBackend.PathInfo[] paths, DisplayBackend.ModeInfo[] modes, bool apply)
        {
            var p = (DisplayBackend.PathInfo[])paths.Clone(); var m = (DisplayBackend.ModeInfo[])modes.Clone();
            uint flags = DisplayBackend.BaseSetFlags | (apply ? DisplayBackend.ApplyFlag : DisplayBackend.ValidateFlag);
            int error = SetDisplayConfig((uint)p.Length, p, (uint)m.Length, m, flags);
            if (error != 0) {
                if (!apply) throw new AutoSetupUnsupportedException("此配置不受支持，严格模式校验失败 / Unsupported exact display mode, strict VALIDATE failed: " + error);
                throw new InvalidOperationException("严格显示切换失败 / Strict display APPLY failed: " + error);
            }
        }
        public string SaveRecovery(DisplayBackend.Snapshot original)
        {
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ScreenOffTray", "首次显示准备");
            if (ownedSnapshot != null) throw new InvalidOperationException("Snapshot already owned by this preparation platform.");
            maintenance = new RuntimeMaintenance(directory, OledPrototype.Program.Log);
            ownedSnapshot = maintenance.CreateSnapshot(delegate(string destination) {
                using (var stream = new FileStream(destination, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.WriteThrough)) {
                    DisplayBackend.WriteSnapshot(stream, original); stream.Flush(true);
                    stream.Position = 0; DisplayBackend.VerifySnapshot(original, DisplayBackend.ReadSnapshot(stream));
                }
            });
            return ownedSnapshot.Path;
        }
        public Task<bool> CompleteRecoveryAsync(Task<bool> guardianStopped, bool safeToArchive)
        { return maintenance == null ? Task.FromResult(false) : maintenance.ArchiveCompletedAsync(ownedSnapshot, guardianStopped, safeToArchive); }
        public IAutoSetupGuardian StartGuardian(string path) { return new AutoSetupGuardian(path); }
        public void Record(string path, string detail)
        {
            if (path == null) { OledPrototype.Program.Log("auto_setup_success: " + detail); return; }
            try { File.WriteAllText(path + ".准备诊断.txt", detail, System.Text.Encoding.UTF8); }
            catch (Exception ex) { OledPrototype.Program.Log("auto_setup_evidence_write_failed: " + ex); }
        }
        private static DisplayBackend.Snapshot Query(uint kind)
        {
            DisplayBackend.CheckLayout();
            for (int i = 0; i < 3; i++) {
                uint pc, mc; uint flags = kind | DisplayBackend.QueryVirtualAware;
                int error = GetDisplayConfigBufferSizes(flags, out pc, out mc);
                if (error == 122) continue;
                if (error != 0) throw VirtualModeNotReadyException.CcdError("GetDisplayConfigBufferSizes", error);
                if (pc > DisplayBackend.MaxPaths || mc > DisplayBackend.MaxModes) throw new InvalidOperationException("CCD buffer limit exceeded.");
                var paths = new DisplayBackend.PathInfo[pc]; var modes = new DisplayBackend.ModeInfo[mc];
                uint pmax = pc, mmax = mc;
                error = QueryDisplayConfig(flags, ref pc, paths, ref mc, modes, IntPtr.Zero);
                if (error == 122) continue;
                if (error != 0) throw VirtualModeNotReadyException.CcdError("QueryDisplayConfig", error);
                if (pc > pmax || mc > mmax) throw new InvalidOperationException("QueryDisplayConfig buffer size exceeded.");
                Array.Resize(ref paths, (int)pc); Array.Resize(ref modes, (int)mc);
                return new DisplayBackend.Snapshot { Paths = paths, Modes = modes };
            }
            throw new VirtualModeNotReadyException("CCD 枚举缓冲区尚未稳定 / CCD enumeration buffers are not ready.");
        }
        private static List<DisplayBackend.Route> Routes(DisplayBackend.Snapshot snapshot)
        {
            var result = new List<DisplayBackend.Route>();
            foreach (var path in snapshot.Paths) {
                var target = new DisplayBackend.TargetName { Header = Header(2, typeof(DisplayBackend.TargetName), path.Target.Adapter, path.Target.Id) };
                var adapter = new DisplayBackend.AdapterName { Header = Header(4, typeof(DisplayBackend.AdapterName), path.Target.Adapter, 0) };
                int te = GetTargetName(ref target), ae = GetAdapterName(ref adapter);
                if ((path.Target.Available != 0 || (path.Flags & 1) != 0) && (te != 0 || ae != 0 || String.IsNullOrWhiteSpace(target.DevicePath) || String.IsNullOrWhiteSpace(adapter.DevicePath)))
                    throw VirtualModeNotReadyException.CcdError("Cannot verify display identity", te, ae);
                result.Add(new DisplayBackend.Route { Path = path, Device = new DisplayTarget {
                    DevicePath = target.DevicePath, AdapterPath = adapter.DevicePath, Name = target.FriendlyName,
                    Technology = path.Target.Technology, Active = (path.Flags & 1) != 0, Available = path.Target.Available != 0 } });
            }
            return result;
        }
        private static string SourceName(DisplayBackend.PathInfo path)
        {
            var value = new DisplayBackend.SourceName { Header = Header(1, typeof(DisplayBackend.SourceName), path.Source.Adapter, path.Source.Id) };
            int error = GetSourceName(ref value);
            if (error != 0) throw VirtualModeNotReadyException.CcdError("Source identity unavailable", error);
            if (String.IsNullOrWhiteSpace(value.GdiName)) throw new VirtualModeNotReadyException("显示源名称尚未就绪 / Source name is not ready.");
            return value.GdiName;
        }
        private static bool ListsResolution(string source, uint width, uint height)
        {
            for (int i = 0; i < 1024; i++) {
                var mode = new AutoDevMode { Size = (ushort)Marshal.SizeOf(typeof(AutoDevMode)) };
                if (!EnumDisplaySettingsEx(source, i, ref mode, 0)) return false;
                if (mode.Width == width && mode.Height == height) return true;
            }
            return false;
        }
        private static DisplayBackend.DeviceHeader Header(uint type, Type structure, DisplayBackend.Luid adapter, uint id)
        { return new DisplayBackend.DeviceHeader { Type = type, Size = (uint)Marshal.SizeOf(structure), Adapter = adapter, Id = id }; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct AutoDevMode
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string DeviceName;
            internal ushort SpecVersion, DriverVersion, Size, DriverExtra;
            internal uint Fields; internal int PositionX, PositionY; internal uint Orientation, FixedOutput;
            internal short Color, Duplex, YResolution, TTOption, Collate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string FormName;
            internal ushort LogPixels;
            internal uint BitsPerPixel, Width, Height, Flags, Frequency, ICMMethod, ICMIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
        }
        [DllImport("user32.dll", ExactSpelling = true)] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
        [DllImport("user32.dll", ExactSpelling = true)] private static extern int QueryDisplayConfig(uint flags, ref uint pc, [Out] DisplayBackend.PathInfo[] paths, ref uint mc, [Out] DisplayBackend.ModeInfo[] modes, IntPtr topology);
        [DllImport("user32.dll", ExactSpelling = true)] private static extern int SetDisplayConfig(uint pc, [In] DisplayBackend.PathInfo[] paths, uint mc, [In] DisplayBackend.ModeInfo[] modes, uint flags);
        [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo", ExactSpelling = true, CharSet = CharSet.Unicode)] private static extern int GetTargetName(ref DisplayBackend.TargetName value);
        [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo", ExactSpelling = true, CharSet = CharSet.Unicode)] private static extern int GetAdapterName(ref DisplayBackend.AdapterName value);
        [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo", ExactSpelling = true, CharSet = CharSet.Unicode)] private static extern int GetSourceName(ref DisplayBackend.SourceName value);
        [DllImport("user32.dll", EntryPoint = "EnumDisplaySettingsExW", ExactSpelling = true, CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettingsEx(string source, int index, ref AutoDevMode mode, uint flags);
    }

    internal static class DisplayReadinessPolicy
    {
        internal static bool CanComplete(DisplaySetupResult result, string observed, DateTime measured, DateTime now)
        {
            return result != null && result.PhysicalOnly && result.Compatible &&
                !String.IsNullOrEmpty(result.Profile) && result.Profile == observed &&
                measured != DateTime.MinValue && measured <= now;
        }
    }

    internal static class DisplaySetupProbe
    {
        internal static VirtualModeRequirement ReadModeRequirement(VirtualModeRequirement expected)
        {
            IntPtr prior = SetThreadDpiAwarenessContext(new IntPtr(-4));
            if (prior == IntPtr.Zero) throw new InvalidOperationException("DPI-aware probing unavailable.");
            try {
                var outputs = ReadOutputs();
                var physical = outputs.FindAll(delegate(Output o) { return o.Physical; });
                var virtuals = outputs.FindAll(delegate(Output o) { return o.Mtt; });
                if (physical.Count > 1 || virtuals.Count > 1)
                    throw new InvalidOperationException("需要单个已启用实体屏和唯一虚拟屏 / One active physical display and one virtual display required.");
                if (physical.Count == 0) throw new VirtualModeNotReadyException("实体显示路径尚未就绪 / Physical display path is not ready.");
                var real = physical[0];
                if (expected != null && !expected.PhysicalKey.StartsWith(real.Device + "|" + real.Adapter + "|", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("实体显示设备已变化 / Physical display identity changed.");
                if (!real.Active) throw new VirtualModeNotReadyException("实体显示源尚未启用 / Physical display source is not active yet.");
                ReadActiveMode(real); VirtualModeConfig.Dimensions(real.Width, real.Height);
                string physicalKey = real.Device + "|" + real.Adapter + "|" + real.Dpi;
                if (expected != null && (expected.Width != real.Width || expected.Height != real.Height || expected.Rotation != real.Path.Target.Rotation || !String.Equals(expected.PhysicalKey, physicalKey, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("实体屏尺寸、方向或缩放已变化 / Physical dimensions, rotation or DPI changed.");
                if (virtuals.Count == 0) throw new VirtualModeNotReadyException("虚拟显示路径尚未就绪 / Virtual display path is not ready.");
                var vdd = virtuals[0];
                if (expected != null && (!String.Equals(expected.VirtualDevice, vdd.Device, StringComparison.OrdinalIgnoreCase) || !String.Equals(expected.Instance, DisplayBackend.RootInstancePath(vdd.Adapter), StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("虚拟设备身份已变化 / Virtual display identity changed.");
                foreach (var other in outputs) if (other.Active && other != real && other != vdd)
                    throw new InvalidOperationException("存在其他活动输出 / Another active output exists.");
                if (real.Path.Target.Rotation < 1 || real.Path.Target.Rotation > 4) throw new InvalidOperationException("Unknown physical rotation.");
                // CCD source dimensions already express portrait orientation.
                // BuildPlan selects the independent virtual source even in clone.
                string source = AutoSetupNative.VirtualSource(vdd.Device);
                var modes = ListedResolutions(source);
                if (modes.Count == 0) throw new VirtualModeNotReadyException("虚拟模式列表尚未就绪 / Virtual mode enumeration is not ready.");
                if (Topology(outputs) != Topology(ReadOutputs())) throw new InvalidOperationException("Display changed while measuring.");
                return new VirtualModeRequirement { Width = real.Width, Height = real.Height, Rotation = real.Path.Target.Rotation,
                    PhysicalKey = physicalKey, VirtualDevice = vdd.Device,
                    Instance = DisplayBackend.RootInstancePath(vdd.Adapter), Listed = modes.Contains(real.Width + "x" + real.Height) };
            } finally { SetThreadDpiAwarenessContext(prior); }
        }
        private sealed class Output
        {
            internal DisplayBackend.PathInfo Path;
            internal string Device, Adapter, Source;
            internal bool Mtt, Physical, Active;
            internal uint Width, Height, Dpi;
            internal int ScaleFactorPercent, ScaleFactorError;
        }
        internal static string[] InspectActiveDpi()
        {
            IntPtr prior = SetThreadDpiAwarenessContext(new IntPtr(-4));
            if (prior == IntPtr.Zero) throw new InvalidOperationException("Cannot establish PMv2 probe context.");
            try {
                var lines = new List<string>();
                foreach (Output output in ReadOutputs()) if (output.Active) {
                    lines.Add("active_source=" + output.Source + "; listed_resolutions=" + String.Join(",", ListedResolutions(output.Source)));
                    try { ReadActiveMode(output); }
                    catch (Exception ex) { lines.Add("dpi_unverified=" + ex.Message); continue; }
                    lines.Add("kind=" + (output.Mtt ? "MttVDD" : output.Physical ? "physical" : "other") + "; source=" + output.Source +
                        "; resolution=" + output.Width + "x" + output.Height + "; GetDpiForWindow=" + output.Dpi +
                        "; scale_percent=" + (output.Dpi * 100.0 / 96).ToString("0.#", CultureInfo.InvariantCulture) +
                        "; GetScaleFactorForMonitor_raw=" + output.ScaleFactorPercent + "; scale_hr=" + output.ScaleFactorError +
                        "; awareness=2; own_hidden_window_monitor=matched; authoritative_api=GetDpiForWindow");
                }
                if (lines.Count == 0) throw new InvalidOperationException("No active display could be read.");
                return lines.ToArray();
            } finally { SetThreadDpiAwarenessContext(prior); }
        }
        internal static DisplaySetupResult Probe()
        { return Probe(false); }
        internal static DisplaySetupResult Probe(bool strictCompatibility)
        {
            // Establish awareness only on this worker thread and restore it in finally.
            // No process DPI change, no registry writes, no APPLY and no recovery file.
            IntPtr prior = SetThreadDpiAwarenessContext(new IntPtr(-4));
            if (prior == IntPtr.Zero) throw new InvalidOperationException("无法建立 DPI-aware 探测线程 / DPI-aware display probing is unavailable.");
            try {
                var outputs = ReadOutputs();
                var virtuals = outputs.FindAll(delegate(Output o) { return o.Mtt; });
                var physical = outputs.FindAll(delegate(Output o) { return o.Physical; });
                if (virtuals.Count != 1) throw new InvalidOperationException("需要唯一可用的 Root\\MttVDD / Exactly one available Root\\MttVDD is required.");
                if (physical.Count != 1 || !physical[0].Active)
                    throw new InvalidOperationException("仅支持一个已启用的实体屏；多屏或实体屏未启用时拒绝 / One active physical display only; multiple displays are refused.");
                Output real = physical[0], vdd = virtuals[0];
                foreach (var output in outputs)
                    if (output.Active && !ReferenceEquals(output, real) && !ReferenceEquals(output, vdd))
                        throw new InvalidOperationException("存在其他活动显示输出，配置冲突 / Another active display output conflicts with this setup.");
                ReadActiveMode(real);
                string profile = real.Device + "|" + real.Adapter + "|" + real.Width + "x" + real.Height + "|" + real.Dpi + "|" + real.Path.Target.Rotation +
                    "|" + vdd.Device + "|" + vdd.Adapter + "|" + DriverVersion(vdd.Adapter);
                var result = new DisplaySetupResult { Profile = profile, PhysicalOnly = !vdd.Active,
                    PhysicalWidth=real.Width, PhysicalHeight=real.Height, PhysicalDpi=real.Dpi };
                string dimensions = real.Width + " × " + real.Height + " / " + (real.Dpi * 100.0 / 96).ToString("0.#", CultureInfo.InvariantCulture) + "%";
                if (vdd.Active) {
                    string metricsFailure = null;
                    try { ReadActiveMode(vdd); }
                    catch (InvalidOperationException ex) { metricsFailure = ex.Message; }
                    result.VirtualWidth=vdd.Width; result.VirtualHeight=vdd.Height; result.VirtualDpi=vdd.Dpi;
                    // Clone mode shares a source; it does not prove an independently set VDD scale.
                    bool independent = real.Source != vdd.Source;
                    result.MatchingLive = independent && real.Width == vdd.Width && real.Height == vdd.Height && real.Dpi == vdd.Dpi;
                    var listed = result.MatchingLive ? new List<string>() : ListedResolutions(vdd.Source);
                    bool unsupported = listed.Count > 0 && !listed.Contains(real.Width + "x" + real.Height);
                    bool cloned = real.Source == vdd.Source || (metricsFailure != null && IsCurrentClone(outputs));
                    DisplaySetupResult guidance = result.MatchingLive ? null : CreateVirtualGuidance(real.Width, real.Height,
                        listed.ToArray(), cloned, metricsFailure != null && !cloned);
                    if (guidance != null) {
                        result.VirtualModeMissing = guidance.VirtualModeMissing;
                        result.ClonedDisplay = guidance.ClonedDisplay;
                        result.MetricsUnavailable = guidance.MetricsUnavailable;
                        result.RequiredResolution = guidance.RequiredResolution;
                        result.Zh = guidance.Zh; result.En = guidance.En;
                        result.Diagnostics = guidance.Diagnostics + (metricsFailure == null ? "" : "\r\n" + metricsFailure);
                    } else {
                    result.Zh = "实体屏：" + dimensions + "\r\n虚拟屏：" + vdd.Width + " × " + vdd.Height + " / " + (vdd.Dpi * 100.0 / 96).ToString("0.#") + "%\r\n" +
                        (result.MatchingLive ? "当前实测一致。请切回“仅在实体屏显示”，再重新检查。" : unsupported ?
                            "此配置不受支持：Windows 未列出与实体屏相同的虚拟屏分辨率。请关闭引导并保持关屏停用；不会修改现有驱动或降低实体屏分辨率。" :
                            "当前读数不一致或仍为复制模式。仅在 Windows 提供相同模式时匹配；若无对应选项，此配置不受支持，请关闭引导并保持暂停。");
                    result.En = "Physical: " + dimensions + "\r\nVirtual: " + vdd.Width + " × " + vdd.Height + " / " + (vdd.Dpi * 100.0 / 96).ToString("0.#") + "%\r\n" +
                        (result.MatchingLive ? "Live readings match. Switch back to physical display only, then recheck." : unsupported ?
                            "Unsupported profile: Windows does not list a matching virtual resolution. Close setup and leave display off disabled. Existing driver settings and physical resolution will not be changed." :
                            "Readings differ or displays are cloned. Match only if Windows offers the same mode. If no matching option exists, this profile is unsupported; close setup and leave display off paused.");
                    }
                } else {
                    // Prepare builds and VALIDATEs a candidate only. No TurnOff, Restore,
                    // Recover, SaveRecovery or driver configuration change is permitted here.
                    if (strictCompatibility) {
                        AutoSetupNative.ValidateCompatibility(vdd.Device); result.Compatible = true;
                    } else {
                        var backend = new DisplayBackend(); backend.Prepare(vdd.Device);
                        result.Compatible = !backend.ValidationAllowsChanges;
                    }
                    result.Zh = "实体屏：" + dimensions + "\r\n当前仅实体屏启用。虚拟屏未启用，无法读取其实时缩放；需要此配置的实测匹配记录。\r\n" +
                        (result.Compatible ? "严格只读兼容校验通过。" : "仅允许模式变化的校验通过，未证实精确匹配；关屏保持暂停。");
                    result.En = "Physical: " + dimensions + "\r\nPhysical display only. The inactive virtual display's live scale cannot be read; a saved measured match for this profile is required.\r\n" +
                        (result.Compatible ? "Strict read-only compatibility validation passed." : "Validation requires mode changes; exact compatibility is unverified and display off stays paused.");
                }
                // A concurrent Settings change must not publish a proof for an earlier topology.
                var final = ReadOutputs();
                if (Topology(outputs) != Topology(final)) throw new InvalidOperationException("探测期间显示配置变化，请重新检查 / Display setup changed during probing; recheck.");
                result.Diagnostics += "\r\n" + DisplayAutoSetup.MeasurementSummary(result,true) + "; physical_source=" + real.Source + "; virtual_source=" + vdd.Source;
                return result;
            } finally { SetThreadDpiAwarenessContext(prior); }
        }
        // Pure classification shared with isolated checks. A listed-mode gap is
        // not permission to rewrite a reused driver's configuration or infer DPI.
        internal static DisplaySetupResult CreateVirtualGuidance(uint width, uint height, string[] modes, bool cloned, bool metricsUnavailable)
        {
            string required = width.ToString(CultureInfo.InvariantCulture) + "x" + height.ToString(CultureInfo.InvariantCulture);
            bool missing = modes != null && modes.Length > 0 && Array.IndexOf(modes, required) < 0;
            if (!missing && !cloned && !metricsUnavailable) return null;
            var result = new DisplaySetupResult { VirtualModeMissing = missing, ClonedDisplay = cloned,
                MetricsUnavailable = metricsUnavailable, RequiredResolution = required };
            string size = width.ToString(CultureInfo.InvariantCulture) + " × " + height.ToString(CultureInfo.InvariantCulture);
            if (missing) {
                result.Zh = "虚拟屏尚未提供 " + size + " 模式，暂时无法完成准备。\r\n请先匹配虚拟屏分辨率，再重新检查。关屏保持停用。";
                result.En = "The virtual display does not offer " + size + ". Setup cannot finish yet.\r\nMatch its resolution, then check again. Display off remains disabled.";
            } else if (metricsUnavailable) {
                result.Zh = "暂时无法可靠读取虚拟屏的缩放。\r\n请检查显示连接后重试；确认完成前，关屏保持停用。";
                result.En = "The virtual display's scale could not be verified.\r\nCheck the display connection and try again. Display off remains disabled.";
            } else {
                result.Zh = "当前实体屏与虚拟屏正在复制显示，尚未验证独立缩放。\r\n可继续一键准备：临时扩展虚拟屏完成匹配，再恢复为仅实体屏显示；期间画面可能闪动。";
                result.En = "The physical and virtual displays are mirrored; independent scaling is not verified.\r\nSetup can temporarily extend the virtual display, match it, then return to physical display only. The screen may flicker.";
            }
            result.Diagnostics = "virtual_mode_missing=" + missing + "; cloned=" + cloned + "; metrics_unavailable=" + metricsUnavailable +
                "; required=" + required + "; listed=" + String.Join(",", modes ?? new string[0]);
            return result;
        }
        private static bool IsCurrentClone(List<Output> outputs)
        {
            // DATABASE_CURRENT supplies a topology id, not an APPLY. Match the
            // returned monitor identities against the live query. Cross-adapter
            // clones can use GPU proxy target/source ids in the database.
            IntPtr topology = Marshal.AllocHGlobal(sizeof(uint));
            try {
                for (int attempt = 0; attempt < 3; attempt++) {
                    uint pc, mc;
                    int error = GetDisplayConfigBufferSizes(4, out pc, out mc);
                    if (error != 0 || pc > 256 || mc > 1024) return false;
                    var paths = new DisplayBackend.PathInfo[pc]; var modes = new DisplayBackend.ModeInfo[mc];
                    error = QueryDisplayConfig(4, ref pc, paths, ref mc, modes, topology);
                    if (error == 122) continue;
                    if (error != 0 || pc > paths.Length || mc > modes.Length || Marshal.ReadInt32(topology) != 2) return false;
                    var active = new List<string>(); var saved = new List<string>();
                    foreach (var output in outputs) if (output.Active) active.Add(output.Device);
                    for (int i = 0; i < pc; i++) {
                        var name = new DisplayBackend.TargetName { Header = Header(2, typeof(DisplayBackend.TargetName), paths[i].Target.Adapter, paths[i].Target.Id) };
                        if (GetTargetName(ref name) != 0 || String.IsNullOrWhiteSpace(name.DevicePath)) return false;
                        saved.Add(name.DevicePath);
                    }
                    return SameCloneDisplays(active.ToArray(), saved.ToArray());
                }
                return false;
            } finally { Marshal.FreeHGlobal(topology); }
        }
        internal static bool SameCloneDisplays(string[] active, string[] saved)
        {
            if (active == null || saved == null || active.Length == 0 || active.Length != saved.Length) return false;
            var devices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string device in active) if (String.IsNullOrWhiteSpace(device) || !devices.Add(device)) return false;
            foreach (string device in saved) if (String.IsNullOrWhiteSpace(device) || !devices.Remove(device)) return false;
            return devices.Count == 0;
        }
        private static string Topology(List<Output> outputs)
        {
            var tokens = new List<string>();
            foreach (var o in outputs) tokens.Add(o.Device + "|" + o.Active + "|" + o.Width + "|" + o.Height + "|" + o.Path.Target.Rotation + "|" + o.Source);
            tokens.Sort(StringComparer.Ordinal); return String.Join(";", tokens);
        }
        private static List<Output> ReadOutputs()
        {
            DisplayBackend.CheckLayout();
            uint pc = 0, mc = 0; DisplayBackend.PathInfo[] paths = null; DisplayBackend.ModeInfo[] modes = null;
            int error = 122;
            for (int i = 0; i < 3 && error == 122; i++) {
                error = GetDisplayConfigBufferSizes(0x11, out pc, out mc);
                if (error != 0) continue;
                if (pc > 256 || mc > 1024) throw new InvalidOperationException("显示配置规模超限 / Display topology is too large.");
                paths = new DisplayBackend.PathInfo[pc]; modes = new DisplayBackend.ModeInfo[mc];
                error = QueryDisplayConfig(0x11, ref pc, paths, ref mc, modes, IntPtr.Zero);
            }
            if (error != 0) throw VirtualModeNotReadyException.CcdError("QueryDisplayConfig", error);
            if (paths == null) throw new InvalidOperationException("QueryDisplayConfig returned no buffer.");
            var result = new List<Output>();
            for (int i = 0; i < pc; i++) {
                var p = paths[i]; if (p.Target.Available == 0) continue;
                var name = new DisplayBackend.TargetName { Header = Header(2, typeof(DisplayBackend.TargetName), p.Target.Adapter, p.Target.Id) };
                var adapter = new DisplayBackend.AdapterName { Header = Header(4, typeof(DisplayBackend.AdapterName), p.Target.Adapter, 0) };
                int targetError = GetTargetName(ref name), adapterError = GetAdapterName(ref adapter);
                if (targetError != 0 || adapterError != 0) throw VirtualModeNotReadyException.CcdError("Cannot verify display identity", targetError, adapterError);
                bool active = (p.Flags & 1) != 0;
                Output existing = result.Find(delegate(Output o) { return o.Device == name.DevicePath; });
                if (existing != null && (!active || existing.Active)) continue;
                if (existing != null) result.Remove(existing);
                var output = new Output { Path = p, Device = name.DevicePath, Adapter = adapter.DevicePath, Active = active };
                output.Mtt = IsExactMtt(adapter.DevicePath);
                output.Physical = !output.Mtt && DisplayBackend.IsPhysical(p.Target.Technology, adapter.DevicePath);
                if (active) {
                    uint index = DisplayBackend.SourceIndex(p);
                    if (index >= mc || modes[index].InfoType != 1 || modes[index].Id != p.Source.Id ||
                        modes[index].Adapter.Low != p.Source.Adapter.Low || modes[index].Adapter.High != p.Source.Adapter.High)
                        throw new InvalidOperationException("显示源模式无法核实 / Source mode cannot be verified.");
                    output.Width = modes[index].Source.Width; output.Height = modes[index].Source.Height;
                    var source = new DisplayBackend.SourceName { Header = Header(1, typeof(DisplayBackend.SourceName), p.Source.Adapter, p.Source.Id) };
                    int sourceError = GetSourceName(ref source);
                    if (sourceError != 0) throw VirtualModeNotReadyException.CcdError("Source identity cannot be verified", sourceError);
                    output.Source = source.GdiName;
                }
                result.Add(output);
            }
            return result;
        }
        private static void ReadActiveMode(Output output)
        {
            uint dpi = 0;
            string failure = "No active monitor matched source " + output.Source;
            MonitorEnum callback = delegate(IntPtr monitor, IntPtr dc, ref Rect bounds, IntPtr data) {
                var info = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
                if (GetMonitorInfo(monitor, ref info) && String.Equals(info.Device, output.Source, StringComparison.OrdinalIgnoreCase)) {
                    int scale;
                    // GetDpiForMonitor must NOT be called on a PM-aware thread.
                    // A private, never-shown PMv2 window gives the monitor's actual
                    // effective DPI via the documented GetDpiForWindow contract.
                    IntPtr window = CreateWindowEx(0x08000080, "STATIC", "ScreenOffTray DPI probe", 0x80000000,
                        info.Monitor.Left + 8, info.Monitor.Top + 8, 1, 1, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                    if (window == IntPtr.Zero) { failure = "CreateWindowEx failed: " + Marshal.GetLastWin32Error(); return true; }
                    try {
                        uint x = GetDpiForWindow(window);
                        int awareness = GetAwarenessFromDpiAwarenessContext(GetWindowDpiAwarenessContext(window));
                        bool sameMonitor = MonitorFromWindow(window, 0) == monitor;
                        int scaleError = GetScaleFactorForMonitor(monitor, out scale);
                        output.ScaleFactorPercent = scale; output.ScaleFactorError = scaleError;
                        failure = "source=" + output.Source + "; GetDpiForWindow=" + x + "; awareness=" + awareness +
                            "; monitor_match=" + sameMonitor + "; scale_hr=" + scaleError + "; scale=" + scale;
                        // Microsoft documents GetDpiForWindow on a PM-aware HWND
                        // as the DPI of its monitor. GetScaleFactorForMonitor is
                        // diagnostic only: on this machine it returns 100 while
                        // the correctly positioned PMv2 HWND returns 120 (125%).
                        // Do not replace that measured DPI or reject it based on
                        // a process-dependent resource scale-factor API.
                        if (awareness == 2 && sameMonitor && x >= 96 && x <= 768) dpi = x;
                    } finally { DestroyWindow(window); }
                }
                return true;
            };
            if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero) || dpi == 0 || output.Width == 0 || output.Height == 0)
                throw new InvalidOperationException("实际分辨率/缩放无法可靠读取 / Actual resolution or scale could not be reliably read. " + failure);
            output.Dpi = dpi;
        }
        private static List<string> ListedResolutions(string source)
        {
            var modes = new List<string>();
            for (int i = 0; i < 1024; i++) {
                var mode = new DevMode { Size = (ushort)Marshal.SizeOf(typeof(DevMode)) };
                if (!EnumDisplaySettingsEx(source, i, ref mode, 0)) break;
                string dimensions = mode.Width + "x" + mode.Height;
                if (mode.Width > 0 && mode.Height > 0 && !modes.Contains(dimensions)) modes.Add(dimensions);
            }
            return modes;
        }
        internal static bool IsExactMtt(string adapterPath)
        {
            string instance = DisplayBackend.RootInstancePath(adapterPath);
            if (instance == null) return false;
            using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\" + instance)) {
                var ids = key == null ? null : key.GetValue("HardwareID") as string[];
                if (ids != null) foreach (string id in ids)
                    if (String.Equals(id, @"Root\MttVDD", StringComparison.OrdinalIgnoreCase) || String.Equals(id, "MttVDD", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
        private static string DriverVersion(string adapterPath)
        {
            string instance = DisplayBackend.RootInstancePath(adapterPath);
            using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\" + instance)) {
                string driver = key == null ? null : key.GetValue("Driver") as string;
                if (String.IsNullOrEmpty(driver)) throw new InvalidOperationException("无法核实驱动配置身份 / Cannot verify driver profile identity.");
                using (var config = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\" + driver))
                    return driver + "|" + (config == null ? "" : Convert.ToString(config.GetValue("DriverVersion")));
            }
        }
        private static DisplayBackend.DeviceHeader Header(uint type, Type structure, DisplayBackend.Luid adapter, uint id)
        { return new DisplayBackend.DeviceHeader { Type = type, Size = (uint)Marshal.SizeOf(structure), Adapter = adapter, Id = id }; }
        [StructLayout(LayoutKind.Sequential)] private struct Rect { internal int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct MonitorInfo
        { internal int Size; internal Rect Monitor, Work; internal uint Flags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string Device; }
        private delegate bool MonitorEnum(IntPtr monitor, IntPtr dc, ref Rect bounds, IntPtr data);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct DevMode
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string DeviceName;
            internal ushort SpecVersion, DriverVersion, Size, DriverExtra;
            internal uint Fields;
            internal int PositionX, PositionY;
            internal uint Orientation, FixedOutput;
            internal short Color, Duplex, YResolution, TTOption, Collate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string FormName;
            internal ushort LogPixels;
            internal uint BitsPerPixel, Width, Height, Flags, Frequency, ICMMethod, ICMIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
        }
        [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
        [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] DisplayBackend.PathInfo[] paths, ref uint modeCount, [Out] DisplayBackend.ModeInfo[] modes, IntPtr topology);
        [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo", CharSet = CharSet.Unicode)] private static extern int GetTargetName(ref DisplayBackend.TargetName name);
        [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo", CharSet = CharSet.Unicode)] private static extern int GetAdapterName(ref DisplayBackend.AdapterName name);
        [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo", CharSet = CharSet.Unicode)] private static extern int GetSourceName(ref DisplayBackend.SourceName name);
        [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnum callback, IntPtr data);
        [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
        [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
        [DllImport("user32.dll", EntryPoint = "EnumDisplaySettingsExW", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettingsEx(string source, int index, ref DevMode mode, uint flags);
        [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr GetWindowDpiAwarenessContext(IntPtr window);
        [DllImport("user32.dll")] private static extern int GetAwarenessFromDpiAwarenessContext(IntPtr context);
        [DllImport("shcore.dll")] private static extern int GetScaleFactorForMonitor(IntPtr monitor, out int scale);
    }
}
