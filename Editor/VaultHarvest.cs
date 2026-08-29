using System;
using System.Collections.Generic;
using System.IO;

namespace CupkekGames.AssetVaults.Editor
{
  /// <summary>
  /// Copies the handful of files a project actually uses out of a folder that is
  /// about to be vaulted.
  ///
  /// <para>This is what makes a 3 GB pack with one referenced texture tractable:
  /// the one file stays in <c>Assets</c> and is committed normally, the other
  /// 2411 go to the vault. It only works because the <c>.meta</c> travels with
  /// the file - the GUID is what every material, prefab and scene points at, so
  /// a copy that regenerates it silently breaks every one of those references
  /// while looking perfectly fine on disk.</para>
  /// </summary>
  public static class VaultHarvest
  {
    /// <summary>
    /// Copy <paramref name="files"/> (project-relative) into
    /// <paramref name="destinationDir"/>, keeping their layout below
    /// <paramref name="packDir"/> and carrying each <c>.meta</c>.
    /// </summary>
    /// <returns>The project-relative destination paths.</returns>
    public static IReadOnlyList<string> Copy(string projectRoot, string packDir,
      IReadOnlyList<string> files, string destinationDir)
    {
      if (files == null || files.Count == 0)
      {
        throw new VaultException("Nothing to copy: no used files were reported.");
      }

      string packFull = Path.GetFullPath(packDir).TrimEnd(Path.DirectorySeparatorChar);
      string destFull = Path.GetFullPath(destinationDir).TrimEnd(Path.DirectorySeparatorChar);
      string root = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar);

      string assets = Path.Combine(root, "Assets") + Path.DirectorySeparatorChar;
      if (!(destFull + Path.DirectorySeparatorChar).StartsWith(assets,
            StringComparison.OrdinalIgnoreCase))
      {
        throw new VaultException(
          "Harvested files have to land inside Assets - the whole point is that the "
          + "project keeps using them.");
      }

      if ((destFull + Path.DirectorySeparatorChar).StartsWith(
            packFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
      {
        throw new VaultException(
          "The destination is inside the pack being harvested, so the copies would "
          + "leave with it.");
      }

      var written = new List<string>(files.Count);
      foreach (string relative in files)
      {
        string source = Path.GetFullPath(Path.Combine(root,
          relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!File.Exists(source))
        {
          throw new VaultException($"Reported as used but not on disk: {relative}");
        }

        if (!source.StartsWith(packFull + Path.DirectorySeparatorChar,
              StringComparison.OrdinalIgnoreCase))
        {
          throw new VaultException($"'{relative}' is not inside the pack being harvested.");
        }

        string tail = source.Substring(packFull.Length + 1);
        string target = Path.Combine(destFull, tail);
        Directory.CreateDirectory(Path.GetDirectoryName(target));

        // Refuse rather than overwrite: two files sharing one GUID is a state
        // Unity resolves arbitrarily, and the loser's references break.
        if (File.Exists(target))
        {
          throw new VaultException(
            $"'{tail}' already exists at the destination. Move or delete it first; "
            + "overwriting could leave two assets claiming one GUID.");
        }

        File.Copy(source, target);
        string meta = source + ".meta";
        if (File.Exists(meta))
        {
          File.Copy(meta, target + ".meta");
        }

        written.Add(target.Substring(root.Length + 1).Replace('\\', '/'));
      }

      return written;
    }
  }
}
