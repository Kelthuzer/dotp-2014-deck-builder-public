using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;

namespace DeckBuilder.Modern;

internal static class CleanupMenuArtLabelFix
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        EventManager.RegisterClassHandler(
            typeof(MenuItem),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnMenuItemLoaded));
    }

    private static void OnMenuItemLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item)
            return;

        string header = item.Header?.ToString() ?? string.Empty;
        if (header.Equals("Cleanup duplicate _art…", StringComparison.Ordinal))
            item.Header = "Cleanup _art…";
    }
}
