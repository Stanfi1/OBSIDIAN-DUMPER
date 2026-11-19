using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Obsidian.Common;
using Obsidian.Dumper.Core.Model;
using Obsidian.Services;
using Obsidian.ViewModels;

namespace Obsidian;

public partial class App : Application
{
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaUseImmersiveDarkMode = 20;

    public static ShellViewModel? Shell { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        AsyncRelayCommand.ExceptionHandler = OnCommandException;

        StartupOptions options = StartupOptions.Parse(e.Args);

        if (options.ShowHelp)
        {
            MessageBox.Show(StartupOptions.UsageText, "OBSIDIAN — command line", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        var shell = new ShellViewModel();
        Shell = shell;

        foreach (string warning in options.Warnings)
            shell.Log.Warn(warning, "startup");

        // --page is validated against the live navigation map so a typo shows an
        // honest message instead of silently landing on the default page.
        if (!string.IsNullOrEmpty(options.Page) &&
            !shell.Navigation.Any(n => string.Equals(n.Key, options.Page, StringComparison.OrdinalIgnoreCase)))
        {
            shell.Log.Warn(
                $"Unknown --page '{options.Page}'. Valid pages: {string.Join(", ", shell.Navigation.Select(n => n.Key))}.",
                "startup");
            options.Page = null;
        }

        if (options.Theme is { Length: > 0 } theme && !StartupOptions.IsKnownTheme(theme))
            shell.Log.Warn($"Unknown --theme '{theme}'. Expected dark, light or system.", "startup");

        // The palette must be in place before the window (or any view) is
        // constructed so the first frame is already correctly themed.
        AppTheme startupTheme = shell.SettingsStore.Current.Theme;
        if (StartupOptions.TryParseTheme(options.Theme, out AppTheme themeOverride))
        {
            startupTheme = themeOverride;
            shell.Log.Info($"Theme override for this run: {themeOverride} (not saved).", "startup");
        }

        ThemeService.Initialise(startupTheme, shell.SettingsStore.Current.AccentColor);
        ThemeService.ThemeChanged += OnThemeChanged;
        if (!string.IsNullOrEmpty(options.Theme) && StartupOptions.IsKnownTheme(options.Theme))
            shell.SettingsPage.ApplyStartupThemeOverride(startupTheme);

        // Density resources must exist before the first view is measured.
        DensityService.Apply(shell.SettingsStore.Current.Density);

        var window = new ShellWindow { DataContext = shell };
        MainWindow = window;
        window.SourceInitialized += (_, _) => ApplyWindowTheme(window, ThemeService.IsDark);
        window.Show();


        if (!string.IsNullOrEmpty(options.Page))
            shell.NavigateTo(options.Page!);

        if (options.AttachPid is > 0)
            _ = AttachDeferredAsync(shell, options);

        if (options.SelectPid is > 0)
            _ = SelectDeferredAsync(shell, options.SelectPid.Value);

        if (!string.IsNullOrEmpty(options.OpenPath))
        {
            if (File.Exists(options.OpenPath))
                shell.Inspector.AddCapture(options.OpenPath, "file");
            else
                shell.Log.Warn($"--open: file not found: {options.OpenPath}", "startup");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            ThemeService.ThemeChanged -= OnThemeChanged;
            ThemeService.Shutdown();
            Shell?.Shutdown();
        }
        catch
        {
            // Exit must never fail on cleanup.
        }

        Shell = null;
        base.OnExit(e);
    }

    /// <summary>Re-applies the immersive dark-mode flag when the palette changes.</summary>
    private static void OnThemeChanged(object? sender, EventArgs e)
    {
        try
        {
            if (Current?.MainWindow is Window window)
                ApplyWindowTheme(window, ThemeService.IsDark);
        }
        catch
        {
            // DWM attributes are cosmetic; never fail the theme change over them.
        }
    }

    private static async Task SelectDeferredAsync(ShellViewModel shell, int pid)
    {
        ProcessRecord? process = await FindProcessAsync(shell, pid, TimeSpan.FromSeconds(5));
        if (process != null)
            shell.Targets.Selected = process;
        else
            shell.Log.Warn($"--select: pid {pid} was not found in the process list.", "startup");
    }

    private static async Task AttachDeferredAsync(ShellViewModel shell, StartupOptions options)
    {
        if (options.AttachPid is not { } pid)
            return;

        ProcessRecord? process = await FindProcessAsync(shell, pid, TimeSpan.FromSeconds(5));
        if (process == null)
        {
            // The process may simply be missing from the snapshot (protected or
            // started after the scan); fall back to a direct lookup.
            try
            {
                using var systemProcess = Process.GetProcessById(pid);
                process = new ProcessRecord
                {
                    Id = pid,
                    Name = systemProcess.ProcessName + ".exe",
                    Architecture = Environment.Is64BitProcess
                        ? Dumper.Core.Model.ProcessArchitecture.X64
                        : Dumper.Core.Model.ProcessArchitecture.X86,
                    CanRead = true,
                };
            }
            catch
            {
                shell.Log.Error($"Attach target pid {pid} no longer exists", "startup");
                return;
            }
        }

        await shell.AttachAsync(process);

        if (options.DumpAll)
        {
            await Task.Delay(400);
            shell.Workspace.DumpAllCommand.Execute(null);
        }

        if (!string.IsNullOrEmpty(options.Page))
            shell.NavigateTo(options.Page!);
    }

    /// <summary>
    /// Waits for the background process scan instead of racing it with a fixed
    /// delay: startup arguments like <c>--attach</c> are commonly passed before
    /// the first scan has finished.
    /// </summary>
    private static async Task<ProcessRecord?> FindProcessAsync(ShellViewModel shell, int pid, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ProcessRecord? process = shell.Targets.Processes.FirstOrDefault(p => p.Id == pid);
            if (process != null)
                return process;
            await Task.Delay(150);
        }

        return shell.Targets.Processes.FirstOrDefault(p => p.Id == pid);
    }

    private static bool _errorShown;

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ReportException(e.Exception);

        if (!_errorShown)
        {
            _errorShown = true;
            try
            {
                MessageBox.Show(
                    e.Exception.Message + "\n\nThe full error was written to the Activity log.",
                    "OBSIDIAN — unexpected error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch
            {
                // Showing the dialog must not be the second crash.
            }
        }

        e.Handled = true;
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
            ReportException(exception);
    }

    private static void OnCommandException(Exception exception)
    {
        Shell?.Log.Error("UI command failed: " + exception, "ui");
        Shell?.Toast("Something went wrong", exception.Message, ToastKind.Error);
        TryWriteCrashLog(exception);
    }

    private static void ReportException(Exception exception)
    {
        try
        {
            Shell?.Log.Error("Unhandled: " + exception, "app");
        }
        catch
        {
            // Logging must not throw during a crash.
        }

        TryWriteCrashLog(exception);
    }

    private static void TryWriteCrashLog(Exception exception)
    {
        foreach (string directory in new[] { AppContext.BaseDirectory, Path.GetTempPath() })
        {
            try
            {
                File.WriteAllText(
                    Path.Combine(directory, "crash.log"),
                    DateTime.Now + Environment.NewLine + exception);
                return;
            }
            catch
            {
                // Try the next location (the install folder is often read-only).
            }
        }
    }

    private static void ApplyWindowTheme(Window window, bool dark)
    {
        try
        {
            IntPtr handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
                return;

            int round = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref round, sizeof(int));
            int immersiveDarkMode = dark ? 1 : 0;
            DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref immersiveDarkMode, sizeof(int));
        }
        catch
        {
            // Older Windows builds simply keep square corners / light chrome.
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}

/// <summary>
/// Command line surface. Kept deliberately small: attach/select a pid, open a
/// capture, pick a page and override the theme for a single run (useful for
/// screenshot tooling). Parse errors are collected instead of thrown so the
/// shell can report them in the Activity log.
/// </summary>
internal sealed class StartupOptions
{
    public const string UsageText =
        "OBSIDIAN Dumper\n\n" +
        "Usage: OBSIDIAN.exe [options]\n\n" +
        "  --attach <pid>     Attach to a process on startup\n" +
        "  --dump             Dump all managed modules after attaching\n" +
        "  --select <pid>     Select a process in the Targets list\n" +
        "  --open <path>      Queue a .NET assembly in the Inspector\n" +
        "  --page <key>       Open a page: targets, workspace, inspector, inject, memory, log, settings\n" +
        "  --theme <mode>     Override the theme for this run: dark, light, system\n" +
        "  --help             Show this message\n";

    private readonly List<string> _warnings = new();

    public int? AttachPid { get; private set; }
    public int? SelectPid { get; private set; }
    public string? OpenPath { get; private set; }
    public string? Page { get; set; }
    public string? Theme { get; private set; }
    public bool DumpAll { get; private set; }
    public bool ShowHelp { get; private set; }
    public IReadOnlyList<string> Warnings => _warnings;

    public static bool IsKnownTheme(string? theme) =>
        theme is not null &&
        (theme.Equals("dark", StringComparison.OrdinalIgnoreCase) ||
         theme.Equals("light", StringComparison.OrdinalIgnoreCase) ||
         theme.Equals("system", StringComparison.OrdinalIgnoreCase));

    /// <summary>Maps a validated <c>--theme</c> value to the runtime enum.</summary>
    public static bool TryParseTheme(string? theme, out AppTheme mode)
    {
        mode = AppTheme.System;
        if (string.IsNullOrWhiteSpace(theme))
            return false;

        switch (theme.Trim().ToLowerInvariant())
        {
            case "dark":
                mode = AppTheme.Dark;
                return true;
            case "light":
                mode = AppTheme.Light;
                return true;
            case "system":
                mode = AppTheme.System;
                return true;
            default:
                return false;
        }
    }

    public static StartupOptions Parse(string[]? args)
    {
        var options = new StartupOptions();
        if (args is null)
            return options;

        for (int i = 0; i < args.Length; i++)
        {
            string token = args[i]?.Trim() ?? string.Empty;
            if (token.Length == 0)
                continue;

            string? inlineValue = null;
            int equals = token.IndexOf('=');
            if (equals > 0)
            {
                inlineValue = token[(equals + 1)..].Trim();
                token = token[..equals];
            }

            switch (token.ToLowerInvariant())
            {
                case "--attach":
                    options.AttachPid = options.ReadPid("--attach", TakeValue(args, ref i, inlineValue));
                    break;
                case "--select":
                    options.SelectPid = options.ReadPid("--select", TakeValue(args, ref i, inlineValue));
                    break;
                case "--open":
                    options.OpenPath = options.ReadString("--open", TakeValue(args, ref i, inlineValue));
                    break;
                case "--page":
                    options.Page = options.ReadString("--page", TakeValue(args, ref i, inlineValue))?.ToLowerInvariant();
                    break;
                case "--theme":
                    options.Theme = options.ReadString("--theme", TakeValue(args, ref i, inlineValue))?.ToLowerInvariant();
                    break;
                case "--dump":
                    if (inlineValue is { Length: > 0 })
                        options._warnings.Add("--dump does not take a value; the value was ignored.");
                    options.DumpAll = true;
                    break;
                case "--help":
                case "-h":
                case "-?":
                case "/?":
                    options.ShowHelp = true;
                    break;
                default:
                    options._warnings.Add($"Ignoring unknown argument '{token}'.");
                    break;
            }
        }

        return options;
    }

    private static string? TakeValue(string[] args, ref int index, string? inlineValue)
    {
        if (inlineValue != null)
            return inlineValue;

        if (index + 1 < args.Length)
        {
            index++;
            return args[index];
        }

        return null;
    }

    private int? ReadPid(string option, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            _warnings.Add($"{option} requires a process id; the option was ignored.");
            return null;
        }

        if (!int.TryParse(value, out int pid) || pid <= 0)
        {
            _warnings.Add($"{option}: '{value}' is not a valid process id; the option was ignored.");
            return null;
        }

        return pid;
    }

    private string? ReadString(string option, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            _warnings.Add($"{option} requires a value; the option was ignored.");
            return null;
        }

        return value.Trim();
    }
}
