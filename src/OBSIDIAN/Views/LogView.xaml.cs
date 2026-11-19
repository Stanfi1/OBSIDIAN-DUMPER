using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Obsidian.Dumper.Core.Diagnostics;
using Obsidian.ViewModels;

namespace Obsidian.Views;

public partial class LogView : UserControl
{
    private LogViewModel? _viewModel;

    public LogView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Subscribe();
        ScrollToEnd();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Unsubscribe();

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Unsubscribe();
        _viewModel = e.NewValue as LogViewModel;
        if (IsLoaded)
            Subscribe();
    }

    private void Subscribe()
    {
        if (_viewModel == null)
            return;
        _viewModel.EntryAdded -= OnEntryAdded;
        _viewModel.EntryAdded += OnEntryAdded;
    }

    private void Unsubscribe()
    {
        if (_viewModel != null)
            _viewModel.EntryAdded -= OnEntryAdded;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            LogFilterBox.Focus();
            LogFilterBox.SelectAll();
            e.Handled = true;
        }
    }

    private void LogList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel != null)
            _viewModel.SetSelectedEntries(LogList.SelectedItems.Cast<LogEntry?>());
    }

    private void OnEntryAdded() => Dispatcher.BeginInvoke(ScrollToEnd);

    private void ScrollToEnd()
    {
        if (!IsLoaded || LogList.Items.Count == 0)
            return;
        LogList.ScrollIntoView(LogList.Items[^1]);
    }
}
