using System.Security.Cryptography;
using Eds.Core.Fs.Vfs;

namespace Eds.Core.Services;

/// <summary>
/// Overwrites a file's data with random bytes before it is deleted. Port of the
/// intent of the Android <c>WipeFilesTask</c> secure-delete step.
///
/// <para><b>Backing-store caveat.</b> This is a true in-place overwrite only on
/// backings that write in place (e.g. <c>StdFs</c> on a real device file). On the
/// FAT write-back driver the whole file is rewritten to fresh clusters on flush,
/// so the original clusters are freed rather than overwritten — however, inside an
/// encrypted container those freed clusters hold ciphertext, so combined with the
/// entry removal this still meaningfully raises the bar against recovery. A future
/// pass can wipe at the raw-volume level for containers.</para>
/// </summary>
public static class WipeUtil
{
    private const int ChunkSize = 64 * 1024;

    /// <summary>
    /// Overwrites <paramref name="file"/>'s bytes <paramref name="passes"/> times
    /// with cryptographically-random data. <paramref name="onProgress"/> receives
    /// the number of bytes written per chunk.
    /// </summary>
    public static void Wipe(IFile file, int passes, CancellationToken ct, Action<long>? onProgress = null)
    {
        long size = file.GetSize();
        if (size <= 0) return;

        var buf = new byte[ChunkSize];
        for (int pass = 0; pass < Math.Max(1, passes); pass++)
        {
            using var io = file.GetRandomAccessIO(FileAccessMode.ReadWrite);
            io.Seek(0);
            long remaining = size;
            while (remaining > 0)
            {
                ct.ThrowIfCancellationRequested();
                int chunk = (int)Math.Min(ChunkSize, remaining);
                RandomNumberGenerator.Fill(buf.AsSpan(0, chunk));
                io.Write(buf, 0, chunk);
                remaining -= chunk;
                onProgress?.Invoke(chunk);
            }
            io.Flush();
        }
    }
}
