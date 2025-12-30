namespace ObdInsight.Services;

/// <summary>
/// Shell-based navigation service implementation.
/// </summary>
public sealed class ShellNavigationService : INavigationService
{
    /// <inheritdoc/>
    public Task NavigateToAsync(string route)
    {
        return Shell.Current.GoToAsync(route);
    }

    /// <inheritdoc/>
    public Task NavigateToAsync(string route, IDictionary<string, object> parameters)
    {
        return Shell.Current.GoToAsync(route, parameters);
    }

    /// <inheritdoc/>
    public Task GoBackAsync()
    {
        return Shell.Current.GoToAsync("..");
    }
}