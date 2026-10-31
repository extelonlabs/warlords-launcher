# Warlords launcher

Open-source Windows launcher for the small StratLords testing group. The original
launcher app is available under the [MIT license](LICENSE). Current source
version: **0.3.2**.

[Download Warlords.exe](https://github.com/extelonlabs/warlords-launcher/releases/latest/download/Warlords.exe).
Players do not need GitHub accounts or the .NET SDK to run the self-contained
download.

## Play

Requires Windows x64, Steam Bannerlord **v1.4.8**, and Warlords Armory exactly
**v1.0.0.17** (Steam Workshop item 3712562953). Steam/TaleWorlds authentication
and normal server admission remain required. This launcher does not grant access.

Run Warlords.exe and follow the main button: choose your game folder if needed,
finish the Armory requirement, then install Warlords once. The launcher shows
Play when setup is complete. Later launches automatically update a module
already managed by the launcher. It checks again before Play. The launcher
downloads compiled StratLords builds from the public channel:

`https://raw.githubusercontent.com/extelonlabs/warlords-launcher/StratLords/channel.json`

Open **What's new** to read player-facing changes for the selected build.
Release notes load separately when opened; unavailable notes do not block Play
or updates. The publisher supplies notes from the exact source revision of the
compiled mod, rather than exposing the private game repository.

It validates the package and release-file SHA-256 hashes, stages the update,
preserves player options and retains a recovery backup. Failed or expired
channels block Play instead of silently selecting an old package. Native,
Armory and other game modules are not installed by this updater.

## Build the launcher

On Windows with the **.NET 10 SDK** installed, run from this repository root:

```powershell
./src/Warlords.Launcher/Build.ps1
```

Output: `artifacts/launcher/Warlords.exe`. Publishing creates a self-contained
Windows x64 executable. The SDK may restore Microsoft's framework/runtime packs
on the first build; the app declares no third-party NuGet packages. No game SDK,
private mod source, owner settings or credentials are needed to compile this app.

This repository contains the launcher app and public release metadata. The
private StratLords game source, build runner, feed publisher, server operations,
licensed game resources and owner configuration are outside this source export.
The game, mod payloads and framework components retain their respective licenses.
The bundled Warlords crest and icon retain their original artwork rights;
the MIT code license does not relicense the name or logo. See
[branding notice](src/Warlords.Launcher/Assets/NOTICE.md).

## Security limits

The current executable and update catalog are **unsigned testing artifacts**.
HTTPS and SHA-256 check integrity relative to the downloaded catalog; they do not
independently authenticate the publisher. Someone who controls the distribution
account or catalog could replace both a payload and its advertised hashes.
Install updates only from a publisher you trust, and review the source if you
build your own launcher. Open source is not a security guarantee.
Choosing a different channel grants that channel the same update authority.
The app does not independently pin a minimum release version; expiry is
enforced when supplied by the channel. The default feed supplies an expiry.

See [SECURITY.md](SECURITY.md) for private vulnerability reporting and
[SOURCE-EXPORT.md](SOURCE-EXPORT.md) for the exact publication boundary.
