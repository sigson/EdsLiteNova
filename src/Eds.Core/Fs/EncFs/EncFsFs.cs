using System.Security.Cryptography;
using Eds.Core.Fs.Util;
using Eds.Core.Fs.Vfs;

namespace Eds.Core.Fs.EncFs;

/// <summary>
/// An EncFS volume presented as a filesystem over an underlying "real" directory
/// tree (which holds the encrypted files with encrypted names). Faithful port of
/// <c>fs.encfs.FS</c>. Opening derives the master key from the password (see
/// <see cref="EncFsVolumeKey"/>); creating generates a random master key and
/// writes a fresh <c>encfs6.xml</c>.
/// </summary>
public sealed class EncFsFs : FileSystemWrapper
{
    private readonly IPath _rootRealPath;
    private readonly Dictionary<IPath, EncFsPath> _cache = new();
    private readonly EncFsRootPath _rootPath;
    private byte[]? _encryptionKey;
    private readonly Config _config;

    /// <summary>Opens an existing EncFS volume rooted at <paramref name="rootPath"/>.</summary>
    public EncFsFs(IPath rootPath, byte[] password) : base(rootPath.FileSystem)
    {
        _config = new Config();
        _config.Read(rootPath);
        _rootRealPath = rootPath;

        var derivedKey = EncFsVolumeKey.DeriveKey(password, _config);
        try { _encryptionKey = EncFsVolumeKey.DecryptVolumeKey(derivedKey, _config); }
        finally { Array.Clear(derivedKey); }

        _rootPath = new EncFsRootPath(this);
    }

    /// <summary>Creates a new EncFS volume (writes encfs6.xml) rooted at <paramref name="rootPath"/>.</summary>
    public EncFsFs(IPath rootPath, Config config, byte[] password) : base(rootPath.FileSystem)
    {
        _config = config;
        _rootRealPath = rootPath;

        int keyLen;
        using (var fe = config.GetDataCodecInfo()!.GetFileEncDec()) keyLen = fe.KeySize;
        _encryptionKey = RandomBytes(keyLen);

        EncryptVolumeKeyAndWriteConfig(password);
        _rootPath = new EncFsRootPath(this);
    }

    public Config Config => _config;
    public IPath EncFsRootRealPath => _rootRealPath;
    internal byte[]? EncryptionKey => _encryptionKey;

    public override IPath GetRootPath() => _rootPath;

    public override IPath GetPath(string pathString)
        => GetPathFromRealPath(GetBase().GetPath(pathString))!;

    public override void Close(bool force)
    {
        if (_encryptionKey != null)
        {
            Array.Clear(_encryptionKey);
            _encryptionKey = null;
        }
    }

    internal EncFsPath? GetPathFromRealPath(IPath? realPath)
    {
        if (realPath == null) return null;
        if (realPath.Equals(_rootRealPath)) return _rootPath;
        if (_cache.TryGetValue(realPath, out var cached)) return cached;
        var p = new EncFsPath(this, realPath, _config.GetNameCodecInfo()!, _encryptionKey!);
        _cache[realPath] = p;
        return p;
    }

    private void EncryptVolumeKeyAndWriteConfig(byte[] password)
    {
        _config.Salt = RandomBytes(20);
        var derivedKey = EncFsVolumeKey.DeriveKey(password, _config);
        try { _config.EncryptedVolumeKey = EncFsVolumeKey.EncryptVolumeKey(derivedKey, _encryptionKey!, _config); }
        finally { Array.Clear(derivedKey); }
        _config.Write(_rootRealPath);
    }

    private static byte[] RandomBytes(int n)
    {
        var b = new byte[n];
        RandomNumberGenerator.Fill(b);
        return b;
    }
}
