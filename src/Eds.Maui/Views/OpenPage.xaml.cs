using Eds.Maui.ViewModels;

namespace Eds.Maui.Views;

public partial class OpenPage : ContentPage
{
    public OpenPage(OpenViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }
}
