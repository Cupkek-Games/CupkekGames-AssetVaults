using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace CupkekGames.AssetVaults.Editor
{
  /// <summary>
  /// The vault window: what is on this PC, what is backed up, and one button for
  /// whichever of those is missing.
  ///
  /// <para>Written to be readable by someone who has never heard of this system.
  /// Every state is a plain sentence rather than a term of art, every button says
  /// what it will do, and the explanation lives in the window instead of a
  /// README. The readiness rows double as new-PC documentation.</para>
  ///
  /// <para>This window must never learn what the storage is. It renders
  /// whatever <see cref="IVaultBackend.IsConfigured"/> reports and whatever the
  /// active <see cref="VaultBackendRegistration"/> offers; anything more and the
  /// abstraction stops being real. The proof is that this assembly no longer
  /// references the Drive one at all.</para>
  /// </summary>
  public class AssetVaultWindow : EditorWindow
  {
    private const string HelpPrefKey = "CupkekGames.AssetVault.ShowHelp";

    // NonSerialized is load-bearing, not decoration. EditorWindow keeps its
    // private fields across a domain reload, and Unity brings a null string back
    // as "" - which reads as "a transfer is running" and "something failed". So
    // every recompile left the window stuck behind a Cancel button and an empty
    // error box, and Cancel did nothing because the CancellationTokenSource is
    // not serializable and came back null. None of this state can be meaningful
    // after a reload anyway: the reload killed the awaiting continuation, so
    // nothing was ever going to arrive and clear it.
    [NonSerialized] private VaultManifest _manifest;
    [NonSerialized] private IVaultBackend _backend;
    [NonSerialized] private string _error;
    [NonSerialized] private string _failure;
    [NonSerialized] private string _busy;
    [NonSerialized] private float _progress;
    [NonSerialized] private CancellationTokenSource _cts;
    [NonSerialized] private List<VaultCandidate> _candidates;
    [NonSerialized] private HashSet<string> _picked;
    private readonly Dictionary<string, PackStats> _stats = new Dictionary<string, PackStats>();
    private readonly HashSet<string> _verified = new HashSet<string>();
    private Vector2 _scroll;
    private bool _showHelp;

    [MenuItem("Tools/Asset Vault")]
    public static void Open()
    {
      GetWindow<AssetVaultWindow>("Asset Vault").minSize = new Vector2(620, 420);
    }

    private void OnEnable()
    {
      _showHelp = EditorPrefs.GetBool(HelpPrefKey, true);
      Reload();
    }

    private void Reload()
    {
      _error = null;
      _stats.Clear();
      _verified.Clear();

      // The candidate list is a snapshot against a manifest that just changed.
      _candidates = null;
      _picked = null;
      try
      {
        _manifest = VaultManifest.Load(VaultComposition.ManifestPath);
        _backend = VaultComposition.ResolveBackend();
      }
      catch (Exception e)
      {
        _error = e.Message;
        _manifest = null;
      }
    }

    private void OnGUI()
    {
      _scroll = EditorGUILayout.BeginScrollView(_scroll);

      DrawHelp();

      if (_error != null)
      {
        EditorGUILayout.HelpBox(_error, MessageType.Error);
        if (GUILayout.Button("Reload")) Reload();
        EditorGUILayout.EndScrollView();
        return;
      }

      // Asked once per repaint, not once per pack: it touches the disk. The
      // seed matters - short-circuiting past IsConfigured leaves it unassigned.
      string problem = "Backend unavailable.";
      bool connected = _backend != null && _backend.IsConfigured(out problem);

      using (new EditorGUI.DisabledScope(_busy != null))
      {
        DrawSetup(connected, problem);
        DrawFailure();
        EditorGUILayout.Space(10);
        DrawPacks(connected);
      }

      EditorGUILayout.EndScrollView();

      if (_busy != null)
      {
        Rect r = EditorGUILayout.GetControlRect(false, 22);
        EditorGUI.ProgressBar(r, _progress, _busy);
        if (GUILayout.Button("Cancel")) _cts?.Cancel();
      }
    }

    private void DrawHelp()
    {
      bool show = EditorGUILayout.Foldout(_showHelp, "What is the Asset Vault?", true);
      if (show != _showHelp)
      {
        _showHelp = show;
        EditorPrefs.SetBool(HelpPrefKey, show);
      }

      if (!_showHelp) return;

      EditorGUILayout.HelpBox(
        "The Vault folder next to Assets holds reference and source material: art packs "
        + "kept for later, source paintings a tool reads, things the game does not ship. "
        + "Unity never opens that folder, so none of it costs import time.\n\n"
        + "Those files are big, and putting them in Git makes the repository enormous and "
        + "slow to clone. So they are backed up to cloud storage instead, and this window "
        + "moves them in and out.\n\n"
        + "The list below shows every pack the vault manages. For each one you can see "
        + "whether the files are on THIS PC, and whether Drive has a backup. They are "
        + "separate things, and the buttons fix whichever is missing.\n\n"
        + "Nothing here ever deletes your work: 'Upload' copies out, 'Download' copies "
        + "back. Anything under Assets is project content, stays in Git, and is not "
        + "affected by any of this.",
        MessageType.Info);
      EditorGUILayout.Space(6);
    }

    private void DrawFailure()
    {
      if (_failure == null) return;

      EditorGUILayout.Space(6);
      using (new EditorGUILayout.HorizontalScope())
      {
        EditorGUILayout.HelpBox("That did not work: " + _failure, MessageType.Error);
        if (GUILayout.Button("Dismiss", GUILayout.Width(70), GUILayout.Height(38)))
        {
          _failure = null;
        }
      }
    }

    private void DrawSetup(bool ready, string problem)
    {
      VaultBackendRegistration backend = VaultBackends.Active;
      EditorGUILayout.LabelField(
        backend == null ? "Connection" : "Connection - " + backend.DisplayName,
        EditorStyles.boldLabel);

      UnityEngine.Object settings = backend?.FindSettingsAsset?.Invoke();

      string detail;
      if (backend == null)
      {
        detail = "No storage is installed, so nothing can be uploaded or downloaded. "
                 + "The list below still shows what is on this PC.";
      }
      else if (settings == null)
      {
        detail = "No settings asset yet.";
      }
      else
      {
        detail = ready ? "Signed in and ready." : problem;
      }

      using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
      {
        Color previous = GUI.color;
        GUI.color = ready ? new Color(0.45f, 0.85f, 0.45f) : new Color(0.95f, 0.75f, 0.3f);
        EditorGUILayout.LabelField(ready ? "OK" : "!", GUILayout.Width(20));
        GUI.color = previous;

        EditorGUILayout.LabelField(detail, EditorStyles.wordWrappedLabel);

        if (backend == null)
        {
          // Nothing to offer: a button here would be a lie.
        }
        else if (settings == null)
        {
          using (new EditorGUI.DisabledScope(backend.CreateSettingsAsset == null))
          {
            if (GUILayout.Button("Create settings", GUILayout.Width(120)))
            {
              backend.CreateSettingsAsset();
              Reload();
            }
          }
        }
        else if (GUILayout.Button(ready ? "Settings" : "Set up...", GUILayout.Width(120)))
        {
          Selection.activeObject = settings;
          EditorGUIUtility.PingObject(settings);
        }
      }

      // Whatever is missing, the backend already said so in actionable words -
      // that is the contract IsConfigured carries. The window adding its own
      // guess on top is how storage vocabulary leaks back in here.
      if (!ready && backend != null && settings != null)
      {
        EditorGUILayout.LabelField("Press Set up to finish connecting.",
          EditorStyles.wordWrappedMiniLabel);
      }
    }

    private void DrawPacks(bool connected)
    {
      using (new EditorGUILayout.HorizontalScope())
      {
        EditorGUILayout.LabelField("Packs stored in the vault", EditorStyles.boldLabel);

        using (new EditorGUI.DisabledScope(_manifest == null))
        {
          if (GUILayout.Button(
            new GUIContent(_candidates == null ? "Add folders..." : "Done adding",
              "Lists the folders in " + VaultDiscovery.DefaultScanRoot + "/ that are not in "
              + "the vault yet, so you can tick several at once. Nothing uploads until you "
              + "press Upload."), GUILayout.Width(110)))
          {
            if (_candidates == null) Rescan();
            else CloseDiscovery();
          }
        }

        if (GUILayout.Button("Refresh", GUILayout.Width(70))) Reload();
      }

      DrawDiscovery();

      if (_manifest == null || _manifest.packs.Count == 0)
      {
        EditorGUILayout.HelpBox(
          "No packs are in the vault yet. Press 'Add folders...' to pick some.",
          MessageType.Info);
        return;
      }

      // Collected, not removed inline: mutating the list mid-foreach throws.
      VaultPack drop = null;

      foreach (VaultPack pack in _manifest.packs)
      {
        string dir;
        try
        {
          dir = VaultManifest.ResolvePackDirectory(VaultComposition.ProjectRoot, pack);
        }
        catch (VaultException e)
        {
          EditorGUILayout.HelpBox(e.Message, MessageType.Error);
          continue;
        }

        if (!_stats.TryGetValue(pack.id, out PackStats stats))
        {
          stats = VaultStatus.Inspect(dir, false);
          _stats[pack.id] = stats;
        }

        PackState state = VaultStatus.Evaluate(pack, dir, _verified.Contains(pack.id), stats);
        if (DrawPackRow(pack, dir, stats, state, connected))
        {
          drop = pack;
        }
      }

      if (drop != null)
      {
        _manifest.packs.Remove(drop);
        _manifest.Save(VaultComposition.ManifestPath);
        Debug.Log($"[AssetVault] no longer managing {drop.id}; nothing was deleted.");
        Reload();
      }
    }

    private void Rescan()
    {
      // Walks every file under every candidate, so it only ever runs on a press.
      _candidates = new List<VaultCandidate>(VaultDiscovery.Scan(
        VaultComposition.ProjectRoot, _manifest, VaultDiscovery.DefaultScanRoot));
      _picked = new HashSet<string>();
    }

    private void CloseDiscovery()
    {
      _candidates = null;
      _picked = null;
    }

    /// <summary>
    /// The answer to "shouldn't a parent folder just cover everything": the scan
    /// covers the parent, the ticks stay yours. Folders holding C# are listed
    /// too, greyed, with the count - a rule you can see beats one you trip over.
    /// </summary>
    private void DrawDiscovery()
    {
      if (_candidates == null) return;

      using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
      {
        EditorGUILayout.LabelField(
          $"Folders in {VaultDiscovery.DefaultScanRoot}/ that are not in the vault yet",
          EditorStyles.boldLabel);

        if (_candidates.Count == 0)
        {
          EditorGUILayout.LabelField(
            $"Every folder in {VaultDiscovery.DefaultScanRoot}/ is already in the vault. To add "
            + "another, move it there first - anything under Assets is project content and "
            + "stays in Git.",
            EditorStyles.wordWrappedMiniLabel);
        }

        int blocked = 0;
        foreach (VaultCandidate candidate in _candidates)
        {
          if (!candidate.CanVault) blocked++;

          using (new EditorGUILayout.HorizontalScope())
          {
            bool on = _picked.Contains(candidate.RelativePath);
            bool now = EditorGUILayout.ToggleLeft(candidate.Name, on, GUILayout.Width(220));
            if (now != on)
            {
              if (now) _picked.Add(candidate.RelativePath);
              else _picked.Remove(candidate.RelativePath);
            }

            EditorGUILayout.LabelField(
              $"{VaultStatus.Describe(candidate.Bytes)} · {candidate.FileCount} files",
              EditorStyles.miniLabel, GUILayout.Width(150));

            EditorGUILayout.LabelField(
              candidate.CanVault
                ? string.Empty
                : $"holds {candidate.ScriptCount} C# files",
              EditorStyles.miniLabel);
          }
        }

        if (blocked > 0)
        {
          EditorGUILayout.LabelField(
            $"{blocked} folder(s) here contain C#. Nothing in the vault compiles, so that is a "
            + "warning rather than a problem: if the project actually uses one of those "
            + "packages, it belongs under Assets instead of here.",
            EditorStyles.wordWrappedMiniLabel);
        }

        EditorGUILayout.Space(4);
        using (new EditorGUILayout.HorizontalScope())
        {
          using (new EditorGUI.DisabledScope(_picked.Count == 0))
          {
            if (GUILayout.Button(_picked.Count == 1
                  ? "Add 1 folder to the vault"
                  : $"Add {_picked.Count} folders to the vault"))
            {
              AddPicked();
            }
          }

          if (GUILayout.Button(
            new GUIContent("Pick another folder...",
              "For a folder deeper inside " + VaultDiscovery.DefaultScanRoot
              + "/ than the list above reaches."), GUILayout.Width(150)))
          {
            AddBrowsed();
          }
        }
      }

      EditorGUILayout.Space(6);
    }

    private void AddPicked()
    {
      var failures = new List<string>();
      int added = 0;
      foreach (string relative in _picked)
      {
        try
        {
          VaultPack pack = _manifest.Add(VaultComposition.ProjectRoot,
            Path.Combine(VaultComposition.ProjectRoot,
              relative.Replace('/', Path.DirectorySeparatorChar)));
          Debug.Log($"[AssetVault] now managing {pack.id} ({pack.path}). "
                    + "It is not backed up until you press Upload.");
          added++;
        }
        catch (VaultException e)
        {
          // One bad folder must not cost the others; report at the end.
          failures.Add(e.Message);
        }
      }

      if (added > 0)
      {
        _manifest.Save(VaultComposition.ManifestPath);
      }

      CloseDiscovery();
      Reload();
      if (failures.Count > 0) _failure = string.Join("\n", failures);
    }

    private void AddBrowsed()
    {
      string browsed = EditorUtility.OpenFolderPanel(
        "Pick a folder to store in the vault",
        Path.Combine(VaultComposition.ProjectRoot, VaultDiscovery.DefaultScanRoot),
        string.Empty);
      if (string.IsNullOrEmpty(browsed)) return;

      try
      {
        VaultPack pack = _manifest.Add(VaultComposition.ProjectRoot, browsed);
        _manifest.Save(VaultComposition.ManifestPath);
        Debug.Log($"[AssetVault] now managing {pack.id} ({pack.path}). "
                  + "It is not backed up until you press Upload.");
        CloseDiscovery();
        Reload();
      }
      catch (Exception e)
      {
        // Reload() would wipe _error, and these are all rules worth reading, so
        // they go to the banner that survives it.
        CloseDiscovery();
        Reload();
        _failure = e.Message;
      }
    }

    /// <summary>Returns true when the row asked to be dropped from the manifest.</summary>
    private bool DrawPackRow(VaultPack pack, string dir, PackStats stats, PackState state,
      bool connected)
    {
      bool drop = false;

      using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
      {
        using (new EditorGUILayout.HorizontalScope())
        {
          EditorGUILayout.LabelField(PrettyName(pack), EditorStyles.boldLabel);
          GUILayout.FlexibleSpace();
          EditorGUILayout.LabelField(
            stats.FileCount > 0
              ? $"{VaultStatus.Describe(stats.Bytes)} · {stats.FileCount} files"
              : "not on this PC",
            EditorStyles.miniLabel, GUILayout.Width(170));
        }

        // Two independent facts, spelled out, because "where are my files" is the
        // question this window exists to answer.
        DrawFact("On this PC", Directory.Exists(dir),
          Directory.Exists(dir) ? "yes, the folder is here" : "no, the folder is missing");
        DrawFact("Backed up", pack.HasBeenPushed,
          pack.HasBeenPushed
            ? $"yes, version {pack.version}"
            : "no, there is no copy in storage");

        EditorGUILayout.LabelField(Explain(state), EditorStyles.wordWrappedMiniLabel);

        EditorGUILayout.Space(2);
        using (new EditorGUILayout.HorizontalScope())
        {
          bool onDisk = Directory.Exists(dir);

          using (new EditorGUI.DisabledScope(!connected || !onDisk))
          {
            if (GUILayout.Button(
              new GUIContent(pack.HasBeenPushed ? "Update backup" : "Upload",
                "Copies this pack from your PC to storage. Your local files are not changed.")))
            {
              Upload(pack, dir);
            }
          }

          // "Re-download" only makes sense when there is something to re-download;
          // with no backup it read as an offer the disabled state then refused.
          string label = onDisk && pack.HasBeenPushed ? "Re-download" : "Download to this PC";
          string tip;
          if (!pack.HasBeenPushed)
          {
            tip = "There is no copy in storage yet, so there is nothing to download.";
          }
          else if (onDisk)
          {
            tip = "Replaces the local folder with the backed-up copy.";
          }
          else
          {
            tip = "Copies this pack from storage back onto this PC.";
          }

          using (new EditorGUI.DisabledScope(!connected || !pack.HasBeenPushed))
          {
            if (GUILayout.Button(new GUIContent(label, tip)))
            {
              Download(pack, dir);
            }
          }

          using (new EditorGUI.DisabledScope(!onDisk))
          {
            if (GUILayout.Button(
              new GUIContent("Check files",
                "Reads every .meta file and compares them with the backup, to prove nothing "
                + "was lost. Slow on big packs."), GUILayout.Width(90)))
            {
              _stats[pack.id] = VaultStatus.Inspect(dir, true);
              _verified.Add(pack.id);
            }

            if (GUILayout.Button(new GUIContent("Show", "Open the folder in Explorer."),
                  GUILayout.Width(60)))
            {
              EditorUtility.RevealInFinder(dir);
            }
          }
        }

        drop = DrawStopManaging(pack);
      }

      return drop;
    }

    /// <summary>
    /// The way back out. Deliberately not called "Remove": it deletes nothing,
    /// and a button that sounds like it might would never get pressed.
    /// </summary>
    private static bool DrawStopManaging(VaultPack pack)
    {
      if (!GUILayout.Button(
        new GUIContent("Stop managing this pack",
          "Takes the pack off this list. Your files stay on this PC and any backup "
          + "stays where it is; only the vault forgets about it.")))
      {
        return false;
      }

      return EditorUtility.DisplayDialog(
        $"Stop managing {PrettyName(pack)}?",
        "The vault will take it off the list.\n\nNothing is deleted: the folder stays "
        + "on this PC and any backup stays where it is. You can add it again later.",
        "Stop managing", "Keep it");
    }

    private static void DrawFact(string label, bool ok, string detail)
    {
      using (new EditorGUILayout.HorizontalScope())
      {
        Color previous = GUI.color;
        GUI.color = ok ? new Color(0.45f, 0.85f, 0.45f) : new Color(0.8f, 0.8f, 0.8f);
        EditorGUILayout.LabelField(ok ? "OK" : "--", GUILayout.Width(24));
        GUI.color = previous;
        EditorGUILayout.LabelField(label, GUILayout.Width(140));
        EditorGUILayout.LabelField(detail, EditorStyles.miniLabel);
      }
    }

    private static string PrettyName(VaultPack pack)
    {
      string name = Path.GetFileName(pack.path.TrimEnd('/'));
      return string.IsNullOrEmpty(name) ? pack.id : name;
    }

    // Plain sentences, not state names. The enum is for code; this is for people.
    private static string Explain(PackState state)
    {
      switch (state)
      {
        case PackState.Missing:
          return "The files are not on this PC. Download them to get the folder back.";
        case PackState.LocalOnly:
          return "The files are here but nothing is backed up. If this disk died they "
                 + "would be gone, so upload them.";
        case PackState.Present:
          return "The files are here and there is a backup. Nothing to do.";
        case PackState.Modified:
          return "The files here differ from the backup: the number of files does not match. "
                 + "Upload to make Drive match this PC, or download to go back to the backup.";
        case PackState.Verified:
          return "Checked file by file: this PC matches the backup exactly.";
        case PackState.GuidDrift:
          return "The files are here but their Unity IDs do not match the backup, so "
                 + "materials and prefabs pointing at this pack may break. Re-downloading "
                 + "restores the recorded IDs.";
        default:
          return string.Empty;
      }
    }

    private IProgress<TransferProgress> Reporter()
    {
      return new Progress<TransferProgress>(p =>
      {
        _progress = p.HasTotal ? p.Fraction01 : 0f;
        if (!string.IsNullOrEmpty(p.Note)) _busy = p.Note;
        Repaint();
      });
    }

    private void Upload(VaultPack pack, string dir)
    {
      RunAsync($"Uploading {PrettyName(pack)}", async ct =>
      {
        string zip = Path.Combine(Path.GetTempPath(), pack.archive);
        try
        {
          PackStats stats = VaultStatus.Inspect(dir, true);
          ArchiveBuilder.Create(dir, zip, Reporter(), ct);
          await _backend.UploadAsync(zip, pack.archive, Reporter(), ct);

          // Record what was sent, so a later download can prove it arrived whole.
          pack.bytes = stats.Bytes;
          pack.fileCount = stats.FileCount;
          pack.guidHash = stats.GuidHash;
          pack.sha256 = ArchiveBuilder.Sha256(zip);
          _manifest.Save(VaultComposition.ManifestPath);
          // Plan section 10: print the untrack commands, never run them. Between
          // `git rm --cached` and a working download the only copy of those bytes
          // is one local disk, so a human makes that call.
          Debug.Log(
            $"[AssetVault] uploaded {pack.id} ({stats.FileCount} files). It is still in "
            + $"git too. To take it out of git once you trust the backup, add "
            + $"'/{pack.path}/' to .gitignore and run:  git rm -r --cached \"{pack.path}\"");
        }
        finally
        {
          if (File.Exists(zip)) File.Delete(zip);
        }
      });
    }

    private void Download(VaultPack pack, string dir)
    {
      RunAsync($"Downloading {PrettyName(pack)}", async ct =>
      {
        string zip = Path.Combine(Path.GetTempPath(), pack.archive);
        try
        {
          await _backend.DownloadAsync(pack.archive, zip, Reporter(), ct);

          string actual = ArchiveBuilder.Sha256(zip);
          if (!string.IsNullOrEmpty(pack.sha256) && actual != pack.sha256)
          {
            throw new VaultException(
              "The downloaded file does not match what was uploaded, so it was not "
              + "installed. Nothing on this PC was changed.");
          }

          ArchiveBuilder.ExtractInPlace(zip, dir, Reporter(), ct);
          Debug.Log($"[AssetVault] downloaded {pack.id}.");
        }
        finally
        {
          if (File.Exists(zip)) File.Delete(zip);
        }
      });
    }

    private async void RunAsync(string label, Func<CancellationToken, Task> work)
    {
      _busy = label;
      _progress = 0f;
      _failure = null;
      _cts = new CancellationTokenSource();
      string failure = null;
      try
      {
        await work(_cts.Token);
      }
      catch (OperationCanceledException)
      {
        Debug.Log("[AssetVault] cancelled; nothing was changed.");
      }
      catch (Exception e)
      {
        Debug.LogError("[AssetVault] " + e);
        // Some exceptions carry no message, and "That did not work:" followed by
        // nothing is worse than a type name.
        failure = string.IsNullOrEmpty(e.Message) ? e.GetType().Name : e.Message;
      }
      finally
      {
        _busy = null;
        _cts?.Dispose();
        _cts = null;
        // Reload() resets the window's state, so the reason has to outlive it.
        Reload();
        _failure = failure;
        Repaint();
      }
    }
  }
}
