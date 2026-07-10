using System.Text;
using Eds.Core.Fs;
using Eds.Core.Fs.Std;
using Eds.Core.Fs.Util;
using Eds.Core.Fs.Vfs;
using Xunit;

namespace Eds.Core.Tests;

/// <summary>
/// Phase C: exercises the fs/util decorator wrappers (FileSystemWrapper /
/// DirectoryWrapper / FileWrapper / FSRecordWrapper) via a trivial identity
/// wrapper over StdFs. This validates the plumbing (path re-mapping, contents
/// mapping, create/list delegation) that EncFS builds on. See gap guide §4.2.
/// </summary>
public class WrappersTests
{
    // --- identity wrapper set ------------------------------------------

    private sealed class IdFs : FileSystemWrapper
    {
        public IdFs(IFileSystem b) : base(b) { }
        public override IPath GetRootPath() => Wrap(GetBase().GetRootPath());
        public override IPath GetPath(string s) => Wrap(GetBase().GetPath(s));
        internal IPath Wrap(IPath real) => new IdPath(this, real);
    }

    private sealed class IdPath : PathBase
    {
        private readonly IdFs _fs;
        private readonly IPath _real;
        public IdPath(IdFs fs, IPath real) : base(fs) { _fs = fs; _real = real; }
        internal IdFs Fs => _fs;
        public override string PathString => _real.PathString;
        public override bool Exists() => _real.Exists();
        public override bool IsFile() => _real.IsFile();
        public override bool IsDirectory() => _real.IsDirectory();
        public override IDirectory GetDirectory() => new IdDir(this, _real.GetDirectory());
        public override IFile GetFile() => new IdFile(this, _real.GetFile());
    }

    private sealed class IdDir : DirectoryWrapper
    {
        public IdDir(IPath p, IDirectory b) : base(p, b) { }
        protected override IPath GetPathFromBasePath(IPath basePath) => ((IdPath)Path).Fs.Wrap(basePath);
    }

    private sealed class IdFile : FileWrapper
    {
        public IdFile(IPath p, IFile b) : base(p, b) { }
        protected override IPath GetPathFromBasePath(IPath basePath) => ((IdPath)Path).Fs.Wrap(basePath);
    }

    // --- test ----------------------------------------------------------

    private static string NewTempRoot()
    {
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"eds_wrap_{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Wrappers_Delegate_Create_List_Read_Rename()
    {
        string root = NewTempRoot();
        try
        {
            var fs = new IdFs(new StdFs(root));
            var rootDir = fs.GetRootPath().GetDirectory();

            var sub = rootDir.CreateDirectory("wsub");
            Assert.IsType<IdDir>(sub); // create returns a wrapped directory

            var file = sub.CreateFile("w.bin");
            Assert.IsType<IdFile>(file);

            var payload = Encoding.UTF8.GetBytes("wrapper delegation test");
            using (var io = file.GetRandomAccessIO(FileAccessMode.ReadWrite))
            {
                io.Write(payload, 0, payload.Length);
                io.Flush();
            }

            // list returns wrapped paths
            using (var contents = sub.List())
            {
                bool found = false;
                foreach (var p in contents)
                {
                    Assert.IsType<IdPath>(p);
                    if (new StringPathUtil(p.PathString).GetFileName() == "w.bin") found = true;
                }
                Assert.True(found);
            }

            // read back through a freshly resolved wrapped path
            var reopened = fs.GetPath("/wsub/w.bin").GetFile();
            Assert.Equal(payload.Length, reopened.GetSize());
            using (var io = reopened.GetRandomAccessIO(FileAccessMode.Read))
            {
                var read = new byte[payload.Length];
                int total = 0, n;
                while (total < read.Length && (n = io.Read(read, total, read.Length - total)) > 0) total += n;
                Assert.True(read.AsSpan().SequenceEqual(payload));
            }

            // rename via wrapper re-maps the path
            reopened.Rename("renamed.bin");
            Assert.True(fs.GetPath("/wsub/renamed.bin").Exists());
            Assert.False(fs.GetPath("/wsub/w.bin").Exists());
        }
        finally
        {
            try { System.IO.Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }
}
