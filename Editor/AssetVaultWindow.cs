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
    [NonSerialized] private readonly Dictionary<string, UsageReport> _usage =
      new Dictionary<string, UsageReport>();
    [NonSerialized] private VaultUsageStore _findings;

    /// <summary>
    /// Everything about one pack that the window draws, worked out ONCE per
    /// reload rather than per repaint.
    ///
    /// <para>This exists because the window was unusable. Drawing a row asked
    /// the disk whether the folder existed four times, and the shared-usage row
    /// called <see cref="VaultUsageStore.CountAssets"/>, which walks every file
    /// in the pack. Across 19 packs holding thousands of files each that is tens
    /// of thousands of file stats per frame, sixty times a second.</para>
    /// </summary>
    private struct PackView
    {
      public VaultPack Pack;
      public string Name;
      public string Dir;
      public bool OnDisk;
      public PackStats Stats;
      public PackState State;
      public int AssetsNow;
      public string Error;

      /// <summary>One of <see cref="VaultCategory.All"/>, or empty.</summary>
      public string Category;

      /// <summary>
      /// Typed and derived tags together, which is what a filter should match
      /// and what a row should show. The two are kept apart only in the editor,
      /// where one is yours to change and the other is not.
      /// </summary>
      public List<string> Tags;
    }

    [NonSerialized] private List<PackView> _views;

    // Facet counts, built with the snapshot: a sidebar that recounted per
    // repaint would be the CountAssets mistake again in a new place.
    [NonSerialized] private List<Facet> _categoryFacets;
    [NonSerialized] private List<Facet> _tagFacets;
    [NonSerialized] private string _filter = string.Empty;

    /// <summary>
    /// A sidebar click, held as one string. One facet at a time on purpose:
    /// intersecting facets needs a query language, and the text filter is
    /// already the answer for anything more specific than "show me the audio".
    /// </summary>
    [NonSerialized] private string _facet;

    private struct Facet
    {
      public string Key;
      public string Label;
      public int Count;
    }

    [NonSerialized] private bool _connected;
    [NonSerialized] private string _problem;
    [NonSerialized] private UnityEngine.Object _settings;
    [NonSerialized] private string _expanded;
    [NonSerialized] private readonly HashSet<string> _usageExpanded = new HashSet<string>();
    private readonly Dictionary<string, PackStats> _stats = new Dictionary<string, PackStats>();
    private readonly HashSet<string> _verified = new HashSet<string>();
    private Vector2 _scroll;
    private bool _showHelp;

    [MenuItem("Tools/CupkekGames/Asset Vault")]
    public static void Open()
    {
      GetWindow<AssetVaultWindow>("Asset Vault").minSize = new Vector2(620, 420);
    }

    private void OnEnable()
    {
      _showHelp = EditorPrefs.GetBool(HelpPrefKey, true);
      _showSidebar = EditorPrefs.GetBool(SidebarPrefKey, true);
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
      _views = null;
      // _usage deliberately survives: it describes folders on disk rather than
      // the manifest, and it can cost minutes to rebuild.
      try
      {
        _manifest = VaultManifest.Load(VaultComposition.ManifestPath);
        _backend = VaultComposition.ResolveBackend();
        _findings = VaultUsageStore.Load(VaultComposition.ProjectRoot);
        BuildViews();
      }
      catch (Exception e)
      {
        _error = e.Message;
        _manifest = null;
      }
    }

    /// <summary>
    /// Ask the disk everything the window needs, once. Every field read while
    /// drawing comes from here; nothing in OnGUI touches the filesystem.
    /// </summary>
    private void BuildViews()
    {
      _views = new List<PackView>();
      _categoryFacets = new List<Facet>();
      _tagFacets = new List<Facet>();
      VaultBackendRegistration registration = VaultBackends.Active;
      _settings = registration?.FindSettingsAsset?.Invoke();
      _problem = "Backend unavailable.";
      _connected = _backend != null && _backend.IsConfigured(out _problem);

      if (_manifest == null) return;

      foreach (VaultPack pack in _manifest.packs)
      {
        var view = new PackView { Pack = pack, Name = PrettyName(pack) };
        try
        {
          view.Dir = VaultManifest.ResolvePackDirectory(VaultComposition.ProjectRoot, pack);
        }
        catch (VaultException e)
        {
          view.Error = e.Message;
          _views.Add(view);
          continue;
        }

        view.OnDisk = Directory.Exists(view.Dir);

        // A verified check hashed every .meta in the pack; re-inspecting would
        // throw that away and quietly downgrade the pack's state.
        if (!_stats.TryGetValue(pack.id, out PackStats stats))
        {
          stats = VaultStatus.Inspect(view.Dir, false);
          _stats[pack.id] = stats;
        }

        view.Stats = stats;
        view.State = VaultStatus.Evaluate(pack, view.Dir, _verified.Contains(pack.id), stats);

        // Only needed to date a shared usage record, and only when there is one.
        VaultUsageRecord record = _findings?.Find(Relative(view.Dir));
        view.AssetsNow = record == null ? -1 : VaultUsageStore.CountAssets(view.Dir);

        view.Category = pack.category ?? VaultCategory.Uncategorised;
        view.Tags = new List<string>(pack.tags ?? new List<string>());
        foreach (string derived in stats.Content)
        {
          if (!view.Tags.Contains(derived)) view.Tags.Add(derived);
        }

        _views.Add(view);
      }

      BuildFacets();
    }

    /// <summary>
    /// Every category and tag in the vault with how many packs carry it.
    ///
    /// <para>Categories are listed even at zero, because an empty group is the
    /// useful signal that nothing has been filed there yet. Tags are not: a tag
    /// nobody uses is noise in a list whose whole job is to show what exists.</para>
    /// </summary>
    private void BuildFacets()
    {
      var categories = new Dictionary<string, int>();
      var tags = new Dictionary<string, int>();

      foreach (PackView view in _views)
      {
        if (view.Error != null) continue;

        string category = VaultCategory.IsKnown(view.Category)
          ? view.Category
          : VaultCategory.Uncategorised;
        categories.TryGetValue(category, out int seen);
        categories[category] = seen + 1;

        foreach (string tag in view.Tags)
        {
          tags.TryGetValue(tag, out int count);
          tags[tag] = count + 1;
        }
      }

      _categoryFacets = new List<Facet>();
      foreach (string category in VaultCategory.All)
      {
        categories.TryGetValue(category, out int count);
        _categoryFacets.Add(new Facet
        {
          Key = CategoryFacet + category,
          Label = VaultCategory.Label(category),
          Count = count,
        });
      }

      if (categories.TryGetValue(VaultCategory.Uncategorised, out int loose) && loose > 0)
      {
        _categoryFacets.Add(new Facet
        {
          Key = CategoryFacet,
          Label = "Uncategorised",
          Count = loose,
        });
      }

      _tagFacets = new List<Facet>();
      foreach (KeyValuePair<string, int> tag in tags)
      {
        _tagFacets.Add(new Facet { Key = tag.Key, Label = tag.Key, Count = tag.Value });
      }

      // Commonest first, then alphabetical, so the shape of the vault reads off
      // the top of the list rather than out of an arbitrary dictionary order.
      _tagFacets.Sort((a, b) => a.Count != b.Count
        ? b.Count.CompareTo(a.Count)
        : string.CompareOrdinal(a.Label, b.Label));
    }

    /// <summary>
    /// Category facets share a namespace with tag facets, so they are prefixed
    /// rather than risking a tag called "audio" selecting the audio category.
    /// </summary>
    private const string CategoryFacet = "\u0000category:";

    /// <summary>Does this pack survive the sidebar selection and the text box?</summary>
    private bool Matches(PackView view)
    {
      if (_facet != null)
      {
        if (_facet.StartsWith(CategoryFacet, StringComparison.Ordinal))
        {
          string wanted = _facet.Substring(CategoryFacet.Length);
          string actual = VaultCategory.IsKnown(view.Category)
            ? view.Category
            : VaultCategory.Uncategorised;
          if (!string.Equals(wanted, actual, StringComparison.OrdinalIgnoreCase)) return false;
        }
        else if (!view.Tags.Contains(_facet))
        {
          return false;
        }
      }

      if (string.IsNullOrWhiteSpace(_filter)) return true;

      string needle = _filter.Trim().ToLowerInvariant();
      if (view.Name.ToLowerInvariant().Contains(needle)) return true;
      if (view.Pack.id.ToLowerInvariant().Contains(needle)) return true;
      if (VaultCategory.Label(view.Category).ToLowerInvariant().Contains(needle)) return true;
      foreach (string tag in view.Tags)
      {
        if (tag.Contains(needle)) return true;
      }

      return false;
    }

    private void OnGUI()
    {
      DrawHelp();

      if (_error != null)
      {
        EditorGUILayout.HelpBox(_error, MessageType.Error);
        if (GUILayout.Button("Reload")) Reload();
        return;
      }

      using (new EditorGUI.DisabledScope(_busy != null))
      {
        DrawSetup(_connected, _problem);
        DrawFailure();
        EditorGUILayout.Space(6);
        DrawToolbar();

        if (_views == null) BuildViews();

        // The sidebar and the list scroll independently. One scroll around
        // both would push the facets off the top the moment the list is long,
        // which is exactly when they are worth having.
        using (new EditorGUILayout.HorizontalScope())
        {
          if (_showSidebar) DrawSidebar();

          using (new EditorGUILayout.VerticalScope())
          {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            DrawArbitraryUsage();
            DrawDiscovery();
            DrawPacks(_connected);
            EditorGUILayout.EndScrollView();
          }
        }
      }

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
        "Assets/Vault holds packs that are part of the project but not part of the "
        + "repository. Unity imports and uses them exactly like anything else; git simply "
        + "does not carry them.\n\n"
        + "Those files are big, and putting them in Git makes the repository enormous and "
        + "slow to clone. So they are backed up to cloud storage instead, and this window "
        + "moves them in and out.\n\n"
        + "The list below shows every pack the vault manages, one line each. The dot on "
        + "the left is green when the pack is both on this PC and backed up, amber when "
        + "it is only one of those, and grey when it is neither - they are separate "
        + "things. Click a line to open it: the detail says which one is missing in "
        + "plain words, and the buttons fix it.\n\n"
        + "Nothing here ever deletes your work: 'Upload' copies out, 'Download' copies "
        + "back. Anything under Assets but outside Assets/Vault stays in Git and is not "
        + "affected by any of this.\n\n"
        + "A fresh clone will be missing these packs until someone downloads them, so "
        + "expect missing art and, for packs with scripts, compile errors until then.",
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

      // From the snapshot: FindSettingsAsset runs AssetDatabase.FindAssets, and
      // once per repaint is once per frame.
      UnityEngine.Object settings = _settings;

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

    private void DrawToolbar()
    {
      using (new EditorGUILayout.HorizontalScope())
      {
        if (GUILayout.Button(new GUIContent(_showSidebar ? "\u25C0 Facets" : "\u25B6 Facets",
              "Show or hide the category and tag list."), EditorStyles.miniButton,
              GUILayout.Width(70)))
        {
          _showSidebar = !_showSidebar;
          EditorPrefs.SetBool(SidebarPrefKey, _showSidebar);
        }

        // Not a plain TextField: the search style brings the clear button with
        // it, and a filter you cannot clear in one click gets left on.
        string typed = EditorGUILayout.TextField(_filter, SearchStyle(),
          GUILayout.MinWidth(120));
        if (typed != _filter)
        {
          _filter = typed;
          Repaint();
        }

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

        using (new EditorGUI.DisabledScope(VaultUsageReporters.Active == null))
        {
          if (GUILayout.Button(new GUIContent("Check a folder...",
                "Ask what the project uses from ANY folder, before deciding whether to "
                + "vault it. This is the question you want answered first."),
                GUILayout.Width(120)))
          {
            CheckArbitraryFolder();
          }
        }

        if (GUILayout.Button("Refresh", GUILayout.Width(70))) Reload();
      }
    }

    private static GUIStyle SearchStyle()
    {
      GUIStyle style = GUI.skin.FindStyle("ToolbarSearchTextField")
        ?? GUI.skin.FindStyle("SearchTextField");
      return style ?? EditorStyles.textField;
    }

    /// <summary>
    /// The list itself: grouped by category, filtered by the sidebar and the
    /// search box, one row per pack.
    /// </summary>
    private void DrawPacks(bool connected)
    {
      if (_manifest == null || _manifest.packs.Count == 0)
      {
        EditorGUILayout.HelpBox(
          "No packs are in the vault yet. Press 'Add folders...' to pick some.",
          MessageType.Info);
        return;
      }

      if (_views == null) BuildViews();

      DrawColumnHeader();

      // Collected, not removed inline: mutating the list mid-foreach throws.
      VaultPack drop = null;
      int shown = 0;
      string group = null;

      foreach (PackView view in Ordered())
      {
        if (view.Error != null)
        {
          EditorGUILayout.HelpBox(view.Error, MessageType.Error);
          continue;
        }

        if (!Matches(view)) continue;

        string category = VaultCategory.IsKnown(view.Category)
          ? view.Category
          : VaultCategory.Uncategorised;
        if (category != group)
        {
          group = category;
          DrawGroupHeader(category);
        }

        shown++;
        if (DrawPackRow(view, connected))
        {
          drop = view.Pack;
        }
      }

      if (shown == 0)
      {
        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField(
          _facet == null && string.IsNullOrWhiteSpace(_filter)
            ? "Nothing to show."
            : "No pack matches. Clear the filter to see the rest.",
          EditorStyles.centeredGreyMiniLabel);
      }

      if (drop != null)
      {
        _manifest.packs.Remove(drop);
        _manifest.Save(VaultComposition.ManifestPath);
        Debug.Log($"[AssetVault] no longer managing {drop.id}; nothing was deleted.");
        Reload();
      }
    }

    /// <summary>
    /// The snapshot in display order: by category, then by name inside it.
    /// Sorted here rather than in <c>BuildViews</c> because the order is a
    /// presentation choice and the snapshot is the data.
    /// </summary>
    private List<PackView> Ordered()
    {
      var ordered = new List<PackView>(_views);
      ordered.Sort((a, b) =>
      {
        int byCategory = VaultCategory.Order(a.Category).CompareTo(
          VaultCategory.Order(b.Category));
        return byCategory != 0
          ? byCategory
          : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
      });

      return ordered;
    }

    private static void DrawGroupHeader(string category)
    {
      EditorGUILayout.Space(4);
      Rect row = EditorGUILayout.GetControlRect(false, 16f);
      EditorGUI.LabelField(row, VaultCategory.Label(category).ToUpperInvariant(),
        EditorStyles.miniBoldLabel);
      EditorGUI.DrawRect(new Rect(row.x, row.yMax - 1f, row.width, 1f),
        EditorGUIUtility.isProSkin
          ? new Color(1f, 1f, 1f, 0.08f)
          : new Color(0f, 0f, 0f, 0.10f));
    }

    /// <summary>
    /// Every category and tag with a count, click to filter. The one part of
    /// the window that answers "what is even in here" without scrolling.
    /// </summary>
    private void DrawSidebar()
    {
      using (new EditorGUILayout.VerticalScope(GUILayout.Width(SidebarWidth)))
      {
        _sidebarScroll = EditorGUILayout.BeginScrollView(_sidebarScroll);

        EditorGUILayout.LabelField("CATEGORY", EditorStyles.miniBoldLabel);
        foreach (Facet facet in _categoryFacets) DrawFacet(facet);

        if (_tagFacets.Count > 0)
        {
          EditorGUILayout.Space(6);
          EditorGUILayout.LabelField("TAGS", EditorStyles.miniBoldLabel);
          foreach (Facet facet in _tagFacets) DrawFacet(facet);
        }

        EditorGUILayout.Space(6);
        using (new EditorGUI.DisabledScope(_facet == null))
        {
          if (GUILayout.Button("Clear", EditorStyles.miniButton)) _facet = null;
        }

        EditorGUILayout.EndScrollView();
      }
    }

    private void DrawFacet(Facet facet)
    {
      bool selected = _facet == facet.Key;
      Rect row = EditorGUILayout.GetControlRect(false, 16f);

      if (selected)
      {
        EditorGUI.DrawRect(row, EditorGUIUtility.isProSkin
          ? new Color(1f, 1f, 1f, 0.10f)
          : new Color(0f, 0f, 0f, 0.10f));
      }

      // A zero category is worth listing - it says nothing is filed there yet -
      // but it should not look like something to click.
      using (new EditorGUI.DisabledScope(facet.Count == 0))
      {
        EditorGUI.LabelField(new Rect(row.x + 2f, row.y, row.width - 34f, row.height),
          facet.Label, selected ? EditorStyles.boldLabel : EditorStyles.label);
        EditorGUI.LabelField(new Rect(row.xMax - 32f, row.y, 30f, row.height),
          facet.Count.ToString(), RightMini());
      }

      if (facet.Count > 0 && Event.current.type == EventType.MouseDown
          && Event.current.button == 0 && row.Contains(Event.current.mousePosition))
      {
        _facet = selected ? null : facet.Key;
        Event.current.Use();
        Repaint();
      }
    }

    private const float SidebarWidth = 148f;
    private const string SidebarPrefKey = "CupkekGames.AssetVault.ShowSidebar";
    [NonSerialized] private bool _showSidebar;
    [NonSerialized] private Vector2 _sidebarScroll;

    /// <summary>
    /// Three right-aligned numbers with no labels are a puzzle. One 14px strip
    /// solves it for the whole list.
    /// </summary>
    private void DrawColumnHeader()
    {
      Rect row = EditorGUILayout.GetControlRect(false, 14f);
      float right = row.x + row.width;
      EditorGUI.LabelField(new Rect(row.x + 14f, row.y, 200f, row.height),
        "pack", EditorStyles.miniLabel);
      EditorGUI.LabelField(new Rect(right - 250f, row.y, 78f, row.height),
        "size", RightMini());
      EditorGUI.LabelField(new Rect(right - 168f, row.y, 62f, row.height),
        "files", RightMini());
      EditorGUI.LabelField(new Rect(right - 102f, row.y, 102f, row.height),
        "state", RightMini());
    }

    [NonSerialized] private string _probedFolder;
    [NonSerialized] private int _probedAssets = -1;

    /// <summary>
    /// The decision usually happens BEFORE a folder is a pack: it is still in
    /// Assets and the question is whether it should be. So the report has to
    /// work on anything, not only on things already in the manifest.
    /// </summary>
    private void CheckArbitraryFolder()
    {
      string picked = EditorUtility.OpenFolderPanel(
        "Which folder should I check?",
        Path.Combine(VaultComposition.ProjectRoot, "Assets"), string.Empty);
      if (string.IsNullOrEmpty(picked)) return;

      _probedFolder = picked;
      _probedAssets = VaultUsageStore.CountAssets(picked);
      RunUsage(VaultUsageReporters.Active, ProbeKey, picked);
    }

    private const string ProbeKey = "\u0000probe";

    private void DrawArbitraryUsage()
    {
      if (_probedFolder == null || !_usage.ContainsKey(ProbeKey)) return;

      using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
      {
        using (new EditorGUILayout.HorizontalScope())
        {
          EditorGUILayout.LabelField(Path.GetFileName(_probedFolder.TrimEnd('/')),
            EditorStyles.boldLabel);
          if (GUILayout.Button("Close", GUILayout.Width(60)))
          {
            _usage.Remove(ProbeKey);
            _probedFolder = null;
            _probedAssets = -1;
            return;
          }
        }

        DrawUsage(ProbeKey, _probedFolder, _probedAssets);
      }

      EditorGUILayout.Space(6);
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
            $"Every folder in {VaultDiscovery.DefaultScanRoot}/ is already in the vault. To "
            + "add another, move it there first; Unity keeps using it from its new "
            + "location, it just stops being in Git.",
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
            $"{blocked} folder(s) here contain C#. Vaulting those is allowed, but a fresh "
            + "clone will not compile until it has downloaded them - the download is part "
            + "of setting a machine up, not an optional extra.",
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

    /// <summary>
    /// One pack, one line. Nineteen packs of tall cards was a scrolling
    /// exercise; the summary a reader wants is the name, the size and whether
    /// it is safe, and everything else belongs behind a click.
    ///
    /// <para>Reads only from the <see cref="PackView"/> snapshot - no
    /// filesystem calls happen while drawing.</para>
    /// </summary>
    /// <returns>True when the row asked to be dropped from the manifest.</returns>
    private bool DrawPackRow(PackView view, bool connected)
    {
      bool open = _expanded == view.Pack.id;
      Rect row = EditorGUILayout.GetControlRect(false, 18f);

      if (open)
      {
        EditorGUI.DrawRect(new Rect(row.x - 2f, row.y - 1f, row.width + 4f, row.height + 2f),
          EditorGUIUtility.isProSkin
            ? new Color(1f, 1f, 1f, 0.06f)
            : new Color(0f, 0f, 0f, 0.06f));
      }

      // A dot says the two things that matter at a glance: is it here, is it
      // backed up. Green both, amber one, grey neither.
      bool backed = view.Pack.HasBeenPushed;
      Color previous = GUI.color;
      GUI.color = view.OnDisk && backed ? new Color(0.45f, 0.85f, 0.45f)
        : view.OnDisk || backed ? new Color(0.95f, 0.75f, 0.3f)
        : new Color(0.6f, 0.6f, 0.6f);
      EditorGUI.LabelField(new Rect(row.x, row.y, 14f, row.height), "\u25CF");
      GUI.color = previous;

      float right = row.x + row.width;
      var nameRect = new Rect(row.x + 14f, row.y, row.width - 14f - 250f, row.height);
      var sizeRect = new Rect(right - 250f, row.y, 78f, row.height);
      var fileRect = new Rect(right - 168f, row.y, 62f, row.height);
      var stateRect = new Rect(right - 102f, row.y, 102f, row.height);

      string name = (open ? "\u25BC  " : "\u25B6  ") + view.Name;
      EditorGUI.LabelField(nameRect, name);
      DrawChips(nameRect, name, view.Tags);
      EditorGUI.LabelField(sizeRect,
        view.OnDisk ? VaultStatus.Describe(view.Stats.Bytes) : "-",
        RightMini());
      EditorGUI.LabelField(fileRect,
        view.OnDisk ? view.Stats.FileCount.ToString("N0") : "-", RightMini());
      EditorGUI.LabelField(stateRect, ShortState(view.State), RightMini());

      // Left button only: swallowing a right-click here would eat the context
      // menu without ever putting one in its place.
      if (Event.current.type == EventType.MouseDown && Event.current.button == 0
          && row.Contains(Event.current.mousePosition))
      {
        _expanded = open ? null : view.Pack.id;
        Event.current.Use();
        Repaint();
      }

      return open && DrawPackDetail(view, connected);
    }

    // Per window, not static: a static cache would owe the lifecycle analyzer
    // an AutoStaticsCleanup answer, and the window is exactly the lifetime this
    // style should have anyway. Not a field initialiser because EditorStyles is
    // not ready when the window is constructed.
    [NonSerialized] private GUIStyle _rightMini;

    /// <summary>
    /// Tags after the pack name, in whatever width the name did not use.
    ///
    /// <para>Dim on purpose: they are context for a row you are already reading,
    /// not a column. When they do not fit they are cut to a count, because a
    /// truncated tag name is a tag name that reads as a different tag.</para>
    /// </summary>
    private void DrawChips(Rect nameRect, string name, List<string> tags)
    {
      if (tags == null || tags.Count == 0) return;

      float used = EditorStyles.label.CalcSize(new GUIContent(name)).x + 8f;
      var rect = new Rect(nameRect.x + used, nameRect.y, nameRect.width - used, nameRect.height);
      if (rect.width < 30f) return;

      var text = new System.Text.StringBuilder();
      int fitted = 0;
      foreach (string tag in tags)
      {
        string candidate = text.Length == 0 ? tag : text + "  " + tag;
        if (EditorStyles.miniLabel.CalcSize(new GUIContent(candidate)).x > rect.width - 26f)
        {
          break;
        }

        text.Clear();
        text.Append(candidate);
        fitted++;
      }

      if (fitted < tags.Count) text.Append("  +").Append(tags.Count - fitted);

      Color previous = GUI.color;
      GUI.color = new Color(previous.r, previous.g, previous.b, 0.55f);
      EditorGUI.LabelField(rect, text.ToString(), EditorStyles.miniLabel);
      GUI.color = previous;
    }

    private GUIStyle RightMini()
    {
      return _rightMini ??= new GUIStyle(EditorStyles.miniLabel)
      {
        alignment = TextAnchor.MiddleRight
      };
    }

    /// <summary>Four words, not a sentence - the sentence is in the detail.</summary>
    private static string ShortState(PackState state)
    {
      switch (state)
      {
        case PackState.Missing: return "not on this PC";
        case PackState.LocalOnly: return "no backup";
        case PackState.Present: return "backed up";
        case PackState.Modified: return "changed";
        case PackState.Verified: return "verified";
        case PackState.GuidDrift: return "ids differ";
        default: return string.Empty;
      }
    }

    /// <summary>The detail for the one expanded pack. Only ever one is open.</summary>
    private bool DrawPackDetail(PackView view, bool connected)
    {
      bool drop = false;
      VaultPack pack = view.Pack;

      using (new EditorGUI.IndentLevelScope())
      {
        EditorGUILayout.LabelField(Explain(view.State), EditorStyles.wordWrappedMiniLabel);

        using (new EditorGUILayout.HorizontalScope())
        {
          using (new EditorGUI.DisabledScope(!connected || !view.OnDisk))
          {
            if (GUILayout.Button(new GUIContent(pack.HasBeenPushed ? "Update backup" : "Upload",
              "Copies this pack from your PC to storage. Your local files are not changed.")))
            {
              Upload(pack, view.Dir);
            }
          }

          string label = view.OnDisk && pack.HasBeenPushed ? "Re-download" : "Download";
          string tip = !pack.HasBeenPushed
            ? "There is no copy in storage yet, so there is nothing to download."
            : view.OnDisk
              ? "Replaces the local folder with the backed-up copy."
              : "Copies this pack from storage back onto this PC.";
          using (new EditorGUI.DisabledScope(!connected || !pack.HasBeenPushed))
          {
            if (GUILayout.Button(new GUIContent(label, tip))) Download(pack, view.Dir);
          }

          using (new EditorGUI.DisabledScope(!view.OnDisk))
          {
            if (GUILayout.Button(new GUIContent("Check files",
              "Reads every .meta and compares it with the backup. Slow on big packs."),
              GUILayout.Width(84)))
            {
              _stats[pack.id] = VaultStatus.Inspect(view.Dir, true);
              _verified.Add(pack.id);
              // Not BuildViews(): the list is mid-draw. Dropping it makes the
              // next frame rebuild, which is the first frame that can show it.
              _views = null;
            }

            if (GUILayout.Button(new GUIContent("Show", "Open the folder in Explorer."),
              GUILayout.Width(50)))
            {
              EditorUtility.RevealInFinder(view.Dir);
            }
          }
        }

        DrawTaxonomy(view);
        DrawUsage(pack.id, view.Dir, view.AssetsNow);
        drop = DrawStopManaging(pack);
      }

      EditorGUILayout.Space(2);
      return drop;
    }

    /// <summary>
    /// Where a pack is filed and where it came from.
    ///
    /// <para>Only the answers a scan cannot reach are editable. What the pack is
    /// made of is shown beside them and greyed, because it is counted from the
    /// files themselves every reload - typing it would only be a chance to be
    /// wrong about it later.</para>
    /// </summary>
    private void DrawTaxonomy(PackView view)
    {
      VaultPack pack = view.Pack;

      using (new EditorGUILayout.HorizontalScope())
      {
        int current = VaultCategory.IsKnown(pack.category)
          ? VaultCategory.Order(pack.category) + 1
          : 0;
        int picked = EditorGUILayout.Popup(
          new GUIContent("Category", "What this pack primarily is. Groups the list."),
          current, CategoryLabels(), GUILayout.Width(260));
        if (picked != current)
        {
          pack.category = picked == 0
            ? VaultCategory.Uncategorised
            : VaultCategory.All[picked - 1];
          CommitTaxonomy();
        }
      }

      using (new EditorGUILayout.HorizontalScope())
      {
        EditorGUILayout.LabelField(new GUIContent("Where from",
          "The part no scan can work out."), GUILayout.Width(146));

        foreach (string tag in VaultTag.Provenance)
        {
          bool on = pack.tags.Contains(tag);
          bool now = EditorGUILayout.ToggleLeft(tag, on, GUILayout.Width(94));
          if (now == on) continue;

          if (now) pack.tags.Add(tag);
          else pack.tags.Remove(tag);
          CommitTaxonomy();
        }
      }

      using (new EditorGUILayout.HorizontalScope())
      {
        EditorGUILayout.LabelField(new GUIContent("Vendor",
          "Free text, stored as a vendor: tag - the one place the vocabulary is "
          + "open, because no fixed list holds every publisher's name."),
          GUILayout.Width(146));

        string vendor = string.Empty;
        foreach (string tag in pack.tags)
        {
          if (VaultTag.IsVendor(tag)) vendor = tag.Substring(VaultTag.VendorPrefix.Length);
        }

        // Delayed: a keystroke is not a decision, and every commit writes the
        // manifest and rebuilds the snapshot.
        string typed = EditorGUILayout.DelayedTextField(vendor, GUILayout.Width(180));
        if (typed != vendor)
        {
          pack.tags.RemoveAll(VaultTag.IsVendor);
          string clean = VaultTag.Normalise(typed);
          if (clean.Length > 0) pack.tags.Add(VaultTag.VendorPrefix + clean);
          CommitTaxonomy();
        }
      }

      using (new EditorGUI.DisabledScope(true))
      {
        EditorGUILayout.LabelField("Contains",
          view.Stats.Content.Count == 0
            ? view.OnDisk ? "nothing recognised" : "not on this PC, so nothing counted"
            : string.Join("  ", view.Stats.Content),
          EditorStyles.miniLabel);
      }
    }

    private void CommitTaxonomy()
    {
      _manifest.Save(VaultComposition.ManifestPath);
      // Not BuildViews(): the list is mid-draw. Dropping it rebuilds next frame.
      _views = null;
      Repaint();
    }

    private static string[] CategoryLabels()
    {
      var labels = new string[VaultCategory.All.Length + 1];
      labels[0] = "Uncategorised";
      for (int i = 0; i < VaultCategory.All.Length; i++)
      {
        labels[i + 1] = VaultCategory.Label(VaultCategory.All[i]);
      }

      return labels;
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

    /// <summary>
    /// "What does the game actually use from this?" - the question that decides
    /// whether a folder can be vaulted at all.
    /// </summary>
    /// <param name="assetsNow">
    /// How many assets the folder holds right now, counted once by the caller.
    /// Counting it here meant walking the whole pack every frame. -1 when there
    /// is no shared record to date, in which case nothing needs the number.
    /// </param>
    private void DrawUsage(string key, string dir, int assetsNow)
    {
      IVaultUsageReporter reporter = VaultUsageReporters.Active;
      bool onDisk = Directory.Exists(dir);

      using (new EditorGUILayout.HorizontalScope())
      {
        using (new EditorGUI.DisabledScope(reporter == null || !onDisk))
        {
          if (GUILayout.Button(new GUIContent(
                _usage.ContainsKey(key) ? "Check usage again" : "What does the game use?",
                reporter == null
                  ? "No usage reporter is installed."
                  : "Searches every scene, prefab and asset for references into this "
                    + "folder. Slow on a big pack - minutes, not seconds."),
                GUILayout.Width(180)))
          {
            // Asking again means asking again: drop whatever the reporter cached.
            if (_usage.ContainsKey(key)) reporter.Invalidate();
            RunUsage(reporter, key, dir);
          }
        }

        if (reporter == null)
        {
          EditorGUILayout.LabelField(
            "No usage reporter installed, so the vault cannot tell you what is in use.",
            EditorStyles.miniLabel);
        }
        else if (!_usage.ContainsKey(key))
        {
          EditorGUILayout.LabelField("via " + reporter.DisplayName, EditorStyles.miniLabel);
        }
      }

      if (!_usage.TryGetValue(key, out UsageReport report))
      {
        DrawSharedRecord(dir, assetsNow);
        return;
      }

      int used = report.UsedFiles.Count;
      EditorGUILayout.LabelField(
        used == 0
          ? $"Nothing in this folder is referenced ({report.TotalAssets} assets checked)."
          : $"{used} of {report.TotalAssets} assets are referenced by the project.",
        EditorStyles.wordWrappedMiniLabel);

      // The honest caveat, on the face of the result rather than in a doc: a
      // reference built at runtime is invisible to any static search, so this
      // narrows the work and never authorises a delete.
      EditorGUILayout.LabelField(
        "Files loaded by name at runtime cannot be detected, so treat this as a "
        + "shortlist, not proof. Uploading and downloading once is what makes a "
        + "mistake cost nothing.",
        EditorStyles.wordWrappedMiniLabel);

      if (used == 0) return;

      bool open = _usageExpanded.Contains(key);
      bool now = EditorGUILayout.Foldout(open, $"The {used} file(s) in use", true);
      if (now != open)
      {
        if (now) _usageExpanded.Add(key);
        else _usageExpanded.Remove(key);
      }

      if (now)
      {
        foreach (string f in report.UsedFiles)
        {
          EditorGUILayout.LabelField("    " + f, EditorStyles.miniLabel);
        }
      }

      if (GUILayout.Button(new GUIContent(
            $"Copy those {used} file(s) into Assets...",
            "Copies them, with their .meta files so nothing that points at them breaks, "
            + "into a folder you pick. The rest of the pack can then be vaulted.")))
      {
        Harvest(key, dir, report);
      }
    }

    private string Relative(string dir)
    {
      string root = VaultComposition.ProjectRoot.TrimEnd(Path.DirectorySeparatorChar);
      string full = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar);
      return full.Length > root.Length
        ? full.Substring(root.Length + 1).Replace('\\', '/')
        : full.Replace('\\', '/');
    }

    /// <summary>
    /// The answer somebody else already paid for. Shown with its age and a
    /// staleness check, because a shared snapshot is exactly the thing that
    /// gets acted on long after it stopped being true.
    /// </summary>
    private void DrawSharedRecord(string dir, int assetsNow)
    {
      VaultUsageRecord record = _findings?.Find(Relative(dir));
      if (record == null || assetsNow < 0) return;

      bool changed = assetsNow != record.totalAssets;

      EditorGUILayout.LabelField(
        $"Last checked {record.Age}: {record.usedFiles.Count} of {record.totalAssets} "
        + $"assets referenced, via {record.reporter}.",
        EditorStyles.wordWrappedMiniLabel);

      if (changed)
      {
        EditorGUILayout.HelpBox(
          $"That answer is out of date: the folder held {record.totalAssets} assets when it "
          + $"was checked and holds {assetsNow} now. Check it again before acting on it.",
          MessageType.Warning);
      }
      else
      {
        EditorGUILayout.LabelField(
          "From " + VaultUsageStore.DefaultRelativePath + ", so it may have come from "
          + "another machine. The folder is unchanged since, but a new reference to it "
          + "would not show up here.",
          EditorStyles.wordWrappedMiniLabel);
      }
    }

    private void RunUsage(IVaultUsageReporter reporter, string key, string dir)
    {
      _failure = null;
      try
      {
        UsageReport report = reporter.Report(dir, CancellationToken.None);
        _usage[key] = report;

        _findings ??= VaultUsageStore.Load(VaultComposition.ProjectRoot);
        _findings.Record(Relative(dir), report, report.ProjectFilesRead);
        _findings.Save(VaultComposition.ProjectRoot);
        Debug.Log($"[AssetVault] recorded in {VaultUsageStore.DefaultRelativePath}; "
                  + "commit it so nobody else has to run this scan.");
      }
      catch (OperationCanceledException)
      {
        Debug.Log("[AssetVault] usage search cancelled.");
      }
      catch (Exception e)
      {
        Debug.LogError("[AssetVault] " + e);
        _failure = string.IsNullOrEmpty(e.Message) ? e.GetType().Name : e.Message;
      }

      Repaint();
    }

    private void Harvest(string key, string dir, UsageReport report)
    {
      string picked = EditorUtility.SaveFolderPanel(
        "Where should the files still in use go?",
        Path.Combine(VaultComposition.ProjectRoot, "Assets"), string.Empty);
      if (string.IsNullOrEmpty(picked)) return;

      try
      {
        IReadOnlyList<string> written = VaultHarvest.Copy(
          VaultComposition.ProjectRoot, dir, report.UsedFiles, picked);
        AssetDatabase.Refresh();
        Debug.Log($"[AssetVault] copied {written.Count} file(s) out of {key}, GUIDs intact. "
                  + "The rest of the folder can now be vaulted.");
        _usage.Remove(key);
      }
      catch (Exception e)
      {
        Debug.LogError("[AssetVault] " + e);
        _failure = e.Message;
      }

      Repaint();
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
