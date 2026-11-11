using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OledPrototype;

namespace ScreenOffTray.Tests
{
    internal static class VirtualModeAdaptationTests
    {
        static int checks;
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; Console.WriteLine("PASS " + message); }
        static void Reject(Action action, string message) { try { action(); } catch { Check(true, message); return; } throw new Exception(message); }
        static byte[] Xml(string resolutions)
        {
            return new UTF8Encoding(true).GetBytes("<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<vdd_settings>\r\n<!-- 用户配置 </resolutions> 保留 -->\r\n<monitors><count>1</count></monitors><global><g_refresh_rate>60</g_refresh_rate><g_refresh_rate>90</g_refresh_rate><g_refresh_rate>120</g_refresh_rate><g_refresh_rate>144</g_refresh_rate><g_refresh_rate>165</g_refresh_rate><g_refresh_rate>244</g_refresh_rate></global>\r\n<resolutions>" + resolutions + "</resolutions><options><HDRPlus>true</HDRPlus><logging>false</logging><custom>keep</custom></options>\r\n</vdd_settings>");
        }
        static byte[] Sample() { return Xml("<resolution><width>800</width><height>600</height><refresh_rate>60</refresh_rate></resolution>"); }
        static VirtualModeRequirement Requirement(bool listed)
        { return new VirtualModeRequirement { Width = 2880, Height = 1800, Rotation = 1, PhysicalKey = "physical|168|driver", VirtualDevice = "virtual", Instance = @"ROOT\DISPLAY\0002", DriverKey = "MikeTheTech|oem69.inf|11.30.4.434", Listed = listed }; }
        sealed class Fake : IVirtualModePlatform
        {
            internal byte[] Config = Sample();
            internal int Reads, ConfigReads, Commits, Writes, Restarts, Delays;
            internal bool Listed, NeverList;
            internal Func<int, VirtualModeRequirement> OnRead;
            internal Action OnRestart, OnCommit, OnDelay;
            internal Func<byte[]> OnConfigRead;
            public VirtualModeRequirement Read() { Reads++; return OnRead == null ? Requirement(Listed) : OnRead(Reads); }
            public byte[] ReadConfig() { ConfigReads++; return OnConfigRead == null ? Config : OnConfigRead(); }
            public string Commit(byte[] original, byte[] desired, VirtualModeRequirement requirement)
            {
                Commits++;
                if (OnCommit != null) OnCommit();
                if (!VirtualModeConfig.Same(original, Config)) throw new Exception("changed source");
                if (!VirtualModeConfig.Same(original, desired)) { Config = desired; Writes++; }
                return "fake commit";
            }
            public void Restart(VirtualModeRequirement requirement) { Restarts++; if (OnRestart != null) OnRestart(); Listed = !NeverList; }
            public void Delay() { Delays++; if (OnDelay != null) OnDelay(); }
        }
        sealed class Evidence : IDisplayEvidenceStore
        {
            internal DisplayEvidence Value = new DisplayEvidence { Observed = "old-profile", Completed = "old-profile", Checked = DateTime.UtcNow.ToString("o") };
            public DisplayEvidence Load() { return Value; }
            public void Save(DisplayEvidence value) { Value = value; }
        }
        sealed class Clock : IAutoSetupClock { internal long Value; public long Milliseconds { get { return Value; } } }
        sealed class Scheduler : IAutoSetupScheduler
        {
            internal int Starts;
            sealed class Token : IDisposable { public void Dispose() { } }
            public IDisposable Schedule(Action action, int delay) { Starts++; return new Token(); }
        }
        // Returns a valid post-reload plan, then deliberately stops in Capture:
        // no fake or real APPLY, guardian, recovery or settings success can occur.
        sealed class FreshPlan : IAutoSetupPlatform
        {
            internal int Plans, Captures, Recoveries;
            internal uint Adapter = 900, Width = 2880;
            public AutoSetupPlan PlanCheck() {
                Plans++;
                var path = new DisplayBackend.PathInfo(); path.Flags = 1; path.Source.Adapter.Low = Adapter; path.Source.Id = 1; path.Source.ModeIndex = 0;
                path.Target.Adapter.Low = Adapter; path.Target.Id = 1; path.Target.Rotation = 1; path.Target.Available = 1;
                var source = new DisplayBackend.ModeInfo { InfoType = 1, Id = 1, Adapter = path.Source.Adapter,
                    Source = new DisplayBackend.SourceMode { Width = Width, Height = 1800, PixelFormat = 4 } };
                var snapshot = new DisplayBackend.Snapshot { Paths = new[] { path }, Modes = new[] { source }, DevicePaths = new[] { "physical" }, AdapterPaths = new[] { "physical-adapter" } };
                return new AutoSetupPlan { Original = snapshot, PhysicalOnly = snapshot, VirtualOnly = new DisplayBackend.Plan { DevicePath = "virtual" } };
            }
            public DisplayBackend.Snapshot Capture() { Captures++; throw new Exception("intentional fake stop after fresh PlanCheck"); }
            public void Validate(DisplayBackend.Snapshot value) { throw new Exception("not reached"); }
            public void Apply(DisplayBackend.Snapshot expected, DisplayBackend.Snapshot desired, Action before) { throw new Exception("APPLY forbidden"); }
            public DisplaySetupResult Probe() { throw new Exception("not reached"); }
            public string SaveRecovery(DisplayBackend.Snapshot value) { Recoveries++; throw new Exception("recovery forbidden"); }
            public IAutoSetupGuardian StartGuardian(string path) { throw new Exception("guardian forbidden"); }
            public void Record(string path, string detail) { }
        }
        static void Pump(ConcurrentQueue<Action> queue, Func<bool> finished)
        {
            var end = DateTime.UtcNow.AddSeconds(5);
            while (!finished()) {
                Action action; while (queue.TryDequeue(out action)) action();
                if (DateTime.UtcNow > end) throw new Exception("fake completion timeout");
                Thread.Sleep(1);
            }
            Action pending; while (queue.TryDequeue(out pending)) pending();
        }
        static void ConfigTests()
        {
            foreach (var size in new uint[][] { new uint[] {1920,1080}, new uint[] {2560,1600}, new uint[] {3440,1440}, new uint[] {1080,1920}, new uint[] {2880,1800}, new uint[] {1973,1237} }) {
                var original = Sample(); var changed = VirtualModeConfig.Add(original, size[0], size[1]);
                string addition = "<resolution><width>" + size[0] + "</width><height>" + size[1] + "</height><refresh_rate>60</refresh_rate></resolution>";
                string text = Encoding.UTF8.GetString(changed);
                Check(text.Contains(addition), size[0] + "x" + size[1] + " uses source dimensions without aspect presets or portrait swap");
                Check(VirtualModeConfig.Same(original, Encoding.UTF8.GetBytes(text.Replace(addition, ""))), "all original bytes, comments, options, modes and six global rates preserved");
                Check(Object.ReferenceEquals(changed, VirtualModeConfig.Add(changed, size[0], size[1])), "exact XML mode is idempotent");
            }
            byte[] bom = new byte[Sample().Length + 3]; bom[0] = 239; bom[1] = 187; bom[2] = 191; Buffer.BlockCopy(Sample(), 0, bom, 3, Sample().Length);
            Check(VirtualModeConfig.Add(bom, 1080, 1920)[0] == 239, "UTF-8 BOM preserved");
            Reject(() => VirtualModeConfig.Add(Encoding.UTF8.GetBytes("<broken>"), 1920, 1080), "malformed XML refused");
            Reject(() => VirtualModeConfig.Add(Encoding.UTF8.GetBytes("<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///unread'>]><vdd_settings>&e;</vdd_settings>"), 1920, 1080), "DTD/entity refused without resolving content");
            Reject(() => VirtualModeConfig.Add(new byte[VirtualModeConfig.Limit + 1], 1920, 1080), "oversized file refused");
            Reject(() => VirtualModeConfig.Add(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Sample()).Replace("<count>1", "<count>2")), 1920, 1080), "multiple virtual monitors refused");
            Reject(() => VirtualModeConfig.Add(Sample(), 0, 1080), "invalid physical dimensions refused");
            Reject(() => VirtualModeConfig.Add(Encoding.Unicode.GetBytes("<vdd_settings/>"), 1920, 1080), "unsupported encoding refused");
        }
        static void FileTests(string temporary)
        {
            string file = Path.Combine(temporary, "vdd_settings.xml"), records = Path.Combine(temporary, "records"); File.WriteAllBytes(file, Sample());
            var first = File.ReadAllBytes(file); var desired = VirtualModeConfig.Add(first, 2880, 1800);
            File.WriteAllBytes(file, Xml("<resolution><width>1024</width><height>768</height><refresh_rate>60</refresh_rate></resolution>"));
            byte[] external = File.ReadAllBytes(file);
            Reject(() => VirtualModeNative.WriteConfig(file, records, first, desired, Requirement(false), () => {}), "changed source refused before write or journal");
            Check(VirtualModeConfig.Same(external, File.ReadAllBytes(file)) && !Directory.Exists(records), "external edit retained without journal or overwrite");
            File.WriteAllBytes(file, first);
            string info = VirtualModeNative.WriteConfig(file, records, first, desired, Requirement(false), () => {});
            Check(VirtualModeConfig.Same(desired, File.ReadAllBytes(file)), "temporary-file commit readback equals exact append");
            var originals = Directory.GetFiles(records, "*.original.xml");
            Check(originals.Length == 1 && VirtualModeConfig.Same(first, File.ReadAllBytes(originals[0])) && Directory.GetFiles(records, "*.txt").Length == 1 && info.Contains(records), "original and per-change record retained; no ownership claim");
            var actualBackups = Directory.GetFiles(temporary, "*.replaced.xml");
            Check(actualBackups.Length == 1 && VirtualModeConfig.Same(first, File.ReadAllBytes(actualBackups[0])), "atomic replacement retains actual replaced bytes for conflict verification");
            int verify = 0;
            Reject(() => VirtualModeNative.WriteConfig(file, records, desired, VirtualModeConfig.Add(desired, 1080, 1920), Requirement(false), () => { if (++verify == 2) throw new Exception("physical changed before write"); }), "physical change at final commit boundary refuses mutation");
            Check(VirtualModeConfig.Same(desired, File.ReadAllBytes(file)), "last boundary refusal preserves configuration");
            using (var guard = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Reject(() => VirtualModeNative.WriteConfig(file, records, desired, desired, Requirement(false), () => {}), "concurrent exclusive writer refused");
            Check(Directory.GetFiles(temporary, "*.tmp").Length == 0, "uncommitted staging cleaned after final verification failure");
            int replacements = 0;
            Reject(() => VirtualModeNative.WriteConfig(file, records, desired, Encoding.UTF8.GetBytes("broken staged XML"), Requirement(false), () => {}, (s,t,b) => replacements++), "invalid staged XML refused before replacement");
            Check(replacements == 0 && VirtualModeConfig.Same(desired, File.ReadAllBytes(file)) && Directory.GetFiles(temporary, "*.tmp").Length == 0, "failed temporary XML validation preserves destination and cleans only staging");
            byte[] next = VirtualModeConfig.Add(desired, 1080, 1920);
            Reject(() => VirtualModeNative.WriteConfig(file, records, desired, next, Requirement(false), () => {}, (s,t,b) => { throw new IOException("injected replacement failure"); }), "atomic replacement failure is explicit");
            Check(VirtualModeConfig.Same(desired, File.ReadAllBytes(file)) && Directory.GetFiles(temporary, "*.tmp").Length == 0, "replacement failure leaves valid original XML and removes uncommitted staging");
            byte[] thirdParty = Xml("<resolution><width>1600</width><height>900</height><refresh_rate>75</refresh_rate></resolution>");
            string conflictBackup = null;
            Reject(() => VirtualModeNative.WriteConfig(file, records, desired, next, Requirement(false), () => {}, (s,t,b) => { File.WriteAllBytes(t, thirdParty); conflictBackup = b; File.Replace(s,t,b,false); }), "last-boundary external update detected from actual replaced backup");
            Check(conflictBackup != null && VirtualModeConfig.Same(thirdParty, File.ReadAllBytes(conflictBackup)) && VirtualModeConfig.Same(next, File.ReadAllBytes(file)), "racing third-party bytes retained; conflict never triggers automatic rollback");
            Reject(() => VirtualModeNative.WriteConfig(file, records, next, desired, Requirement(false), () => {}, (s,t,b) => { File.Replace(s,t,b,false); File.WriteAllBytes(t, thirdParty); }), "external update after replacement is also detected");
            Check(VirtualModeConfig.Same(thirdParty, File.ReadAllBytes(file)), "post-replacement third-party update preserved without rollback");
            Reject(() => VirtualModeNative.ConfigFile(@"\\server\share"), "UNC path refused without network access");
            Reject(() => VirtualModeNative.ConfigFile(@"C:\unknown\..\elsewhere"), "noncanonical path refused");
            Reject(() => VirtualModeNative.ConfigFile(@"C:\unknown:stream"), "alternate-stream path refused");
        }
        static void FlowTests()
        {
            var known = new Fake { Listed = true }; var result = VirtualModeAdaptation.Run(known, () => false);
            Check(result.Success && known.ConfigReads == 0 && known.Writes == 0 && known.Restarts == 0, "exact live mode no-op never touches XML or reloads");
            var add = new Fake(); result = VirtualModeAdaptation.Run(add, () => false);
            Check(result.Success && result.Changed && result.Reloaded && add.Writes == 1 && add.Restarts == 1, "missing live mode appends and reloads exactly once then verifies new modes");
            var presentXml = new Fake { Config = VirtualModeConfig.Add(Sample(), 2880, 1800) }; result = VirtualModeAdaptation.Run(presentXml, () => false);
            Check(result.Success && !result.Changed && presentXml.Writes == 0 && presentXml.Restarts == 1, "XML-present but live-absent mode requires one explicit reload without duplicate write");
            var never = new Fake { NeverList = true }; result = VirtualModeAdaptation.Run(never, () => false);
            Check(!result.Success && never.Restarts == 1 && never.Delays == 39, "mode not applied fails after bounded virtual-time polling without restarting again");
            var uac = new Fake { OnRestart = () => { throw new OperationCanceledException("UAC cancelled"); } }; result = VirtualModeAdaptation.Run(uac, () => false);
            Check(!result.Success && result.Error.Contains("UAC") && uac.Restarts == 1 && uac.Delays == 0, "UAC cancellation cannot become success or retry");
            var reboot = new Fake { OnRestart = () => { throw new Exception("reboot required"); } }; result = VirtualModeAdaptation.Run(reboot, () => false);
            Check(!result.Success && result.Error.Contains("reboot") && reboot.Restarts == 1, "reboot-required operation fails without reboot or retry");
            var broken = new Fake { Config = Encoding.UTF8.GetBytes("invalid") }; result = VirtualModeAdaptation.Run(broken, () => false);
            Check(!result.Success && broken.Restarts == 0 && broken.Writes == 0, "broken XML never reloads");
            var external = new Fake(); external.OnCommit = () => external.Config = new byte[] { 1 }; result = VirtualModeAdaptation.Run(external, () => false);
            Check(!result.Success && external.Restarts == 0 && external.Writes == 0, "concurrent external source mutation stops before reload");
            var rotation = new Fake { OnRead = n => { var r = Requirement(false); if (n > 1) { r.Width = 1080; r.Height = 1920; r.Rotation = 2; } return r; } };
            result = VirtualModeAdaptation.Run(rotation, () => false);
            Check(!result.Success && rotation.Restarts == 0 && rotation.Writes == 0, "physical size/rotation change invalidates adaptation before configuration commit");
            var changedAfter = new Fake(); changedAfter.OnRead = n => { var r = Requirement(changedAfter.Listed); if (changedAfter.Restarts > 0) r.Instance = @"ROOT\DISPLAY\0003"; return r; };
            result = VirtualModeAdaptation.Run(changedAfter, () => false);
            Check(!result.Success && changedAfter.Restarts == 1, "different post-reload instance is rejected even when exact mode lists");
            bool cancel = false; var cancelled = new Fake { OnCommit = () => cancel = true }; result = VirtualModeAdaptation.Run(cancelled, () => cancel);
            Check(!result.Success && cancelled.Restarts == 0 && cancelled.Writes == 1, "cancel after write preserves added XML but never starts reload");
            var wrong = new FreshPlan { Width = 1080 }; int verified = 0;
            Reject(() => new ModeCheckedAutoSetup(wrong, Requirement(true), () => verified++).PlanCheck(), "new PlanCheck with changed physical source refused");
            Check(verified == 2 && wrong.Recoveries == 0, "mode-to-plan verification occurs before recovery or guardian");
        }
        static void LifecycleTests()
        {
            foreach (bool cancelling in new[] { true, false }) {
                var queue = new ConcurrentQueue<Action>(); var evidence = new Evidence(); var clock = new Clock(); var scheduler = new Scheduler();
                var plan = new FreshPlan(); int factories = 0;
                using (var entered = new ManualResetEvent(false)) using (var released = new ManualResetEvent(false))
                using (var state = new DisplaySetupState(a => {}, evidence, () => { factories++; return plan; }, () => new DisplaySetupResult(), clock, scheduler, a => queue.Enqueue(a))) {
                    var fake = new Fake { OnRestart = () => { entered.Set(); released.WaitOne(); } };
                    Check(state.PrepareDisplay(() => fake), "explicit shared prepare entry starts fake mode worker");
                    Check(entered.WaitOne(3000), "fake reload is running on worker");
                    Check(state.Preparing && !state.CanPrepareDisplay && !state.CanPrepare && !state.IsReady && !state.Complete(), "unified gate excludes idle, hotkey readiness, retry and completion during reload");
                    Check(factories == 0 && scheduler.Starts == 0 && plan.Plans == 0 && plan.Recoveries == 0, "no old setup watchdog, CCD baseline or platform exists across reload");
                    Check(evidence.Value.Completed == "" && evidence.Value.Observed == "", "prior dimensions/DPI evidence is invalidated before adapting");
                    state.Recheck(); Check(!state.Checking && !state.PrepareDisplay(() => fake), "background check and second explicit attempt cannot overlap device operation");
                    clock.Value = 120000; // Fake UAC longer than the old 30s budget.
                    if (cancelling) {
                        state.CancelPreparation(); Check(state.Preparing, "cancel/exit cannot abandon an in-progress fake restart");
                    }
                    released.Set();
                    Pump(queue, () => !state.Preparing);
                    if (cancelling) Check(factories == 0 && !state.IsReady && state.LastPreparationError.Length > 0, "completionPost settles cancellation despite ordinary post being dropped during exit");
                    else Check(factories == 1 && plan.Plans == 1 && plan.Captures == 1 && plan.Recoveries == 0 && scheduler.Starts > 0 && !state.IsReady,
                        "fresh PlanCheck starts after reload with fresh clock; deliberate fake preflight failure never enables display off");
                }
            }
            var current = new DisplaySetupResult { PhysicalOnly = true, Compatible = true, Profile = "old-profile" };
            var ev = new Evidence(); var callbacks = new ConcurrentQueue<Action>();
            using (var state = new DisplaySetupState(a => callbacks.Enqueue(a), ev, () => { throw new Exception("no platform"); }, () => current)) {
                state.Recheck(); Pump(callbacks, () => state.HasSettledResult); Check(state.IsReady, "saved same-profile evidence can be reused before physical change");
                state.Invalidate(); current = new DisplaySetupResult { PhysicalOnly = true, Compatible = true, Profile = "new-1080x1920-rotation2" };
                state.Recheck(); Pump(callbacks, () => state.HasSettledResult);
                Check(!state.IsReady && !state.CanComplete && ev.Value.Completed == "", "existing display-change invalidate/recheck rejects old dimensions and rotation evidence");
            }
        }
        static void ReviewFixTests(string temporary)
        {
            string file = Path.Combine(temporary, "vdd_settings.xml");
            byte[] exact = VirtualModeConfig.Add(Sample(), 2880, 1800);
            File.WriteAllBytes(file, exact); File.SetAttributes(file, FileAttributes.ReadOnly);
            try {
                var readOnly = new Fake { Config = File.ReadAllBytes(file), OnCommit = () => { throw new UnauthorizedAccessException("read-only configuration must not be committed"); } };
                var outcome = VirtualModeAdaptation.Run(readOnly, () => false);
                Check(outcome.Success && !outcome.Changed && outcome.Reloaded && readOnly.Commits == 0 && readOnly.Writes == 0 && readOnly.Restarts == 1,
                    "read-only XML with exact mode skips Commit and requests one explicit fake reload");
                Check(readOnly.ConfigReads >= 3 && VirtualModeConfig.Same(exact, File.ReadAllBytes(file)) && (File.GetAttributes(file) & FileAttributes.ReadOnly) != 0,
                    "read-only no-write branch retains readback checks, original bytes and attributes");
            } finally { File.SetAttributes(file, FileAttributes.Normal); }
            byte[] original = Sample(); File.WriteAllBytes(file, original);
            string records = Path.Combine(temporary, "post-commit-records"), backup = null, log = null;
            var platform = new Fake();
            platform.OnCommit = () => VirtualModeNative.WriteConfig(file, records, original, exact, Requirement(false), () => {}, (s,t,b) => {
                File.Replace(s,t,b,false); backup = b;
                log = Directory.GetFiles(records, "*.txt")[0];
                File.SetAttributes(log, FileAttributes.ReadOnly); // File.Replace returned; the later log append fails with access denied.
            });
            VirtualModeAdaptationResult failed;
            try { failed = VirtualModeAdaptation.Run(platform, () => false); }
            finally { if (log != null) File.SetAttributes(log, FileAttributes.Normal); }
            Check(!failed.Success && platform.Commits == 1 && platform.Restarts == 0,
                "access error after replacement fails adaptation without reload or retry");
            Check(failed.Error.Contains("XML was atomically replaced") && failed.Error.Contains("logging incomplete") && !failed.Error.Contains("no write") && !failed.Error.Contains("未写入"),
                "post-replacement access failure accurately reports committed XML with unconfirmed logging");
            Check(backup != null && failed.Error.Contains(backup) && failed.Error.Contains(records) && VirtualModeConfig.Same(original, File.ReadAllBytes(backup)) && VirtualModeConfig.Same(exact, File.ReadAllBytes(file)),
                "post-commit failure retains backup paths and committed XML without automatic rollback");
        }
        static void BusyNotificationTests()
        {
            var callbacks = new ConcurrentQueue<Action>(); var evidence = new Evidence();
            var scheduler = new Scheduler(); int factories = 0, notifications = 0;
            using (var entered = new ManualResetEvent(false)) using (var released = new ManualResetEvent(false))
            using (var state = new DisplaySetupState(a => {}, evidence, () => { throw new Exception("protected setup must not start after cancellation"); }, () => new DisplaySetupResult(), new Clock(), scheduler, a => callbacks.Enqueue(a))) {
                state.Changed += () => { if (++notifications == 1) throw new InvalidOperationException("injected initial Changed failure"); };
                var fake = new Fake { OnRestart = () => { entered.Set(); released.WaitOne(); } };
                try {
                    Check(state.PrepareDisplay(() => { factories++; return fake; }), "throwing initial Changed subscriber does not escape or prevent worker dispatch");
                    Check(entered.WaitOne(3000) && factories == 1 && state.Preparing && scheduler.Starts == 0,
                        "mode worker owns the busy gate after notification error; old setup watchdog not started");
                    Check(state.Diagnostics.Contains("mode_setup_notify_failed") && state.Diagnostics.Contains("injected initial Changed failure"),
                        "initial callback exception is recorded while operation is running");
                    state.CancelPreparation(); Check(state.Preparing, "cancel still waits for the already-running fake device operation");
                } finally { released.Set(); }
                Pump(callbacks, () => !state.Preparing);
                Check(state.CanPrepareDisplay && !state.IsReady && notifications >= 2 && fake.Restarts == 1,
                    "completion releases busy gate and publishes cancellation after initial subscriber failure");
                Check(state.Diagnostics.Contains("mode_setup_notify_failed") && state.Diagnostics.Contains("injected initial Changed failure"),
                    "settled diagnostics retain the callback exception instead of erasing it");
            }
        }
        static void PostReloadAndRouteTests()
        {
            int polls = 0; var delayed = new Fake();
            delayed.OnRead = n => { if (delayed.Restarts == 0) return Requirement(false); if (++polls <= 3) throw new VirtualModeNotReadyException("fake CCD not ready"); return Requirement(true); };
            var result = VirtualModeAdaptation.Run(delayed, () => false);
            Check(result.Success && polls == 4 && delayed.Delays == 3 && delayed.Restarts == 1, "three transient post-reload reads then success use one restart and bounded virtual delays");
            polls = 0; var absent = new Fake();
            absent.OnRead = n => { if (absent.Restarts == 0) return Requirement(false); polls++; throw new VirtualModeNotReadyException("fake PnP still unavailable"); };
            result = VirtualModeAdaptation.Run(absent, () => false);
            Check(!result.Success && polls == 40 && absent.Delays == 39 && absent.Restarts == 1 && result.Error.Contains("fake PnP still unavailable"), "persistent transient state stops after forty reads without a second reload");
            foreach (bool physical in new[] { false, true }) {
                polls = 0; var changed = new Fake();
                changed.OnRead = n => { var r = Requirement(changed.Restarts > 0); if (changed.Restarts > 0) { polls++; if (physical) r.Rotation = 2; else r.Instance = @"ROOT\DISPLAY\0003"; } return r; };
                result = VirtualModeAdaptation.Run(changed, () => false);
                Check(!result.Success && polls == 1 && changed.Delays == 0 && changed.Restarts == 1, physical ? "confirmed physical rotation change fails immediately without retry" : "confirmed device instance change fails immediately without retry");
            }
            foreach (Exception error in new Exception[] { new UnauthorizedAccessException("fake permissions"), new InvalidOperationException("fake bad configuration") }) {
                polls = 0; var fatal = new Fake();
                fatal.OnRead = n => { if (fatal.Restarts == 0) return Requirement(false); polls++; throw error; };
                result = VirtualModeAdaptation.Run(fatal, () => false);
                Check(!result.Success && polls == 1 && fatal.Delays == 0 && result.Error == error.Message, "non-transient read error is not swallowed or retried: " + error.GetType().Name);
            }
            bool cancelled = false; polls = 0; var cancelWait = new Fake { OnDelay = () => cancelled = true };
            cancelWait.OnRead = n => { if (cancelWait.Restarts == 0) return Requirement(false); polls++; throw new VirtualModeNotReadyException("fake waiting"); };
            result = VirtualModeAdaptation.Run(cancelWait, () => cancelled);
            Check(!result.Success && polls == 1 && cancelWait.Delays == 1 && cancelWait.Restarts == 1 && result.Error.Contains("cancelled"), "cancellation during bounded wait stops before another read or reload");
            var early = new Fake { OnRead = n => { throw new VirtualModeNotReadyException("pre-reload not ready"); } };
            result = VirtualModeAdaptation.Run(early, () => false);
            Check(!result.Success && early.Reads == 1 && early.Delays == 0 && early.Restarts == 0, "typed transient before restart is not retried");
            var configError = new Fake();
            configError.OnRead = n => { if (configError.Restarts > 0) throw new VirtualModeNotReadyException("fake enumeration gap"); return Requirement(false); };
            configError.OnConfigRead = () => { if (configError.Restarts > 0) throw new UnauthorizedAccessException("post-reload config denied"); return configError.Config; };
            result = VirtualModeAdaptation.Run(configError, () => false);
            Check(!result.Success && configError.Delays == 0 && result.Error.Contains("config denied"), "configuration permission error fails immediately even during transient CCD absence");
            Check(VirtualModeNotReadyException.CcdError("CCD", 1167) is VirtualModeNotReadyException && VirtualModeNotReadyException.CcdError("CCD", 122) is VirtualModeNotReadyException, "known device-disconnected and insufficient-buffer codes are typed transient");
            Check(!(VirtualModeNotReadyException.CcdError("CCD", 5, 1167) is VirtualModeNotReadyException) && !(VirtualModeNotReadyException.CcdError("CCD", 87) is VirtualModeNotReadyException), "access denied and invalid parameter codes remain fatal, including mixed status");
            try { VirtualModeNative.RequireStarted(13,0,0,0); throw new Exception("missing node accepted"); } catch(VirtualModeNotReadyException) { Check(true,"missing PnP node is typed transient"); }
            try { VirtualModeNative.RequireStarted(0,0,0,0); throw new Exception("unstarted node accepted"); } catch(VirtualModeNotReadyException) { Check(true,"not-yet-started healthy PnP node is typed transient"); }
            foreach (uint problem in new uint[] { 14, 22 }) {
                Exception failure = null; try { VirtualModeNative.RequireStarted(0,0,8,problem); } catch(Exception ex) { failure = ex; }
                Check(failure != null && !(failure is VirtualModeNotReadyException), "confirmed PnP problem stays fatal: " + problem);
            }
            Exception reboot = null; try { VirtualModeNative.RequireStarted(0,0,0x108,0); } catch(Exception ex) { reboot = ex; }
            Check(reboot != null && !(reboot is VirtualModeNotReadyException), "restart-required PnP status is not retried");

            VirtualRouteTests();
        }
        static void VirtualRouteTests()
        {
            var baseline = new FreshPlan().PlanCheck().PhysicalOnly;
            var physicalPath = baseline.Paths[0]; physicalPath.Target.ModeIndex = 1; baseline.Paths[0] = physicalPath;
            baseline.Modes = new[] { baseline.Modes[0], new DisplayBackend.ModeInfo { InfoType = 2, Adapter = physicalPath.Target.Adapter, Id = physicalPath.Target.Id } };
            var first = ModeRoute(10, 7, false); var active = ModeRoute(11, 7, true); var last = ModeRoute(12, 7, false);
            var alternatives = new List<DisplayBackend.Route> { first, active, last };
            int names = 0; uint selected = 0;
            Func<DisplayBackend.PathInfo,string> readName = path => { names++; selected = path.Source.Id; return "fake-source-" + path.Source.Id; };
            string name = AutoSetupNative.SelectedVirtualSource(baseline, alternatives, "virtual", readName);
            Check(name == "fake-source-11" && selected == 11 && names == 1, "same target alternative sources select active route and read only its source name");
            alternatives = new List<DisplayBackend.Route> { last, first }; names = 0;
            name = AutoSetupNative.SelectedVirtualSource(baseline, alternatives, "virtual", readName);
            Check(name == "fake-source-12" && names == 1, "inactive alternatives select first OS-priority source without requiring equal names");
            alternatives.Add(ModeRoute(13, 8, false)); names = 0;
            Reject(() => AutoSetupNative.SelectedVirtualSource(baseline, alternatives, "virtual", readName), "same monitor identity with genuinely different target IDs remains rejected");
            Check(names == 0, "ambiguous targets rejected before reading or enumerating any source");
            names = 0; alternatives = new List<DisplayBackend.Route> { ModeRoute(13, 8, true), first }; alternatives[0].Device.DevicePath = "other-monitor";
            name = AutoSetupNative.SelectedVirtualSource(baseline, alternatives, "virtual", readName);
            Check(name == "fake-source-10" && names == 1, "another target's active route is ignored rather than guessed");

            var shared = ModeRoute(physicalPath.Source.Id, 7, true);
            var sharedPath = shared.Path; sharedPath.Source.Adapter = physicalPath.Source.Adapter; shared.Path = sharedPath;
            var clone = CloneBaseline(baseline, shared);
            names = 0; alternatives = new List<DisplayBackend.Route> { shared, first };
            name = AutoSetupNative.SelectedVirtualSource(clone, alternatives, "virtual", readName);
            Check(name == "fake-source-10" && names == 1, "active shared clone source plus inactive independent source selects and reads only independent source");
            Check(clone.Paths.Length == 2 && clone.Paths[1].Source.Id == physicalPath.Source.Id && DisplayAutoCcd.SameLuid(clone.Paths[1].Source.Adapter, physicalPath.Source.Adapter),
                "source selection leaves the real mixed clone baseline unchanged");

            // Same-adapter clones exercise shared-source exclusion independently
            // of the cross-adapter proxy filter.
            var sameAdapterShared = ModeRoute(physicalPath.Source.Id, 7, true);
            var samePath = sameAdapterShared.Path; samePath.Source.Adapter = samePath.Target.Adapter = physicalPath.Source.Adapter; sameAdapterShared.Path = samePath;
            var sameAdapterIndependent = ModeRoute(10, 7, false);
            var independentPath = sameAdapterIndependent.Path; independentPath.Source.Adapter = independentPath.Target.Adapter = physicalPath.Source.Adapter; sameAdapterIndependent.Path = independentPath;
            names = 0;
            name = AutoSetupNative.SelectedVirtualSource(CloneBaseline(baseline, sameAdapterShared), new List<DisplayBackend.Route> { sameAdapterShared, sameAdapterIndependent }, "virtual", readName);
            Check(name == "fake-source-10" && names == 1, "same-adapter active shared source is filtered before inactive independent selection");

            var proxy = ModeRoute(30, 7, true); var proxyPath = proxy.Path; proxyPath.Source.Adapter.Low = 2000; proxy.Path = proxyPath;
            names = 0;
            name = AutoSetupNative.SelectedVirtualSource(baseline, new List<DisplayBackend.Route> { proxy, first }, "virtual", readName);
            Check(name == "fake-source-10" && names == 1, "active cross-adapter proxy with a distinct source is excluded before source selection");
            names = 0;
            name = AutoSetupNative.SelectedVirtualSource(clone, new List<DisplayBackend.Route> { shared, last, first }, "virtual", readName);
            Check(name == "fake-source-12" && names == 1, "OS priority among inactive independent alternatives survives active clone filtering");

            var otherShared = ModeRoute(physicalPath.Source.Id, 8, true); var otherPath = otherShared.Path; otherPath.Source.Adapter = physicalPath.Source.Adapter; otherShared.Path = otherPath;
            names = 0;
            Reject(() => AutoSetupNative.SelectedVirtualSource(baseline, new List<DisplayBackend.Route> { otherShared, first }, "virtual", readName),
                "different target on an otherwise filtered shared route is still rejected");
            Check(names == 0, "filtered-route target ambiguity is rejected before any source enumeration");
            var otherProxy = ModeRoute(30, 8, true); otherPath = otherProxy.Path; otherPath.Source.Adapter.Low = 2000; otherProxy.Path = otherPath;
            Reject(() => AutoSetupNative.SelectedVirtualSource(baseline, new List<DisplayBackend.Route> { first, otherProxy }, "virtual", readName),
                "later cross-adapter proxy target cannot be hidden by source filtering");
            Check(names == 0, "reverse candidate order also rejects hidden target ambiguity before enumeration");
        }
        static DisplayBackend.Snapshot CloneBaseline(DisplayBackend.Snapshot physical, DisplayBackend.Route shared)
        {
            var path = shared.Path; path.Source.ModeIndex = 0; path.Target.ModeIndex = 2;
            return new DisplayBackend.Snapshot {
                Paths = new[] { physical.Paths[0], path },
                Modes = new[] { physical.Modes[0], physical.Modes[1], new DisplayBackend.ModeInfo { InfoType = 2, Id = path.Target.Id, Adapter = path.Target.Adapter } },
                DevicePaths = new[] { physical.DevicePaths[0], shared.Device.DevicePath }, AdapterPaths = new[] { physical.AdapterPaths[0], shared.Device.AdapterPath }
            };
        }
        static DisplayBackend.Route ModeRoute(uint source, uint target, bool active)
        {
            var path = new DisplayBackend.PathInfo { Flags = active ? 1U : 0U };
            path.Source.Adapter.Low = path.Target.Adapter.Low = 1000; path.Source.Id = source; path.Target.Id = target; path.Target.Technology = 17; path.Target.Available = 1;
            return new DisplayBackend.Route { Path = path, Device = new DisplayTarget { DevicePath = "virtual", AdapterPath = "fake-virtual-adapter", Technology = 17, Available = true, Active = active } };
        }
        static int Main(string[] args)
        {
            string temporary = Path.Combine(Path.GetTempPath(), "ScreenOffMode-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temporary);
            try { if(args.Length == 1 && args[0] == "--route-selection-only") VirtualRouteTests(); else if(args.Length == 1 && args[0] == "--review-p2-only") PostReloadAndRouteTests(); else if(args.Length == 1 && args[0] == "--busy-notification-only") BusyNotificationTests(); else if(args.Length == 1 && args[0] == "--review-fixes-only") ReviewFixTests(temporary); else if(args.Length == 1 && args[0] == "--atomic-file-only") FileTests(temporary); else { ConfigTests(); FileTests(temporary); FlowTests(); LifecycleTests(); ReviewFixTests(temporary); BusyNotificationTests(); PostReloadAndRouteTests(); } Console.WriteLine("RESULT checks=" + checks + " failures=0; temporary XML, fake elevation/reload/CCD/time only; no product Main or native device calls."); return 0; }
            catch (Exception ex) { Console.WriteLine("FAIL " + ex); return 1; }
            finally { Directory.Delete(temporary, true); }
        }
    }
}
