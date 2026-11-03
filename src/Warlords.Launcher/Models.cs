using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Warlords.Launcher;

public static class Profile
{
    public const string LauncherVersion = "0.4.0";
    public const string DefaultChannelUrl = "https://raw.githubusercontent.com/extelonlabs/warlords-launcher/StratLords/channel.json";
    public const string Repository = "MB-Warlords/The-Last-Concord";
    public const string Branch = "StratLords";
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
public sealed record OperationProgress(string Message, double? Fraction = null);
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
}
public sealed record InstallResult(string ModulePath, string? BackupPath);
public sealed record LauncherPreferences(string GameRoot = "", string Channel = "");
