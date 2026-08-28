using System;
using System.IO;
using System.IO.Compression;
using System.Threading;

namespace CupkekGames.AssetVaults
{
  /// <summary>
  /// Packs a folder into one archive and back again.
  ///
  /// <para>One archive per pack rather than syncing loose files: Drive charges an
  /// API round trip per file, so a 2400-file pack would spend minutes in pure
  /// overhead. An archive is also atomic - you either have the pack or you do
  /// not, never half of one.</para>
  /// </summary>
  public static class ArchiveBuilder
  {
    /// <summary>
    /// Zip <paramref name="sourceDir"/>, including the folder's own sibling
    /// <c>.meta</c>, which lives one level up and carries the folder's GUID.
    /// Leaving it out makes Unity mint a new one on restore.
    /// </summary>
    public static void Create(string sourceDir, string destZip,
      IProgress<TransferProgress> progress, CancellationToken ct)
    {
      if (!Directory.Exists(sourceDir))
      {
        throw new VaultException($"Nothing to archive at {sourceDir}");
      }

      if (File.Exists(destZip)) File.Delete(destZip);
      EnsureParentDirectory(destZip);

      string leaf = new DirectoryInfo(sourceDir).Name;
      string[] files = Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories);
      var sizes = new long[files.Length];
      long total = 0L;
      for (int i = 0; i < files.Length; i++)
      {
        sizes[i] = new FileInfo(files[i]).Length;
        total += sizes[i];
      }

      long done = 0L;
      using (var zip = new FileStream(destZip, FileMode.Create, FileAccess.Write))
      using (var archive = new ZipArchive(zip, ZipArchiveMode.Create))
      {
        for (int i = 0; i < files.Length; i++)
        {
          ct.ThrowIfCancellationRequested();
          string relative = files[i].Substring(sourceDir.Length).TrimStart(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
          archive.CreateEntryFromFile(files[i], leaf + "/" + relative.Replace('\\', '/'),
            CompressionLevel.Fastest);

          done += sizes[i];
          if ((i & 63) == 0)
          {
            progress?.Report(new TransferProgress(done, total, $"packing {i + 1}/{files.Length}"));
          }
        }

        string folderMeta = sourceDir.TrimEnd(Path.DirectorySeparatorChar) + ".meta";
        if (File.Exists(folderMeta))
        {
          archive.CreateEntryFromFile(folderMeta, leaf + ".meta", CompressionLevel.Fastest);
        }
      }

      progress?.Report(new TransferProgress(total, total, "packed"));
    }

    /// <summary>
    /// Extract to a temporary place and move into position only on success, so a
    /// failed or cancelled extract can never leave a half-populated folder that
    /// later reads as a healthy pack.
    /// </summary>
    /// <remarks>
    /// <paramref name="targetDir"/> is DELETED before the move. This method
    /// cannot know which directories are legitimate targets, so callers must
    /// resolve the path through <see cref="VaultManifest.ResolvePackDirectory"/>
    /// - which proves it sits inside the vault - and never hand over a path
    /// taken straight from the manifest.
    /// </remarks>
    public static void ExtractInPlace(string zip, string targetDir,
      IProgress<TransferProgress> progress, CancellationToken ct)
    {
      string leaf = new DirectoryInfo(targetDir).Name;
      string staging = Path.Combine(Path.GetTempPath(), "vault-stage-" + Guid.NewGuid().ToString("N"));

      try
      {
        Directory.CreateDirectory(staging);
        progress?.Report(new TransferProgress(0, 0, "unpacking"));
        ZipFile.ExtractToDirectory(zip, staging);
        ct.ThrowIfCancellationRequested();

        string extracted = Path.Combine(staging, leaf);
        if (!Directory.Exists(extracted))
        {
          throw new VaultException($"The archive did not contain a '{leaf}' folder.");
        }

        if (Directory.Exists(targetDir)) Directory.Delete(targetDir, true);
        EnsureParentDirectory(targetDir);
        Directory.Move(extracted, targetDir);

        string stagedMeta = Path.Combine(staging, leaf + ".meta");
        if (File.Exists(stagedMeta))
        {
          File.Copy(stagedMeta, targetDir.TrimEnd(Path.DirectorySeparatorChar) + ".meta", true);
        }

        progress?.Report(new TransferProgress(1, 1, "unpacked"));
      }
      finally
      {
        if (Directory.Exists(staging))
        {
          try { Directory.Delete(staging, true); } catch (IOException) { }
        }
      }
    }

    private static void EnsureParentDirectory(string path)
    {
      string parent = Path.GetDirectoryName(path);
      if (!string.IsNullOrEmpty(parent))
      {
        Directory.CreateDirectory(parent);
      }
    }

    public static string Sha256(string file)
    {
      using (var sha = System.Security.Cryptography.SHA256.Create())
      using (var stream = File.OpenRead(file))
      {
        byte[] hash = sha.ComputeHash(stream);
        var sb = new System.Text.StringBuilder(hash.Length * 2);
        foreach (byte b in hash) sb.Append(b.ToString("x2"));
        return sb.ToString();
      }
    }
  }
}
