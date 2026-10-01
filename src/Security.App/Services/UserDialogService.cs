using System.Threading.Tasks;
using System.Windows;

namespace Security.App.Services;

/// <summary>
/// Thin wrapper over native message boxes so view models can ask for
/// confirmation without a UI dependency and without code-behind.
/// </summary>
public interface IUserDialogService
{
    /// <summary>Returns true when the operator confirms.</summary>
    Task<bool> ConfirmAsync(string title, string message);

    void Inform(string title, string message);
}

public sealed class UserDialogService : IUserDialogService
{
    public Task<bool> ConfirmAsync(string title, string message)
    {
        // MessageBox is synchronous; marshal to the UI thread if needed.
        return RunOnUiThread(() =>
            MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning)
            == MessageBoxResult.Yes);
    }

    public void Inform(string title, string message)
    {
        _ = RunOnUiThread<bool>(() =>
        {
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
            return true;
        });
    }

    private static Task<T> RunOnUiThread<T>(System.Func<T> action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            return Task.FromResult(action());

        return Task.FromResult(dispatcher.Invoke(action));
    }
}
