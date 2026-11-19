using System.Windows.Controls;
using System.Windows.Input;
using Obsidian.ViewModels;

namespace Obsidian.Views;

public partial class InspectorView : UserControl
{
    public InspectorView()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not InspectorViewModel vm)
            return;

        // Ctrl+F jumps to the type filter; Ctrl+R reloads the open capture.
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            TypeFilterBox.Focus();
            TypeFilterBox.SelectAll();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.R && Keyboard.Modifiers == ModifierKeys.Control &&
            vm.ReloadCommand.CanExecute(null))
        {
            vm.ReloadCommand.Execute(null);
            e.Handled = true;
        }
    }
}
