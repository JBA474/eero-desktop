using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace EeroDesktop;

/// <summary>A host shown in a table row: its display name, its address (empty when it has none), and an optional web port.</summary>
public sealed record HostTarget(string Name, string Host, int? WebPort = null);

/// <summary>
/// Right-click menu for tables that list hosts (Devices, eeros, reservations, port forwards): Ping (built-in window),
/// Connect with SSH (Windows OpenSSH in Windows Terminal or a console), Open in Browser (HTTP/HTTPS), Open with FTP
/// (the user's FTP program, else File Explorer) and Copy Address.
/// </summary>
public static partial class HostActions
{
    /// <summary>IPv4, IPv6 (incl. %zone) or a DNS name. Anything else is refused, so nothing odd reaches a command line.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9.:\-%]*$")]
    private static partial Regex SafeHost();

    [GeneratedRegex(@"^[A-Za-z0-9._\-]+$")]
    private static partial Regex SafeUser();

    public static bool IsSafeHost(string host) => host.Length is > 0 and < 254 && SafeHost().IsMatch(host);

    /// <summary>The host as it goes in a URL: IPv6 in brackets (zone "%" escaped as "%25").</summary>
    public static string UrlHost(string host) => host.Contains(':') ? $"[{host.Replace("%", "%25")}]" : host;

    public static string Url(string scheme, string host, int? port = null) =>
        port is { } p && !(scheme == "http" && p == 80) && !(scheme == "https" && p == 443) && !(scheme == "ftp" && p == 21)
            ? $"{scheme}://{UrlHost(host)}:{p}/"
            : $"{scheme}://{UrlHost(host)}/";

    /// <summary>Adds the host menu to a table. <paramref name="target"/> maps a row to its host (null = no menu for that row).</summary>
    public static void Attach(DataGrid grid, Func<object, HostTarget?> target)
    {
        var menu = new ContextMenu();
        AutomationProperties.SetAutomationId(menu, "HostMenu");
        grid.ContextMenu = menu;
        DataGridRow? RowOf(object source) =>
            source is DependencyObject d ? ItemsControl.ContainerFromElement(grid, d) as DataGridRow : null;

        // Right-click selects the row under the pointer, so the menu always acts on the row that was clicked.
        grid.PreviewMouseRightButtonDown += (_, e) =>
        {
            if (RowOf(e.OriginalSource) is { } row && !Equals(grid.SelectedItem, row.Item)) grid.SelectedItem = row.Item;
        };

        // The menu opens for the row it was opened on: the row under the pointer, or the focused row from the keyboard
        // (Shift+F10 / Menu key). Not on headers or empty space.
        grid.ContextMenuOpening += (_, e) =>
        {
            if (RowOf(e.OriginalSource)?.Item is not { } item || target(item) is not { } t) { e.Handled = true; return; }
            Fill(menu, t, () => Window.GetWindow(grid));
        };

        // Test seam: UI Automation can't right-click without moving the user's real mouse, so the end-to-end tests set
        // EERO_DESKTOP_TEST_HOST_MENU=1 and selecting a row opens the same menu for it.
        if (Environment.GetEnvironmentVariable("EERO_DESKTOP_TEST_HOST_MENU") == "1")
            grid.SelectionChanged += (_, _) =>
            {
                if (grid.SelectedItem is not { } item || target(item) is not { } t) return;
                Fill(menu, t, () => Window.GetWindow(grid));
                menu.PlacementTarget = grid;
                menu.IsOpen = true;
            };
    }

