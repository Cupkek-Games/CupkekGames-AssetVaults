using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace CupkekGames.AssetVaults
{
  /// <summary>
  /// What a pack is, for the reader. Documentation only - the vault sits outside
  /// <c>Assets</c>, so nothing in it can affect the build and there is nothing
  /// left to refuse. (A <c>code</c> kind used to exist for exactly that refusal.)
  /// </summary>
  public static class VaultPackKind
  {
    public const string Art = "art";
    public const string Source = "source";
  }

  [Serializable]
  public class VaultPack
  {
    public string id;
    public string path;
    public string kind;
    public string version;
    public string archive;
    public string source;
    public string note;

    // Recorded by a successful push; all four are checked on pull.
    public long bytes;
    public string sha256;
    public int fileCount;
    public string guidHash;

    /// <summary>A pack that has never been pushed has nothing to pull.</summary>
    public bool HasBeenPushed => !string.IsNullOrEmpty(sha256);
  }

  /// <summary>
  /// The vault manifest, read from and written to JSON.
  ///
  /// <para>JSON rather than a ScriptableObject for one decisive reason: a fresh
  /// clone must be able to pull packs BEFORE Unity has ever opened the project,
  /// and the PowerShell scripts in <c>scripts/</c> read this same file. One
  /// source of truth, two consumers.</para>
  ///
  /// <para><c>comment</c> is a real field so that round-tripping through
  /// JsonUtility does not silently delete the human explanation at the top of
  /// the file.</para>
  /// </summary>
  [Serializable]
  public class VaultManifest
  {
    public string comment;
    public string remote;
    public List<VaultPack> packs = new List<VaultPack>();

    /// <summary>
    /// Where vaulted packs live: <b>inside</b> <c>Assets</c>, because the whole
    /// point is that Unity keeps using them normally.
    ///
    /// <para>The vault is about <b>git size</b>, not import cost. A vaulted pack
    /// is imported, referenced by materials and prefabs, and works exactly like
    /// any other content - it simply is not in the repository. Git ignores it,
    /// Drive holds it, and a machine that needs it downloads it once.</para>
    ///
    /// <para>A brief detour had this folder beside <c>Assets</c> instead. That
    /// removed the import cost and, with it, the entire point: Unity could not
    /// see the content, so nothing could reference it and every pack had to be
    /// harvested before it could be used.</para>
    /// </summary>
    public const string VaultRoot = "Assets/Vault";

    public const string DefaultRelativePath = "Assets/Vault/vault.json";

    /// <summary>The vault root as an absolute path, separators normalised.</summary>
    public static string ResolveVaultRoot(string projectRoot)
      => Path.Combine(Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar),
        VaultRoot.Replace('/', Path.DirectorySeparatorChar));

    public static string ResolvePath(string projectRoot)
      => Path.Combine(projectRoot, DefaultRelativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// A pack's folder on disk, proved to be inside <see cref="VaultRoot"/>.
    ///
    /// <para>The guard is not paranoia. <c>path</c> comes from a JSON file
    /// people edit by hand, and a download deletes the target folder before
    /// replacing it - so <c>"path": "../Assets"</c>, or an absolute path, or a
    /// typo that resolves upward, would delete something that was never part of
    /// the vault. Every caller that is about to touch a pack's folder resolves
    /// it through here.</para>
    /// </summary>
    public static string ResolvePackDirectory(string projectRoot, VaultPack pack)
    {
      if (pack == null || string.IsNullOrWhiteSpace(pack.path))
      {
        throw new VaultException("A pack in the manifest has no path.");
      }

      string root = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar);
      string vault = ResolveVaultRoot(projectRoot) + Path.DirectorySeparatorChar;
      string full = Path.GetFullPath(Path.Combine(root,
        pack.path.Replace('/', Path.DirectorySeparatorChar)));

      if (!full.TrimEnd(Path.DirectorySeparatorChar).StartsWith(vault,
            StringComparison.OrdinalIgnoreCase))
      {
        throw new VaultException(
          $"Pack '{pack.id}' points at '{pack.path}', which is outside {VaultRoot}/. "
          + "Refusing to touch it: fix the path in the manifest.");
      }

      return full;
    }

    public static VaultManifest Load(string fullPath)
    {
      if (!File.Exists(fullPath))
      {
        throw new VaultException($"Vault manifest not found at {fullPath}");
      }

      var manifest = JsonUtility.FromJson<VaultManifest>(File.ReadAllText(fullPath));
      if (manifest == null)
      {
        throw new VaultException($"Vault manifest at {fullPath} is not valid JSON.");
      }

      manifest.packs ??= new List<VaultPack>();
      return manifest;
    }

    public void Save(string fullPath)
    {
      // Trailing newline because this file lives in git and JsonUtility does not
      // write one, so every save otherwise shows up as "\ No newline at end of
      // file" against whatever a human or another tool last wrote.
      File.WriteAllText(fullPath, JsonUtility.ToJson(this, true) + "\n");
    }

    public VaultPack Find(string id)
    {
      foreach (VaultPack pack in packs)
      {
        if (string.Equals(pack.id, id, StringComparison.OrdinalIgnoreCase))
        {
          return pack;
        }
      }

      return null;
    }

    /// <summary>
    /// Register a folder as a vault pack.
    ///
    /// <para>The rules live here rather than in the window so every caller gets
    /// them: inside <see cref="VaultRoot"/>, and not already managed.</para>
    ///
    /// <para>A vaulted pack stays a normal part of the project: Unity imports
    /// it, materials and prefabs reference it, and it behaves like anything else
    /// in <c>Assets</c>. What changes is only that git does not carry it. So the
    /// archive must preserve every <c>.meta</c>, because those GUIDs are what
    /// every reference into the pack resolves through - see
    /// <c>ArchiveBuilder</c>.</para>
    ///
    /// <para>There is no refusal for packs containing C#. Downloading is a setup
    /// step here (owner decision, 2026-08-29): a fresh clone is not expected to
    /// compile until its vaulted packs arrive, which is what makes whole-folder
    /// vaulting possible at all.</para>
    /// </summary>
    public VaultPack Add(string projectRoot, string absoluteFolder)
    {
      if (!Directory.Exists(absoluteFolder))
      {
        throw new VaultException($"There is no folder at {absoluteFolder}");
      }

      string root = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar);
      string vault = ResolveVaultRoot(projectRoot) + Path.DirectorySeparatorChar;
      string full = Path.GetFullPath(absoluteFolder).TrimEnd(Path.DirectorySeparatorChar);
      if (!full.StartsWith(vault, StringComparison.OrdinalIgnoreCase))
      {
        throw new VaultException(
          $"Only folders inside {VaultRoot}/ can go in the vault. Move the folder there "
          + "first - Unity keeps using it from that location, it just stops being in git.");
      }

      string relative = full.Substring(root.Length + 1).Replace('\\', '/');
      foreach (VaultPack existing in packs)
      {
        if (string.Equals(existing.path, relative, StringComparison.OrdinalIgnoreCase))
        {
          throw new VaultException($"'{relative}' is already in the vault as '{existing.id}'.");
        }
      }

      string id = Slug(new DirectoryInfo(full).Name);
      if (id.Length == 0)
      {
        throw new VaultException(
          $"Cannot make a name for '{relative}'. Give the folder letters or digits in its name.");
      }

      if (Find(id) != null)
      {
        throw new VaultException($"A pack called '{id}' is already in the vault.");
      }

      var pack = new VaultPack
      {
        id = id,
        path = relative,
        kind = VaultPackKind.Art,
        version = "1.0.0",
        archive = $"{id}-1.0.0.zip",
        source = string.Empty,
        note = string.Empty,
      };

      packs.Add(pack);
      return pack;
    }

    /// <summary>
    /// Folder name to pack id: lowercase, punctuation and case boundaries to
    /// dashes. "Fantasy Skybox" and "FantasySkybox" both give "fantasy-skybox",
    /// so a generated id matches the convention already in the manifest instead
    /// of running the words together.
    /// </summary>
    private static string Slug(string name)
    {
      var sb = new StringBuilder(name.Length + 4);
      for (int i = 0; i < name.Length; i++)
      {
        char c = name[i];
        if (!char.IsLetterOrDigit(c))
        {
          if (sb.Length > 0 && sb[sb.Length - 1] != '-') sb.Append('-');
          continue;
        }

        // A capital starts a new word after a lowercase run ("VaultTest"), and
        // at the end of an acronym run ("URPAssets" -> urp-assets).
        bool boundary = char.IsUpper(c) && i > 0 && char.IsLetterOrDigit(name[i - 1])
          && (!char.IsUpper(name[i - 1])
              || (i + 1 < name.Length && char.IsLower(name[i + 1])));
        if (boundary && sb.Length > 0 && sb[sb.Length - 1] != '-') sb.Append('-');

        sb.Append(char.ToLowerInvariant(c));
      }

      return sb.ToString().Trim('-');
    }

    /// <summary>
    /// Find a pack, or throw. Callers get an exception rather than a null they
    /// might ignore and act on.
    /// </summary>
    public VaultPack FindVaultable(string id)
    {
      VaultPack pack = Find(id);
      if (pack == null)
      {
        throw new VaultException($"No pack '{id}' in the manifest.");
      }

      return pack;
    }
  }
}
