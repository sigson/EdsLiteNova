using Eds.Maui.ViewModels;

namespace Eds.Maui.Views;

public partial class CreatePage : ContentPage
{
    public CreatePage(CreateViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }
}
