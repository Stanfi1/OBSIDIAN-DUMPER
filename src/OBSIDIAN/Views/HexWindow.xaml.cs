using System.Windows;
using System.Windows.Input;

namespace Obsidian.Views;

/// <summary>Themed, resizable window that hosts the reusable hex viewer.</summary>
public partial class HexWindow : Window
{
    public HexWindow(string title, byte[] bytes, string? sourceLabel = null)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        Viewer.Bytes = bytes;
        Viewer.SourceLabel = sourceLabel ?? title;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
            return;

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // DragMove can throw when the button is released during the call.
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }
}