    private static void Fill(ContextMenu menu, HostTarget t, Func<Window?> owner)
    {
        menu.Items.Clear();
        if (!IsSafeHost(t.Host))
        {
            menu.Items.Add(new MenuItem { Header = Plain($"{t.Name} has no IP address right now"), IsEnabled = false });
            return;
        }

        MenuItem Item(string header, string id, Action act)
        {
            var mi = new MenuItem { Header = Plain(header) };
            AutomationProperties.SetAutomationId(mi, id);
            mi.Click += (_, _) =>
            {
                try { act(); }
                catch (Exception ex)
                {
                    MessageBox.Show(owner()!, ex.Message, "eero Desktop", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            };
            return mi;
        }

        menu.Items.Add(new MenuItem { Header = Plain(t.Name == t.Host ? t.Host : $"{t.Name} ({t.Host})"), IsEnabled = false });
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Ping", "HostPing", () => PingWindow.Open(owner(), t.Name, t.Host)));
        menu.Items.Add(Item("Connect with SSH…", "HostSsh", () => Ssh(owner(), t)));
        menu.Items.Add(Item($"Open {Url("http", t.Host, t.WebPort)}", "HostHttp", () => Open(Url("http", t.Host, t.WebPort))));
        menu.Items.Add(Item($"Open {Url("https", t.Host, t.WebPort)}", "HostHttps", () => Open(Url("https", t.Host, t.WebPort))));
        menu.Items.Add(Item($"Open {Url("ftp", t.Host)} (FTP)", "HostFtp", () => Ftp(t.Host)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Copy Address", "HostCopy", () => Clipboard.SetText(t.Host)));
    }

    /// <summary>Menu text shown as typed: WPF reads "_" as an access-key marker, so device names like "Android_7I5K" would lose it.</summary>
    private static string Plain(string text) => text.Replace("_", "__");

    private static void Open(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    // ---- SSH ----

    public static string? SshExe()
    {
        var system = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH", "ssh.exe");
        return File.Exists(system) ? system : null;
    }

    private static string? WindowsTerminal()
    {
        var wt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "wt.exe");
        return File.Exists(wt) ? wt : null;
    }

    private static void Ssh(Window? owner, HostTarget t)
    {
        if (SshExe() is not { } ssh)
        {
            MessageBox.Show(owner!, "Windows' SSH client isn't installed. Add it in Settings › System › Optional Features › OpenSSH Client, then try again.",
                "Connect with SSH", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var settings = AppSettings.Load();
        var lastUser = settings.SshUsers.GetValueOrDefault(t.Host) ?? Environment.UserName;
        var dialog = new FormDialog(owner, "Connect with SSH",
                $"Opens an SSH session to {t.Name} ({t.Host}) in a terminal window. You'll be asked for the password there (or your SSH key is used).")
            .Text("user", "User name", lastUser, required: true)
            .Text("port", "Port", "22", required: true);
        if (!dialog.Prompt()) return;

        var user = dialog.Get("user");
        if (!SafeUser().IsMatch(user)) throw new ArgumentException("User names can only contain letters, numbers, dots, dashes and underscores.");
        if (!int.TryParse(dialog.Get("port"), out var port) || port is < 1 or > 65535) throw new ArgumentException("Port must be a number from 1 to 65535.");

        settings.SshUsers[t.Host] = user;
        settings.Save();

        var sshArgs = new List<string> { ssh };
        if (port != 22) sshArgs.AddRange(["-p", port.ToString()]);
        sshArgs.Add($"{user}@{t.Host}");

        ProcessStartInfo psi;
        if (WindowsTerminal() is { } wt)
        {
            psi = new ProcessStartInfo(wt) { UseShellExecute = false };
            foreach (var a in new[] { "new-tab", "--title", $"SSH {t.Host}" }.Concat(sshArgs)) psi.ArgumentList.Add(a);
        }
        else
        {
            psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false };
            psi.ArgumentList.Add("/k");
            foreach (var a in sshArgs) psi.ArgumentList.Add(a);
        }
        Process.Start(psi);
    }

    // ---- FTP ----

    private static readonly string[] BrowserProgIds = ["MSEdge", "Chrome", "Firefox", "Brave", "Opera", "Vivaldi", "IE.", "AppX"];

    private static bool IsBrowser(string? s) => s is not null &&
        (BrowserProgIds.Any(b => s.Contains(b, StringComparison.OrdinalIgnoreCase)) ||
         new[] { "msedge.exe", "chrome.exe", "firefox.exe", "brave.exe", "opera.exe", "vivaldi.exe" }.Any(b => s.Contains(b, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Browsers dropped FTP, so an FTP link that would open in a browser goes to the FTP program registered for ftp://
    /// (e.g. WinSCP or FileZilla), else to File Explorer, which still browses FTP sites.
    /// </summary>
    private static void Ftp(string host)
    {
        var url = Url("ftp", host);
        var chosen = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\Shell\Associations\UrlAssociations\ftp\UserChoice", "ProgId", null) as string;
        if (chosen is not null && !IsBrowser(chosen)) { Open(url); return; }

        if (Registry.GetValue(@"HKEY_CLASSES_ROOT\ftp\shell\open\command", "", null) is string command && !IsBrowser(command)
            && SplitCommand(command) is { } cmd && File.Exists(cmd.Exe))
        {
            var psi = new ProcessStartInfo(cmd.Exe) { UseShellExecute = false };
            foreach (var a in cmd.Args) psi.ArgumentList.Add(a == "%1" || a == "%L" ? url : a);
            if (!cmd.Args.Any(a => a is "%1" or "%L")) psi.ArgumentList.Add(url);
            Process.Start(psi);
            return;
        }

        Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = false, ArgumentList = { url } });
    }

    /// <summary>Splits a registry command like <c>"C:\Program Files\X\x.exe" /flag "%1"</c> into the program and its arguments.</summary>
    public static (string Exe, List<string> Args)? SplitCommand(string command)
    {
        var parts = new List<string>();
        foreach (Match m in Regex.Matches(command.Trim(), "\"([^\"]*)\"|(\\S+)"))
            parts.Add(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
        return parts.Count == 0 ? null : (Environment.ExpandEnvironmentVariables(parts[0]), parts.Skip(1).ToList());
    }
}
