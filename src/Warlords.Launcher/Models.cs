using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Warlords.Launcher;

public static class Profile
{
    public const string LauncherVersion = "0.5.0";
    public const string DefaultChannelUrl = "https://raw.githubusercontent.com/extelonlabs/warlords-launcher/StratLords/channel.json";
    // Launcher self-update: a manifest signed by the owner's offline key. Only this
    // pinned public key is trusted; it never comes from the feed.
    public const string LauncherUpdateUrl = "https://raw.githubusercontent.com/extelonlabs/warlords-launcher/StratLords/launcher.json";
    public const string LauncherReleasesUrl = "https://github.com/extelonlabs/warlords-launcher/releases/latest";
    public const string LauncherUpdateKeyModulus = "uC5OAKE+v5jiL3qbWviECsMqTT8LFHRRAlQqZojefAz3QGIx1aczF7zCQlypwistOe65kg01/t6ScFHQ/yTymZjcb44f6hf6890U+quMoCMKfixPONxgKStx18vlsYtIxdcSQ229TqnP+c49JPfldQGPDEb0U6JLjoWvQ//i2tDHPKFm43D0WtzVQTZtuWfAiaX3vSxOXZKZ8zpauRhQeST9DPbpQMwnKdEg/P0nRtrTjY5HCkCzkOrW6EMhkXJp6BwN5AD8SusnxowJziYznvuKO502pEd2fAti6qYdNX2pqITFN8GoHF3Y2cDINx0BYKfffV0jQ1aOYxSDY1ONVyyVfw9yamggOt8c6l1E5PTo9Yy6WqMZKT0El0uj8XZMzSTs2ZXUO9TE6ONfhavQ738babtMjnPd4Nnxs1SQKRFBYHNKuCW62PIl9v4kr4Cf4jUD6uZJXgcKLpLnUcCE2rum6Bq1RzsBrG+W+WnUFUiYHvCxgOqJATuCXN1kltgZ";
    public const string Net48DownloadUrl = "https://dotnet.microsoft.com/download/dotnet-framework/net48";
    public const string WebsiteUrl = "https://stratlords.online/";
    public const string DiscordUrl = "https://discord.gg/W6ZEx2DqXF";
    public const string GitHubUrl = "https://github.com/extelonlabs/warlords-launcher";
    public const string Repository = "MB-Warlords/The-Last-Concord";
    // Mod source branch the launcher follows. The feed may still name the previous branch while it moves.
    public const string Branch = "main";
    public const string PreviousBranch = "StratLords";
    public const string ModuleId = "MBWarlords.LastConcord";
    public const string GameVersion = "v1.4.8";
    public const string ArmoryVersion = "v1.0.0.17";
    public static readonly string[] Modules = ["Native", "Multiplayer", "MBWarlords.Armory", ModuleId];
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
}

public sealed record ReleaseFile(string Path, long Size, string Sha256);

public sealed record ReleaseChannel
{
    public int SchemaVersion { get; init; }
    public string Branch { get; init; } = "";
    public string SourceRepository { get; init; } = "";
    public string SourceCommit { get; init; } = "";
    public string Version { get; init; } = "";
    public string RequiredGameVersion { get; init; } = "";
    public string RequiredArmoryVersion { get; init; } = "";
    public string BuildId { get; init; } = "";
    public DateTimeOffset CreatedUtc { get; init; }
    public DateTimeOffset? ExpiresUtc { get; init; }
    public string Package { get; init; } = "";
    public string PackageSha256 { get; init; } = "";
    public long PackageSize { get; init; }
    public List<ReleaseFile> Files { get; init; } = [];
}

public sealed record ChannelSource(ReleaseChannel Release, string Location);

// launcher.json: the exact manifest bytes (base64) and their RSA SHA-256 signature.
public sealed record LauncherUpdateEnvelope(string Manifest, string Signature);
public sealed record LauncherUpdate(int SchemaVersion, string Version, string Url, long Size, string Sha256);
public sealed record ReleaseChangelog
{
    public int SchemaVersion { get; init; }
    public string SourceCommit { get; init; } = "";
    public string Version { get; init; } = "";
    public List<ChangelogEntry> Entries { get; init; } = [];
}
public sealed record ChangelogEntry
{
    public string Version { get; init; } = "";
    public string Date { get; init; } = "";
    public List<ChangelogSection> Sections { get; init; } = [];
}
public sealed record ChangelogSection
{
    public string Title { get; init; } = "";
    public List<string> Items { get; init; } = [];
}
public sealed record OperationProgress(string Message, double? Fraction = null, long? DoneBytes = null, long? TotalBytes = null);
public sealed record GameInspection(string GameRoot, string? InstalledVersion, bool CanPlay, IReadOnlyList<string> Messages)
{
    public bool GameFound { get; init; }
    public bool GameReady { get; init; }
    public bool ArmoryReady { get; init; }
    public bool WarlordsNeedsManualAttention { get; init; }
    public bool CanInstall => GameReady && ArmoryReady && !WarlordsNeedsManualAttention;
    public IReadOnlyList<string> GameMessages { get; init; } = [];
    public IReadOnlyList<string> ArmoryMessages { get; init; } = [];
    public IReadOnlyList<string> WarlordsMessages { get; init; } = [];
    // Full folder paths of Warlords copies that Steam Workshop installed for Bannerlord.
    public IReadOnlyList<string> OldWorkshopCopies { get; init; } = [];
}
public sealed record InstallResult(string ModulePath, string? BackupPath);
// Channel is ignored. It stays because older settings files have it and unknown fields are refused.
public sealed record LauncherPreferences(string GameRoot = "", string Channel = "");
