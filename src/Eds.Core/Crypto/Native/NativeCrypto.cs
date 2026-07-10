using System.Runtime.InteropServices;

namespace Eds.Core.Crypto.Native;

/// <summary>
/// Source-generated P/Invoke bindings to the native <c>edscrypto</c> shim
/// (see native/include/edscrypto.h). Replaces the original JNI layer.
///
/// The library name is "edscrypto"; the .NET runtime resolves it to
/// edscrypto.dll / libedscrypto.so / libedscrypto.dylib per platform, or the
/// statically-linked "__Internal" image on iOS (see <see cref="NativeLibraryResolver"/>).
///
/// Buffers are passed as spans/arrays; the marshaller pins them for the call,
/// which is the managed equivalent of JNI's GetPrimitiveArrayCritical.
/// Contexts are opaque <see cref="nint"/> handles, wrapped by SafeHandles.
/// </summary>
internal static partial class NativeCrypto
{
    internal const string Lib = "edscrypto";

    // ---- AES -----------------------------------------------------------
    [LibraryImport(Lib, EntryPoint = "eds_aes_init")]
    internal static partial nint AesInit(ReadOnlySpan<byte> key, int keyLen);
    [LibraryImport(Lib, EntryPoint = "eds_aes_encrypt")]
    internal static partial void AesEncrypt(nint ctx, Span<byte> block);
    [LibraryImport(Lib, EntryPoint = "eds_aes_decrypt")]
    internal static partial void AesDecrypt(nint ctx, Span<byte> block);
    [LibraryImport(Lib, EntryPoint = "eds_aes_close")]
    internal static partial void AesClose(nint ctx);

    // ---- Serpent -------------------------------------------------------
    [LibraryImport(Lib, EntryPoint = "eds_serpent_init")]
    internal static partial nint SerpentInit(ReadOnlySpan<byte> key, int keyLen);
    [LibraryImport(Lib, EntryPoint = "eds_serpent_encrypt")]
    internal static partial void SerpentEncrypt(nint ctx, Span<byte> block);
    [LibraryImport(Lib, EntryPoint = "eds_serpent_decrypt")]
    internal static partial void SerpentDecrypt(nint ctx, Span<byte> block);
    [LibraryImport(Lib, EntryPoint = "eds_serpent_close")]
    internal static partial void SerpentClose(nint ctx);

    // ---- Twofish -------------------------------------------------------
    [LibraryImport(Lib, EntryPoint = "eds_twofish_init")]
    internal static partial nint TwofishInit(ReadOnlySpan<byte> key, int keyLen);
    [LibraryImport(Lib, EntryPoint = "eds_twofish_encrypt")]
    internal static partial void TwofishEncrypt(nint ctx, Span<byte> block);
    [LibraryImport(Lib, EntryPoint = "eds_twofish_decrypt")]
    internal static partial void TwofishDecrypt(nint ctx, Span<byte> block);
    [LibraryImport(Lib, EntryPoint = "eds_twofish_close")]
    internal static partial void TwofishClose(nint ctx);

    // ---- GOST 28147-89 -------------------------------------------------
    [LibraryImport(Lib, EntryPoint = "eds_gost_init")]
    internal static partial nint GostInit(ReadOnlySpan<byte> key, int keyLen, int useTestSbox);
    [LibraryImport(Lib, EntryPoint = "eds_gost_encrypt")]
    internal static partial void GostEncrypt(nint ctx, Span<byte> block);
    [LibraryImport(Lib, EntryPoint = "eds_gost_decrypt")]
    internal static partial void GostDecrypt(nint ctx, Span<byte> block);
    [LibraryImport(Lib, EntryPoint = "eds_gost_close")]
    internal static partial void GostClose(nint ctx);

