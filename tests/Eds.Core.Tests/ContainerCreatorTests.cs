using System.Text;
using Eds.Core.Containers;
using Eds.Core.Fs;
using Eds.Core.Fs.Fat;
using Xunit;

namespace Eds.Core.Tests;

public class ContainerCreatorTests
{
    [Theory]
    [InlineData(ContainerCreator.Format.TrueCrypt)]
    [InlineData(ContainerCreator.Format.Luks)]
    public void Create_Format_Reopen_WriteRead(ContainerCreator.Format format)
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"eds_new_{Guid.NewGuid():N}.vol");
        var password = "create wizard password"u8.ToArray();
        var payload = "created from the app ✔"u8.ToArray();
        const long volumeSize = 8 * 1024 * 1024;

        try
        {
            ContainerCreator.Create(tmp, password, new ContainerCreator.Options
            {
                Format = format,
                Cipher = ContainerCreator.Cipher.Aes,
                Hash = ContainerCreator.Hash.Sha512,
                VolumeSize = volumeSize,
                FormatFat = true,
            });

            // reopen, mount FAT, write a file, read it back
            using (var baseIo = StreamRandomAccessIO.OpenFile(tmp, writable: true))
            using (var container = new EdsContainer(baseIo))
            {
                Assert.True(container.Open(password));
                using var vol = container.GetEncryptedVolume();
                var fs = FatFileSystem.Mount(vol);
                Assert.Equal(FatType.Fat16, fs.Type);
                fs.WriteFile("/hello.txt", payload);

                var entry = fs.ResolvePath("/hello.txt");
                Assert.NotNull(entry);
                Assert.Equal(payload, fs.ReadAllBytes(entry!));
            }

            // fully independent reopen to confirm persistence
            using (var baseIo = StreamRandomAccessIO.OpenFile(tmp, writable: false))
            using (var container = new EdsContainer(baseIo))
            {
                Assert.True(container.Open(password));
                using var vol = container.GetEncryptedVolume();
                var fs = FatFileSystem.Mount(vol);
                Assert.Equal(payload, fs.ReadAllBytes(fs.ResolvePath("/hello.txt")!));
            }
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void Create_WrongPassword_Rejected()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"eds_new_{Guid.NewGuid():N}.vol");
        try
        {
            ContainerCreator.Create(tmp, "right"u8.ToArray(), new ContainerCreator.Options
            {
                Format = ContainerCreator.Format.TrueCrypt,
                VolumeSize = 8 * 1024 * 1024,
                FormatFat = false,
            });
            using var baseIo = StreamRandomAccessIO.OpenFile(tmp, writable: false);
            using var container = new EdsContainer(baseIo);
            Assert.False(container.Open("wrong"u8.ToArray()));
        }
        finally { try { File.Delete(tmp); } catch { } }
    }
}
