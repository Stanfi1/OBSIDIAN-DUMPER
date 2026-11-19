using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Obsidian.ViewModels;

namespace Obsidian.Views;

public partial class TargetsView : UserControl
{
    public TargetsView()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
        TargetsList.PreviewMouseRightButtonDown += OnListPreviewMouseRightButtonDown;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not TargetsViewModel vm)
            return;

        // Ctrl+F focuses the filter box; Enter on a selected row attaches it.
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            FilterBox.Focus();
            FilterBox.SelectAll();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && TargetsList.IsKeyboardFocusWithin &&
            vm.AttachCommand.CanExecute(null))
        {
            vm.AttachCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>Right-click selects the row under the cursor before the context menu
    /// opens, so menu actions always target what the operator clicked.</summary>
    private void OnListPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(TargetsList, e.OriginalSource as DependencyObject)
            is ListBoxItem item)
        {
            item.IsSelected = true;
            item.Focus();
        }
    }

    private void Rows_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not TargetsViewModel vm)
            return;

        // Only rows attach: a double click on the empty area below the last row used
        // to attach the previously selected process.
        if (ItemsControl.ContainerFromElement(TargetsList, e.OriginalSource as DependencyObject)
            is not ListBoxItem)
        {
            return;
        }

        if (vm.AttachCommand.CanExecute(null))
        {
            vm.AttachCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void TargetsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Keeps the restored selection visible after a filter/rescan refresh.
        if (sender is ListBox list && list.SelectedItem != null)
            list.ScrollIntoView(list.SelectedItem);
    }
}
