using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace CupkekGames.AssetVaults
{
  /// <summary>
  /// A pack's versions in the vault. An upload whose files changed since the
  /// last one is a new version (the patch number goes up and the archive is
  /// named after it), and once it is safely up, the pack's older archives are
  /// removed: a teammate who downloads an old one gets files the project has
  /// since taken out of the pack, under the same IDs as the copies it now
  /// keeps, so two assets would answer to one ID.
  /// </summary>
  public static class VaultVersions
  {
    /// <summary>The archive a pack's version is stored under: "&lt;id&gt;-&lt;version&gt;.zip".</summary>
    public static string ArchiveName(string id, string version) => $"{id}-{version}.zip";

    /// <summary>"1.0.1" to "1.0.2".</summary>
    public static string NextPatch(string version)
    {
      string[] parts = (version ?? string.Empty).Split('.');
      if (parts.Length != 3
          || !int.TryParse(parts[0], out int major)
          || !int.TryParse(parts[1], out int minor)
          || !int.TryParse(parts[2], out int patch))
      {
        throw new VaultException(
          $"Cannot raise the version '{version}': a pack's version is three numbers, like 1.0.2.");
      }

      return $"{major}.{minor}.{patch + 1}";
    }

    /// <summary>
    /// Readies a pack for an upload: when it has been uploaded before and its
    /// files have changed since (another count or other IDs), its version goes
    /// up and its archive is renamed to match. A pack whose archive has a name
    /// of its own (not "&lt;id&gt;-&lt;version&gt;.zip") keeps it and is overwritten.
    /// Returns whether it moved to a new version.
    /// </summary>
    public static bool PrepareUpload(VaultPack pack, int fileCount, string guidHash)
    {
      if (!pack.HasBeenPushed) return false;
      if (pack.fileCount == fileCount && pack.guidHash == guidHash) return false;
      if (pack.archive != ArchiveName(pack.id, pack.version)) return false;

      pack.version = NextPatch(pack.version);
      pack.archive = ArchiveName(pack.id, pack.version);
      return true;
    }

    /// <summary>
    /// The pack's older archives among what is in the vault: named
    /// "&lt;id&gt;-&lt;version&gt;.zip" for this pack's id, not its current archive,
    /// and not named by any pack in the manifest.
    /// </summary>
    public static List<string> OlderArchives(VaultPack pack, VaultManifest manifest, IEnumerable<string> stored)
    {
      var named = new HashSet<string>();
      foreach (VaultPack other in manifest.packs)
      {
        if (!string.IsNullOrEmpty(other.archive)) named.Add(other.archive);
      }

      var older = new List<string>();
      var ofThisPack = new Regex("^" + Regex.Escape(pack.id) + @"-\d+\.\d+\.\d+\.zip$");
      foreach (string name in stored)
      {
        if (ofThisPack.IsMatch(name) && !named.Contains(name)) older.Add(name);
      }

      return older;
    }
  }
}
