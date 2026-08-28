using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CupkekGames.AssetVaults
{
  /// <summary>
  /// Everything a storage backend must do, and nothing more.
  ///
  /// <para>Four operations, deliberately. Nothing above this interface may learn
  /// that a transfer involves OAuth, chunking, a subprocess or a particular
  /// vendor. The rule that keeps that honest: <b>no word from a backend's
  /// vocabulary (remote, chunk, oauth, drive, bucket) may appear in this
  /// assembly.</b> If one does, the abstraction has leaked and the second
  /// implementation will not fit.</para>
  /// </summary>
  public interface IVaultBackend
  {
    /// <summary>Stable id used in settings and logs, e.g. "gdrive".</summary>
    string Id { get; }

    string DisplayName { get; }

    /// <summary>
    /// Whether this backend can run right now. <paramref name="problem"/> must be
    /// ACTIONABLE text shown straight to the user ("rclone not found, press
    /// Download"), never a bare false: the readiness checklist renders it.
    /// </summary>
    bool IsConfigured(out string problem);

    Task<IReadOnlyList<RemoteObject>> ListAsync(CancellationToken ct);

    Task DownloadAsync(string remoteName, string localFile,
      IProgress<TransferProgress> progress, CancellationToken ct);

    Task UploadAsync(string localFile, string remoteName,
      IProgress<TransferProgress> progress, CancellationToken ct);
  }

  /// <summary>One object in the vault, as the backend reports it.</summary>
  public readonly struct RemoteObject
  {
    public readonly string Name;
    public readonly long Bytes;

    public RemoteObject(string name, long bytes)
    {
      Name = name;
      Bytes = bytes;
    }
  }

  /// <summary>
  /// Backend-neutral progress. Deliberately not a percentage: a backend that
  /// cannot know the total reports <see cref="TotalBytes"/> as 0 and the UI
  /// shows an indeterminate bar rather than a lie.
  /// </summary>
  public readonly struct TransferProgress
  {
    public readonly long TransferredBytes;
    public readonly long TotalBytes;
    public readonly string Note;

    public TransferProgress(long transferred, long total, string note)
    {
      TransferredBytes = transferred;
      TotalBytes = total;
      Note = note;
    }

    public bool HasTotal => TotalBytes > 0L;

    public float Fraction01 =>
      TotalBytes > 0L ? Math.Min(1f, (float)((double)TransferredBytes / TotalBytes)) : 0f;
  }

  /// <summary>Thrown when a backend cannot do its job. Message is user-facing.</summary>
  public class VaultException : Exception
  {
    public VaultException(string message) : base(message) { }
    public VaultException(string message, Exception inner) : base(message, inner) { }
  }
}
