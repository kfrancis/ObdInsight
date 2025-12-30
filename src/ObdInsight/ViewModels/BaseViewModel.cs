using CommunityToolkit.Mvvm.ComponentModel;

namespace ObdInsight.ViewModels;

/// <summary>
/// Base class for all ViewModels providing common functionality.
/// </summary>
public abstract partial class BaseViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotBusy))]
    private bool _isBusy;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>
    /// Inverse of IsBusy for UI binding convenience.
    /// </summary>
    public bool IsNotBusy => !IsBusy;

    /// <summary>
    /// Whether there is an error to display.
    /// </summary>
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    partial void OnErrorMessageChanged(string? value)
    {
        OnPropertyChanged(nameof(HasError));
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnBusyChanged();
    }

    /// <summary>
    /// Called when IsBusy changes. Override to add additional behavior.
    /// </summary>
    protected virtual void OnBusyChanged() { }

    /// <summary>
    /// Clears any existing error message.
    /// </summary>
    protected void ClearError() => ErrorMessage = null;

    /// <summary>
    /// Sets an error message to display.
    /// </summary>
    protected void SetError(string message) => ErrorMessage = message;

    /// <summary>
    /// Executes an async operation with busy indicator handling.
    /// </summary>
    protected async Task ExecuteBusyAsync(Func<Task> operation)
    {
        if (IsBusy) return;

        try
        {
            IsBusy = true;
            ClearError();
            await operation();
        }
        catch (Exception ex)
        {
            SetError(ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Executes an async operation with busy indicator handling and returns a result.
    /// </summary>
    protected async Task<T?> ExecuteBusyAsync<T>(Func<Task<T>> operation) where T : class
    {
        if (IsBusy) return null;

        try
        {
            IsBusy = true;
            ClearError();
            return await operation();
        }
        catch (Exception ex)
        {
            SetError(ex.Message);
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
