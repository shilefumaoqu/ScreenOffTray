using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
namespace ScreenOffTray.Tests {
    internal static class UpdateVersionTests {
        private sealed class Fake : HttpMessageHandler {
            internal string Tag;internal int Disposals;
            protected override void Dispose(bool disposing){if(disposing)Disposals++;base.Dispose(disposing);}
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token) {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage=request, Content=new StringContent("{\"draft\":false,\"prerelease\":false,\"tag_name\":\""+Tag+"\",\"html_url\":\"https://github.com/test/repo/releases/tag/"+Tag+"\"}") });
            }
        }
        private sealed class AssetFake : HttpMessageHandler {
            private readonly string url;
            internal AssetFake(string value){url=value;}
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token) {
                string json="{\"draft\":false,\"prerelease\":false,\"tag_name\":\"v0.2.6\",\"html_url\":\"https://github.com/test/repo/releases/tag/v0.2.6\",\"assets\":[{\"name\":\"ScreenOffTray-0.2.6-Setup-x64.exe\",\"browser_download_url\":\""+url+"\"}]}";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage=request, Content=new StringContent(json) });
            }
        }
        private static int Main() {
            try {
            Test("0.1.9.9","v0.1.9.10",UpdateCheckStatus.Available);
            Test("0.1.9.10","v0.1.9.9",UpdateCheckStatus.UpToDate);
            Test("0.1.9.10","v0.1.9.10",UpdateCheckStatus.UpToDate);
            Test("0.1.9.10","v0.1.9.11",UpdateCheckStatus.Available);
            Test("0.1.9.13","v0.2.0",UpdateCheckStatus.Available);
            Test("0.2.0","v0.1.9.13",UpdateCheckStatus.UpToDate);
            Test("0.2.0","v0.2.0",UpdateCheckStatus.UpToDate);
            Test("0.2.0","v0.2.1",UpdateCheckStatus.Available);
            Test("0.2.1","v0.2.0",UpdateCheckStatus.UpToDate);
            Test("0.2.1","v0.2.1",UpdateCheckStatus.UpToDate);
            Test("0.2.1","v0.2.2",UpdateCheckStatus.Available);
            Test("0.2.2","v0.2.1",UpdateCheckStatus.UpToDate);
            Test("0.2.2","v0.2.2",UpdateCheckStatus.UpToDate);
            Test("0.2.2","v0.2.3",UpdateCheckStatus.Available);
            Test("0.2.3","v0.2.2",UpdateCheckStatus.UpToDate);
            Test("0.2.3","v0.2.3",UpdateCheckStatus.UpToDate);
            Test("0.2.3","v0.2.4",UpdateCheckStatus.Available);
            Test("0.2.4","v0.2.3",UpdateCheckStatus.UpToDate);
            Test("0.2.4","v0.2.4",UpdateCheckStatus.UpToDate);
            Test("0.2.4","v0.2.5",UpdateCheckStatus.Available);
            Test("0.2.5","v0.2.4",UpdateCheckStatus.UpToDate);
            Test("0.2.7","v0.2.8",UpdateCheckStatus.Available);
            Test("0.2.8","v0.2.8",UpdateCheckStatus.UpToDate);
            Test("0.2.5","v0.2.5",UpdateCheckStatus.UpToDate);
            Test("0.2.4.0","v0.2.5.0",UpdateCheckStatus.Available);
            Test("0.2.5","v0.2.6",UpdateCheckStatus.Available);
            Test("0.2.6","v0.2.5",UpdateCheckStatus.UpToDate);
            Test("0.2.6.0","v0.2.6",UpdateCheckStatus.UpToDate);
            using(var client=new UpdateChecker("test/repo",new Version("0.2.4"),new AssetFake("https://github.com/test/repo/releases/download/v0.2.6/ScreenOffTray-0.2.6-Setup-x64.exe"),TimeSpan.FromSeconds(2),16384)) {
                var result=client.CheckAsync(CancellationToken.None).GetAwaiter().GetResult();
                if(result.Status!=UpdateCheckStatus.Available || result.InstallerDownload==null || result.InstallerDownload.AbsoluteUri!="https://github.com/test/repo/releases/download/v0.2.6/ScreenOffTray-0.2.6-Setup-x64.exe")throw new Exception("Exact installer asset was not selected");
            }
            using(var client=new UpdateChecker("test/repo",new Version("0.2.4"),new AssetFake("https://example.invalid/ScreenOffTray-0.2.6-Setup-x64.exe"),TimeSpan.FromSeconds(2),16384)) {
                var result=client.CheckAsync(CancellationToken.None).GetAwaiter().GetResult();
                if(result.Status!=UpdateCheckStatus.Available || result.InstallerDownload!=null)throw new Exception("Unexpected installer URL was accepted");
            }
            using(var client=new UpdateChecker("test/repo",new Version("0.2.4"),new AssetFake("https://github.com/test/repo/releases/download/V0.2.6/ScreenOffTray-0.2.6-Setup-x64.exe"),TimeSpan.FromSeconds(2),16384)) {
                var result=client.CheckAsync(CancellationToken.None).GetAwaiter().GetResult();
                if(result.Status!=UpdateCheckStatus.Available || result.InstallerDownload!=null)throw new Exception("Case-mismatched release tag was accepted");
            }
            var unused=new Fake();using(var checker=new UpdateChecker("",new Version("0.1.9.11"),unused,TimeSpan.FromSeconds(2),16384)) {
                var field=typeof(UpdateChecker).GetField("client",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
                if(field.GetValue(checker)!=null)throw new Exception("Unused client was allocated");
                if(checker.CheckAsync(CancellationToken.None).Result.Status!=UpdateCheckStatus.NotConfigured || field.GetValue(checker)!=null)throw new Exception("Blank update source created HTTP client");
            }
            if(unused.Disposals!=1)throw new Exception("Unconsumed handler ownership was not released once");
            Console.WriteLine("PASS update-parser and installer-asset checks, including 0.2.7 to 0.2.8; fake HTTP only"); return 0;
            } catch(Exception error) { Console.WriteLine(error.GetType().FullName); Console.WriteLine(error.Message); return 1; }
        }
        private static void Test(string current,string tag,UpdateCheckStatus expected) {
            using(var client=new UpdateChecker("test/repo",new Version(current),new Fake{Tag=tag},TimeSpan.FromSeconds(2),16384)) {
                var result=client.CheckAsync(CancellationToken.None).GetAwaiter().GetResult();
                if(result.Status!=expected)throw new Exception(current+" vs "+tag+": "+result.Status+" "+result.Error);
            }
        }
    }
}


