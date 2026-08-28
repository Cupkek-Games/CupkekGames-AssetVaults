using System.Collections.Generic;
using System.IO;

namespace CupkekGames.AssetVaults
{
  /// <summary>One folder found under the scan root, with the facts needed to decide about it.</summary>
  public readonly struct VaultCandidate
  {
    public readonly string Name;
    public readonly string RelativePath;
    public readonly string AbsolutePath;
    public readonly int FileCount;
    public readonly long Bytes;
    public readonly int ScriptCount;

    public VaultCandidate(string name, string relativePath, string absolutePath,
      int fileCount, long bytes, int scriptCount)
    {
      Name = name;
      RelativePath = relativePath;
      AbsolutePath = absolutePath;
      FileCount = fileCount;
      Bytes = bytes;
      ScriptCount = scriptCount;
    }

    public bool CanVault => ScriptCount == 0;
  }

  /// <summary>
  /// Finds folders in the vault root that the manifest does not know about yet.
  ///
  /// <para>Putting a folder in <c>Vault/</c> and listing it in the manifest are
  /// two different acts, and this class is the bridge. The folder being there
  /// says Unity should ignore it; the manifest entry says Drive holds a copy.
  /// Listing stays opt-in because between an untracked pack and a verified push
  /// the only copy of those bytes is one local disk.</para>
  ///
  /// <para>The C# count survives from when packs lived under <c>Assets</c>. It
  /// is no longer a refusal - nothing in <c>Vault/</c> compiles - but a pack
  /// full of scripts is a sign someone moved a real dependency out of the
  /// project by mistake, so it is still worth showing.</para>
  /// </summary>
  public static class VaultDiscovery
  {
    /// <summary>The vault root; see <see cref="VaultManifest.VaultRoot"/>.</summary>
    public const string DefaultScanRoot = VaultManifest.VaultRoot;

    /// <summary>
    /// Immediate subfolders of <paramref name="relativeScanRoot"/> that the
    /// manifest does not already know about, cheapest-useful facts attached.
    /// One enumeration pass per folder: this walks every file in the tree, so it
    /// belongs behind an explicit button, never on repaint.
    /// </summary>
    public static IReadOnlyList<VaultCandidate> Scan(string projectRoot, VaultManifest manifest,
      string relativeScanRoot)
    {
      var found = new List<VaultCandidate>();
      string root = Path.Combine(projectRoot,
        relativeScanRoot.Replace('/', Path.DirectorySeparatorChar));
      if (!Directory.Exists(root))
      {
        return found;
      }

      var managed = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
      foreach (VaultPack pack in manifest.packs)
      {
        managed.Add(pack.path);
      }

      foreach (string dir in Directory.GetDirectories(root))
      {
        string relative = relativeScanRoot.TrimEnd('/') + "/" + new DirectoryInfo(dir).Name;
        if (managed.Contains(relative))
        {
          continue;
        }

        int files = 0;
        int scripts = 0;
        long bytes = 0L;
        foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
          files++;
          bytes += new FileInfo(file).Length;
          if (file.EndsWith(".cs", System.StringComparison.OrdinalIgnoreCase))
          {
            scripts++;
          }
        }

        found.Add(new VaultCandidate(new DirectoryInfo(dir).Name, relative, dir,
          files, bytes, scripts));
      }

      // Biggest first: the whole point of the vault is the large ones, and a
      // 3 GB pack should not be below a 200 KB one because of the alphabet.
      found.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));
      return found;
    }
  }
}
