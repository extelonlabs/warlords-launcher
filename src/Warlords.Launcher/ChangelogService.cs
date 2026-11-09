using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Warlords.Launcher;

/// <summary>Reads optional, build-specific notes as bounded plain text.</summary>
public sealed class ChangelogService
{
    private const int MaximumBytes = 256 * 1024;
    private static readonly string[] Categories = ["New", "Adjusted", "Fixed", "Removed", "Disabled", "Activated"];
    private static readonly Regex Commit = new("^[0-9a-f]{40}$", RegexOptions.CultureInvariant);
    private static readonly Regex ReleaseVersion = new("^[0-9]+\\.[0-9]+\\.[0-9]+$", RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions Json = new(Profile.Json) { MaxDepth = 12 };
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Warlords-Release-Notes/" + Profile.LauncherVersion);
        client.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        return client;
    }

    public Task<string> LoadAsync(ChannelSource source, CancellationToken cancellationToken)
        // Local metadata/path checks can involve a slow user-selected disk too.
        // Keep all optional-note I/O and parsing off the window's dispatcher.
        => Task.Run(() => LoadCoreAsync(source, cancellationToken), cancellationToken);

    private static async Task<string> LoadCoreAsync(ChannelSource source, CancellationToken cancellationToken)
    {
        var release = source.Release;
        if (!Commit.IsMatch(release.SourceCommit) || !ValidVersion(release.Version))
            throw new InvalidDataException("The current build identity is invalid.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var token = deadline.Token;
        byte[] bytes;
        if (Uri.TryCreate(source.Location, UriKind.Absolute, out var location) && !location.IsFile)
        {
            RequireHttps(location);
            // Deriving a new relative path deliberately drops the channel query.
            // Signed channel parameters must not be forwarded to another endpoint.
            var address = new Uri(location, "changelogs/" + release.SourceCommit + ".json");
            var request = new UriBuilder(address) { Query = "warlords=" + Guid.NewGuid().ToString("N") };
            using var response = await GetHttpsAsync(request.Uri, token).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                throw new FileNotFoundException("Release notes are not available for this build.");
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaximumBytes)
                throw new InvalidDataException("Release notes exceed the supported limit.");
            using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            bytes = await ReadBoundedAsync(stream, token).ConfigureAwait(false);
        }
        else
        {
            var channelPath = Path.GetFullPath(location?.IsFile == true ? location.LocalPath : source.Location);
            var directory = Path.GetDirectoryName(channelPath) ?? throw new InvalidDataException("The local channel directory is unavailable.");
            var path = Path.Combine(directory, "changelogs", release.SourceCommit + ".json");
            SafeFiles.NoLinks(path);
            var file = new FileInfo(path);
            if (!file.Exists) throw new FileNotFoundException("Release notes are not available for this local build.");
            if (file.Length > MaximumBytes) throw new InvalidDataException("Release notes exceed the supported limit.");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16384,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            bytes = await ReadBoundedAsync(stream, token).ConfigureAwait(false);
        }

        token.ThrowIfCancellationRequested();
        var feed = JsonSerializer.Deserialize<ReleaseChangelog>(bytes, Json)
            ?? throw new InvalidDataException("Release notes are empty.");
        Validate(feed, release);
        return Format(feed);
    }

    private static void Validate(ReleaseChangelog feed, ReleaseChannel release)
    {
        if (feed.SchemaVersion != 1 || feed.SourceCommit != release.SourceCommit || feed.Version != release.Version
            || feed.Entries is not { Count: > 0 and <= 8 })
            throw new InvalidDataException("Release notes do not match the current build.");
        var currentVersion = Version.Parse(release.Version);
        foreach (var entry in feed.Entries)
        {
            if (entry is null || !ValidVersion(entry.Version) || Version.Parse(entry.Version) > currentVersion
                || entry.Date is not { Length: 10 }
                || !DateTime.TryParseExact(entry.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
                || entry.Sections is not { Count: > 0 and <= 6 })
                throw new InvalidDataException("Release notes contain an invalid release entry.");
            foreach (var section in entry.Sections)
            {
                if (section is null || !Categories.Contains(section.Title, StringComparer.Ordinal)
                    || section.Items is not { Count: > 0 and <= 20 }
                    || section.Items.Any(item => !PlainText(item, 1400)))
                    throw new InvalidDataException("Release notes contain an invalid section.");
            }
        }
    }

    private static bool ValidVersion(string? value) => value is { Length: > 0 and <= 40 }
        && ReleaseVersion.IsMatch(value) && Version.TryParse(value, out _);

    private static bool PlainText(string? value, int maximum) => !string.IsNullOrWhiteSpace(value)
        && value!.Length <= maximum && !value.Any(char.IsControl);

    private static string Format(ReleaseChangelog feed)
    {
        var text = new StringBuilder();
        foreach (var entry in feed.Entries)
        {
            if (text.Length != 0) text.AppendLine().AppendLine();
            var date = DateTime.ParseExact(entry.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            text.Append("Warlords ").Append(entry.Version).Append(" · ")
                .AppendLine(date.ToString("d MMM yyyy", CultureInfo.CurrentCulture));
            foreach (var section in entry.Sections)
            {
                text.AppendLine().Append(section.Title).AppendLine(":");
                foreach (var item in section.Items) text.Append("• ").AppendLine(item);
            }
        }
        return text.ToString().TrimEnd();
    }

    private static void RequireHttps(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidDataException("Online release notes must use HTTPS without embedded credentials.");
    }

    private static async Task<HttpResponseMessage> GetHttpsAsync(Uri uri, CancellationToken token)
    {
        for (var redirects = 0; ; redirects++)
        {
            RequireHttps(uri);
            var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) return response;
            var destination = response.Headers.Location;
            response.Dispose();
            if (redirects >= 5 || destination is null)
                throw new InvalidDataException("Release notes could not follow a safe redirect.");
            uri = destination.IsAbsoluteUri ? destination : new Uri(uri, destination);
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, CancellationToken token)
    {
        using var result = new MemoryStream();
        var buffer = new byte[16384];
        int read;
        while ((read = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            if (result.Length + read > MaximumBytes) throw new InvalidDataException("Release notes exceed the supported limit.");
            result.Write(buffer, 0, read);
        }
        return result.ToArray();
    }
}
