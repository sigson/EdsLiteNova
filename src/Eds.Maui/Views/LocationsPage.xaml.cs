using Eds.Maui.ViewModels;

namespace Eds.Maui.Views;

public partial class LocationsPage : ContentPage
{
    private readonly LocationsViewModel _vm;

    public LocationsPage(LocationsViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _vm.RefreshCommand.Execute(null); // reflect opens/closes from other pages
    }
}
