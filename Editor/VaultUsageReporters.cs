using System;
using System.Collections.Generic;
using System.Threading;
using Unity.Scripting.LifecycleManagement;

namespace CupkekGames.AssetVaults.Editor
{
  /// <summary>What a usage reporter found in one folder.</summary>
  public readonly struct UsageReport
  {
    /// <summary>Project-relative paths inside the folder that something references.</summary>
    public readonly IReadOnlyList<string> UsedFiles;

    /// <summary>Assets examined, so "3 of 2412" can be stated rather than "3".</summary>
    public readonly int TotalAssets;

    /// <summary>Who produced this, shown to the user so the answer has a source.</summary>
    public readonly string ReporterName;

    /// <summary>How many project files were examined, for a sense of coverage.</summary>
    public readonly int ProjectFilesRead;

    public UsageReport(IReadOnlyList<string> usedFiles, int totalAssets, string reporterName,
      int projectFilesRead = 0)
    {
      UsedFiles = usedFiles ?? Array.Empty<string>();
      TotalAssets = totalAssets;
      ReporterName = reporterName;
      ProjectFilesRead = projectFilesRead;
    }

    public bool AnythingUsed => UsedFiles.Count > 0;
  }

  /// <summary>
  /// Answers the one question that decides whether a folder can be vaulted:
  /// of everything in it, what does the project actually reference?
  ///
  /// <para><b>No implementation of this can be trusted to authorise a deletion.</b>
  /// A reference assembled at runtime - <c>Resources.Load</c> with a built path,
  /// an Addressables address from a string, a name looked up in a table - is
  /// invisible to every static analysis there is. A reporter narrows the work;
  /// the ordering rule does the protecting: nothing leaves git until a verified
  /// round trip has succeeded, so a wrong answer costs one Download press rather
  /// than the asset.</para>
  /// </summary>
  public interface IVaultUsageReporter
  {
    string DisplayName { get; }

    /// <summary>
    /// Higher wins when several are installed. The built-in scan is 0, so
    /// anything more thorough takes over simply by installing.
    /// </summary>
    int Priority { get; }

    /// <summary>
    /// Which files inside <paramref name="folder"/> are referenced from outside
    /// it. Runs synchronously and can take minutes on a large folder, so callers
    /// are expected to be an explicit user action, never a repaint.
    /// </summary>
    UsageReport Report(string folder, CancellationToken ct);

    /// <summary>
    /// Drop any cached view of the project, so the next report re-reads it.
    /// Called when the user explicitly asks for a fresh answer.
    /// </summary>
    void Invalidate();
  }

  /// <summary>
  /// Where usage reporters announce themselves, exactly as
  /// <see cref="VaultBackends"/> does for storage.
  ///
  /// <para>The vault ships no reporter of its own. Answering this question
  /// properly means walking serialized properties, prefab variants, nested
  /// prefabs, Addressables entries, VFX graphs and material properties, and a
  /// half-hearted scanner would <b>under</b>-report - which is the dangerous
  /// direction, because a missed reference reads as "safe to remove". Better to
  /// offer nothing and say so than to offer a confident wrong answer.</para>
  /// </summary>
  public static class VaultUsageReporters
  {
    // Same lifecycle as VaultBackends: a domain reload clears this and re-runs
    // the [InitializeOnLoad] that fills it; entering play mode does neither.
    [NoAutoStaticsCleanup]
    private static readonly List<IVaultUsageReporter> Registered = new List<IVaultUsageReporter>();

    public static IReadOnlyList<IVaultUsageReporter> All => Registered;

    /// <summary>
    /// The most thorough reporter installed. Registration order is whatever
    /// [InitializeOnLoad] happens to do, so it must not decide this.
    /// </summary>
    public static IVaultUsageReporter Active
    {
      get
      {
        IVaultUsageReporter best = null;
        foreach (IVaultUsageReporter r in Registered)
        {
          if (best == null || r.Priority > best.Priority) best = r;
        }

        return best;
      }
    }

    public static void Register(IVaultUsageReporter reporter)
    {
      if (reporter == null)
      {
        throw new ArgumentNullException(nameof(reporter));
      }

      // Keyed on the name, the way backends are keyed on their id: a re-run of
      // [InitializeOnLoad] replaces rather than stacks a second copy.
      for (int i = 0; i < Registered.Count; i++)
      {
        if (string.Equals(Registered[i].DisplayName, reporter.DisplayName,
              StringComparison.OrdinalIgnoreCase))
        {
          Registered[i] = reporter;
          return;
        }
      }

      Registered.Add(reporter);
    }
  }
}
