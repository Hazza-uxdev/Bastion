using System;
using System.Linq;
using System.Windows;
namespace SecureVault;
public partial class App : Application
{
    public static bool StartHiddenRequested { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        StartHiddenRequested = e.Args.Any(arg =>
            string.Equals(arg, "--start-hidden", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "/start-hidden", StringComparison.OrdinalIgnoreCase));

        base.OnStartup(e);
    }
}
