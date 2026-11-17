using System;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using ScreenOffTray.SettingsTransport;
[assembly:System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8",FrameworkDisplayName=".NET Framework 4.8")]

namespace ScreenOffTray.Tests
{
    internal static class InAppUpdateTests
    {
        private static int checks;
        private static string output;
        private static readonly byte[] Data=System.Text.Encoding.UTF8.GetBytes("a verifiable installer fixture; never executable");
        private static string Hash(byte[] bytes){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","");}
        private static void Check(bool value,string name){if(!value)throw new Exception(name);Console.WriteLine("PASS "+name);checks++;}
        private sealed class Fake : HttpMessageHandler
        {
            internal string Mode;internal int Calls;internal bool Disposed;
            protected override void Dispose(bool disposing){Disposed=true;base.Dispose(disposing);}
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
            {
                token.ThrowIfCancellationRequested();Calls++;
                if(Mode.StartsWith("meta")) {
                    string fields=Mode=="meta-missing"?"":",\"size\":"+(Mode=="meta-large"?"134217729":Data.Length.ToString())+",\"digest\":\"sha256:"+(Mode=="meta-bad"?"bad":Hash(Data))+"\"";
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){RequestMessage=request,Content=new StringContent("{\"draft\":false,\"prerelease\":false,\"tag_name\":\"v9.0.0\",\"html_url\":\"https://github.com/shilefumaoqu/ScreenOffTray/releases/tag/v9.0.0\",\"assets\":[{\"name\":\"ScreenOffTray-9.0.0-Setup-x64.exe\",\"browser_download_url\":\"https://github.com/shilefumaoqu/ScreenOffTray/releases/download/v9.0.0/ScreenOffTray-9.0.0-Setup-x64.exe\""+fields+"}]}")});
                }
                var response=new HttpResponseMessage(HttpStatusCode.OK){RequestMessage=request};
                if(Mode=="redirect" || Mode=="unsafe" || Mode=="loop") {
                    if(Calls==1 || Mode=="loop") {
                        response.StatusCode=HttpStatusCode.Redirect;
                        response.Headers.Location=new Uri(Mode=="unsafe"?"https://evil.invalid/payload":"https://release-assets.githubusercontent.com/test");return Task.FromResult(response);
                    }
                }
                if(Mode=="http")response.StatusCode=HttpStatusCode.ServiceUnavailable;
                response.Content=Mode=="blocked"?(HttpContent)new StreamContent(new BlockingStream()):new ByteArrayContent(Mode=="corrupt"?new byte[Data.Length]:Data);
                if(Mode=="truncate" || Mode=="oversize")response.Content.Headers.ContentLength=null;
                return Task.FromResult(response);
            }
        }
        private sealed class BlockingStream : Stream
        {
            public override bool CanRead{get{return true;}} public override bool CanWrite{get{return false;}} public override bool CanSeek{get{return false;}}
            public override long Length{get{throw new NotSupportedException();}} public override long Position{get{throw new NotSupportedException();}set{throw new NotSupportedException();}}
            public override async Task<int> ReadAsync(byte[] buffer,int offset,int count,CancellationToken token){await Task.Delay(30000,token);return 0;}
            public override int Read(byte[] buffer,int offset,int count){throw new NotSupportedException();}public override void Flush(){}
            public override long Seek(long n,SeekOrigin origin){throw new NotSupportedException();}public override void SetLength(long n){throw new NotSupportedException();}public override void Write(byte[] b,int o,int c){throw new NotSupportedException();}
        }
        private static UpdateCheckResult Release(long length=0)
        {return new UpdateCheckResult(UpdateCheckStatus.Available,new Version("9.0.0"),new Uri("https://github.com/shilefumaoqu/ScreenOffTray/releases/tag/v9.0.0"),null,new Uri("https://github.com/shilefumaoqu/ScreenOffTray/releases/download/v9.0.0/ScreenOffTray-9.0.0-Setup-x64.exe"),length==0?Data.Length:length,Hash(Data));}
        private static async Task Download(string mode,bool succeeds,long length=0,bool cancel=false)
        {
            string directory=Path.Combine(output,mode);var fake=new Fake{Mode=mode};bool success=false;UpdatePackage package=null;
            using(var cts=new CancellationTokenSource()) {
                if(cancel)cts.CancelAfter(100);
                try{package=await UpdateDownload.Fetch(Release(length),delegate{},cts.Token,fake,directory);success=true;}
                catch(Exception ex){if(ex is OutOfMemoryException)throw;}
            }
            Check(success==succeeds,mode+" result");Check(fake.Disposed,mode+" releases HTTP handler");
            if(package!=null){Check(File.ReadAllBytes(package.Installer).Length==Data.Length,mode+" writes verified file");package.Cleanup();}
            Check(!Directory.Exists(directory),mode+" removes only owned temporary files");
            if(mode=="unsafe")Check(fake.Calls==1,"untrusted redirect is never requested");
            if(mode=="loop")Check(fake.Calls==5,"redirects have a fixed bound");
        }
        private static async Task Run()
        {
            foreach(string mode in new[]{"meta","meta-missing","meta-bad","meta-large"}) {
                using(var checker=new UpdateChecker(UpdateDownload.Repository,new Version("1.0.0"),new Fake{Mode=mode},TimeSpan.FromSeconds(1),16384)) {
                    var result=await checker.CheckAsync(CancellationToken.None);
                    Check(result.CanDownload==(mode=="meta"),mode+" digest/size gate");Check(result.InstallerDownload!=null,mode+" retains explicit manual fallback");
                }
            }
            foreach(string uri in new[]{"http://github.com/a","https://evil.invalid/a","https://github.com:444/a","https://user@github.com/a","https://github.com/a#b"})Check(!UpdateDownload.SafeDownloadUri(new Uri(uri)),"reject URL "+uri);
            await Download("normal",true);await Download("redirect",true);await Download("unsafe",false);await Download("loop",false);
            await Download("corrupt",false);await Download("http",false);await Download("truncate",false,Data.Length+1);await Download("oversize",false,Data.Length-1);await Download("blocked",false,0,true);
            string kept=Path.Combine(output,"cleanup-boundary");Directory.CreateDirectory(kept);File.WriteAllText(Path.Combine(kept,"unrelated"),"keep");File.WriteAllText(Path.Combine(kept,"installer.part"),"partial");
            new UpdatePackage{Directory=kept}.Cleanup();Check(File.Exists(Path.Combine(kept,"unrelated")) && !File.Exists(Path.Combine(kept,"installer.part")),"cleanup never recursively deletes unrelated files");File.Delete(Path.Combine(kept,"unrelated"));Directory.Delete(kept);
            Check(UpdateInstall.SameVersion(new Version("0.2.5"),new Version("0.2.5.0")),"normalize four-part installer versions");
            Check(!UpdateInstall.CompleteFiles(output,new Version("0.2.5")),"missing installed files reject startup");
            string args=UpdateInstall.InstallerArguments(@"C:\Program Files\ScreenOffTray");
            Check(args.Contains("/UPDATEONLY=1") && args.Contains("/NORESTART ") && args.Contains("/NOCLOSEAPPLICATIONS") && args.Contains("/NOFORCECLOSEAPPLICATIONS") && args.Contains("/NORESTARTAPPLICATIONS"),"upgrade reuses installer with no close/restart side effects");
            bool denied=false;try{UpdateInstall.InstallerArguments("x\" /bad");}catch(InvalidDataException){denied=true;}Check(denied,"installation directory cannot inject arguments");
            Check(UpdateInstall.Run("not-a-task")==2,"invalid helper entry never starts product Main");
            Check(Program.SelectMode(new[]{"--apply-update",Guid.NewGuid().ToString("N")})==Program.EntryMode.ApplyUpdate,"internal helper dispatch");
            Check(Program.SelectMode(new[]{"--apply-update","x"})==Program.EntryMode.Invalid,"invalid helper arguments reject before Main");
            Check(UpdateInstall.Run(Guid.NewGuid().ToString("N"))==2,"helper outside its private task directory cannot execute");
            string pipeName="ScreenOffTray.Test."+Guid.NewGuid().ToString("N");
            using(var handoff=new UpdateHandoff{Pipe=new NamedPipeServerStream(pipeName,PipeDirection.InOut,1)}) {
                var received=Task.Run(delegate{using(var client=new NamedPipeClientStream(".",pipeName,PipeDirection.InOut)){client.Connect(1000);return Frames.Read(client).Kind;}});
                var sent=Task.Run(delegate{handoff.Pipe.WaitForConnection();handoff.Commit();});
                var complete=Task.WhenAll(sent,received);if(await Task.WhenAny(complete,Task.Delay(3000))!=complete)throw new TimeoutException("Local pipe test unavailable");
                await complete;Check(await received=="commitUpdate","actual helper commit frame on a local pipe");
            }
        }
        private static int Main(string[] args)
        {try{output=args[0];Directory.CreateDirectory(output);if(args.Length==2 && args[1]=="--live")Live().GetAwaiter().GetResult();else Run().GetAwaiter().GetResult();Console.WriteLine("PASS "+checks+" checks. No product Main, hooks, display reads/writes, registry writes, UAC or installer execution.");return 0;}catch(Exception ex){Console.WriteLine(ex);return 1;}}
        private static async Task Live()
        {
            UpdateCheckResult release;
            using(var checker=new UpdateChecker(UpdateDownload.Repository,new Version("0.2.4"),new HttpClientHandler{AllowAutoRedirect=false,UseCookies=false},TimeSpan.FromSeconds(15),256*1024))release=await checker.CheckAsync(CancellationToken.None);
            Check(release.Status==UpdateCheckStatus.Available && release.CanDownload,"official live metadata has size and SHA256 for "+release.LatestVersion);
            var clock=System.Diagnostics.Stopwatch.StartNew();
            var package=await UpdateDownload.Fetch(release,delegate{},CancellationToken.None,null,Path.Combine(output,"live-download"));
            try {
                Check(UpdateInstall.SameVersion(UpdateInstall.FileVersion(package.Installer),release.LatestVersion),"downloaded installer file version matches Release");
                string receipt="Release="+release.LatestVersion+"; Bytes="+release.InstallerBytes+"; SHA256="+release.InstallerSha256+"; ElapsedMs="+clock.ElapsedMilliseconds+"; installer not executed";
                File.WriteAllText(Path.Combine(output,"live-download-receipt.txt"),receipt);Console.WriteLine(receipt);
            }finally{package.Cleanup();}
            Check(!Directory.Exists(package.Directory),"live download cleanup");
        }
    }
}
