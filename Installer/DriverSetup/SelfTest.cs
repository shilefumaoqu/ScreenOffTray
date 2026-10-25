using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;
using System.Xml;

namespace ScreenOffTray.DriverSetup
{
    internal static class SelfTest
    {
        private static int checks;
        private static void Assert(bool value, string name)
        { checks++; if (!value) throw new InvalidOperationException("Self-test failed: " + name); }
        private static Device Good()
        {
            return new Device { HardwareIds = new string[] { @"Root\MttVDD" }, InstanceId = @"ROOT\SCREENOFFTRAYVDD\0000",
                ClassGuid = Native.DisplayClass.ToString("B"), Provider = "MikeTheTech", MatchingId = @"root\mttvdd",
                Version = "11.30.4.434", Inf = "oem1.inf", Present = true, Started = true, Problem = 0, Available = true };
        }
        private static string Select(params Device[] devices) { return Policy.Select(new List<Device>(devices)); }
        private static void Reject(Action work, string name)
        { bool failed = false; try { work(); } catch (InvalidOperationException) { failed = true; } Assert(failed, name); }
        internal static void Run()
        {
            Assert(Policy.Exact(new string[] { @"ROOT\MTTVDD" }), "case-insensitive exact root ID");
            Assert(Policy.Exact(new string[] { "MttVDD" }), "alternate exact ID");
            Assert(!Policy.Exact(new string[] { @"ROOT\MttVDD2", @"ROOT\tdIdd", @"ROOT\GameViewerIddDriver" }), "UU/ToDesk/prefix excluded");
            Assert(!Policy.Exact(null), "null HWID excluded");
            Assert(Select() == "create", "zero instances");
            Assert(Select(Good()) == "reuse", "one present compatible usable instance");
            Assert(Select(Good(), Good()) == "conflict", "duplicates");
            Device d = Good(); d.Present = false; Assert(Select(d) == "conflict", "one ghost blocks create");
            Assert(Select(Good(), d) == "conflict", "present plus ghost");
            d = Good(); d.Started = false; Assert(Select(d) == "conflict", "disabled/stopped");
            d = Good(); d.Problem = 22; Assert(Select(d) == "conflict", "problem device");
            d = Good(); d.Available = false; Assert(Select(d) == "conflict", "no CCD target");
            d = Good(); d.Provider = "Other"; Assert(Select(d) == "conflict", "provider mismatch");
            d = Good(); d.Version = "11.30.4.433"; Assert(Select(d) == "conflict", "older driver");
            d = Good(); d.Version = "12.0.0.0"; Assert(Select(d) == "reuse", "newer driver never downgraded");
            d = Good(); d.Version = "bad"; Assert(Select(d) == "conflict", "unparseable version");
            d = Good(); d.ClassGuid = Guid.Empty.ToString("B"); Assert(Select(d) == "conflict", "class mismatch");
            d = Good(); d.MatchingId = @"ROOT\tdIdd"; Assert(Select(d) == "conflict", "bound model mismatch");
            d = Good(); d.Inf = null; Assert(Select(d) == "conflict", "missing bound INF");
            Receipt r = new Receipt { Owned = true, Token = Guid.NewGuid().ToString("N"), InstanceId = Good().InstanceId, BoundInf = "oem1.inf", BoundVersion = "11.30.4.434" };
            r.Marker = "ScreenOffTray:" + r.Token; d = Good(); d.Marker = r.Marker;
            Policy.AssertOwned(r, d); Assert(true, "exact marked owned device");
            r.Owned = false; Reject(delegate { Policy.AssertOwned(r, d); }, "reused receipt cannot own"); r.Owned = true;
            d.Marker = "foreign"; Reject(delegate { Policy.AssertOwned(r, d); }, "foreign marker"); d.Marker = r.Marker;
            d.InstanceId = @"ROOT\DISPLAY\9999"; Reject(delegate { Policy.AssertOwned(r, d); }, "other instance"); d.InstanceId = r.InstanceId;
            d.Version = "12.0.0.0"; Reject(delegate { Policy.AssertOwned(r, d); }, "external upgrade preserves device"); d.Version = r.BoundVersion;
            d.Inf = "oem99.inf"; Reject(delegate { Policy.AssertOwned(r, d); }, "external rebind preserves device");
            foreach (uint[] size in new uint[][] { new uint[] { 2560, 1600 }, new uint[] { 3840, 2160 }, new uint[] { 1600, 2560 } })
            {
                XmlDocument xml = new XmlDocument(); xml.LoadXml(Policy.Config(size[0], size[1]));
                Assert(xml.SelectSingleNode("/vdd_settings/resolutions/resolution/width").InnerText == size[0].ToString(), "dynamic width");
                Assert(xml.SelectSingleNode("/vdd_settings/resolutions/resolution/height").InnerText == size[1].ToString(), "dynamic height");
                Assert(xml.SelectNodes("/vdd_settings/resolutions/resolution").Count == 1 && xml.SelectSingleNode("/vdd_settings/monitors/count").InnerText == "1", "single dynamic monitor");
                Assert(!Policy.Config(size[0], size[1]).Contains("125"), "no DPI hardcode");
            }
            Reject(delegate { Policy.Config(0, 1600); }, "invalid resolution");
            Reject(delegate { Policy.Config(99999, 1600); }, "oversized resolution");
            r.PriorValue = true; r.PriorKind = "ExpandString"; r.PriorData = @"%SystemDrive%\existing";
            Receipt roundtrip = new JavaScriptSerializer().Deserialize<Receipt>(new JavaScriptSerializer().Serialize(r));
            Assert(roundtrip.PriorKind == r.PriorKind && roundtrip.PriorData == r.PriorData && roundtrip.Token == r.Token, "registry kind/raw data journal roundtrip");
            OledPrototype.DisplayBackend.CheckLayout(); Assert(true, "CCD native ABI");
            Assert(System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.DevInfo)) == 32, "SetupAPI x64 devinfo ABI");
            int originalChecks = checks;
            List<object> faults = new List<object>();
            RollbackFaultChecks(faults);
            RegistrationFaultChecks(faults);
            Program.Output(new { status = "passed", checks = checks, originalChecks = originalChecks,
                newChecks = checks - originalChecks, pure = true, nativeMutations = 0, faultInjection = faults });
        }
        private static Receipt OwnReceipt()
        {
            Receipt r = new Receipt { Owned = true, Token = Guid.NewGuid().ToString("N"), InstanceId = Good().InstanceId };
            r.Marker = "ScreenOffTray:" + r.Token;
            return r;
        }
        private static void RollbackFaultChecks(List<object> faults)
        {
            string[] cases = { "persistent-journal", "unrelated-alongside-own", "foreign-marker",
                "remove-failure", "enumeration-failure", "removal-pending", "layout-failure",
                "registry-post-restore-journal-failure", "unrelated-only", "wrong-hardware-id", "wrong-class" };
            for (int scenario = 0; scenario < cases.Length; scenario++)
            {
                Receipt r = OwnReceipt(); Device own = Good(); own.Marker = r.Marker;
                Device foreign = Good(); foreign.InstanceId = @"ROOT\DISPLAY\9999"; foreign.Marker = "foreign";
                List<Device> live = new List<Device>();
                if (scenario != 8) live.Add(own);
                if (scenario == 1 || scenario == 8) live.Add(foreign);
                if (scenario == 2) own.Marker = "foreign";
                if (scenario == 9) own.HardwareIds = new string[] { @"Root\tdIdd" };
                if (scenario == 10) own.ClassGuid = Guid.Empty.ToString("B");
                List<string> trace = new List<string>(), removed = new List<string>();
                int writes = 0, layouts = 0, registryRestores = 0;
                Exception original = new InvalidOperationException("original-install-failure");
                InvalidOperationException failure = Program.RollbackInstall(r, original,
                    delegate {
                        trace.Add("enumerate");
                        if (scenario == 4) throw new IOException("enumeration-failure");
                        return new List<Device>(live);
                    },
                    delegate(Device d) {
                        trace.Add("remove:" + d.InstanceId); removed.Add(d.InstanceId);
                        if (scenario == 3) throw new IOException("remove-failure");
                        if (scenario != 5) live.Remove(d);
                        return true;
                    },
                    delegate {
                        trace.Add("layout"); layouts++;
                        if (scenario == 6) throw new IOException("layout-failure");
                    },
                    delegate {
                        trace.Add("registry"); registryRestores++;
                        // Models StateStore.RestoreRegistry's trailing Save, after safe restoration.
                        if (scenario == 7) throw new IOException("registry-post-restore-journal-failure");
                    },
                    delegate(Receipt value) { trace.Add("journal"); writes++; throw new IOException("persistent-journal-write-failure"); });
                string name = cases[scenario];
                Assert(writes == 2 && layouts == 1 && trace[0] == "journal" && trace[trace.Count - 1] == "journal", name + ": bounded saves cannot skip layout");
                Assert(Object.ReferenceEquals(failure.InnerException, original) && failure.Message.StartsWith(original.Message) &&
                    failure.Message.Contains("rollback journal: persistent-journal-write-failure"), name + ": original failure retained");
                bool removable = scenario == 0 || scenario == 1 || scenario == 3 || scenario == 5 || scenario == 6 || scenario == 7;
                Assert(removed.Count == (removable ? 1 : 0) && removed.TrueForAll(delegate(string id) { return id == r.InstanceId; }), name + ": exact ownership only");
                bool restoreConfig = scenario == 0 || scenario == 7;
                Assert(registryRestores == (restoreConfig ? 1 : 0) && r.Phase == (scenario == 0 ? "rolled-back" : "failed"), name + ": configuration safety gates");
                if (restoreConfig) Assert(trace.IndexOf("layout") < trace.IndexOf("registry"), name + ": layout before configuration");
                if (scenario == 1 || scenario == 8) Assert(live.Contains(foreign), name + ": unrelated node preserved");
                if (scenario == 3 || scenario == 6 || scenario == 7)
                    Assert(failure.Message.Contains(cases[scenario]), name + ": secondary failure retained");
                faults.Add(new { scenario = name, journalFailures = writes, layoutAttempts = layouts,
                    registryAttempts = registryRestores, removedInstances = removed, phase = r.Phase, trace = trace, error = failure.Message });
            }
        }
        private static void RegistrationFaultChecks(List<object> faults)
        {
            string[] cases = { "marker-assignment-failure", "register-intent-journal-failure", "registration-success",
                "collision-before-registration", "registered-receipt-journal-failure" };
            for (int scenario = 0; scenario < cases.Length; scenario++)
            {
                Receipt r = OwnReceipt(); List<string> trace = new List<string>();
                int registrations = 0; bool marker = false; string durableId = null, durableMarker = null, durablePhase = null;
                Exception injected = new IOException(cases[scenario]), caught = null;
                try
                {
                    Native.RegisterNewDevice(r,
                        delegate(Receipt value) {
                            trace.Add("save:" + value.Phase);
                            if (scenario == 1 || (scenario == 4 && value.Phase == "registered")) throw injected;
                            durableId = value.InstanceId; durableMarker = value.Marker; durablePhase = value.Phase;
                        },
                        delegate { trace.Add("hardware-id"); },
                        delegate { trace.Add("marker"); if (scenario == 0) throw injected; marker = true; },
                        delegate { trace.Add("collision-check"); if (scenario == 3) throw injected; },
                        delegate {
                            trace.Add("register"); registrations++;
                            Assert(marker && durableId == r.InstanceId && durableMarker == r.Marker && durablePhase == "register-intent", "registration requires durable exact intent and marker");
                        });
                }
                catch (Exception ex) { caught = ex; }
                bool reachesRegistration = scenario == 2 || scenario == 4;
                Assert(registrations == (reachesRegistration ? 1 : 0), cases[scenario] + ": registration count");
                Assert(scenario == 2 ? caught == null : Object.ReferenceEquals(caught, injected), cases[scenario] + ": original failure propagated");
                if (scenario == 0) Assert(String.Join(",", trace) == "save:register-intent,hardware-id,marker", "marker failure stops before collision/registration");
                if (scenario == 1) Assert(trace.Count == 1 && !marker && durableId == null, "intent failure stops all later steps");
                if (reachesRegistration) Assert(String.Join(",", trace) == "save:register-intent,hardware-id,marker,collision-check,register,save:registered", "production registration ordering");
                if (scenario == 4) Assert(durablePhase == "register-intent" && marker, "post-register journal failure retains recoverable marked identity");
                faults.Add(new { scenario = cases[scenario], registrations = registrations, markerAssigned = marker,
                    durablePhase = durablePhase, trace = trace, error = caught == null ? null : caught.Message });
            }
        }
    }
}
