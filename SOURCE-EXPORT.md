# Public launcher source boundary

This snapshot contains only the original Warlords launcher app, a standalone
launcher build script, its license and public documentation. The app files are
copied byte-for-byte from the maintained launcher project. The standalone build
script uses repository-local output and has no workspace/producer dependencies.

An explicit filename allowlist excludes the private feed publisher, package
producer, owner configuration, credentials, operational receipts, game resources,
other repositories, generated binaries and build output. Every filename equal
to AGENTS.md is excluded case-insensitively at every depth. Links and reparse
points are refused. No private game repository was exported.

`source-manifest.json` records SHA-256 and byte length for every exported file
except the manifest itself. It is deterministic for a given source snapshot and
documentation; it includes no owner filesystem paths, timestamps or credentials.
This manifest describes the source snapshot, not a publisher signature or proof
that a prebuilt executable was reproduced from it.

The MIT license covers the original launcher source. Bannerlord, Armory,
downloaded mod payloads and framework components are not relicensed by it.
