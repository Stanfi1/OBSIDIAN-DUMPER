using System.Windows.Input;
using Obsidian.ViewModels;

namespace Obsidian.Views;

public partial class MemoryView
{
    public MemoryView()
    {
        InitializeComponent();
    }

    private void RegionList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || DataContext is not MemoryViewModel viewModel)
            return;

        if (viewModel.HexRegionCommand.CanExecute(null))
            viewModel.HexRegionCommand.Execute(null);
    }

    private void HitList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || DataContext is not MemoryViewModel viewModel)
            return;

        if (viewModel.OpenHitHexCommand.CanExecute(null))
            viewModel.OpenHitHexCommand.Execute(null);
    }
}
