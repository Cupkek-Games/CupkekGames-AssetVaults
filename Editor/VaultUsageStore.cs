using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace CupkekGames.AssetVaults.Editor
{
  /// <summary>One folder's usage answer, as recorded for everyone else.</summary>
  [Serializable]
  public class VaultUsageRecord
  {
    /// <summary>Project-relative folder this describes.</summary>
    public string folder;

    /// <summary>ISO-8601 UTC. Sortable, unambiguous, and readable in a diff.</summary>
    public string scannedUtc;

    /// <summary>Which reporter produced it; answers differ between engines.</summary>
    public string reporter;

    /// <summary>Assets in the folder at scan time. Doubles as the staleness tripwire.</summary>
    public int totalAssets;

    /// <summary>Project files the reporter read, for a sense of what it covered.</summary>
    public int projectFilesRead;

    /// <summary>The answer, and the harvest list: what has to stay behind.</summary>
    public List<string> usedFiles = new List<string>();

    public DateTime? ScannedAt =>
      DateTime.TryParse(scannedUtc, null,
        System.Globalization.DateTimeStyles.AdjustToUniversal |
        System.Globalization.DateTimeStyles.AssumeUniversal, out DateTime parsed)
        ? parsed
        : (DateTime?)null;

    public string Age
    {
      get
      {
        DateTime? at = ScannedAt;
        if (at == null) return "at an unknown time";

        TimeSpan since = DateTime.UtcNow - at.Value;
        if (since.TotalMinutes < 90) return $"{Math.Max(0, (int)since.TotalMinutes)} minutes ago";
        if (since.TotalHours < 36) return $"{(int)since.TotalHours} hours ago";
        return $"{(int)since.TotalDays} days ago";
      }
    }
  }

  /// <summary>
  /// Usage answers, written to a file git carries.
  ///
  /// <para>A full scan of this project reads 44,000 files and takes over two
  /// minutes. Making every machine and every new clone pay that again to learn
  /// something that does not change hour to hour is waste, and worse, it means
  /// the person deciding what to vault often will not bother - so the decision
  /// gets made on a guess instead.</para>
  ///
  /// <para>Committed on purpose, beside <c>vault.json</c>. It is a finding about
  /// the project, like the manifest, not a per-user cache: <c>Library/</c> would
  /// be the wrong home precisely because nobody else would see it. It sits in
  /// the vault folder but is never itself vaulted - only pack folders get
  /// ignore lines.</para>
  ///
  /// <para><b>A record is a snapshot, and snapshots go stale.</b> Someone
  /// referencing a file after a scan makes the stored answer wrong in the
  /// direction that costs assets. Two defences: the age is always shown, and
  /// <see cref="VaultUsageRecord.totalAssets"/> is compared against the folder
  /// as it stands now - a changed count proves the record is out of date without
  /// re-reading anything.</para>
  /// </summary>
  [Serializable]
  public class VaultUsageStore
  {
    public string comment;
    public List<VaultUsageRecord> reports = new List<VaultUsageRecord>();

    public const string DefaultRelativePath = "Assets/Vault/usage-reports.json";

    private const string Explanation =
      "What the project references from each folder, recorded so nobody has to re-run a "
      + "multi-minute scan to find out. Written by the Asset Vault window (Tools > Asset "
      + "Vault). Committed on purpose: these are findings about the project, not a local "
      + "cache. Each record is a SNAPSHOT - if totalAssets no longer matches the folder, "
      + "or the date is old, re-scan before acting on it.";

    public static string ResolvePath(string projectRoot)
      => Path.Combine(projectRoot,
        DefaultRelativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Never throws: a missing or corrupt file is an empty store.</summary>
    public static VaultUsageStore Load(string projectRoot)
    {
      string path = ResolvePath(projectRoot);
      if (!File.Exists(path))
      {
        return new VaultUsageStore();
      }

      try
      {
        var store = JsonUtility.FromJson<VaultUsageStore>(File.ReadAllText(path));
        if (store == null) return new VaultUsageStore();

        store.reports ??= new List<VaultUsageRecord>();
        return store;
      }
      catch (Exception)
      {
        // Findings are regenerable. Losing them is not worth failing the window
        // over, and refusing to open would be a worse outcome than an empty list.
        return new VaultUsageStore();
      }
    }

    public void Save(string projectRoot)
    {
      comment = Explanation;
      reports.Sort((a, b) => string.CompareOrdinal(a.folder, b.folder));

      string path = ResolvePath(projectRoot);
      Directory.CreateDirectory(Path.GetDirectoryName(path));
      File.WriteAllText(path, JsonUtility.ToJson(this, true) + "\n");
    }

    public VaultUsageRecord Find(string relativeFolder)
    {
      foreach (VaultUsageRecord r in reports)
      {
        if (string.Equals(r.folder, relativeFolder, StringComparison.OrdinalIgnoreCase))
        {
          return r;
        }
      }

      return null;
    }

    /// <summary>Replace any previous answer for this folder; only the latest is useful.</summary>
    public VaultUsageRecord Record(string relativeFolder, UsageReport report, int filesRead)
    {
      VaultUsageRecord record = Find(relativeFolder);
      if (record == null)
      {
        record = new VaultUsageRecord();
        reports.Add(record);
      }

      record.folder = relativeFolder;
      record.scannedUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
      record.reporter = report.ReporterName;
      record.totalAssets = report.TotalAssets;
      record.projectFilesRead = filesRead;
      record.usedFiles = new List<string>(report.UsedFiles);
      return record;
    }

    /// <summary>
    /// Assets in a folder right now, counted the same way a reporter counts them
    /// so the two numbers are comparable. Walks one folder, not the project.
    /// </summary>
    public static int CountAssets(string folder)
    {
      if (!Directory.Exists(folder)) return 0;

      int count = 0;
      foreach (string meta in Directory.EnumerateFiles(folder, "*.meta",
                 SearchOption.AllDirectories))
      {
        if (File.Exists(meta.Substring(0, meta.Length - ".meta".Length))) count++;
      }

      return count;
    }
  }
}
