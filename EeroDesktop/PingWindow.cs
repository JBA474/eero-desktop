using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace EeroDesktop;

/// <summary>Continuous ping of one host (once a second until stopped), with running totals. Opened from a host's right-click menu.</summary>
public sealed class PingWindow : Window
{
    private const int TimeoutMs = 2000;
    private const int MaxLines = 1000;

    private static readonly Dictionary<string, PingWindow> OpenWindows = new(StringComparer.OrdinalIgnoreCase);

    private readonly string _host;
    private readonly TextBox _log = new()
    {
        IsReadOnly = true, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 13,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        TextWrapping = TextWrapping.NoWrap, AcceptsReturn = true,
    };
    private readonly TextBlock _stats = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
    private readonly Button _toggle = new() { Content = "Stop", MinWidth = 90 };
    private readonly List<string> _lines = [];
    private CancellationTokenSource? _cts;
    private int _sent, _received;
    private long _min = long.MaxValue, _max, _total;

    public static void Open(Window? owner, string name, string host)
    {
        if (OpenWindows.TryGetValue(host, out var existing)) { existing.Activate(); return; }
        var w = new PingWindow(owner, name, host);
        OpenWindows[host] = w;
        w.Show();
    }

    private PingWindow(Window? owner, string name, string host)
    {
        _host = host;
        Owner = owner;
        Title = $"Ping {host}";
        Width = 560;
        Height = 440;
        MinWidth = 380;
        MinHeight = 260;
        WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;

        AutomationProperties.SetAutomationId(_log, "PingLog");
        AutomationProperties.SetName(_log, "Ping replies");
        AutomationProperties.SetAutomationId(_stats, "PingStats");
        AutomationProperties.SetAutomationId(_toggle, "PingToggle");

        var header = new TextBlock
        {
            Text = name == host ? $"Pinging {host} once a second." : $"Pinging {name} ({host}) once a second.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
        };
        var close = new Button { Content = "Close", MinWidth = 90, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        close.Click += (_, _) => Close();
        _toggle.Click += (_, _) => { if (_cts is null) Start(); else Stop(); };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(_toggle);
        buttons.Children.Add(close);

        var root = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        DockPanel.SetDock(_stats, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(buttons);
        root.Children.Add(_stats);
        root.Children.Add(_log);
        Content = root;

        Loaded += (_, _) => Start();
        Closed += (_, _) => { Stop(); OpenWindows.Remove(_host); };
        UpdateStats();
    }

    private void Start()
    {
        _cts = new CancellationTokenSource();
        _toggle.Content = "Stop";
        _ = LoopAsync(_cts.Token);
    }

    private void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        _toggle.Content = "Start";
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        using var ping = new Ping();
        while (!ct.IsCancellationRequested)
        {
            var started = DateTime.UtcNow;
            string line;
            try
            {
                var reply = await ping.SendPingAsync(_host, TimeSpan.FromMilliseconds(TimeoutMs), cancellationToken: ct);
                if (ct.IsCancellationRequested) break;
                _sent++;
                if (reply.Status == IPStatus.Success)
                {
                    _received++;
                    _total += reply.RoundtripTime;
                    _min = Math.Min(_min, reply.RoundtripTime);
                    _max = Math.Max(_max, reply.RoundtripTime);
                    var ttl = reply.Options?.Ttl is { } t ? $" TTL={t}" : "";
                    line = $"Reply from {reply.Address}: time={(reply.RoundtripTime < 1 ? "<1" : reply.RoundtripTime.ToString())} ms{ttl}";
                }
                else line = reply.Status == IPStatus.TimedOut ? "Request timed out." : $"No reply: {Describe(reply.Status)}";
            }
            catch (OperationCanceledException) { break; }
            catch (PingException ex)
            {
                _sent++;
                line = "Error: " + (ex.InnerException?.Message ?? ex.Message);
            }

            Append($"{DateTime.Now:HH:mm:ss}  {line}");
            UpdateStats();

            var wait = TimeSpan.FromSeconds(1) - (DateTime.UtcNow - started);
            if (wait > TimeSpan.Zero)
            {
                try { await Task.Delay(wait, ct); } catch (OperationCanceledException) { break; }
            }
        }
    }

    private static string Describe(IPStatus s) => s switch
    {
        IPStatus.DestinationHostUnreachable => "destination host unreachable",
        IPStatus.DestinationNetworkUnreachable => "destination network unreachable",
        IPStatus.TtlExpired => "TTL expired in transit",
        _ => s.ToString(),
    };

    private void Append(string line)
    {
        _lines.Add(line);
        if (_lines.Count > MaxLines) _lines.RemoveRange(0, _lines.Count - MaxLines);
        var atEnd = _log.VerticalOffset + _log.ViewportHeight >= _log.ExtentHeight - 2;
        _log.Text = string.Join(Environment.NewLine, _lines);
        if (atEnd) _log.ScrollToEnd();
    }

    private void UpdateStats()
    {
        var lost = _sent - _received;
        var pct = _sent == 0 ? 0 : (int)Math.Round(100.0 * lost / _sent);
        _stats.Text = _received == 0
            ? $"Sent {_sent} · Received {_received} · Lost {lost} ({pct}%)"
            : $"Sent {_sent} · Received {_received} · Lost {lost} ({pct}%) · Min {_min} ms · Average {_total / _received} ms · Max {_max} ms";
    }
}
