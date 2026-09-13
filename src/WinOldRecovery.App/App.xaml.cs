using System.ComponentModel;
using System.Windows;
using WinOldRecovery.Native;

namespace WinOldRecovery.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        try
        {
            Privileges.EnableBackupAndRestore();
        }
        catch (Win32Exception exception)
        {
            MessageBox.Show(
                $"WinOld Recovery could not enable the Windows privileges needed to read old user files.{Environment.NewLine}{Environment.NewLine}{exception.Message}",
                "WinOld Recovery could not start",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(exitCode: 1);
            return;
        }

        base.OnStartup(e);
    }
}
