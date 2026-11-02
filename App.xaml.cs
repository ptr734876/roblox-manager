using System.Windows;
using RobloxAccountManager.Services;

namespace RobloxAccountManager;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DiagnosticsService.Initialize();
        base.OnStartup(e);
    }
}
