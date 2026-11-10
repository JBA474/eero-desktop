using System.Threading;
using System.Windows;
using System.Windows.Threading;
using EeroDesktop.Api;

namespace EeroDesktop;

public partial class App : Application
{
    // One running copy per Windows user. A second launch asks the first to show its window; "--exit" asks it to quit.
    private const string InstanceName = @"Local\EeroDesktop.SingleInstance";
    private const string ShowEventName = @"Local\EeroDesktop.Show";
    private const string ExitEventName = @"Local\EeroDesktop.Exit";

    private Mutex? _instance;
    private TrayIcon? _tray;
    private NetworkMonitor? _monitor;
    private bool _exiting;

    public App()
    {
        // Fluent theme: follows Windows light/dark by default, or the Light/Dark choice in Settings.
        var theme = AppSettings.Load().Theme;
        ThemeMode = ThemeFor(theme);
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/EeroDesktop;component/Themes/AppStyles.xaml", UriKind.Absolute),
        });
#pragma warning disable WFO5001 // WinForms dark mode is marked experimental; the tray menu follows the app theme (set once per start).
        System.Windows.Forms.Application.SetColorMode(theme switch
        {
            "Light" => System.Windows.Forms.SystemColorMode.Classic,
            "Dark" => System.Windows.Forms.SystemColorMode.Dark,
            _ => System.Windows.Forms.SystemColorMode.System,
        });
#pragma warning restore WFO5001
    }

    public static ThemeMode ThemeFor(string? theme) => theme switch
    {
        "Light" => ThemeMode.Light,
        "Dark" => ThemeMode.Dark,
        _ => ThemeMode.System,
    };

    /// <summary>Switches the theme immediately (windows, title bars and dialogs update live).</summary>
    public static void ApplyTheme(string theme)
    {
        Current.ThemeMode = ThemeFor(theme);
        ThemeChanged?.Invoke();
    }

    /// <summary>Raised after the Light/Dark/System choice changes (code-drawn charts pick their colors again).</summary>
    public static event Action? ThemeChanged;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var exitRequest = e.Args.Any(a => a.Equals("--exit", StringComparison.OrdinalIgnoreCase));

        var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
        _instance = new Mutex(true, InstanceName, out var first);
        if (!first || exitRequest)
        {
            if (!first) (exitRequest ? exitEvent : showEvent).Set();
            else _instance.ReleaseMutex();
            _instance.Dispose(); _instance = null;
            // Nothing was opened, so end the process directly. Application.Shutdown() here can run before WPF
            // has finished starting (App.InitializeComponent) and crash with "Application object is being shut down".
            Environment.Exit(0);
            return;
        }
        new Thread(() =>
        {
            var handles = new WaitHandle[] { showEvent, exitEvent };
            while (true)
            {
                var which = WaitHandle.WaitAny(handles);
                Dispatcher.BeginInvoke(which == 0 ? BringToFront : ExitApp);
                if (which == 1) return;
            }
        }) { IsBackground = true, Name = "SingleInstanceListener" }.Start();

        DispatcherUnhandledException += OnUnhandled;
        try { HistoryStore.Prune(); } catch (Exception ex) { AppSettings.Log("ERROR", "History cleanup failed: " + ex.Message); }

        var state = AppState.Current;
        var saved = TokenStore.Load();
        if (!string.IsNullOrEmpty(saved))
        {
            state.Api.Token = saved;
            try
            {
                await state.LoadAccountAsync();
                // "Start in the system tray" (Settings) or --tray: only the tray icon until the user opens the window.
                ShowMain(startHidden: e.Args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase)) || AppSettings.Load().StartInTray);
                return;
            }
            catch (Exception)
            {
                // Session expired or offline: fall through to sign-in.
                state.SignOut();
            }
        }
        ShowLogin();
    }

    public void ShowLogin()
    {
        var login = new LoginWindow();
        login.Closed += (_, _) =>
        {
            if (login.SignedIn) ShowMain();
            else ExitApp();
        };
        MainWindow = login;
        login.Show();
    }

    public void ShowMain(bool startHidden = false)
    {
        var main = new MainWindow();
        main.Closing += (_, args) =>
        {
            // Closing the window hides it to the tray (when enabled) so alerts keep working.
            if (_exiting || main.SignedOut || _tray is null || !AppSettings.Load().CloseToTray) return;
            args.Cancel = true;
            main.Hide();
            var settings = AppSettings.Load();
            if (!settings.TrayTipShown)
            {
                settings.TrayTipShown = true;
                settings.Save();
                _tray.Notify("eero Desktop is still running", "It keeps watching your network from the tray. Right-click the icon to exit or change alerts.");
            }
        };
        main.Closed += (_, _) =>
        {
            StopMonitoring();
            if (main.SignedOut && !_exiting) ShowLogin();
            else ExitApp();
        };
        MainWindow = main;
        if (!startHidden) main.Show(); // hidden: the tray icon (started below) opens it
        StartMonitoring();
    }

    private void StartMonitoring()
    {
        StopMonitoring();
        _tray = new TrayIcon();
        _tray.OpenRequested += BringToFront;
        _tray.ExitRequested += ExitApp;
        _monitor = new NetworkMonitor(a => _tray?.Notify(a.Title, a.Body, a.Warning));
        _monitor.Summary += s => _tray?.SetStatus(s);
        _monitor.Start();
    }

    private void StopMonitoring()
    {
        _monitor?.Dispose(); _monitor = null;
        _tray?.Dispose(); _tray = null;
    }

    /// <summary>Settings page "Send a Test Notification". False when the tray icon isn't running.</summary>
    public bool ShowTestNotification()
    {
        if (_tray is null) return false;
        _tray.Notify("eero Desktop notifications are working", "Alerts about your network will appear like this.");
        return true;
    }

    /// <summary>Shows a notification from the tray icon (false when it isn't running).</summary>
    public bool ShowNotification(string title, string body)
    {
        if (_tray is null) return false;
        _tray.Notify(title, body);
        return true;
    }

    private void BringToFront()
    {
        if (MainWindow is not { } w) return;
        w.Show();
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Activate();
        w.Topmost = true; w.Topmost = false; // bring above other apps when asked from another process
    }

    /// <summary>Quits for real (tray "Exit", "--exit", or closing while tray mode is off).</summary>
    public void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        StopMonitoring();
        foreach (var w in Windows.Cast<Window>().ToList()) w.Close();
        try { _instance?.ReleaseMutex(); } catch (ApplicationException) { /* released on another thread */ }
        _instance?.Dispose(); _instance = null;
        Shutdown();
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "eero Desktop", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
