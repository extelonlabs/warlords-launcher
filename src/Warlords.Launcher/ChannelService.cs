using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Warlords.Launcher;

public sealed class ChannelService
{
    private const int MaximumChannelBytes = 8 * 1024 * 1024;
    private const long MaximumLauncherBytes = 512L * 1024 * 1024;
    private static readonly HttpClient Http = CreateClient();
    // Same redirect rules, but long enough for the launcher exe itself.
    private static readonly HttpClient Downloads = CreateDownloadClient();

    private static HttpClient CreateDownloadClient()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Warlords-Launcher/" + Profile.LauncherVersion);
        return client;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Warlords-Closed-Test/" + Profile.LauncherVersion);
        client.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true, MaxAge = TimeSpan.Zero };
        client.DefaultRequestHeaders.Pragma.ParseAdd("no-cache");
        return client;
    }

    public async Task<ChannelSource> LoadAsync(string location, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        cancellationToken = timeout.Token;
        byte[] bytes;
        if (Uri.TryCreate(location, UriKind.Absolute, out var uri) && !uri.IsFile)
        {
            RequireHttps(uri);
            // The stable channel is intentionally mutable. Use a fresh request at the
            // CDN as well as no-cache headers; do not alter immutable package URLs.
            var requestUri = new UriBuilder(uri);
            requestUri.Query = (string.IsNullOrEmpty(uri.Query) ? "" : uri.Query.Substring(1) + "&")
                + "warlords=" + Guid.NewGuid().ToString("N");
            using var response = await GetHttpsAsync(requestUri.Uri, cancellationToken);
            response.EnsureSuccessStatusCode();
            RequireHttps(response.RequestMessage!.RequestUri!);
            if (response.Content.Headers.ContentLength > MaximumChannelBytes)
                throw new InvalidDataException("The update channel is larger than the supported limit.");
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            bytes = await ReadBoundedAsync(stream, cancellationToken);
            location = uri.AbsoluteUri;
        }
        else
        {
            location = Path.GetFullPath(uri?.IsFile == true ? uri.LocalPath : location);
            var info = new FileInfo(location);
            if (!info.Exists)
                throw new FileNotFoundException("Choose channel.json from the extracted Warlords test pack.", location);
            if (info.Length > MaximumChannelBytes)
                throw new InvalidDataException("The update channel is larger than the supported limit.");
            using var stream = File.OpenRead(location);
            bytes = await ReadBoundedAsync(stream, cancellationToken);
        }

        var release = JsonSerializer.Deserialize<ReleaseChannel>(bytes, Profile.Json)
            ?? throw new InvalidDataException("The update channel is empty.");
        InstallService.ValidateRelease(release);
        if (Uri.TryCreate(location, UriKind.Absolute, out var sourceUri) && sourceUri.Scheme == Uri.UriSchemeHttps)
        {
            if (!Uri.TryCreate(release.Package, UriKind.Absolute, out var packageUri))
                throw new InvalidDataException("An online channel must use an absolute HTTPS package URL.");
            RequireHttps(packageUri);
        }
        return new ChannelSource(release, location);
    }

    /// <summary>Reads the signed launcher manifest. Returns it only when it names a newer launcher.</summary>
    public async Task<LauncherUpdate?> CheckLauncherUpdateAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await GetHttpsAsync(new Uri(Profile.LauncherUpdateUrl + "?warlords=" + Guid.NewGuid().ToString("N")), timeout.Token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        var envelope = JsonSerializer.Deserialize<LauncherUpdateEnvelope>(await ReadBoundedAsync(stream, timeout.Token), Profile.Json)
            ?? throw new InvalidDataException("The launcher update manifest is empty.");
        // Check the signature on the exact bytes before reading anything inside them.
        var manifest = Convert.FromBase64String(envelope.Manifest);
        using (var rsa = RSA.Create())
        {
            rsa.ImportParameters(new RSAParameters { Modulus = Convert.FromBase64String(Profile.LauncherUpdateKeyModulus), Exponent = [1, 0, 1] });
            if (!rsa.VerifyData(manifest, Convert.FromBase64String(envelope.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                throw new InvalidDataException("The launcher update is not signed by the Warlords key.");
        }
        var update = JsonSerializer.Deserialize<LauncherUpdate>(manifest, Profile.Json)
            ?? throw new InvalidDataException("The launcher update manifest is empty.");
        if (update.SchemaVersion != 1 || !Version.TryParse(update.Version, out var version)
            || update.Size is <= 0 or > MaximumLauncherBytes
            || !System.Text.RegularExpressions.Regex.IsMatch(update.Sha256, "^[0-9a-f]{64}$")
            || !Uri.TryCreate(update.Url, UriKind.Absolute, out var url))
            throw new InvalidDataException("The launcher update manifest is invalid.");
        RequireHttps(url);
        // Strictly newer only: an old signed manifest can never roll a launcher back.
        return version > Version.Parse(Profile.LauncherVersion) ? update : null;
    }

    /// <summary>Downloads the signed launcher to destination and checks its size and SHA-256.</summary>
    public async Task DownloadLauncherAsync(LauncherUpdate update, string destination, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        if (File.Exists(destination)) File.Delete(destination);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(20));
            using var response = await GetHttpsAsync(new Uri(update.Url), timeout.Token, Downloads);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } length && length != update.Size)
                throw new InvalidDataException("The launcher download size does not match the signed manifest.");
            using (var input = await response.Content.ReadAsStreamAsync(timeout.Token))
            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous))
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[131072];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, timeout.Token)) > 0)
                {
                    total += read;
                    if (total > update.Size) throw new InvalidDataException("The server sent more data than the signed launcher.");
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer, 0, read, timeout.Token);
                    progress.Report(new("Downloading launcher " + update.Version, (double)total / update.Size, Step: "download"));
                }
                if (total != update.Size) throw new InvalidDataException("The launcher download ended early.");
                progress.Report(new("Checking the fingerprint", 1, Step: "check"));
                if (Net48.ToHex(hash.GetHashAndReset()).ToLowerInvariant() != update.Sha256)
                    throw new InvalidDataException("The launcher download does not match the signed fingerprint.");
            }
        }
        catch
        {
            if (File.Exists(destination)) File.Delete(destination);
            throw;
        }
    }

    private static void RequireHttps(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidDataException("Online channels and packages must use HTTPS without embedded credentials.");
    }

    private static async Task<HttpResponseMessage> GetHttpsAsync(Uri uri, CancellationToken cancellationToken, HttpClient? client = null)
    {
        for (var redirects = 0; redirects <= 5; redirects++)
        {
            RequireHttps(uri);
            var response = await (client ?? Http).GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var status = (int)response.StatusCode;
            if (status is not (301 or 302 or 303 or 307 or 308)) return response;
            var destination = response.Headers.Location;
            response.Dispose();
            if (destination is null) throw new InvalidDataException("The channel server returned a redirect without a destination.");
            uri = destination.IsAbsoluteUri ? destination : new Uri(uri, destination);
        }
        throw new InvalidDataException("The channel server redirected too many times.");
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = new byte[65536];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (output.Length + read > MaximumChannelBytes)
                throw new InvalidDataException("The server response is larger than the supported limit.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }
}
