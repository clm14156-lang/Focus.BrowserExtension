using FocusApp.Core;

namespace FocusApp.BrowserBridge;

public static class FocusThemeResolver
{
    // Mirrors Desktop FocusSessionViewModel.RefreshFocusTheme/CurrentGreetingState.
    // "System" is the app's local greeting schedule, never the Windows color scheme.
    public static string Resolve(string? preference, DateTimeOffset localNow)
    {
        var mode = Enum.TryParse<FocusThemeMode>(preference, out var parsed) && Enum.IsDefined(parsed)
            ? parsed : FocusThemeMode.System;
        var dark = mode == FocusThemeMode.Dark || mode == FocusThemeMode.System
            && (localNow.Hour < 5 || localNow.Hour >= 18);
        return dark ? "dark" : "light";
    }
}
