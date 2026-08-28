using System;
using System.Collections.Generic;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace CupkekGames.AssetVaults.Editor
{
  /// <summary>
  /// Everything the vault window needs to offer one backend, supplied by that
  /// backend's own assembly.
  /// </summary>
  public sealed class VaultBackendRegistration
  {
    /// <summary>Stable id, matching <see cref="IVaultBackend.Id"/>.</summary>
    public string Id;

    public string DisplayName;

    /// <summary>Builds the backend for a project root. Required.</summary>
    public Func<string, IVaultBackend> CreateBackend;

    /// <summary>
    /// The asset holding this backend's configuration, so the window can select
    /// and ping it without knowing what shape it is. Null when there is none yet.
    /// </summary>
    public Func<UnityEngine.Object> FindSettingsAsset;

    /// <summary>
    /// Creates that asset. Null if the backend needs no configuration; the
    /// window then simply offers no button.
    /// </summary>
    public Action CreateSettingsAsset;
  }

  /// <summary>
  /// Where backends announce themselves.
  ///
  /// <para>This exists to make the dependency point the right way. The window
  /// used to build a <c>GoogleDriveBackend</c> by name, which meant the generic
  /// editor assembly referenced the Drive one - fine in a single project,
  /// impossible to package: nobody could take the vault without also taking
  /// Google Drive. Now the Drive assembly references the core and registers into
  /// this, so the arrow runs core &lt;- backend and either can ship alone.</para>
  ///
  /// <para>Registration is an explicit call from a backend's
  /// <c>[InitializeOnLoad]</c>, not a type scan. Hard rule 1 of this project
  /// bans reflection, and this is the same shape Unity's own editor
  /// initialisation uses, so nothing is given up for it.</para>
  ///
  /// <para>Deliberately NOT cleaned up on statics teardown: a domain reload
  /// clears this list and re-runs every <c>[InitializeOnLoad]</c> that fills it,
  /// while entering play mode without a domain reload does neither. Clearing it
  /// on either boundary is what would break it.</para>
  /// </summary>
  public static class VaultBackends
  {
    // Opted out of statics cleanup on purpose, for the reason in the summary
    // above: entering play mode without a domain reload would clear this list
    // without re-running the [InitializeOnLoad] that fills it, and the window
    // would come back reporting no storage installed. The analyzer makes an
    // unclassified static a compile error once the package carries its
    // .globalconfig, so this says which of the two it is.
    [NoAutoStaticsCleanup]
    private static readonly List<VaultBackendRegistration> Registered =
      new List<VaultBackendRegistration>();

    public static IReadOnlyList<VaultBackendRegistration> All => Registered;

    /// <summary>
    /// The backend the window uses. One is the expected case; if a project ever
    /// installs two, the first to register wins and this is where a chooser
    /// would go.
    /// </summary>
    public static VaultBackendRegistration Active =>
      Registered.Count > 0 ? Registered[0] : null;

    public static void Register(VaultBackendRegistration registration)
    {
      if (registration == null)
      {
        throw new ArgumentNullException(nameof(registration));
      }

      if (string.IsNullOrEmpty(registration.Id) || registration.CreateBackend == null)
      {
        throw new ArgumentException(
          "A vault backend must register an Id and a CreateBackend factory.",
          nameof(registration));
      }

      // Replace rather than append: an assembly reload that re-runs
      // [InitializeOnLoad] against a list that somehow survived would otherwise
      // stack duplicates of the same backend.
      for (int i = 0; i < Registered.Count; i++)
      {
        if (string.Equals(Registered[i].Id, registration.Id, StringComparison.OrdinalIgnoreCase))
        {
          Registered[i] = registration;
          return;
        }
      }

      Registered.Add(registration);
    }
  }
}
