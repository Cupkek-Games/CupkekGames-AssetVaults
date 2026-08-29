# CupkekGames Asset Vaults

Keeps large reference and source material out of git, without pretending it does not exist.

Art packs bought for later, source paintings a tool reads, texture libraries you use three files from: they bloat a repository and slow every clone, but deleting them loses work. This package stores them in a `Vault/` folder beside `Assets/`, backs each pack up to cloud storage as one archive, and restores it on demand with its `.meta` GUIDs intact.

## The one idea worth understanding

**`Vault/` sits next to `Assets/`, not inside it.** Unity never imports that folder, so:

- it costs no import time and adds no AssetDatabase entries
- nothing in the project can reference a vaulted file, so nothing breaks when one leaves
- a stray `.cs` in there is inert text, not a compile error

`Assets/ThirdParty` (or wherever your used packs live) is the opposite and stays that way. The vault is for what you keep, not what you ship. The moment you want a file in the game, you copy it into `Assets/` where it is tracked normally.

## What's inside

**Runtime** (`CupkekGames.AssetVaults.asmdef`) — no editor dependency, so the whole model is usable from a script or a test.

- `VaultManifest` / `VaultPack` — `Vault/vault.json`, the record of which packs exist and what was last uploaded. JSON rather than a ScriptableObject so a fresh clone can restore packs before Unity has ever opened the project.
- `VaultManifest.Add` — registers a folder, proving it lives inside `Vault/`.
- `VaultManifest.ResolvePackDirectory` — a pack's folder, proven to be inside the vault. Every caller that is about to delete or replace a pack goes through this: `path` comes from a hand-edited file, and a `../` would otherwise take out something that was never part of the vault.
- `VaultStatus` — cheap inspection (counts, sizes) plus `ComputeGuidDigest`, a SHA256 over the pack's sorted `.meta` GUIDs. An archive checksum proves the bytes arrived; only this proves the `.meta` files came with them.
- `ArchiveBuilder` — one zip per pack, carrying the folder's own sibling `.meta`. Extracts to staging and moves into place only on success, so a cancelled restore cannot leave a half-populated folder that later reads as healthy.
- `VaultDiscovery` — folders in `Vault/` the manifest does not know about yet.
- `IVaultBackend` — four operations, and nothing above the interface may learn that a transfer involves OAuth, chunking or a subprocess.

**Editor** (`CupkekGames.AssetVaults.Editor.asmdef`)

- `AssetVaultWindow` — **Tools > CupkekGames > Asset Vault**. Per pack, two independent facts: is it on this PC, is it backed up. Buttons fix whichever is missing.
- `VaultBackends` — where storage backends register themselves.
- `VaultComposition` — project paths and the active backend.

## Storage backends

This package has none, on purpose. It installs and runs alone: the window opens, reports that no storage is installed, and remains a useful list of what is on disk.

A backend package references this one and registers itself from its own `[InitializeOnLoad]`:

```csharp
[InitializeOnLoad]
internal static class MyBackendRegistration
{
    static MyBackendRegistration()
    {
        VaultBackends.Register(new VaultBackendRegistration
        {
            Id = "mystorage",
            DisplayName = "My Storage",
            CreateBackend = projectRoot => new MyBackend(projectRoot),
            FindSettingsAsset = () => MySettings.Find(),
            CreateSettingsAsset = MySettings.Create,
        });
    }
}
```

Explicit registration, not a type scan. The arrow runs core ← backend, which is what lets either ship alone.

See `com.cupkekgames.assetvaults.googledrive` for a complete implementation.

## The rule that prevents data loss

**Never remove a pack from git until a verified round trip has succeeded.** Between `git rm --cached` and a working download, the only copy of those bytes is one local disk. So: upload, download into a temp directory, check the size, sha256, file count and GUID digest all match, and only then add the `.gitignore` entry. Upload prints the exact ignore line and `git rm` command; it does not run them.

## Dependencies

None.

## Installation

Install via the CupkekGames UPM scoped registry (`https://www.docs.cupkek.games/upm`), or as a local `file:` path during development.
