using Eds.Core.Crypto;
using Eds.Core.Crypto.Engines;
using Eds.Core.Crypto.Hash;
using Eds.Core.Fs;
using Eds.Core.Fs.Fat;

namespace Eds.Core.Containers;

/// <summary>
/// Orchestrates creation of a new encrypted container and (optionally) formats a
/// FAT filesystem inside it — the "create" counterpart to <see cref="EdsContainer"/>.
/// Reuses the tested write paths (StdLayout/VeraCryptLayout/LuksLayout.FormatNew
/// and <see cref="FatFormatter"/>).
/// </summary>
public static class ContainerCreator
{
    public enum Format { TrueCrypt, VeraCrypt, Luks }
    public enum Cipher { Aes, Serpent, Twofish }
    public enum Hash { Sha512, Sha256, Ripemd160, Whirlpool }

    public sealed class Options
    {
        public Format Format { get; init; } = Format.VeraCrypt;
        public Cipher Cipher { get; init; } = Cipher.Aes;
        public Hash Hash { get; init; } = Hash.Sha512;
        /// <summary>Usable volume size in bytes (the FAT payload). Min ~4 MiB for FAT16.</summary>
        public long VolumeSize { get; init; } = 16 * 1024 * 1024;
        public bool FormatFat { get; init; } = true;

        /// <summary>Keyfiles to mix into the password (TrueCrypt/VeraCrypt only).</summary>
        public IReadOnlyList<Func<Stream>>? Keyfiles { get; init; }

        /// <summary>VeraCrypt PIM (custom KDF iterations). 0 = format default. Ignored for TrueCrypt/LUKS.</summary>
        public int Pim { get; init; }
    }

    /// <summary>Creates the container file at <paramref name="path"/>.</summary>
    public static void Create(string path, byte[] password, Options options)
    {
        using var io = StreamRandomAccessIO.OpenFile(path, writable: true);
        Create(io, password, options);
    }

    /// <summary>Creates a container in an already-open writable IO.</summary>
    public static void Create(IRandomAccessIO io, byte[] password, Options options)
    {
        var layout = BuildLayout(options);
        try
        {
            // Keyfiles apply to TrueCrypt/VeraCrypt; LUKS keeps the raw password.
            byte[] effective = options.Format == Format.Luks
                ? (byte[])password.Clone()
                : KeyfileMixer.Apply(password, options.Keyfiles);
            layout.SetPassword(effective);
            layout.SetHashFunc(MakeHash(options.Hash));
            layout.SetEngine(MakeEngine(options.Cipher));
            if (options.Pim > 0) layout.SetNumKDFIterations(options.Pim);

            long volumeSize = options.VolumeSize;
            switch (options.Format)
            {
                case Format.Luks:
                    ((LuksLayout)layout).FormatNew(io, volumeSize);
                    break;
                default: // TrueCrypt / VeraCrypt take the *total* container size
                    long reserved = 2 * StdLayout.HeaderSize * 2; // header + backup areas
                    ((StdLayout)layout).FormatNew(io, volumeSize + reserved);
                    break;
            }

            if (options.FormatFat)
            {
                using var vol = new EncryptedFile(io, layout);
                FatFormatter.FormatFat16(vol, volumeSize);
                vol.Flush();
            }
        }
        finally
        {
            layout.Dispose();
        }
    }

    private static VolumeLayoutBase BuildLayout(Options o) => o.Format switch
    {
        Format.TrueCrypt => new StdLayout(),
        Format.VeraCrypt => new VeraCryptLayout(),
        Format.Luks => new LuksLayout(),
        _ => throw new ArgumentOutOfRangeException(nameof(o)),
    };

    private static IFileEncryptionEngine MakeEngine(Cipher c) => c switch
    {
        Cipher.Aes => new AesXts(),
        Cipher.Serpent => new SerpentXts(),
        Cipher.Twofish => new TwofishXts(),
        _ => new AesXts(),
    };

    private static IMessageDigest MakeHash(Hash h) => h switch
    {
        Hash.Sha512 => BclDigest.Sha512(),
        Hash.Sha256 => BclDigest.Sha256(),
        Hash.Ripemd160 => new Ripemd160(),
        Hash.Whirlpool => new Whirlpool(),
        _ => BclDigest.Sha512(),
    };
}
