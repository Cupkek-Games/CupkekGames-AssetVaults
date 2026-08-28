using System.IO;
using UnityEngine;

namespace CupkekGames.AssetVaults.Editor
{
  /// <summary>
  /// Project paths and the active backend, in one place so the window never
  /// works either out for itself.
  ///
  /// <para>This used to name <c>GoogleDriveBackend</c> directly and was called
  /// the composition root for that reason. Backends now announce themselves
  /// through <see cref="VaultBackends"/>, so nothing here knows what storage
  /// exists - which is the whole reason the core can be packaged on its
  /// own.</para>
  /// </summary>
  public static class VaultComposition
  {
    public static string ProjectRoot =>
      Directory.GetParent(Application.dataPath).FullName;

    public static string ManifestPath => VaultManifest.ResolvePath(ProjectRoot);

    /// <summary>
    /// The active backend, or null when no backend assembly is installed. The
    /// window renders that as a connection row rather than throwing.
    /// </summary>
    public static IVaultBackend ResolveBackend()
    {
      VaultBackendRegistration registration = VaultBackends.Active;
      return registration?.CreateBackend(ProjectRoot);
    }
  }
}
