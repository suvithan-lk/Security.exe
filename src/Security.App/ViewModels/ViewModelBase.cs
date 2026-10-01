using System.Threading.Tasks;
using Security.App.Mvvm;

namespace Security.App.ViewModels;

/// <summary>
/// Implemented by view models that need to refresh when the shell navigates to
/// them. Avoids loading data for views the operator never opens.
/// </summary>
public interface INavigationAware
{
    Task OnNavigatedAsync();
}

/// <summary>Shared base for all screen view models.</summary>
public abstract class ViewModelBase : ObservableObject, INavigationAware
{
    private bool _isBusy;
    private string _errorMessage = string.Empty;

    public bool IsBusy
    {
        get => _isBusy;
        protected set => SetProperty(ref _isBusy, value);
    }

    /// <summary>Non-fatal error surfaced inline; details go to the log.</summary>
    public string ErrorMessage
    {
        get => _errorMessage;
        protected set => SetProperty(ref _errorMessage, value);
    }

    public virtual Task OnNavigatedAsync() => Task.CompletedTask;

    /// <summary>Record a non-fatal failure for the operator without crashing.</summary>
    protected void ReportError(string userMessage, System.Exception? exception = null)
    {
        ErrorMessage = userMessage;
        OnErrorReported(userMessage, exception);
    }

    protected void ClearError() => ErrorMessage = string.Empty;

    /// <summary>Overridable hook so derived types can log without a logger dependency.</summary>
    protected virtual void OnErrorReported(string userMessage, System.Exception? exception)
    {
    }
}
