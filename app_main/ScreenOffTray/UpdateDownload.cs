using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

namespace ScreenOffTray
{
    internal sealed class DownloadProgress
    {
        internal string Stage;
        internal long Received,Total;
    }
    internal sealed class UpdatePackage
    {
        internal string Directory,Installer;
        internal UpdateCheckResult Release;
        internal void Cleanup()
        {
            // Only files created for this task; never recursive deletion.
            foreach(string file in new[]{"installer.part","installer.exe","ticket.json","ScreenOffTray.UpdateWorker.exe","ScreenOffTray.UpdateWorker.exe.config"})
                try{File.Delete(Path.Combine(Directory,file));}catch(IOException){}catch(UnauthorizedAccessException){}
            try{System.IO.Directory.Delete(Directory,false);}catch(IOException){}catch(UnauthorizedAccessException){}
        }
    }
    internal static class UpdateDownload
    {
        internal const long MaximumBytes=UpdateChecker.MaximumInstallerBytes;
        internal const string Repository="shilefumaoqu/ScreenOffTray";
        internal static string CacheRoot {get{return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ScreenOffTray","Updates");}}
        internal static string NewDirectory()
        {
            System.IO.Directory.CreateDirectory(CacheRoot);RejectReparse(CacheRoot);
            string path=Path.Combine(CacheRoot,Guid.NewGuid().ToString("N"));
            var security=new DirectorySecurity();security.SetAccessRuleProtection(true,false);
            security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User,FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null),FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null),FileSystemRights.ReadAndExecute,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
            System.IO.Directory.CreateDirectory(path,security);return path;
        }
        internal static void RejectReparse(string path)
        {
            for(var current=new DirectoryInfo(Path.GetFullPath(path));current!=null;current=current.Parent)
                if(current.Exists && (current.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Update paths cannot use reparse points");
        }
        internal static bool SafeDownloadUri(Uri uri)
        {
            return uri!=null && uri.IsAbsoluteUri && uri.Scheme=="https" && uri.IsDefaultPort && uri.UserInfo.Length==0 && uri.Fragment.Length==0 &&
                (uri.Host.Equals("github.com",StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("release-assets.githubusercontent.com",StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("objects.githubusercontent.com",StringComparison.OrdinalIgnoreCase));
        }
        internal static async Task<UpdatePackage> Fetch(UpdateCheckResult release,Action<DownloadProgress> progress,CancellationToken cancellation,HttpMessageHandler handler=null,string testDirectory=null)
        {
            if(release==null || !release.CanDownload || release.Status!=UpdateCheckStatus.Available || !SafeDownloadUri(release.InstallerDownload))throw new InvalidDataException("This release has no verifiable installer");
            var package=new UpdatePackage{Directory=testDirectory??NewDirectory(),Release=release};
            System.IO.Directory.CreateDirectory(package.Directory);RejectReparse(package.Directory);
            string partial=Path.Combine(package.Directory,"installer.part");package.Installer=Path.Combine(package.Directory,"installer.exe");
            bool success=false;
            try {
                using(var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation))
                using(var client=new HttpClient(handler??new HttpClientHandler{AllowAutoRedirect=false,UseCookies=false},true){Timeout=Timeout.InfiniteTimeSpan}) {
                    deadline.CancelAfter(TimeSpan.FromMinutes(10));Uri uri=release.InstallerDownload;
                    for(int redirect=0;redirect<=4;redirect++) {
                        deadline.Token.ThrowIfCancellationRequested();
                        using(var request=new HttpRequestMessage(HttpMethod.Get,uri)) {
                            request.Headers.UserAgent.ParseAdd("ScreenOffTray-Updater/1");
                            using(var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,deadline.Token).ConfigureAwait(false)) {
                                if(response.RequestMessage==null || response.RequestMessage.RequestUri!=uri)throw new IOException("Unexpected download response URL");
                                int status=(int)response.StatusCode;
                                if(status==301 || status==302 || status==303 || status==307 || status==308) {
                                    if(redirect==4 || response.Headers.Location==null)throw new IOException("Too many update redirects");
                                    uri=new Uri(uri,response.Headers.Location);if(!SafeDownloadUri(uri))throw new IOException("Untrusted update redirect");continue;
                                }
                                if(response.StatusCode!=HttpStatusCode.OK || response.Content==null)throw new IOException("Update download returned HTTP "+status);
                                if(response.Content.Headers.ContentLength.HasValue && response.Content.Headers.ContentLength.Value!=release.InstallerBytes)throw new InvalidDataException("Installer length differs from Release");
                                using(deadline.Token.Register(response.Dispose))
                                using(var input=await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                                using(var output=new FileStream(partial,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536,true)) {
                                    var buffer=new byte[65536];long count=0;var clock=Stopwatch.StartNew();long last=-100;
                                    for(;;) {
                                        int read=await input.ReadAsync(buffer,0,buffer.Length,deadline.Token).ConfigureAwait(false);if(read==0)break;
                                        count+=read;if(count>release.InstallerBytes || count>MaximumBytes)throw new InvalidDataException("Installer exceeds expected size");
                                        await output.WriteAsync(buffer,0,read,deadline.Token).ConfigureAwait(false);
                                        if(clock.ElapsedMilliseconds-last>=100){last=clock.ElapsedMilliseconds;progress(new DownloadProgress{Stage="downloading",Received=count,Total=release.InstallerBytes});}
                                    }
                                    if(count!=release.InstallerBytes)throw new InvalidDataException("Incomplete installer download");
                                }
                                deadline.Token.ThrowIfCancellationRequested();progress(new DownloadProgress{Stage="verifying",Received=release.InstallerBytes,Total=release.InstallerBytes});
                                using(var file=File.OpenRead(partial))CheckHash(file,release.InstallerSha256);
                                deadline.Token.ThrowIfCancellationRequested();File.Move(partial,package.Installer);success=true;return package;
                            }
                        }
                    }
                    throw new IOException("Update redirect limit reached");
                }
            }finally{if(!success)package.Cleanup();}
        }
        internal static void CheckHash(Stream file,string expected)
        {
            using(var sha=SHA256.Create()) {
                string hash=BitConverter.ToString(sha.ComputeHash(file)).Replace("-","");
                if(!String.Equals(hash,expected,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Installer SHA256 mismatch");
            }
            file.Position=0;
        }
    }
}
