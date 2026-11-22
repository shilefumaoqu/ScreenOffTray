using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using OledPrototype;

namespace ScreenOffTray.DriverSetup
{
    internal static partial class Program
    {
        private static int Main(string[] args)
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            try
            {
                if (args.Length == 1 && args[0] == "--self-test") { SelfTest.Run(); return 0; }
                if (!Environment.Is64BitProcess) throw new InvalidOperationException("x64 helper required");
                if(args.Length==3 && args[0]=="--verify-processes" && args[1]=="--app-dir" && Path.IsPathRooted(args[2])) {CheckStopped(Path.GetFullPath(args[2]));Output(new{status="verified-stopped",readOnly=true});return 0;}
                if (args.Length == 1 && args[0] == "--probe") return Probe();
                if ((args.Length == 1 || (args.Length == 3 && args[1] == "--app-dir" && Path.IsPathRooted(args[2]))) && (args[0] == "--verify-existing" || args[0] == "--verify-first-install-pending")) {
                    if (args.Length == 3) {
                        CheckStopped(Path.GetFullPath(args[2]));
                        Policy.Bundle(Path.Combine(Path.GetFullPath(args[2]),"Driver"));
                    }
                    var devices = Native.Enumerate(); var receipt = StateStore.Read(StateStore.DefaultRoot);
                    if (args[0] == "--verify-first-install-pending") VerifyFirstInstallPending(devices,receipt);
                    else VerifyExisting(devices, receipt);
                    Output(new { status = "verified-existing", readOnly = true }); return 0;
                }
                if (args.Length != 3 || (args[0] != "install" && args[0] != "uninstall") || args[1] != "--bundle-dir" || !Path.IsPathRooted(args[2]))
                {
                    Output(new { status = "usage", contract = "--verify-existing | --probe | --self-test | install --bundle-dir ABSOLUTE_DRIVER_DIRECTORY | uninstall --bundle-dir ABSOLUTE_DRIVER_DIRECTORY" }); return 2;
                }
                if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
                    throw new UnauthorizedAccessException("Run install/uninstall elevated; probe/self-test require no elevation");
                // Both operations require the original recovery payload to remain available.
                string bundle = Path.GetFullPath(args[2]); Policy.Bundle(bundle);
                using (StateStore store = new StateStore(StateStore.DefaultRoot))
                {
                    Receipt receipt = StateStore.Read(store.Root);
                    return args[0] == "install" ? Install(bundle, store, receipt) : Uninstall(store, receipt);
                }
            }
            catch (UnauthorizedAccessException ex) { Output(new { status = "permission-error", error = ex.Message }); return 5; }
            catch (Exception ex) { Output(new { status = "failed", error = ex.Message, recovery = StateStore.DefaultRoot }); return 20; }
        }
    }
}
