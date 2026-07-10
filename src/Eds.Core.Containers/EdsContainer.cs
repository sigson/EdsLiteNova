using Eds.Core.Crypto;
using Eds.Core.Exceptions;
using Eds.Core.Fs;

namespace Eds.Core.Containers;

/// <summary>
/// Facade that opens a container by trying the known volume layouts in order
/// of priority. Simplified port of <c>container.EdsContainer</c>: it holds the
/// opened layout and exposes a transparent <see cref="EncryptedFile"/> over the
/// decrypted payload.
///
/// Priority mirrors the original sweep: VeraCrypt is tried before TrueCrypt
/// (its "VERA" signature is distinct; the far larger iteration count means a
/// TrueCrypt container is rejected quickly by VeraCrypt and vice-versa).
/// </summary>
public sealed class EdsContainer : IDisposable
{
    private readonly IRandomAccessIO _io;
    private IVolumeLayout? _layout;

    public EdsContainer(IRandomAccessIO io) => _io = io;

    public IVolumeLayout Layout =>
        _layout ?? throw new InvalidOperationException("Container is not open");

    /// <summary>
    /// Attempts to open the container with the given password.
    /// </summary>
    /// <param name="password">UTF-8 password bytes (cleared by the caller/SecureBuffer).</param>
    /// <param name="reporter">Optional progress/cancellation.</param>
    /// <returns>true if a layout opened.</returns>
    public bool Open(byte[] password, IContainerOpeningProgressReporter? reporter = null)
        => Open(password, new ContainerOpenOptions { Reporter = reporter });

    /// <summary>
    /// Attempts to open the container with a password plus optional keyfiles / PIM
    /// (see <see cref="ContainerOpenOptions"/>). Keyfiles are mixed into the
    /// password for TrueCrypt/VeraCrypt layouts; the PIM selects the VeraCrypt KDF
    /// iteration count.
    /// </summary>
    public bool Open(byte[] password, ContainerOpenOptions options)
    {
        var reporter = options.Reporter;
        foreach (var factory in LayoutFactories)
        {
            var layout = factory();
            byte[]? effective = null;
            try
            {
                layout.SetOpeningProgressReporter(reporter);
                // Keyfiles apply only to TrueCrypt/VeraCrypt (StdLayout); LUKS uses
                // the raw password (its own keyslot scheme is separate).
                effective = layout is StdLayout
                    ? KeyfileMixer.Apply(password, options.Keyfiles)
                    : (byte[])password.Clone();
                layout.SetPassword(effective);
                effective = null; // ownership transferred to the layout
                if (options.Pim > 0) layout.SetNumKDFIterations(options.Pim);
                _io.Seek(0);
                if (layout.ReadHeader(_io))
                {
                    _layout = layout;
                    return true;
                }
            }
            catch (OperationCancelledException)
            {
                if (effective != null) Array.Clear(effective);
                layout.Dispose();
                throw;
            }
            catch (EdsException)
            {
                // wrong format/version/CRC for this layout - try the next one
            }
            if (effective != null) Array.Clear(effective);
            layout.Dispose();
        }
        return false;
    }

    /// <summary>
    /// Re-encrypts the volume header with a new password (and optional new keyfiles
    /// / PIM for TrueCrypt/VeraCrypt), keeping the same master key so existing data
    /// stays readable. The container must already be open. Port of
    /// <c>ChangeContainerPasswordTask</c>; supports TrueCrypt/VeraCrypt (header
    /// re-encryption) and LUKS (keyslot re-keying).
    /// </summary>
    public void ChangePassword(byte[] newPassword, ContainerOpenOptions? options = null)
    {
        switch (_layout)
        {
            case StdLayout sl:
            {
                var eff = KeyfileMixer.Apply(newPassword, options?.Keyfiles);
                sl.SetPassword(eff); // layout takes ownership + clears on dispose
                sl.SetNumKDFIterations(options?.Pim ?? 0);
                sl.WriteHeader(_io); // re-derives the header key; master key unchanged
                break;
            }
            case LuksLayout luks:
                luks.ChangePassword((byte[])newPassword.Clone(), _io);
                break;
            default:
                throw new NotSupportedException("Password change is not supported for this container type.");
        }
        _io.Flush();
    }

    /// <summary>Opens a transparent read/write view of the decrypted volume.</summary>
    public EncryptedFile GetEncryptedVolume()
    {
        if (_layout == null) throw new InvalidOperationException("Container is not open");
        return new EncryptedFile(_io, _layout);
    }

    /// <summary>
    /// Opens a transparent read/write view backed by an LRU sector cache. This
    /// is the variant a mounted filesystem should use (see
    /// <see cref="EncryptedFileWithCache"/>): repeated navigation over the same
    /// directory sectors is served from the cache instead of re-decrypting.
    /// </summary>
    public EncryptedFileWithCache GetCachedEncryptedVolume()
    {
        if (_layout == null) throw new InvalidOperationException("Container is not open");
        return new EncryptedFileWithCache(_io, _layout);
    }

    private static readonly Func<IVolumeLayout>[] LayoutFactories =
    {
        () => new LuksLayout(),      // cheap magic check ("LUKS\xba\xbe")
        () => new VeraCryptLayout(),
        () => new StdLayout(),       // TrueCrypt
    };

    public void Dispose()
    {
        _layout?.Dispose();
        _io.Dispose();
    }
}
