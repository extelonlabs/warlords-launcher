using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Warlords.Launcher;

public sealed class ChannelService
{
    private const int MaximumChannelBytes = 8 * 1024 * 1024;
    private static readonly HttpClient Http = CreateClient();

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
            requestUri.Query = (string.IsNullOrEmpty(uri.Query) ? "" : uri.Query[1..] + "&")
                + "warlords=" + Guid.NewGuid().ToString("N");
            using var response = await GetHttpsAsync(requestUri.Uri, cancellationToken);
            response.EnsureSuccessStatusCode();
            RequireHttps(response.RequestMessage!.RequestUri!);
            if (response.Content.Headers.ContentLength > MaximumChannelBytes)
                throw new InvalidDataException("The update channel is larger than the supported limit.");
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
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
            await using var stream = File.OpenRead(location);
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

    public async Task<string> ReadBranchHeadAsync(CancellationToken cancellationToken)
    {
        using var response = await GetHttpsAsync(new Uri(
            $"https://api.github.com/repos/{Profile.Repository}/git/ref/heads/{Profile.Branch}"), cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = JsonDocument.Parse(await ReadBoundedAsync(stream, cancellationToken));
        var sha = document.RootElement.GetProperty("object").GetProperty("sha").GetString() ?? "";
        if (sha.Length != 40 || !System.Text.RegularExpressions.Regex.IsMatch(sha, "^[0-9a-f]{40}$"))
            throw new InvalidDataException("GitHub returned an invalid branch identity.");
        return sha;
    }

    private static void RequireHttps(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidDataException("Online channels and packages must use HTTPS without embedded credentials.");
    }

    private static async Task<HttpResponseMessage> GetHttpsAsync(Uri uri, CancellationToken cancellationToken)
    {
        for (var redirects = 0; redirects <= 5; redirects++)
        {
            RequireHttps(uri);
            var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
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
