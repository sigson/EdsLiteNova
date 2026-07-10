using Eds.Core.Fs.Util;
using Eds.Core.Fs.Vfs;

namespace Eds.Core.Fs.EncFs;

/// <summary>
/// A path inside an EncFS volume. Faithful port of <c>fs.encfs.Path</c>. Wraps a
/// path in the underlying "real" filesystem (whose components are the encrypted
/// names) and lazily computes the decrypted path and the per-directory chained
/// IV used to encode child names. Name encoding/decoding goes through the
/// volume's <see cref="INameCodec"/>.
/// </summary>
public class EncFsPath : PathBase
{
    private readonly EncFsFs _fs;
    private readonly INameCodecInfo _namingInfo;
    private readonly IPath _realPath;
    private readonly byte[] _encryptionKey;
    private StringPathUtil? _encodedPath;
    private StringPathUtil? _decodedPath;
    private byte[]? _chainedIV;

    public EncFsPath(EncFsFs fs, IPath realPath, INameCodecInfo namingAlg, byte[] encryptionKey)
        : base(fs)
    {
        _fs = fs;
        _realPath = realPath;
        _namingInfo = namingAlg;
        _encryptionKey = encryptionKey;
    }

    public EncFsFs EncFs => _fs;
    public IPath RealPath => _realPath;
    public INameCodecInfo NamingCodecInfo => _namingInfo;

    public override string PathString => _realPath.PathString;
    public override string PathDesc => GetDecodedPath().ToString();

    public override bool Exists() => _realPath.Exists();
    public override bool IsFile() => _realPath.IsFile();
    public override bool IsDirectory() => _realPath.IsDirectory();

    public override IPath? GetParentPath()
    {
        if (_realPath.Equals(_fs.EncFsRootRealPath)) return null;
        var pp = _realPath.GetParentPath();
        return pp == null ? null : _fs.GetPathFromRealPath(pp);
    }

    public override IPath Combine(string part)
    {
        StringPathUtil encodedParts = CalcCombinedEncodedParts(part);
        IPath newRealPath = _realPath.Combine(encodedParts.GetFileName());
        EncFsPath newPath = _fs.GetPathFromRealPath(newRealPath)!;
        if (newPath._decodedPath == null)
        {
            StringPathUtil? decodedParts = _decodedPath;
            if (decodedParts != null) decodedParts = decodedParts.Combine(part);
            newPath.SetDecodedPath(decodedParts);
        }
        newPath._encodedPath ??= encodedParts;
        return newPath;
    }

    public StringPathUtil CalcCombinedEncodedParts(string part)
    {
        StringPathUtil encodedParts = GetEncodedPath();
        using INameCodec codec = _namingInfo.GetEncDec();
        byte[]? iv = _namingInfo.UseChainedNamingIV() ? GetChainedIV() : null;
        codec.Init(_encryptionKey);
        if (iv != null) codec.SetIV(iv);
        return encodedParts.Combine(codec.EncodeName(part));
    }

    public override IDirectory GetDirectory() => new EncFsDirectory(this, _realPath.GetDirectory());

    public override IFile GetFile()
    {
        Config c = _fs.Config;
        return new EncFsFile(
            this,
            _realPath.GetFile(),
            c.GetDataCodecInfo()!,
            _encryptionKey,
            c.UseExternalFileIV ? GetChainedIV() : null,
            c.BlockSize,
            c.UseUniqueIV,
            c.AllowHoles,
            c.MacBytes,
            c.MacRandBytes,
            false);
    }

    public override StringPathUtil GetPathUtil() => GetDecodedPath();

    public StringPathUtil GetDecodedPath()
    {
        if (_decodedPath == null)
        {
            try { _decodedPath = DecodePath(); }
            catch { _decodedPath = new StringPathUtil(_realPath.PathString); }
        }
        return _decodedPath;
    }

    public StringPathUtil GetEncodedPath()
        => _encodedPath ??= BuildEncodedPathFromRealPath(_realPath);

    public void SetDecodedPath(StringPathUtil? decodedPath) => _decodedPath = decodedPath;
    public void SetEncodedPath(StringPathUtil encodedPath) => _encodedPath = encodedPath;

    public virtual byte[]? GetChainedIV()
    {
        if (_chainedIV == null)
        {
            try { _chainedIV = CalcChainedIV(); }
            catch { /* leave null */ }
        }
        return _chainedIV;
    }

    public override bool IsRootDirectory() => GetEncodedPath().IsEmpty;

    private StringPathUtil DecodePath()
    {
        StringPathUtil encodedParts = GetEncodedPath();
        EncFsPath? parent = (EncFsPath?)GetParentPath();
        StringPathUtil decodedParent = parent == null ? new StringPathUtil() : parent.GetDecodedPath();
        using INameCodec codec = _namingInfo.GetEncDec();
        codec.Init(_encryptionKey);
        if (_namingInfo.UseChainedNamingIV() && parent != null)
            codec.SetIV(parent.GetChainedIV());
        string decodedName = codec.DecodeName(encodedParts.GetFileName());
        if (_namingInfo.UseChainedNamingIV())
            _chainedIV = codec.GetChainedIV(decodedName);
        return decodedParent.Combine(decodedName);
    }

    private byte[]? CalcChainedIV()
    {
        using INameCodec codec = _namingInfo.GetEncDec();
        codec.Init(_encryptionKey);
        if (_namingInfo.UseChainedNamingIV())
        {
            EncFsPath? parent = (EncFsPath?)GetParentPath();
            codec.SetIV(parent?.GetChainedIV());
        }
        return codec.GetChainedIV(GetDecodedPath().GetFileName());
    }

    internal StringPathUtil BuildEncodedPathFromRealPath(IPath realPath)
    {
        var encodedParts = new StringPathUtil();
        IPath rootPath = _fs.EncFsRootRealPath;
        IPath? cur = realPath;
        while (cur != null && !cur.Equals(rootPath))
        {
            encodedParts = new StringPathUtil(PathUtil.GetNameFromPath(cur), encodedParts);
            cur = cur.GetParentPath();
        }
        if (cur == null) throw new IOException("Failed building path");
        return encodedParts;
    }
}

/// <summary>The volume root path. Port of <c>FS.RootPath</c>.</summary>
public sealed class EncFsRootPath : EncFsPath
{
    public EncFsRootPath(EncFsFs fs)
        : base(fs, fs.EncFsRootRealPath, fs.Config.GetNameCodecInfo()!, fs.EncryptionKey!)
    {
        SetDecodedPath(new StringPathUtil());
        SetEncodedPath(new StringPathUtil());
    }

    public override bool IsRootDirectory() => true;
    public override IPath? GetParentPath() => null;

    public override byte[]? GetChainedIV()
    {
        using var codec = NamingCodecInfo.GetEncDec();
        return new byte[codec.IVSize];
    }
}
