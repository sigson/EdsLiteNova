using Eds.Maui.Views;

namespace Eds.Maui;

public partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();

        // Detail pages navigated to by route (not tabs).
        Routing.RegisterRoute("open", typeof(OpenPage));
        Routing.RegisterRoute("vault", typeof(VaultBrowserPage));
        Routing.RegisterRoute("changepw", typeof(ChangePasswordPage));
    }
}
