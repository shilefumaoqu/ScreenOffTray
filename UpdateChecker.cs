using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace ScreenOffTray
{
    public enum UpdateCheckStatus { NotConfigured, UpToDate, Available, NoRelease, Failed, Cancelled }

    public sealed class UpdateCheckResult
    {
        public UpdateCheckStatus Status { get; private set; }
        public Version LatestVersion { get; private set; }
        public Uri ReleasePage { get; private set; }
        public string Error { get; private set; }

        internal UpdateCheckResult(UpdateCheckStatus status, Version version, Uri page, string error)
        {
            Status = status;
            LatestVersion = version;
            ReleasePage = page;
            Error = error;
        }
    }

    public sealed class UpdateChecker : IDisposable
    {
        private const int DefaultBodyLimit = 256 * 1024;
        private readonly HttpClient client;
        private readonly Uri endpoint;
        private readonly string repository;
        private readonly Version currentVersion;
        private readonly TimeSpan timeout;
        private readonly int bodyLimit;
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private readonly CancellationToken lifetimeToken;
        private string configurationError;
        private int disposed;

        public bool IsConfigured { get { return endpoint != null && configurationError == null; } }

        public static UpdateChecker FromConfiguration()
        {
            string repository = null;
            string error = null;
            try { repository = ConfigurationManager.AppSettings["GitHubRepository"]; }
            catch (ConfigurationErrorsException) { error = "Cannot read GitHubRepository configuration."; }

            // Per-client policy only: never alter ServicePointManager or system TLS settings.
            var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
            var checker = new UpdateChecker(repository, GetCurrentVersion(), handler,
                TimeSpan.FromSeconds(10), DefaultBodyLimit);
            if (error != null) checker.configurationError = error;
            return checker;
        }

        public static Version GetCurrentVersion()
        {
            var attribute = (AssemblyFileVersionAttribute)Attribute.GetCustomAttribute(
                typeof(UpdateChecker).Assembly, typeof(AssemblyFileVersionAttribute));
            Version version;
            if (attribute == null || !Version.TryParse(attribute.Version, out version))
                throw new InvalidOperationException("AssemblyFileVersion is missing or invalid.");
            return version;
        }

        // Owns and disposes handler. Injection is for isolated tests; production uses FromConfiguration.
        // Injected handlers must not follow redirects or supply credentials/cookies.
        public UpdateChecker(string repository, Version currentVersion, HttpMessageHandler handler,
            TimeSpan timeout, int maxResponseBytes)
        {
            if (currentVersion == null) throw new ArgumentNullException("currentVersion");
            if (handler == null) throw new ArgumentNullException("handler");
            if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1))
                throw new ArgumentOutOfRangeException("timeout");
            if (maxResponseBytes < 1 || maxResponseBytes > 1024 * 1024)
                throw new ArgumentOutOfRangeException("maxResponseBytes");

            this.repository = (repository ?? String.Empty).Trim();
            this.currentVersion = currentVersion;
            lifetimeToken = lifetime.Token;
            this.timeout = timeout;
            bodyLimit = maxResponseBytes;
            if (this.repository.Length != 0)
            {
                if (ValidRepository(this.repository))
                    endpoint = new Uri("https://api.github.com/repos/" + this.repository + "/releases/latest");
                else configurationError = "GitHubRepository must be an owner/repo name.";
            }
            client = new HttpClient(handler, true) { Timeout = Timeout.InfiniteTimeSpan };
        }

        private static bool ValidRepository(string value)
        {
            if (!Regex.IsMatch(value, @"\A[A-Za-z0-9](?:[A-Za-z0-9-]{0,37}[A-Za-z0-9])?/[A-Za-z0-9_.-]{1,100}\z"))
                return false;
            string name = value.Substring(value.IndexOf('/') + 1);
            return name != "." && name != "..";
        }

        public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested) return Result(UpdateCheckStatus.Cancelled, null);
            if (Volatile.Read(ref disposed) != 0) return Result(UpdateCheckStatus.Failed, "Update checker is disposed.");
            if (configurationError != null) return Result(UpdateCheckStatus.Failed, configurationError);
            if (!IsConfigured) return Result(UpdateCheckStatus.NotConfigured, null);

            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetimeToken))
            {
                deadline.CancelAfter(timeout);
                try
                {
                    using (var request = new HttpRequestMessage(HttpMethod.Get, endpoint))
                    {
                        request.Headers.UserAgent.ParseAdd("ScreenOffTray/" + currentVersion);
                        request.Headers.Accept.ParseAdd("application/vnd.github+json");
                        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
                        using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                            deadline.Token).ConfigureAwait(false))
                        {
                            deadline.Token.ThrowIfCancellationRequested();
                            if (response.RequestMessage == null || response.RequestMessage.RequestUri != endpoint)
                                return Result(UpdateCheckStatus.Failed, "Unexpected update response URL.");
                            // Redirects (including repository renames) require an explicit configuration correction.
                            if (response.StatusCode == HttpStatusCode.NotFound)
                                return Result(UpdateCheckStatus.NoRelease, null);
                            if (response.StatusCode != HttpStatusCode.OK)
                                return Result(UpdateCheckStatus.Failed, "GitHub returned HTTP " + (int)response.StatusCode + ".");
                            if (response.Content == null)
                                return Result(UpdateCheckStatus.Failed, "Empty GitHub response.");

                            // Dispose also interrupts stream reads on Framework implementations that do not
                            // promptly honor ReadAsync's cancellation token. The deadline covers headers + body.
                            using (deadline.Token.Register(response.Dispose))
                            {
                                string json = await ReadBodyAsync(response.Content, deadline.Token).ConfigureAwait(false);
                                deadline.Token.ThrowIfCancellationRequested();
                                UpdateCheckResult result = ParseRelease(json);
                                deadline.Token.ThrowIfCancellationRequested();
                                return result;
                            }
                        }
                    }
                }
                catch (OperationCanceledException) { return CancellationResult(cancellationToken); }
                catch (HttpRequestException) { return FailureOrCancellation(deadline, cancellationToken, "GitHub request failed."); }
                catch (IOException) { return FailureOrCancellation(deadline, cancellationToken, "Cannot read GitHub response."); }
                catch (ObjectDisposedException) { return FailureOrCancellation(deadline, cancellationToken, "Update request was disposed."); }
                catch (InvalidOperationException) { return FailureOrCancellation(deadline, cancellationToken, "Invalid GitHub response."); }
                catch (ArgumentException) { return FailureOrCancellation(deadline, cancellationToken, "Invalid GitHub response."); }
            }
        }

        private async Task<string> ReadBodyAsync(HttpContent content, CancellationToken token)
        {
            if (content.Headers.ContentLength.HasValue && content.Headers.ContentLength.Value > bodyLimit)
                throw new IOException("Response body exceeds limit.");
            using (var stream = await content.ReadAsStreamAsync().ConfigureAwait(false))
            using (var bytes = new MemoryStream())
            {
                var buffer = new byte[Math.Min(4096, bodyLimit + 1)];
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    int count = await stream.ReadAsync(buffer, 0,
                        Math.Min(buffer.Length, bodyLimit - (int)bytes.Length + 1), token).ConfigureAwait(false);
                    if (count == 0) break;
                    if (bytes.Length + count > bodyLimit) throw new IOException("Response body exceeds limit.");
                    bytes.Write(buffer, 0, count);
                }
                return new UTF8Encoding(false, true).GetString(bytes.ToArray());
            }
        }

        private UpdateCheckResult ParseRelease(string json)
        {
            var serializer = new JavaScriptSerializer { MaxJsonLength = bodyLimit, RecursionLimit = 16 };
            var release = serializer.DeserializeObject(json) as Dictionary<string, object>;
            object draft, prerelease, tagValue, pageValue;
            if (release == null || !release.TryGetValue("draft", out draft) || !(draft is bool) ||
                !release.TryGetValue("prerelease", out prerelease) || !(prerelease is bool))
                return Result(UpdateCheckStatus.Failed, "Invalid GitHub release flags.");
            if ((bool)draft || (bool)prerelease) return Result(UpdateCheckStatus.NoRelease, null);
            if (!release.TryGetValue("tag_name", out tagValue) || !(tagValue is string) ||
                !release.TryGetValue("html_url", out pageValue) || !(pageValue is string))
                return Result(UpdateCheckStatus.Failed, "Missing GitHub release version or page.");

            string tag = (string)tagValue;
            Version latest;
            if (tag.Length > 64 || !Regex.IsMatch(tag, @"\A[vV]?[0-9]+(?:\.[0-9]+){1,3}\z") ||
                !Version.TryParse(tag.TrimStart('v', 'V'), out latest))
                return Result(UpdateCheckStatus.Failed, "Release tag must be a numeric version.");

            string page = (string)pageValue;
            string prefix = "https://github.com/" + repository + "/releases/tag/";
            Uri releasePage;
            // Literal prefix + tag excludes userinfo, custom ports, query/fragment, alternate repos,
            // percent-encoding and URI normalization tricks. Repository casing is insensitive on GitHub.
            if (!page.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(page.Substring(prefix.Length), tag, StringComparison.Ordinal) ||
                !Uri.TryCreate(page, UriKind.Absolute, out releasePage))
                return Result(UpdateCheckStatus.Failed, "Unexpected GitHub release page.");

            return new UpdateCheckResult(Normalize(latest).CompareTo(Normalize(currentVersion)) > 0
                ? UpdateCheckStatus.Available : UpdateCheckStatus.UpToDate, latest, releasePage, null);
        }

        private static Version Normalize(Version version)
        {
            return new Version(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
        }

        private static UpdateCheckResult Result(UpdateCheckStatus status, string error)
        {
            return new UpdateCheckResult(status, null, null, error);
        }

        private static UpdateCheckResult CancellationResult(CancellationToken caller)
        {
            return caller.IsCancellationRequested ? Result(UpdateCheckStatus.Cancelled, null)
                : Result(UpdateCheckStatus.Failed, "GitHub request timed out or was cancelled by transport.");
        }

        private static UpdateCheckResult FailureOrCancellation(CancellationTokenSource deadline,
            CancellationToken caller, string error)
        {
            return deadline.IsCancellationRequested ? CancellationResult(caller) : Result(UpdateCheckStatus.Failed, error);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            lifetime.Cancel();
            client.Dispose();
            lifetime.Dispose();
        }
    }
}
