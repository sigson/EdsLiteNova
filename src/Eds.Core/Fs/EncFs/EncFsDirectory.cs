using Eds.Core.Fs.Util;
using Eds.Core.Fs.Vfs;

namespace Eds.Core.Fs.EncFs;

/// <summary>
/// A directory inside an EncFS volume. Port of <c>fs.encfs.Directory</c>. Lists
/// the underlying (encrypted-name) directory and maps each child to an
/// <see cref="EncFsPath"/>, filtering out the config file and undecodable
/// entries. Create/rename go through the name codec.
/// </summary>
public sealed class EncFsDirectory : DirectoryWrapper
{
    public EncFsDirectory(EncFsPath path, IDirectory realDir) : base(path, realDir) { }

    public new EncFsPath Path => (EncFsPath)base.Path;

    public override string GetName() => Path.GetDecodedPath().GetFileName();

    public override IDirectoryContents List()
    {
        IDirectoryContents baseContents = GetBase().List();
        return new EncFsContents(this, baseContents);
    }

    public override void Rename(string newName)
    {
        if (Path.NamingCodecInfo.UseChainedNamingIV() || Path.EncFs.Config.UseExternalFileIV)
            throw new NotSupportedException("Rename is not supported with chained/external IV");
        StringPathUtil newEncoded = ((EncFsPath)Path.GetParentPath()!).CalcCombinedEncodedParts(newName);
        base.Rename(newEncoded.GetFileName());
    }

    public override IFile CreateFile(string name)
    {
        StringPathUtil? decodedPath = Path.GetDecodedPath();
        if (decodedPath != null) decodedPath = decodedPath.Combine(name);
        StringPathUtil newEncoded = Path.CalcCombinedEncodedParts(name);
        var res = (EncFsFile)base.CreateFile(newEncoded.GetFileName());
        res.EncFsPathTyped.SetEncodedPath(newEncoded);
        if (decodedPath != null) res.EncFsPathTyped.SetDecodedPath(decodedPath);
        res.GetOutputStream().Dispose(); // creates the (encrypted) empty file with its header
        return res;
    }

    public override IDirectory CreateDirectory(string name)
    {
        StringPathUtil? decodedPath = Path.GetDecodedPath();
        if (decodedPath != null) decodedPath = decodedPath.Combine(name);
        StringPathUtil newEncoded = Path.CalcCombinedEncodedParts(name);
        var res = (EncFsDirectory)base.CreateDirectory(newEncoded.GetFileName());
        res.Path.SetEncodedPath(newEncoded);
        if (decodedPath != null) res.Path.SetDecodedPath(decodedPath);
        return res;
    }

    public override void MoveTo(IDirectory dst)
    {
        if (Path.NamingCodecInfo.UseChainedNamingIV() || Path.EncFs.Config.UseExternalFileIV)
            throw new NotSupportedException("MoveTo is not supported with chained/external IV");
        base.MoveTo(dst);
    }

    protected override IPath GetPathFromBasePath(IPath basePath) => Path.EncFs.GetPathFromRealPath(basePath)!;

    private sealed class EncFsContents : IDirectoryContents
    {
        private readonly EncFsDirectory _owner;
        private readonly IDirectoryContents _base;

        public EncFsContents(EncFsDirectory owner, IDirectoryContents baseContents)
        {
            _owner = owner;
            _base = baseContents;
        }

        public void Dispose() => _base.Dispose();

        public IEnumerator<IPath> GetEnumerator()
        {
            foreach (var src in _base)
            {
                EncFsPath? p;
                try { p = _owner.Path.EncFs.GetPathFromRealPath(src); }
                catch { p = null; }
                if (p == null) continue;
                if (!IsValid(p)) continue;
                yield return p;
            }
        }

        private static bool IsValid(EncFsPath p)
        {
            try
            {
                var parent = (EncFsPath?)p.GetParentPath();
                if (parent == null || !parent.IsRootDirectory()) return true;
                string name = p.GetEncodedPath().GetFileName();
                return name != Config.ConfigFileName && name != Config.ConfigFileName2;
            }
            catch { return false; }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