    // ---- XTS mode ------------------------------------------------------
    [LibraryImport(Lib, EntryPoint = "eds_xts_init")]
    internal static partial nint XtsInit();
    [LibraryImport(Lib, EntryPoint = "eds_xts_attach")]
    internal static partial void XtsAttach(nint ctx, nint cipherA, nint cipherB);
    [LibraryImport(Lib, EntryPoint = "eds_xts_encrypt")]
    internal static partial int XtsEncrypt(nint ctx, Span<byte> data, int offset, int len, ulong sector);
    [LibraryImport(Lib, EntryPoint = "eds_xts_decrypt")]
    internal static partial int XtsDecrypt(nint ctx, Span<byte> data, int offset, int len, ulong sector);
    [LibraryImport(Lib, EntryPoint = "eds_xts_close")]
    internal static partial void XtsClose(nint ctx);

    // ---- CBC mode ------------------------------------------------------
    [LibraryImport(Lib, EntryPoint = "eds_cbc_init")]
    internal static partial nint CbcInit(int fileBlockSize);
    [LibraryImport(Lib, EntryPoint = "eds_cbc_attach")]
    internal static partial void CbcAttach(nint ctx, nint cipher);
    [LibraryImport(Lib, EntryPoint = "eds_cbc_encrypt")]
    internal static partial int CbcEncrypt(nint ctx, Span<byte> data, int offset, int len, Span<byte> iv16, int incrementIv);
    [LibraryImport(Lib, EntryPoint = "eds_cbc_decrypt")]
    internal static partial int CbcDecrypt(nint ctx, Span<byte> data, int offset, int len, Span<byte> iv16, int incrementIv);
    [LibraryImport(Lib, EntryPoint = "eds_cbc_close")]
    internal static partial void CbcClose(nint ctx);

    // ---- CFB-128 mode --------------------------------------------------
    [LibraryImport(Lib, EntryPoint = "eds_cfb_init")]
    internal static partial nint CfbInit();
    [LibraryImport(Lib, EntryPoint = "eds_cfb_attach")]
    internal static partial void CfbAttach(nint ctx, nint cipher);
    [LibraryImport(Lib, EntryPoint = "eds_cfb_encrypt")]
    internal static partial int CfbEncrypt(nint ctx, Span<byte> data, int offset, int len, Span<byte> iv16);
    [LibraryImport(Lib, EntryPoint = "eds_cfb_decrypt")]
    internal static partial int CfbDecrypt(nint ctx, Span<byte> data, int offset, int len, Span<byte> iv16);
    [LibraryImport(Lib, EntryPoint = "eds_cfb_close")]
    internal static partial void CfbClose(nint ctx);

    // ---- RIPEMD-160 ----------------------------------------------------
    [LibraryImport(Lib, EntryPoint = "eds_ripemd160_init")]
    internal static partial nint Ripemd160Init();
    [LibraryImport(Lib, EntryPoint = "eds_ripemd160_reset")]
    internal static partial void Ripemd160Reset(nint ctx);
    [LibraryImport(Lib, EntryPoint = "eds_ripemd160_update")]
    internal static partial void Ripemd160Update(nint ctx, ReadOnlySpan<byte> data, int offset, int len);
    [LibraryImport(Lib, EntryPoint = "eds_ripemd160_final")]
    internal static partial void Ripemd160Final(nint ctx, Span<byte> out20);
    [LibraryImport(Lib, EntryPoint = "eds_ripemd160_free")]
    internal static partial void Ripemd160Free(nint ctx);

    // ---- Whirlpool -----------------------------------------------------
    [LibraryImport(Lib, EntryPoint = "eds_whirlpool_init")]
    internal static partial nint WhirlpoolInit();
    [LibraryImport(Lib, EntryPoint = "eds_whirlpool_reset")]
    internal static partial void WhirlpoolReset(nint ctx);
    [LibraryImport(Lib, EntryPoint = "eds_whirlpool_update")]
    internal static partial void WhirlpoolUpdate(nint ctx, ReadOnlySpan<byte> data, int offset, int len);
    [LibraryImport(Lib, EntryPoint = "eds_whirlpool_final")]
    internal static partial void WhirlpoolFinal(nint ctx, Span<byte> out64);
    [LibraryImport(Lib, EntryPoint = "eds_whirlpool_free")]
    internal static partial void WhirlpoolFree(nint ctx);

    [LibraryImport(Lib, EntryPoint = "eds_crypto_version")]
    internal static partial int Version();
}
