using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Eds.Core.App;
using Eds.Core.Containers;
using Eds.Core.Crypto;
using Eds.Maui.Services;

namespace Eds.Maui.ViewModels;

/// <summary>
/// "Create container" wizard: collects format/cipher/hash/size/PIM and a
/// destination path, then creates a new encrypted volume (with a FAT filesystem
/// inside) via <see cref="EdsAppController.CreateContainerAsync"/> and registers it
/// as a location so it appears on the Locations tab.
/// </summary>
public partial class CreateViewModel : ObservableObject
{
    private readonly EdsAppController _app;
    private readonly IFolderPicker _folderPicker;

    public string[] Formats { get; } = { "VeraCrypt", "TrueCrypt", "LUKS1" };
    public string[] Ciphers { get; } = { "AES", "Serpent", "Twofish" };
    public string[] Hashes { get; } = { "SHA-512", "SHA-256", "RIPEMD-160", "Whirlpool" };

    [ObservableProperty] private string _filePath = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private int _formatIndex;
    [ObservableProperty] private int _cipherIndex;
    [ObservableProperty] private int _hashIndex;
    [ObservableProperty] private int _sizeMb = 16;
    [ObservableProperty] private int _pim;
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string _status = "";

    public CreateViewModel(EdsAppController app, IFolderPicker folderPicker)
    {
        _app = app;
        _folderPicker = folderPicker;
    }

    [RelayCommand]
    private async Task PickFolderAsync()
    {
        try
        {
            var res = await _folderPicker.PickAsync(CancellationToken.None);
            if (res.IsSuccessful && res.Path is { } dir)
                FilePath = Path.Combine(dir, "new-volume.hc");
        }
        catch (Exception ex) { Status = "Error: " + ex.Message; }
    }

    [RelayCommand]
    private async Task CreateAsync()
    {
        if (string.IsNullOrWhiteSpace(FilePath)) { Status = "Choose a destination file path."; return; }
        if (Password.Length == 0) { Status = "Enter a password."; return; }
        if (SizeMb < 4) { Status = "Minimum size is 4 MiB (FAT16)."; return; }

        Busy = true;
        Status = "Creating… key derivation can take a moment.";
        try
        {
            var opts = new ContainerCreator.Options
            {
                Format = FormatIndex switch
                {
                    1 => ContainerCreator.Format.TrueCrypt,
                    2 => ContainerCreator.Format.Luks,
                    _ => ContainerCreator.Format.VeraCrypt,
                },
                Cipher = (ContainerCreator.Cipher)CipherIndex,
                Hash = (ContainerCreator.Hash)HashIndex,
                VolumeSize = (long)SizeMb * 1024 * 1024,
                FormatFat = true,
                Pim = Pim,
            };
            var pw = SecureBuffer.FromPassword(Password);
            Password = "";
            await _app.CreateContainerAsync(FilePath, pw, opts);
            Status = $"Created {Path.GetFileName(FilePath)} — open it from the Locations tab.";
        }
        catch (Exception ex)
        {
            Status = "Error: " + ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }
}
