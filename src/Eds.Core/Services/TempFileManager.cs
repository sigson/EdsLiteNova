using System.Security.Cryptography;
using Eds.Core.Fs.Vfs;

namespace Eds.Core.Services;

/// <summary>
/// A decrypted working copy of a location file on the real disk, plus the baseline
/// (size + content hash) used to tell whether the external app changed it.
/// </summary>
public sealed class TempFileHandle
{
    public IFile SourceFile { get; }
    public string TempPath { get; }
    public string OriginalName { get; }
    internal byte[] BaselineHash { get; set; }
    internal long BaselineSize { get; set; }

    internal TempFileHandle(IFile sourceFile, string tempPath, string originalName, byte[] hash, long size)
    {
        SourceFile = sourceFile;
        TempPath = tempPath;
        OriginalName = originalName;
        BaselineHash = hash;
        BaselineSize = size;
    }
}

/// <summary>
/// The "temp file" mechanism from the Android service (<c>PrepareTempFilesTask</c>/
/// <c>StartTempFileTask</c>/<c>SaveTempFileChangesTask</c>/<c>ClearTempFolderTask</c>),
/// lifted into platform-independent core: decrypt a file out of an opened location
/// into a temporary directory on the real filesystem, let an external application
/// edit it, detect whether it changed, and — if so — re-encrypt the new content
/// back into the location. Temp copies are securely cleared afterwards.
///
/// <para>The only platform dependency (actually launching the external editor) is
/// injected as <see cref="IExternalFileOpener"/>; everything here works over
/// <see cref="IFile"/> and <see cref="System.IO"/> and is unit-testable.</para>
/// </summary>
public sealed class TempFileManager
{
    private readonly string _tempDir;

    public TempFileManager(string? tempDir = null)
    {
        _tempDir = tempDir ?? Path.Combine(Path.GetTempPath(), "eds-temp");
        Directory.CreateDirectory(_tempDir);
    }

    public string TempDirectory => _tempDir;

    /// <summary>Decrypts <paramref name="source"/> into the temp directory and records a baseline.</summary>
    public TempFileHandle PrepareTempFile(IFile source)
    {
        string tempPath = Path.Combine(_tempDir, Guid.NewGuid().ToString("N") + "_" + Sanitize(source.GetName()));
        using (var input = source.GetInputStream())
        using (var fs = File.Create(tempPath))
            input.CopyTo(fs);

        var (hash, size) = HashFile(tempPath);
        return new TempFileHandle(source, tempPath, source.GetName(), hash, size);
    }

    /// <summary>True if the temp copy differs from the recorded baseline.</summary>
    public bool HasChanged(TempFileHandle handle)
    {
        if (!File.Exists(handle.TempPath)) return false;
        var (hash, size) = HashFile(handle.TempPath);
        return size != handle.BaselineSize || !hash.AsSpan().SequenceEqual(handle.BaselineHash);
    }

    /// <summary>
    /// If the temp copy changed, writes it back into the source location file
    /// (re-encrypting via the location's filesystem) and refreshes the baseline.
    /// Returns whether anything was written.
    /// </summary>
    public bool SaveChanges(TempFileHandle handle)
    {
        if (!HasChanged(handle)) return false;
        using (var input = File.OpenRead(handle.TempPath))
        using (var output = handle.SourceFile.GetOutputStream())
        {
            input.CopyTo(output);
            output.Flush();
        }
        var (hash, size) = HashFile(handle.TempPath);
        handle.BaselineHash = hash;
        handle.BaselineSize = size;
        return true;
    }

    /// <summary>Securely wipes and removes the temp copy.</summary>
    public void Clear(TempFileHandle handle) => WipeAndDelete(handle.TempPath);

    /// <summary>Securely wipes and removes every file in the temp directory.</summary>
    public void ClearAll()
    {
        if (!Directory.Exists(_tempDir)) return;
        foreach (var f in Directory.EnumerateFiles(_tempDir))
            WipeAndDelete(f);
    }

    /// <summary>
    /// Full round-trip: decrypt → open externally → (on return) save any changes
    /// back → securely clear. Returns whether changes were written back.
    /// </summary>
    public async Task<bool> OpenAndTrackAsync(
        IFile source, IExternalFileOpener opener, string? mimeType = null, CancellationToken ct = default)
    {
        var handle = PrepareTempFile(source);
        try
        {
            await opener.OpenAsync(handle.TempPath, mimeType, ct).ConfigureAwait(false);
            return SaveChanges(handle);
        }
        finally
        {
            Clear(handle);
        }
    }

    // ---- helpers -------------------------------------------------------

    private static (byte[] hash, long size) HashFile(string path)
    {
        using var fs = File.OpenRead(path);
        long size = fs.Length;
        return (SHA256.HashData(fs), size);
    }

    private static void WipeAndDelete(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            long len = new FileInfo(path).Length;
            if (len > 0)
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
                {
                    var buf = new byte[Math.Min(len, 64 * 1024)];
                    RandomNumberGenerator.Fill(buf);
                    long remaining = len;
                    fs.Seek(0, SeekOrigin.Begin);
                    while (remaining > 0)
                    {
                        int chunk = (int)Math.Min(buf.Length, remaining);
                        fs.Write(buf, 0, chunk);
                        remaining -= chunk;
                    }
                    fs.Flush(true);
                }
            }
            File.Delete(path);
        }
        catch { /* best-effort cleanup */ }
    }

    private static string Sanitize(string name)
    {
        Span<char> invalid = stackalloc char[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' };
        var chars = name.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (invalid.Contains(chars[i]) || char.IsControl(chars[i])) chars[i] = '_';
        var s = new string(chars).Trim();
        return string.IsNullOrEmpty(s) ? "file" : s;
    }
}
