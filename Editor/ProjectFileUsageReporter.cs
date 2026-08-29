using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using Unity.Scripting.LifecycleManagement;
using UnityEditor;
using UnityEngine;

namespace CupkekGames.AssetVaults.Editor
{
  /// <summary>
  /// Every reference in the project, read once and kept.
  ///
  /// <para>Reading the project is by far the expensive half of a usage report -
  /// 44,000 files here - and none of it depends on which folder is being asked
  /// about. Building this once turns the second and every later question from a
  /// minute into an instant, which matters because deciding what to vault means
  /// asking about a dozen folders, not one.</para>
  /// </summary>
  // partial: the statics-cleanup codegen emits its reset into this type.
  internal sealed partial class ProjectReferenceIndex
  {
    // Unity's text-serialized asset types. `.meta` earns its place: a model
    // importer's external-object remap points at materials by GUID from inside
    // the meta, and nothing else records that reference.
    [NoAutoStaticsCleanup]
    private static readonly HashSet<string> SerializedExtensions =
      new HashSet<string>(StringComparer.OrdinalIgnoreCase)
      {
        ".unity", ".prefab", ".asset", ".mat", ".controller", ".overrideController",
        ".anim", ".playable", ".signal", ".physicMaterial", ".physicsMaterial2D",
        ".renderTexture", ".spriteatlas", ".spriteatlasv2", ".terrainlayer",
        ".preset", ".vfx", ".shadergraph", ".shadersubgraph", ".inputactions",
        ".guiskin", ".fontsettings", ".flare", ".cubemap", ".mixer", ".lighting",
        ".shadervariants", ".brush", ".mask", ".meta", ".asmdef", ".asmref",
        ".uss", ".uxml", ".tss",
      };

    [NoAutoStaticsCleanup]
    private static readonly HashSet<string> PathReferencingExtensions =
      new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".uss", ".uxml", ".tss" };

    [NoAutoStaticsCleanup]
    private static readonly Regex GuidToken =
      new Regex(@"\b[0-9a-f]{32}\b", RegexOptions.Compiled);

    [NoAutoStaticsCleanup]
    private static readonly Regex QuotedText =
      new Regex("[\"']([^\"'\n]{3,})[\"']", RegexOptions.Compiled);

    // Derived state: safe to drop on a session boundary because it rebuilds.
    [AutoStaticsCleanup]
    private static ProjectReferenceIndex _cached;

    /// <summary>guid -> the files mentioning it, project-relative.</summary>
    private readonly Dictionary<string, List<string>> _byGuid =
      new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>A quoted string seen in UI Toolkit markup -> the files it was in.</summary>
    private readonly Dictionary<string, List<string>> _byQuoted =
      new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

    public int FilesRead { get; private set; }

    /// <summary>Forget the index so the next report re-reads the project.</summary>
    public static void Invalidate() => _cached = null;

    public static ProjectReferenceIndex Get(CancellationToken ct)
    {
      if (_cached != null) return _cached;

      var index = new ProjectReferenceIndex();
      index.Build(ct);
      _cached = index;
      return index;
    }

    private void Build(CancellationToken ct)
    {
      string root = Directory.GetParent(Application.dataPath).FullName
        .TrimEnd(Path.DirectorySeparatorChar);
      string assetsDir = Application.dataPath.Replace('/', Path.DirectorySeparatorChar);

      // Enumerate first so the progress bar can tell the truth. Listing is
      // cheap; reading is what costs.
      var files = new List<string>(32768);
      foreach (string f in Directory.EnumerateFiles(assetsDir, "*", SearchOption.AllDirectories))
      {
        if (SerializedExtensions.Contains(Path.GetExtension(f))) files.Add(f);
      }

      try
      {
        for (int i = 0; i < files.Count; i++)
        {
          ct.ThrowIfCancellationRequested();
          if ((i & 127) == 0 && EditorUtility.DisplayCancelableProgressBar(
                "Reading project references",
                $"{i:N0} of {files.Count:N0} files", i / (float)files.Count))
          {
            throw new OperationCanceledException();
          }

          string file = files[i];
          string text;
          try
          {
            text = File.ReadAllText(file);
          }
          catch (IOException)
          {
            continue;
          }

          FilesRead++;
          string relative = file.Substring(root.Length + 1).Replace('\\', '/');

          foreach (Match m in GuidToken.Matches(text))
          {
            Add(_byGuid, m.Value, relative);
          }

          if (!PathReferencingExtensions.Contains(Path.GetExtension(file))) continue;

          foreach (Match m in QuotedText.Matches(text))
          {
            string quoted = m.Groups[1].Value;
            int query = quoted.IndexOf('?');
            if (query >= 0) quoted = quoted.Substring(0, query);
            quoted = quoted.Replace('\\', '/').TrimEnd('/');
            if (quoted.Length == 0) continue;

            int assets = quoted.IndexOf("Assets/", StringComparison.OrdinalIgnoreCase);
            if (assets >= 0)
            {
              Add(_byQuoted, quoted.Substring(assets), relative);
              continue;
            }

            // Only a reference with NO path at all is ambiguous enough to need
            // matching by filename. UI Toolkit normally writes the full path -
            // url("project://database/Assets/.../blond.png?...guid=...") - and
            // indexing its leaf too made every same-named file elsewhere look
            // used. Measured: five files in a marketing folder reported as
            // referenced because the real ones share their names.
            if (quoted.IndexOf('/') >= 0) continue;
            Add(_byQuoted, quoted, relative);
          }
        }
      }
      finally
      {
        EditorUtility.ClearProgressBar();
      }
    }

