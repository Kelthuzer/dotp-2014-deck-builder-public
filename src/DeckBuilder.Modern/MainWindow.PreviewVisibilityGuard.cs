using System.Windows;

namespace DeckBuilder.Modern;

public partial class MainWindow
{
    private bool _previewVisibilityGuardInstalled;
    private bool _previewVisibilityGuardApplying;

    internal void InstallPreviewVisibilityGuard()
    {
        if (_previewVisibilityGuardInstalled)
            return;

        _previewVisibilityGuardInstalled = true;
        CardPreviewViewbox.IsVisibleChanged += PreviewElement_IsVisibleChanged;
        PreviewPlaceholder.IsVisibleChanged += PreviewElement_IsVisibleChanged;

        if (_previewCountBox is not null)
            _previewCountBox.SelectionChanged += (_, _) => EnforcePreviewVisibilityMode();

        EnforcePreviewVisibilityMode();
    }

    private bool IsMultiPreviewModeActive() =>
        _previewCountBox?.SelectedItem is int count && count > 1;

    private void PreviewElement_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_previewVisibilityGuardApplying || !IsMultiPreviewModeActive())
            return;

        if (e.NewValue is bool visible && visible)
            EnforcePreviewVisibilityMode();
    }

    private void EnforcePreviewVisibilityMode()
    {
        if (_previewVisibilityGuardApplying)
            return;

        _previewVisibilityGuardApplying = true;
        try
        {
            if (IsMultiPreviewModeActive())
            {
                CardPreviewViewbox.Visibility = Visibility.Collapsed;
                PreviewPlaceholder.Visibility = Visibility.Collapsed;
            }
        }
        finally
        {
            _previewVisibilityGuardApplying = false;
        }
    }
}
