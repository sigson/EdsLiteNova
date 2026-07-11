using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Eds.Core.Containers;
using Eds.Core.Crypto.BlockCiphers;
using Eds.Core.Crypto.Engines;
using Eds.Core.Crypto.Hash;
using Eds.Core.Crypto.Kdf;
using Eds.Core.Fs;

namespace Eds.Maui.ViewModels;

/// <summary>
/// Drives the home page. Exposes a crypto self-test (proves the managed->native
/// pipeline works on this device) and a container-open flow with live progress.
/// This is the same logic the console host runs, so you can validate the core
/// on a desktop head before ever touching an emulator.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly Eds.Maui.Services.IFilePickerService _filePicker;

    public MainViewModel(Eds.Maui.Services.IFilePickerService filePicker)
        => _filePicker = filePicker;

    [ObservableProperty] private string _log = "";
    [ObservableProperty] private string _containerPath = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private int _progress;
    [ObservableProperty] private bool _busy;

    /// <summary>Progress as a 0..1 fraction for the ProgressBar.</summary>
    public double ProgressFraction => Progress / 100.0;

    partial void OnProgressChanged(int value) => OnPropertyChanged(nameof(ProgressFraction));

    private void Append(string line) => Log += line + Environment.NewLine;

    [RelayCommand]
    private async Task RunSelfTestAsync()
    {
        Log = "";
        Busy = true;
        try
        {
            await Task.Run(() =>
            {
                // AES FIPS-197
                using var aes = new Aes(32);
                aes.Init(Convert.FromHexString("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f"));
                var blk = Convert.FromHexString("00112233445566778899aabbccddeeff");
                aes.EncryptBlock(blk);
                Append(Convert.ToHexString(blk).ToLowerInvariant() == "8ea2b7ca516745bfeafc49904b496089"
                    ? "AES-256 KAT ...... OK" : "AES-256 KAT ...... FAIL");

                using var rmd = new Ripemd160();
                Append(Convert.ToHexString(rmd.DoFinal("abc"u8.ToArray())).ToLowerInvariant()
                       == "8eb208f7e05d987a9b044a8e98c6b087f15a0bfc"
                    ? "RIPEMD-160 KAT ... OK" : "RIPEMD-160 KAT ... FAIL");

                using var wp = new Whirlpool();
                Append(wp.DoFinal("abc"u8.ToArray()).Length == 64 ? "Whirlpool ........ OK" : "Whirlpool ........ FAIL");

                var kdf = new HashBasedPbkdf2(BclDigest.Sha1(), 64);
                var dk = kdf.DeriveKey("password"u8.ToArray(), "salt"u8.ToArray(), 2, 20);
                Append(Convert.ToHexString(dk).ToLowerInvariant() == "ea6c014dc72d6f8ccd1ed92ace1d41f0d8de8957"
                    ? "PBKDF2 RFC6070 ... OK" : "PBKDF2 RFC6070 ... FAIL");

                using var xts = new AesXts();
                var key = new byte[64];
                for (int i = 0; i < 64; i++) key[i] = (byte)(i + 1);
                xts.SetKey(key); xts.Init();
                var data = new byte[1024];
                var orig = (byte[])data.Clone();
                for (int i = 0; i < data.Length; i++) { data[i] = (byte)i; orig[i] = (byte)i; }
                var iv = new byte[16];
                xts.SetIV(iv); xts.Encrypt(data, 0, data.Length);
                xts.SetIV(iv); xts.Decrypt(data, 0, data.Length);
                Append(data.AsSpan().SequenceEqual(orig) ? "AES-XTS round-trip  OK" : "AES-XTS round-trip  FAIL");
            });
            Append("\nSelf-test complete.");
        }
        catch (Exception ex)
        {
            Append("ERROR: " + ex.Message);
        }
        finally
        {
            Busy = false;
        }
    }

    [RelayCommand]
    private async Task PickContainerAsync()
    {
        try
        {
            var result = await _filePicker.PickFileAsync();
            if (result.IsSuccessful && result.Path != null) ContainerPath = result.Path;
        }
        catch (Exception ex)
        {
            Log += $"File picker unavailable: {ex.Message}\nType the container path manually.\n";
        }
    }

    [RelayCommand]
    private async Task OpenContainerAsync()
    {
        if (string.IsNullOrWhiteSpace(ContainerPath)) { Append("Pick a container file first."); return; }
        Log = "";
        Busy = true;
        Progress = 0;
        var progress = new Progress<int>(p => Progress = p);
        try
        {
            await Task.Run(() =>
            {
                using var io = StreamRandomAccessIO.OpenFile(ContainerPath, writable: false);
                using var container = new EdsContainer(io);
                var reporter = new DelegateProgressReporter
                {
                    Progress = progress,
                    OnKdf = k => Append($"trying KDF: {k}"),
                    OnCipher = c => Append($"trying cipher: {c}"),
                };
                bool ok = container.Open(Encoding.UTF8.GetBytes(Password), reporter);
                if (!ok) { Append("\nFailed: wrong password or unsupported format."); return; }
                Append("\nOpened!");
                Append($"cipher: {container.Layout.Engine.CipherName}-{container.Layout.Engine.CipherModeName}");
                Append($"hash:   {container.Layout.GetHashFunc()?.Algorithm}");
                Append("(File manager arrives with the FS layer - see roadmap.)");
            });
        }
        catch (Exception ex)
        {
            Append("ERROR: " + ex.Message);
        }
        finally
        {
            Busy = false;
        }
    }
}
