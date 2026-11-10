using System.Windows;
using WinForms = System.Windows.Forms;

namespace EeroDesktop;

/// <summary>Notification-area icon: shows alerts, reopens the window, and holds the alert/tray preferences.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private readonly WinForms.ToolStripMenuItem _outage, _newDevices, _closeToTray;

    public event Action? OpenRequested;
    public event Action? ExitRequested;

    public TrayIcon()
    {
        using var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/AppIcon.ico"))!.Stream;
        var settings = AppSettings.Load();

        _outage = new WinForms.ToolStripMenuItem("Alert When Internet or eero Goes Down") { CheckOnClick = true, Checked = settings.OutageAlerts };
        _newDevices = new WinForms.ToolStripMenuItem("Alert When a New Device Joins") { CheckOnClick = true, Checked = settings.NewDeviceAlerts };
        _closeToTray = new WinForms.ToolStripMenuItem("Keep Running in Tray When Closed") { CheckOnClick = true, Checked = settings.CloseToTray };
        foreach (var item in new[] { _outage, _newDevices, _closeToTray }) item.CheckedChanged += (_, _) => SaveSettings();

        var open = new WinForms.ToolStripMenuItem("Open eero Desktop") { Font = new System.Drawing.Font(WinForms.Control.DefaultFont, System.Drawing.FontStyle.Bold) };
        open.Click += (_, _) => OpenRequested?.Invoke();
        var exit = new WinForms.ToolStripMenuItem("Exit");
        exit.Click += (_, _) => ExitRequested?.Invoke();

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.AddRange([open, new WinForms.ToolStripSeparator(), _outage, _newDevices, _closeToTray, new WinForms.ToolStripSeparator(), exit]);

        _icon = new WinForms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(stream, WinForms.SystemInformation.SmallIconSize),
            Text = "eero Desktop",
            ContextMenuStrip = menu,
            Visible = true,
        };
        AppSettings.Changed += SyncFromSettings;
        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke();
        _icon.BalloonTipClicked += (_, _) => OpenRequested?.Invoke();
    }

    /// <summary>Settings changed elsewhere (Settings page): update the checkmarks. Same values don't re-fire CheckedChanged.</summary>
    private void SyncFromSettings()
    {
        var s = AppSettings.Load();
        _outage.Checked = s.OutageAlerts;
        _newDevices.Checked = s.NewDeviceAlerts;
        _closeToTray.Checked = s.CloseToTray;
    }

    private void SaveSettings()
    {
        var s = AppSettings.Load();
        s.OutageAlerts = _outage.Checked;
        s.NewDeviceAlerts = _newDevices.Checked;
        s.CloseToTray = _closeToTray.Checked;
        s.Save();
    }

    /// <summary>Shows a Windows notification from the tray icon.</summary>
    public void Notify(string title, string body, bool warning = false)
        => _icon.ShowBalloonTip(10_000, title, body, warning ? WinForms.ToolTipIcon.Warning : WinForms.ToolTipIcon.Info);

    /// <summary>Tooltip text (Windows limits it to 127 characters).</summary>
    public void SetStatus(string text)
    {
        var t = "eero Desktop — " + text;
        _icon.Text = t.Length > 127 ? t[..126] + "…" : t;
    }

    public void Dispose()
    {
        AppSettings.Changed -= SyncFromSettings;
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
    }
}
