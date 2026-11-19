using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Obsidian.ViewModels;

namespace Obsidian.Views;

public partial class WorkspaceView : UserControl
{
    public WorkspaceView()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
        ModuleList.PreviewMouseRightButtonDown += OnModuleListPreviewMouseRightButtonDown;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not WorkspaceViewModel vm)
            return;

        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            ModuleFilterBox.Focus();
            ModuleFilterBox.SelectAll();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.D && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (vm.DumpSelectedCommand.CanExecute(null))
            {
                vm.DumpSelectedCommand.Execute(null);
                e.Handled = true;
            }
            return;
        }

        if (e.Key == Key.Escape && vm.Selected != null)
        {
            vm.Selected = null;
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Space && ModuleList.IsKeyboardFocusWithin && vm.Selected is { } module)
        {
            module.IsSelected = !module.IsSelected;
            e.Handled = true;
        }
    }

    /// <summary>Right-click selects the module under the cursor before the context
    /// menu opens, so its commands always target what the operator clicked.</summary>
    private void OnModuleListPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(ModuleList, e.OriginalSource as DependencyObject) is ListBoxItem item)
        {
            item.IsSelected = true;
            item.Focus();
        }
    }
}
