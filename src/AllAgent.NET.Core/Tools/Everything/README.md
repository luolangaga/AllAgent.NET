# Bundled Windows filename search

Unmodified upstream binaries from https://www.voidtools.com/downloads/:

- Everything 1.4.1.1032: `Everything-1.4.1.1032.{x64,x86,ARM64}.zip`.
- ES 1.1.0.38: `ES-1.1.0.38.{x64,x86,ARM64}.zip`.
- The ARM64 engine is renamed from `EverythingARM64.exe` to `Everything.exe`.

Both upstream licenses (including Everything's PCRE notices) and SHA-256 hashes
are included alongside the binaries. ES source: https://github.com/voidtools/ES.
Archives are downloaded during maintenance, never at build time or first use.

Core content items copy `everything/` into dependent hosts, publish outputs and
NuGet content. The host automatically launches a private hidden folder indexer
with no automatic volume indexing, service installation, network servers or
elevation. It refreshes the directory on worker startup to discover changes made
while stopped, then reuses the live index. The host shuts down its own instance
on normal exit or scope changes. It never exits an installed Everything instance.
