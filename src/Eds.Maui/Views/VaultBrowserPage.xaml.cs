using Eds.Maui.ViewModels;

namespace Eds.Maui.Views;

public partial class VaultBrowserPage : ContentPage
{
    public VaultBrowserPage(VaultBrowserViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }
}