    private static void Add(Dictionary<string, List<string>> map, string key, string file)
    {
      if (!map.TryGetValue(key, out List<string> files))
      {
        files = new List<string>(1);
        map[key] = files;
      }

      // Long lists are pointless: one referrer outside the folder settles it,
      // and a GUID mentioned in 4000 files would otherwise cost real memory.
      if (files.Count < 32 && !files.Contains(file)) files.Add(file);
    }

    /// <summary>True when something OUTSIDE <paramref name="folderPrefix"/> mentions this key.</summary>
    public bool ReferencedFromOutside(Dictionary<string, List<string>> map, string key,
      string folderPrefix)
    {
      if (!map.TryGetValue(key, out List<string> files)) return false;

      foreach (string f in files)
      {
        if (!f.StartsWith(folderPrefix, StringComparison.OrdinalIgnoreCase)) return true;
      }

      return false;
    }

    public bool GuidUsedOutside(string guid, string folderPrefix)
      => ReferencedFromOutside(_byGuid, guid, folderPrefix);

    public bool QuotedUsedOutside(string quoted, string folderPrefix)
      => ReferencedFromOutside(_byQuoted, quoted, folderPrefix);
  }

  /// <summary>
  /// The reporter the vault ships with: finds which assets in a folder anything
  /// else in the project points at.
  ///
  /// <para>Two kinds of reference, because Unity has two. Most assets are
  /// referenced <b>by GUID</b> from YAML - scenes, prefabs, materials,
  /// controllers, and <c>.spriteatlas</c> files, which list their packables by
  /// GUID like anything else. UI Toolkit is the exception: <c>.uss</c> and
  /// <c>.uxml</c> reference images <b>by path</b>, and a scan that only knows
  /// GUIDs reports an icon folder as almost entirely unused. That mistake was
  /// made here once, on a real folder, before the path pass existed.</para>
  ///
  /// <para><b>Every ambiguous case counts as used.</b> Over-reporting keeps a
  /// file that could have been vaulted, costing disk. Under-reporting removes a
  /// file that was needed, costing the asset. Those are not comparable, so the
  /// filename fallback below is deliberately loose.</para>
  ///
  /// <para>What no static scan can see: a path built at runtime. That is why the
  /// window says so next to every result, and why nothing leaves git before a
  /// verified round trip.</para>
  /// </summary>
  public class ProjectFileUsageReporter : IVaultUsageReporter
  {
    public string DisplayName => "Project file scan";

    /// <summary>Lowest priority: anything more thorough that installs should win.</summary>
    public int Priority => 0;

    public UsageReport Report(string folder, CancellationToken ct)
    {
      if (!Directory.Exists(folder))
      {
        throw new VaultException($"There is no folder at {folder}");
      }

      string root = Directory.GetParent(Application.dataPath).FullName
        .TrimEnd(Path.DirectorySeparatorChar);
      string folderFull = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);
      if (!folderFull.StartsWith(root + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase))
      {
        throw new VaultException(
          "This scan reads the project, so it can only report on folders inside it. "
          + "Check a folder before moving it to the vault, not after.");
      }

      string folderPrefix = folderFull.Substring(root.Length + 1).Replace('\\', '/') + "/";
      ProjectReferenceIndex index = ProjectReferenceIndex.Get(ct);

      var used = new List<string>();
      int total = 0;

      foreach (string meta in Directory.EnumerateFiles(folderFull, "*.meta",
                 SearchOption.AllDirectories))
      {
        ct.ThrowIfCancellationRequested();
        string asset = meta.Substring(0, meta.Length - ".meta".Length);
        if (!File.Exists(asset)) continue;   // a folder's own meta

        total++;
        string relative = asset.Substring(root.Length + 1).Replace('\\', '/');

        bool hit = false;
        string guid = ReadGuid(meta);
        if (guid != null && index.GuidUsedOutside(guid, folderPrefix)) hit = true;

        if (!hit && index.QuotedUsedOutside(relative, folderPrefix)) hit = true;
        if (!hit && index.QuotedUsedOutside(Path.GetFileName(asset), folderPrefix)) hit = true;

        if (hit) used.Add(relative);
      }

      used.Sort(StringComparer.OrdinalIgnoreCase);
      return new UsageReport(used, total, DisplayName, index.FilesRead);
    }

    public void Invalidate() => ProjectReferenceIndex.Invalidate();

    // The guid is on the second line, and reading 23,000 metas whole is minutes
    // of pointless IO.
    private static string ReadGuid(string metaPath)
    {
      try
      {
        using (var reader = new StreamReader(metaPath))
        {
          string line;
          int seen = 0;
          while ((line = reader.ReadLine()) != null && seen++ < 8)
          {
            if (line.StartsWith("guid:", StringComparison.Ordinal))
            {
              return line.Substring(5).Trim();
            }
          }
        }
      }
      catch (IOException)
      {
        // Unreadable meta: treat as having no guid rather than failing the run.
      }

      return null;
    }
  }

  [InitializeOnLoad]
  internal static class ProjectFileUsageReporterRegistration
  {
    static ProjectFileUsageReporterRegistration()
    {
      VaultUsageReporters.Register(new ProjectFileUsageReporter());
    }
  }
}
