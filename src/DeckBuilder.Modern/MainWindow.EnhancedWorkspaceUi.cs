using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using DeckBuilder.Core.Models;
using DeckBuilder.Core.Services;
using DeckBuilder.GameData;

namespace DeckBuilder.Modern;

public partial class MainWindow
{
    private static readonly Brush WorkspaceWarningBrush = new SolidColorBrush(Color.FromRgb(185, 28, 28));
    private bool _enhancedWorkspaceUiInstalled;
    private WorkspaceContentVariantScanResult? _workspaceWarningIndexSource;
    private HashSet<string> _nonWorkingCardReferences = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _unsupportedCardReferences = new(StringComparer.OrdinalIgnoreCase);

    private void InstallEnhancedWorkspaceUi()
    {
        if (_enhancedWorkspaceUiInstalled)
        {
            return;
        }

        _enhancedWorkspaceUiInstalled = true;
        InstallWorkspaceWarningRows();
    }

    private void InstallWorkspaceWarningRows()
    {
        AvailableCardsGrid.LoadingRow += AvailableCardsGrid_LoadingRow;
        AvailableCardsGrid.UnloadingRow += AvailableCardsGrid_UnloadingRow;
    }

    private void AvailableCardsGrid_LoadingRow(object? sender, DataGridRowEventArgs e)
    {
        if (e.Row.Item is not CardRecord card)
        {
            return;
        }

        string? warning = WorkspaceWarningFor(card.FileName);
        e.Row.ToolTip = warning;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!ReferenceEquals(e.Row.Item, card))
            {
                return;
            }

            DataGridCell? nameCell = GetCell(e.Row, 0);
            if (nameCell is null)
            {
                return;
            }

            if (warning is null)
            {
                nameCell.ClearValue(Control.ForegroundProperty);
                nameCell.ClearValue(Control.FontWeightProperty);
            }
            else
            {
                nameCell.Foreground = WorkspaceWarningBrush;
                nameCell.FontWeight = FontWeights.SemiBold;
            }
        }));
    }

    private static void AvailableCardsGrid_UnloadingRow(object? sender, DataGridRowEventArgs e)
    {
        e.Row.ToolTip = null;
        DataGridCell? nameCell = GetCell(e.Row, 0);
        nameCell?.ClearValue(Control.ForegroundProperty);
        nameCell?.ClearValue(Control.FontWeightProperty);
    }

    private string? WorkspaceWarningFor(string reference)
    {
        EnsureWorkspaceWarningIndex();
        if (_nonWorkingCardReferences.Contains(reference))
        {
            return "Warning: this card is stored under ---NON WORKING CARDS--- in at least one extracted source.";
        }

        if (_unsupportedCardReferences.Contains(reference))
        {
            return "Warning: this card is stored under ---UNSUPPORTED CARDS--- in at least one extracted source.";
        }

        return null;
    }

    private void EnsureWorkspaceWarningIndex()
    {
        if (ReferenceEquals(_workspaceWarningIndexSource, _workspaceCardVariants))
        {
            return;
        }

        _workspaceWarningIndexSource = _workspaceCardVariants;
        _nonWorkingCardReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _unsupportedCardReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_workspaceCardVariants is null)
        {
            return;
        }

        foreach (WorkspaceContentVariant variant in _workspaceCardVariants.CardVariants)
        {
            string path = variant.RelativePath;
            if (path.Contains("NON WORKING CARDS", StringComparison.OrdinalIgnoreCase))
            {
                _nonWorkingCardReferences.Add(variant.Reference);
            }
            else if (path.Contains("UNSUPPORTED CARDS", StringComparison.OrdinalIgnoreCase))
            {
                _unsupportedCardReferences.Add(variant.Reference);
            }
        }
    }

    private static DataGridCell? GetCell(DataGridRow row, int columnIndex)
    {
        DataGridCellsPresenter? presenter = FindVisualChild<DataGridCellsPresenter>(row);
        return presenter?.ItemContainerGenerator.ContainerFromIndex(columnIndex) as DataGridCell;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            if (child is T typed)
            {
                return typed;
            }

            T? nested = FindVisualChild<T>(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private void DeckLibrary_Click(object sender, RoutedEventArgs e)
    {
        if (_installedDecks.Count == 0)
        {
            MessageBox.Show(
                this,
                "No decks are available. Load a Magic 2014 folder or unpacked workspace first.",
                "Deck library",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (_dirty && MessageBox.Show(
                this,
                "Opening another deck will replace the current unsaved project. Continue?",
                "Unsaved work",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        DeckLibraryWindow dialog = new(_installedDecks, _cardImageLoader, _workspaceDirectory) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SelectedDeck is null)
        {
            return;
        }

        InstalledDeckRecord selected = dialog.SelectedDeck;
        bool createCopy = dialog.RequestedAction == DeckLibraryAction.Copy;
        string projectName = createCopy
            ? $"{selected.DisplayName} Copy"
            : selected.DisplayName;
        int uid = createCopy ? -1 : selected.Deck.Uid;

        _deck = DeckDocumentCloner.Clone(selected.Deck, uid, projectName);
        _editor = new DeckEditor(_deck);
        _projectName = projectName;
        _projectPath = null;
        MergeDeckCardsIntoCatalog();
        SetDirty(createCopy);
        RefreshCollections();

        if (createCopy)
        {
            Status($"Created an independent copy of {selected.DisplayName} from {selected.Source}. A new UID will be assigned when the copy is packaged.");
        }
        else
        {
            Status($"Opened {selected.DisplayName} from {selected.Source} for editing. Use Export game WAD to resave it.");
        }
    }
}
