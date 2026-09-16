using System.Windows;
using System.Windows.Threading;

namespace WinOldRecovery.App;

internal static class UiThread
{
    public static async Task InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        Dispatcher? dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        await dispatcher.InvokeAsync(action);
    }
}
