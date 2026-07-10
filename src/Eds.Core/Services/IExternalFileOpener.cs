namespace Eds.Core.Services;

/// <summary>
/// Platform hook for handing a decrypted temp file to an external application and
/// returning once the user is done editing it. Implemented per platform (Android
/// <c>ACTION_VIEW</c>/<c>ACTION_EDIT</c>, desktop shell-open, iOS document
/// interaction); the core <see cref="TempFileManager"/> stays platform-independent
/// by depending only on this.
/// </summary>
public interface IExternalFileOpener
{
    /// <summary>
    /// Opens <paramref name="tempFilePath"/> externally and completes when the
    /// external interaction has finished (or as close to that as the platform can
    /// signal). <paramref name="mimeType"/> may be null if unknown.
    /// </summary>
    Task OpenAsync(string tempFilePath, string? mimeType, CancellationToken ct);
}
