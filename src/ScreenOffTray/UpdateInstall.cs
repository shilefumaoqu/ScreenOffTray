using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;
using ScreenOffTray.SettingsTransport;

namespace ScreenOffTray
{
    internal sealed class UpdateTicket
    {
        public int ParentPid {get;set;}
        public long ParentTicks {get;set;}
        public string Directory {get;set;}
        public string CurrentVersion {get;set;}
        public string TargetVersion {get;set;}
        public string Language {get;set;}
    }
    internal sealed class UpdateHandoff : IDisposable
    {
        internal NamedPipeServerStream Pipe;
        internal Process Child;
        internal void Commit(){Frames.Write(Pipe,new Packet{Kind="commitUpdate"});}
        public void Dispose(){if(Pipe!=null)Pipe.Dispose();if(Child!=null)Child.Dispose();}
    }
    internal static class UpdateInstall
    {
        internal const string UninstallKey=@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{F8732557-3873-4D44-ABF7-D267AF16E126}_is1";
        internal static string RegisteredDirectory()
        {
            using(var root=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,RegistryView.Registry64))
            using(var key=root.OpenSubKey(UninstallKey,false)) {
                string path=key==null?null:key.GetValue("Inno Setup: App Path") as string;
                if(String.IsNullOrWhiteSpace(path))throw new IOException("In-app installation requires a registered installation / 需要安装版才能在软件内安装更新");
                path=Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);UpdateDownload.RejectReparse(path);return path;
            }
        }
        internal static bool SamePath(string a,string b){return String.Equals(Path.GetFullPath(a).TrimEnd('\\'),Path.GetFullPath(b).TrimEnd('\\'),StringComparison.OrdinalIgnoreCase);}
        internal static Version FileVersion(string path){return new Version(FileVersionInfo.GetVersionInfo(path).FileVersion);}
        internal static bool SameVersion(Version a,Version b){return a.Major==b.Major && a.Minor==b.Minor && Math.Max(0,a.Build)==Math.Max(0,b.Build) && Math.Max(0,a.Revision)==Math.Max(0,b.Revision);}
        internal static bool IsElevated(){return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);}
        internal static async Task<UpdateHandoff> Prepare(UpdatePackage package,string language)
        {
            if(IsElevated())throw new IOException("Please restart ScreenOffTray with normal user permissions before updating / 请使用普通权限启动后更新");
            string installed=RegisteredDirectory(),source=typeof(Program).Assembly.Location;
            if(!SamePath(installed,Path.GetDirectoryName(source)))throw new IOException("Only the registered program can update itself");
            string id=Path.GetFileName(package.Directory);Guid guid;
            if(!Guid.TryParseExact(id,"N",out guid) || !SamePath(package.Directory,Path.Combine(UpdateDownload.CacheRoot,id)))throw new IOException("Invalid update task directory");
            var security=new PipeSecurity();security.SetAccessRuleProtection(true,false);
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid,null),PipeAccessRights.FullControl,AccessControlType.Deny));
            security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User,PipeAccessRights.FullControl,AccessControlType.Allow));
            var handoff=new UpdateHandoff{Pipe=new NamedPipeServerStream("ScreenOffTray.Update."+id,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous,4096,4096,security)};
            try {
                using(var parent=Process.GetCurrentProcess()) {
                    var ticket=new UpdateTicket{ParentPid=parent.Id,ParentTicks=parent.StartTime.ToUniversalTime().Ticks,Directory=installed,
                        CurrentVersion=FileVersion(source).ToString(),TargetVersion=package.Release.LatestVersion.ToString(),Language=language};
                    string path=Path.Combine(package.Directory,"ticket.json");
                    File.WriteAllText(path,new JavaScriptSerializer().Serialize(ticket));
                    string worker=Path.Combine(package.Directory,"ScreenOffTray.UpdateWorker.exe");
                    // A previous rejected attempt may leave only this task's known files.
                    foreach(string old in new[]{worker,worker+".config"})if(File.Exists(old))File.Delete(old);
                    File.Copy(source,worker);File.Copy(source+".config",worker+".config");
                    using(var lockedWorker=new FileStream(worker,FileMode.Open,FileAccess.Read,FileShare.Read))
                    using(var lockedConfig=new FileStream(worker+".config",FileMode.Open,FileAccess.Read,FileShare.Read))
                        handoff.Child=Process.Start(new ProcessStartInfo(worker,"--apply-update "+id){UseShellExecute=false,WorkingDirectory=package.Directory,CreateNoWindow=true});
                    if(handoff.Child==null)throw new IOException("Update helper did not start");
                }
                var work=Task.Run(delegate {
                    handoff.Pipe.WaitForConnection();uint pid;
                    if(!GetNamedPipeClientProcessId(handoff.Pipe.SafePipeHandle.DangerousGetHandle(),out pid) || pid!=(uint)handoff.Child.Id || handoff.Child.HasExited)throw new IOException("Update helper identity mismatch");
                    Packet answer=Frames.Read(handoff.Pipe);if(answer.Kind!="updateReady")throw new IOException(answer.Error??"Update helper rejected this operation");
                });
                if(await Task.WhenAny(work,Task.Delay(20000))!=work) {
                    work.ContinueWith(failed=>{var observed=failed.Exception;},CancellationToken.None,TaskContinuationOptions.OnlyOnFaulted,TaskScheduler.Default);
                    throw new TimeoutException("Update helper did not become ready in time");
                }
                await work;return handoff;
            }catch{handoff.Dispose();throw;}
        }
        internal static string InstallerArguments(string directory)
        {
            if(directory.IndexOf('"')>=0)throw new InvalidDataException("Invalid installation directory");
            return "/SILENT /SUPPRESSMSGBOXES /SP- /NORESTART /RESTARTEXITCODE=3010 /NOCLOSEAPPLICATIONS /NOFORCECLOSEAPPLICATIONS /NORESTARTAPPLICATIONS /UPDATEONLY=1 /DIR=\""+directory+"\"";
        }
        internal static bool CompleteFiles(string directory,Version version)
        {
            try {
                foreach(string file in new[]{"ScreenOffTray.exe","ScreenOffTray.Settings.exe","ScreenOffTray.DriverSetup.exe"})
                    if(!SameVersion(FileVersion(Path.Combine(directory,file)),version))return false;
                return true;
            }catch{return false;}
        }
        private static bool StartVerified(string directory)
        {
            using(var process=Process.Start(new ProcessStartInfo(Path.Combine(directory,"ScreenOffTray.exe"),"--launch-verified"){UseShellExecute=false,WorkingDirectory=directory,CreateNoWindow=true}))
                return process!=null && process.WaitForExit(20000) && (process.ExitCode==VerifiedLaunch.Ready || process.ExitCode==VerifiedLaunch.NeedsConfiguration);
        }
        internal static int Run(string id)
        {
            Guid guid;if(!Guid.TryParseExact(id,"N",out guid) || IsElevated())return 2;
            string directory=Path.Combine(UpdateDownload.CacheRoot,id);UpdateTicket ticket=null;bool parentExited=false,installed=false,committed=false;
            NamedPipeClientStream pipe=null;
            try {
                UpdateDownload.RejectReparse(directory);
                if(!SamePath(AppDomain.CurrentDomain.BaseDirectory,directory) || !SamePath(typeof(Program).Assembly.Location,Path.Combine(directory,"ScreenOffTray.UpdateWorker.exe")))return 2;
                string ticketPath=Path.Combine(directory,"ticket.json");
                if(new FileInfo(ticketPath).Length>8192)throw new InvalidDataException("Update ticket too large");
                string text=File.ReadAllText(ticketPath);
                ticket=new JavaScriptSerializer{MaxJsonLength=8192,RecursionLimit=8}.Deserialize<UpdateTicket>(text);
                if(ticket==null || ticket.ParentPid<=0 || ticket.ParentTicks<=0 || !SamePath(ticket.Directory,RegisteredDirectory()))throw new IOException("Invalid update installation identity");
                using(var parent=Process.GetProcessById(ticket.ParentPid)) {
                    string original=Path.Combine(ticket.Directory,"ScreenOffTray.exe");
                    if(parent.HasExited || parent.StartTime.ToUniversalTime().Ticks!=ticket.ParentTicks || !SamePath(parent.MainModule.FileName,original) || !CompleteFiles(ticket.Directory,new Version(ticket.CurrentVersion)))throw new IOException("Update parent identity changed");
                    using(var source=File.OpenRead(original))using(var copy=File.OpenRead(typeof(Program).Assembly.Location)) {
                        using(var sha=System.Security.Cryptography.SHA256.Create())UpdateDownload.CheckHash(copy,BitConverter.ToString(sha.ComputeHash(source)).Replace("-",""));
                    }
                    pipe=new NamedPipeClientStream(".","ScreenOffTray.Update."+id,PipeDirection.InOut,PipeOptions.Asynchronous);pipe.Connect(5000);uint peer;
                    if(!GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(),out peer) || peer!=(uint)ticket.ParentPid)throw new IOException("Update channel identity mismatch");
                    UpdateCheckResult release;
                    using(var checker=new UpdateChecker(UpdateDownload.Repository,new Version(ticket.CurrentVersion),new System.Net.Http.HttpClientHandler{AllowAutoRedirect=false,UseCookies=false},TimeSpan.FromSeconds(10),256*1024))
                        release=checker.CheckAsync(CancellationToken.None).GetAwaiter().GetResult();
                    if(release.Status!=UpdateCheckStatus.Available || !release.CanDownload || !SameVersion(release.LatestVersion,new Version(ticket.TargetVersion)))throw new IOException("The official release changed; check updates again");
                    using(var installer=new FileStream(Path.Combine(directory,"installer.exe"),FileMode.Open,FileAccess.Read,FileShare.Read)) {
                        if(installer.Length!=release.InstallerBytes)throw new IOException("Installer length changed");
                        UpdateDownload.CheckHash(installer,release.InstallerSha256);
                        if(!SameVersion(FileVersion(Path.Combine(directory,"installer.exe")),release.LatestVersion))throw new IOException("Installer version mismatch");
                        Frames.Write(pipe,new Packet{Kind="updateReady"});
                        using(var timeout=new CancellationTokenSource(15000))using(timeout.Token.Register(pipe.Dispose)) {
                            if(Frames.Read(pipe).Kind!="commitUpdate")throw new IOException("Update was not committed");committed=true;
                        }
                        pipe.Dispose();pipe=null;
                        if(!parent.WaitForExit(60000))throw new TimeoutException("The application is still restoring or saving; update was not installed");
                        parentExited=true;
                        using(var setup=Process.Start(new ProcessStartInfo(Path.Combine(directory,"installer.exe"),InstallerArguments(ticket.Directory)){UseShellExecute=true,Verb="runas",WorkingDirectory=directory})) {
                            if(setup==null)throw new IOException("Installer did not start");
                            setup.WaitForExit();if(setup.ExitCode==3010){Show(ticket,"更新文件已安装，请手动重启 Windows。","Update files installed. Restart Windows manually.");return 10;}
                            if(setup.ExitCode!=0)throw new IOException("Installer stopped, code "+setup.ExitCode);
                        }
                    }
                    if(!CompleteFiles(ticket.Directory,release.LatestVersion))throw new IOException("Installed program versions do not match");
                    if(!StartVerified(ticket.Directory))throw new IOException("Files updated, but startup was not confirmed. Check the tray before retrying");
                    installed=true;
                    Show(ticket,"已更新到 "+release.LatestVersion+"，程序已重新启动。","Updated to "+release.LatestVersion+". The application has restarted.");return 0;
                }
            }catch(Exception ex) {
                if(pipe!=null)try{Frames.Write(pipe,new Packet{Kind="updateError",Error=ex is Win32Exception && ((Win32Exception)ex).NativeErrorCode==1223?"Update approval cancelled":ex.Message});}catch{}
                if(parentExited && ticket!=null) {
                    bool restarted=CompleteFiles(ticket.Directory,new Version(ticket.CurrentVersion)) && StartVerified(ticket.Directory);
                    Show(ticket,(restarted?"更新未完成，旧版本已重新启动。\n":"更新未完成，请核对托盘和安装状态后重试。\n")+ex.Message,(restarted?"Update not completed; the previous version restarted.\n":"Update not completed. Check the tray and installed files before retrying.\n")+ex.Message);
                }
                else if(committed && ticket!=null)Show(ticket,
                    "更新尚未安装，未强制结束程序。请等待保存和显示恢复完成、托盘退出后，从开始菜单重新启动 ScreenOffTray，再检查更新；安装包已保留。\n"+ex.Message,
                    "Update not installed; the application was not terminated. Wait for saving/display recovery and tray exit, reopen ScreenOffTray from Start, then check updates again. The installer was preserved.\n"+ex.Message);
                return 1;
            }finally {
                if(pipe!=null)pipe.Dispose();
                // Keep failed/reboot-pending installations for recovery; no recursive cache cleanup.
                if(installed)new UpdatePackage{Directory=directory}.Cleanup();
            }
        }
        private static void Show(UpdateTicket ticket,string zh,string en){MessageBox.Show(ticket.Language=="en"?en:zh,"ScreenOffTray",MessageBoxButtons.OK,MessageBoxIcon.Information);}
        [DllImport("kernel32.dll",SetLastError=true)] private static extern bool GetNamedPipeClientProcessId(IntPtr pipe,out uint pid);
        [DllImport("kernel32.dll",SetLastError=true)] private static extern bool GetNamedPipeServerProcessId(IntPtr pipe,out uint pid);
    }
}
