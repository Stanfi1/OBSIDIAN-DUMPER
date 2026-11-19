using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Obsidian.ViewModels;

namespace Obsidian;

public partial class ShellWindow : Window
{
    private const int WmGetMinMaxInfo = 0x0024;
    private const int WmNcHitTest = 0x0084;
    private const int WmNcLButtonDown = 0x00A1;
    private const int WmNcLButtonUp = 0x00A2;
    private const int WmNcLButtonDblClk = 0x00A3;
    private const int WmNcMouseMove = 0x00A0;
    private const int WmNcMouseLeave = 0x02A2;
    private const int HtMaxButton = 9;
    private const uint MonitorDefaultToNearest = 2;

    private HwndSource? _hwndSource;
    private INotifyCollectionChanged? _toastCollection;
    private bool _snapLayoutsSupported;

    public ShellWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        StateChanged += OnStateChanged;
        Loaded += OnLoaded;
        Closed += OnClosed;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private ShellViewModel? Vm => DataContext as ShellViewModel;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Snap Layouts (Windows 11) only appear when the maximise button area is
        // part of the non-client frame; older builds keep the client-area button.
        _snapLayoutsSupported = Environment.OSVersion.Version.Build >= 22000;

        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            _hwndSource = source;
            source.AddHook(WndProc);
        }

        UpdateMaximizeButtonState();
        AnimatePage();

    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_toastCollection != null)
        {
            _toastCollection.CollectionChanged -= OnToastsChanged;
            _toastCollection = null;
        }

        if (_hwndSource is { } source)
        {
            source.RemoveHook(WndProc);
            _hwndSource = null;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (message)
        {
            case WmGetMinMaxInfo:
                ApplyMaximizedBounds(hwnd, lParam);
                break;

            case WmNcHitTest when _snapLayoutsSupported && WindowState != WindowState.Minimized:
                if (IsOverMaximizeButton(lParam))
                {
                    // Report the caption button as non-client so Windows shows the
                    // Snap Layouts flyout on hover.
                    handled = true;
                    return new IntPtr(HtMaxButton);
                }
                break;

            // The button itself is drawn by WPF, but the hit test above makes the
            // system treat it as a non-client caption button. Handle the click
            // ourselves so it toggles exactly once (previously the press was
            // swallowed and the window never maximised).
            case WmNcLButtonDown when wParam.ToInt32() == HtMaxButton:
                ToggleMaximize();
                handled = true;
                return IntPtr.Zero;

            case WmNcLButtonUp when wParam.ToInt32() == HtMaxButton:
                handled = true;
                return IntPtr.Zero;

            case WmNcLButtonDblClk when wParam.ToInt32() == HtMaxButton:
                handled = true;
                return IntPtr.Zero;

            // WPF does not see the mouse over a non-client hit-tested area, so
            // drive the caption button's hover surface from the NC messages.
            case WmNcMouseMove:
                UpdateMaximizeHover(IsOverMaximizeButton(lParam));
                break;

            case WmNcMouseLeave:
                UpdateMaximizeHover(false);
                break;
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// A <c>WindowStyle="None"</c> window maximises to the full monitor bounds,
    /// covering the taskbar. Constrain the maximised rectangle to the work area
    /// of the monitor that hosts the window.
    /// </summary>
    private void ApplyMaximizedBounds(IntPtr hwnd, IntPtr lParam)
    {
        IntPtr monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
            return;

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
            return;

        MinMaxInfo minMax = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        NativeRect work = info.Work;
        NativeRect monitorBounds = info.Monitor;

        minMax.MaxPosition = new NativePoint
        {
            X = work.Left - monitorBounds.Left,
            Y = work.Top - monitorBounds.Top,
        };
        minMax.MaxSize = new NativePoint
        {
            X = work.Right - work.Left,
            Y = work.Bottom - work.Top,
        };

        Marshal.StructureToPtr(minMax, lParam, false);
    }

    private bool IsOverMaximizeButton(IntPtr lParam)
    {
        if (MaximizeButton is null || !MaximizeButton.IsVisible)
            return false;

        long packed = lParam.ToInt64();
        var pointer = new NativePoint
        {
            X = (short)(packed & 0xFFFF),
            Y = (short)((packed >> 16) & 0xFFFF),
        };

        Point topLeft = MaximizeButton.PointToScreen(new Point(0, 0));
        Point bottomRight = MaximizeButton.PointToScreen(
            new Point(MaximizeButton.ActualWidth, MaximizeButton.ActualHeight));

        return pointer.X >= topLeft.X && pointer.X <= bottomRight.X &&
               pointer.Y >= topLeft.Y && pointer.Y <= bottomRight.Y;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_toastCollection != null)
        {
            _toastCollection.CollectionChanged -= OnToastsChanged;
            _toastCollection = null;
        }

        if (e.OldValue is ShellViewModel oldVm)
            oldVm.PropertyChanged -= OnVmPropertyChanged;
        if (e.NewValue is ShellViewModel newVm)
        {
            newVm.PropertyChanged += OnVmPropertyChanged;
            _toastCollection = newVm.Toasts;
            _toastCollection.CollectionChanged += OnToastsChanged;
        }
    }

    private void OnToastsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || e.NewItems is null)
            return;

        foreach (object item in e.NewItems)
        {
            if (item is not Toast toast)
                continue;

            // The container does not exist yet when the collection event fires.
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => AnimateToast(toast)));
        }
    }

    private void AnimateToast(Toast toast)
    {
        if (Vm is null || ToastItems.ItemContainerGenerator.ContainerFromItem(toast) is not FrameworkElement container)
            return;

        if (!Vm.SettingsStore.Current.Animations)
        {
            container.Opacity = 1;
            container.RenderTransform = Transform.Identity;
            return;
        }

        var translate = new TranslateTransform(0, -10);
        container.RenderTransform = translate;
        container.Opacity = 0;

        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        container.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = easing });
        translate.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(-10, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = easing });
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.CurrentPage))
            AnimatePage();
    }

    private void AnimatePage()
    {
        if (Vm == null)
            return;

        // Clear any clock still running from the previous transition: a local
        // value alone cannot override an active animation.
        PageHost.BeginAnimation(OpacityProperty, null);
        if (PageHost.RenderTransform is TranslateTransform previous)
            previous.BeginAnimation(TranslateTransform.YProperty, null);

        if (!Vm.SettingsStore.Current.Animations)
        {
            PageHost.Opacity = 1;
            PageHost.RenderTransform = Transform.Identity;
            return;
        }

        var translate = new TranslateTransform(0, 12);
        PageHost.RenderTransform = translate;
        PageHost.Opacity = 0;

        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240)) { EasingFunction = easing };
        var slide = new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(300)) { EasingFunction = easing };
        PageHost.BeginAnimation(OpacityProperty, fade);
        translate.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        UpdateMaximizeButtonState();

        // The window chrome already constrains the maximised rect to the work
        // area (WM_GETMINMAXINFO); no inset is needed, so the UI really fills
        // the screen edge to edge.
        RootBorder.Margin = new Thickness(0);
    }

    private void UpdateMaximizeButtonState()
    {
        if (MaximizeButton is null)
            return;

        bool maximized = WindowState == WindowState.Maximized;
        MaximizeButton.Content = maximized ? "\uE923" : "\uE922"; // Restore / Maximize
        MaximizeButton.ToolTip = maximized ? "Restore" : "Maximize";
        System.Windows.Automation.AutomationProperties.SetName(
            MaximizeButton, maximized ? "Restore" : "Maximize");
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Vm == null)
            return;

        if (e.Key == Key.F5)
        {
            Vm.RefreshCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && Vm.Toasts.Count > 0)
        {
            Vm.ClearToasts();
            e.Handled = true;
            return;
        }

        // Ctrl+1..Ctrl+7 follow the sidebar order.
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is >= Key.D1 and <= Key.D7)
        {
            int index = e.Key - Key.D1;
            if (index < Vm.Navigation.Count)
                Vm.SelectedNav = Vm.Navigation[index];
            e.Handled = true;
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    /// <summary>Single entry point for mouse, keyboard and automation toggles.</summary>
    private void ToggleMaximize()
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void UpdateMaximizeHover(bool over)
    {
        if (MaximizeButton is null)
            return;

        MaximizeButton.Background = over
            ? TryFindResource("ControlSurfaceHover") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Transparent
            : System.Windows.Media.Brushes.Transparent;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void DismissToast_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Toast toast } && Vm != null)
            Vm.DismissToast(toast);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
