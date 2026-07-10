using Eds.Core.Crypto;
using Eds.Core.Crypto.Hash;
using Eds.Core.Fs;

namespace Eds.Core.Containers;

/// <summary>
/// Volume layout abstraction. Mirrors <c>container.VolumeLayout</c> and extends
/// <see cref="IEncryptedFileLayout"/> so an <see cref="EncryptedFile"/> can be
/// laid directly over a container using this layout.
/// </summary>
public interface IVolumeLayout : IEncryptedFileLayout, IDisposable
{
    /// <summary>Reads and validates the header; returns true if opened.</summary>
    bool ReadHeader(IRandomAccessIO input);

    void InitNew();

    void SetPassword(byte[]? password);
    void SetHashFunc(IMessageDigest? hf);
    IMessageDigest? GetHashFunc();
    void SetEngine(IFileEncryptionEngine? engine);
    void SetNumKDFIterations(int num);

    IReadOnlyList<IFileEncryptionEngine> GetSupportedEncryptionEngines();
    IReadOnlyList<IMessageDigest> GetSupportedHashFuncs();

    long GetEncryptedDataSize(long fileSize);

    void SetOpeningProgressReporter(IContainerOpeningProgressReporter? reporter);
}
