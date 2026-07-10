using System.Buffers.Binary;
using System.Security.Cryptography;
using Eds.Core.Crypto;
using Eds.Core.Crypto.Hash;
using Eds.Core.Crypto.Kdf;
using Eds.Core.Crypto.Modes;
using Eds.Core.Exceptions;
using Eds.Core.Fs;

namespace Eds.Core.Containers;

/// <summary>
/// Common base for volume layouts. Faithful port of
/// <c>container.VolumeLayoutBase</c>: holds master key / password / engine /
/// hash, orchestrates PBKDF2, computes the per-block IV and generates fresh
/// key material for new volumes.
/// </summary>
public abstract class VolumeLayoutBase : IVolumeLayout
{
    protected const int SectorSize = 512;

    protected IFileEncryptionEngine? EncEngine;
    protected IMessageDigest? HashFunc;
    protected byte[]? MasterKey;
    protected byte[]? Password;
    protected IContainerOpeningProgressReporter? OpeningProgressReporter;

    private bool _invertIV;

    // ---- IEncryptedFileLayout -----------------------------------------
    public abstract long EncryptedDataOffset { get; }
    public IFileEncryptionEngine Engine =>
        EncEngine ?? throw new InvalidOperationException("Encryption engine is not set");

    public virtual void SetEncryptionEngineIV(IFileEncryptionEngine eng, long decryptedVolumeOffset)
    {
        long block = (decryptedVolumeOffset + EncryptedDataOffset) / eng.FileBlockSize;
        eng.SetIV(GetIVFromBlockIndex(block));
    }

    // ---- IVolumeLayout ------------------------------------------------
    public virtual void InitNew()
    {
        if (EncEngine == null) throw new InvalidOperationException("Encryption engine is not set");
        if (MasterKey != null) Array.Clear(MasterKey);
        MasterKey = new byte[EncEngine.KeySize];
        RandomNumberGenerator.Fill(MasterKey);
    }

    public virtual bool ReadHeader(IRandomAccessIO input)
    {
        if (Password == null) throw new InvalidOperationException("Password is not set");
        return false;
    }

    public void SetHashFunc(IMessageDigest? hf) => HashFunc = hf;
    public IMessageDigest? GetHashFunc() => HashFunc;

    public void SetPassword(byte[]? password)
    {
        if (Password != null) Array.Clear(Password);
        Password = password;
    }

    public virtual void SetNumKDFIterations(int num) { }

    public IFileEncryptionEngine? GetEngine() => EncEngine;

    public void SetEngine(IFileEncryptionEngine? engine)
    {
        EncEngine?.Dispose();
        EncEngine = engine;
        _invertIV = engine != null &&
                    string.Equals(engine.CipherModeName, "cbc-plain", StringComparison.OrdinalIgnoreCase);
    }

    public virtual IReadOnlyList<IFileEncryptionEngine> GetSupportedEncryptionEngines() => Array.Empty<IFileEncryptionEngine>();
    public virtual IReadOnlyList<IMessageDigest> GetSupportedHashFuncs() => Array.Empty<IMessageDigest>();

    public abstract long GetEncryptedDataSize(long fileSize);

    public void SetOpeningProgressReporter(IContainerOpeningProgressReporter? reporter) => OpeningProgressReporter = reporter;

    public virtual void Dispose()
    {
        if (MasterKey != null) { Array.Clear(MasterKey); MasterKey = null; }
        if (Password != null) { Array.Clear(Password); Password = null; }
        SetEngine(null);
        GC.SuppressFinalize(this);
    }

    // ---- helpers ------------------------------------------------------
    protected byte[] DeriveKey(int keySize, IMessageDigest hashFunc, byte[] password, byte[] salt, int numIterations)
    {
        var kdf = new HashBasedPbkdf2(hashFunc)
        {
            Progress = OpeningProgressReporter?.Progress,
            CancellationToken = OpeningProgressReporter?.CancellationToken ?? CancellationToken.None,
        };
        try
        {
            return kdf.DeriveKey(password, salt, numIterations, keySize);
        }
        catch (OperationCancelledException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw new EdsException("Failed deriving key", e);
        }
    }

    protected byte[] GetIVFromBlockIndex(long blockIndex)
    {
        var buf = new byte[Engine.IVSize];
        if (_invertIV)
            BinaryPrimitives.WriteInt64LittleEndian(buf, blockIndex);
        else
            BinaryPrimitives.WriteInt64BigEndian(buf, blockIndex);
        return buf;
    }

    protected void CheckReadHeaderPrereqs()
    {
        if (Password == null) throw new InvalidOperationException("The password is not set");
    }

    public static string GetEncEngineName(IEncryptionEngine ee) => $"{ee.CipherName}-{ee.CipherModeName}";
}
