namespace ObdInsight.Services;

/// <summary>
/// Abstraction for Shell navigation to enable testability.
/// </summary>
public interface INavigationService
{
    /// <summary>
    /// Navigates to a route.
    /// </summary>
    Task NavigateToAsync(string route);

    /// <summary>
    /// Navigates to a route with parameters.
    /// </summary>
    Task NavigateToAsync(string route, IDictionary<string, object> parameters);

    /// <summary>
    /// Navigates back to the previous page.
    /// </summary>
    Task GoBackAsync();
}