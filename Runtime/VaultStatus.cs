using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace CupkekGames.AssetVaults
{
  public enum PackState
  {
    /// <summary>Folder is not on disk. Pull it.</summary>
    Missing,

    /// <summary>On disk, never pushed, so the vault has no copy. Push it.</summary>
    LocalOnly,

    /// <summary>On disk and matching what the manifest recorded.</summary>
    Present,

    /// <summary>On disk but the file count differs from the manifest.</summary>
    Modified,

    /// <summary>Contents verified against the recorded GUID digest.</summary>
    Verified,

    /// <summary>Files are there but the .meta GUIDs are not the ones recorded.</summary>
    GuidDrift,
  }

  public readonly struct PackStats
  {
    public readonly int FileCount;
    public readonly long Bytes;
    public readonly string GuidHash;

    public PackStats(int fileCount, long bytes, string guidHash)
    {
      FileCount = fileCount;
      Bytes = bytes;
      GuidHash = guidHash;
    }
  }

  public static class VaultStatus
  {
    /// <summary>
    /// Cheap by default: counts and sizes only, no hashing. Hashing a multi-GB
    /// pack every time a window repaints is not acceptable, so
    /// <paramref name="includeGuidHash"/> is opt-in and drives the Verify action.
    /// </summary>
    public static PackStats Inspect(string dir, bool includeGuidHash)
    {
      if (!Directory.Exists(dir))
      {
        return new PackStats(0, 0L, string.Empty);
      }

      int count = 0;
      long bytes = 0L;
      foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
      {
        count++;
        bytes += new FileInfo(file).Length;
      }

      string hash = includeGuidHash ? ComputeGuidDigest(dir) : string.Empty;
      return new PackStats(count, bytes, hash);
    }

    /// <summary>
    /// SHA256 over the pack's sorted .meta GUIDs.
    ///
    /// <para>This is the Unity-specific safety net and the reason an archive
    /// checksum alone is not enough. A checksum proves the bytes arrived; only
    /// this proves the <c>.meta</c> files arrived with them. A restore that
    /// dropped metas gives Unity fresh GUIDs and every material, prefab and
    /// scene reference to that pack silently breaks.</para>
    /// </summary>
    public static string ComputeGuidDigest(string dir)
    {
      if (!Directory.Exists(dir))
      {
        return string.Empty;
      }

      var guids = new List<string>();
      foreach (string meta in Directory.EnumerateFiles(dir, "*.meta", SearchOption.AllDirectories))
      {
        string guid = ReadGuid(meta);
        if (!string.IsNullOrEmpty(guid))
        {
          guids.Add(guid);
        }
      }

      guids.Sort(StringComparer.Ordinal);

      using (var sha = SHA256.Create())
      {
        byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", guids)));
        var sb = new StringBuilder(digest.Length * 2);
        foreach (byte b in digest)
        {
          sb.Append(b.ToString("x2"));
        }

        return sb.ToString();
      }
    }

    // StreamReader with explicit disposal rather than File.ReadLines(): that
    // returns a LAZY enumerator, so breaking out after finding the guid line
    // leaks the handle until GC. Across a 2400-file pack that is thousands of
    // live handles and the folder then refuses to delete. Found the hard way in
    // the V0 PowerShell port.
    private static string ReadGuid(string metaPath)
    {
      using (var reader = new StreamReader(metaPath))
      {
        string line;
        while ((line = reader.ReadLine()) != null)
        {
          if (line.StartsWith("guid:", StringComparison.Ordinal))
          {
            return line.Substring(5).Trim();
          }
        }
      }

      return null;
    }

    public static PackState Evaluate(VaultPack pack, string dir, bool verified, PackStats stats)
    {
      if (!Directory.Exists(dir))
      {
        return PackState.Missing;
      }

      if (!pack.HasBeenPushed)
      {
        return PackState.LocalOnly;
      }

      if (stats.FileCount != pack.fileCount)
      {
        return PackState.Modified;
      }

      if (!verified)
      {
        return PackState.Present;
      }

      return stats.GuidHash == pack.guidHash ? PackState.Verified : PackState.GuidDrift;
    }

    public static string Describe(long bytes)
    {
      if (bytes >= 1L << 30) return $"{bytes / (double)(1L << 30):N2} GB";
      if (bytes >= 1L << 20) return $"{bytes / (double)(1L << 20):N1} MB";
      if (bytes >= 1L << 10) return $"{bytes / (double)(1L << 10):N0} KB";
      return $"{bytes} B";
    }
  }
}
