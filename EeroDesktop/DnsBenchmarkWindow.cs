using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Path = System.IO.Path;
using EeroDesktop.Api;

namespace EeroDesktop;

/// <summary>
/// DNS Benchmark: runs dnspyre (https://github.com/Tantalor93/dnspyre) against the ISP's servers and popular public
/// resolvers from this PC, ranks them, shows a comparison chart plus dnspyre's own graphs, and can switch eero to the
/// fastest (or a selected) provider using the same one-change DNS write as the Advanced page.
/// </summary>
public sealed class DnsBenchmarkWindow : Window
{
    public sealed record Row(ProviderResult Result, string Rank, string Provider, string Servers, string Median, string P95,
        string Average, string Success, string Note);

    private static readonly (string Title, string File)[] GraphFiles =
    [
        ("Latency histogram", "latency-histogram.png"),
        ("Latency boxplot", "latency-boxplot.png"),
        ("Latency over time", "latency-lineplot.png"),
        ("Response codes", "responses-barchart.png"),
        ("Throughput", "throughput-lineplot.png"),
    ];

    private static AppState S => AppState.Current;

    /// <summary>Below this median improvement (ms), the recommendation says switching probably isn't worth it.</summary>
    private const double SmallGainMs = 5;

    private readonly Button _run = new() { Content = "Run Benchmark" };
    private readonly Button _switchBest = new() { Content = "Switch eero to the Fastest", IsEnabled = false };
    private readonly Button _useSelected = new() { Content = "Use Selected", IsEnabled = false };
    private readonly Button _openGraphs = new() { Content = "Open Graphs Folder", IsEnabled = false };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 0, 0, 0) };
    private readonly ProgressBar _progress = new() { IsIndeterminate = true, Height = 4, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0) };
    private readonly DataGrid _grid = new() { MinHeight = 180 };
    private readonly Grid _chart = new() { Margin = new Thickness(16, 0, 0, 0) };
    private readonly TextBlock _connection = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 0, 0, 12) };
    private readonly TextBlock _recommendation = new() { FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
    private readonly TextBlock _graphsTitle = new() { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 6) };
    private readonly TabControl _graphs = new() { Height = 300, Visibility = Visibility.Collapsed };

    private List<DnsProvider> _providers = [];
    private List<ProviderResult> _results = [];
    private string? _graphsRoot;
    private CancellationTokenSource? _cts;

    public DnsBenchmarkWindow(Window? owner)
    {
        Owner = owner;
        Title = "DNS Benchmark";
        var wa = SystemParameters.WorkArea;
        Width = Math.Min(1100, wa.Width - 40);
        Height = Math.Min(900, wa.Height - 40);
        WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        AutomationProperties.SetAutomationId(_run, "BenchmarkRun");
        AutomationProperties.SetAutomationId(_switchBest, "BenchmarkSwitchBest");
        AutomationProperties.SetAutomationId(_grid, "BenchmarkGrid");
        AutomationProperties.SetAutomationId(_chart, "BenchmarkChart");
        AutomationProperties.SetAutomationId(_recommendation, "BenchmarkRecommendation");
        AutomationProperties.SetAutomationId(_graphs, "BenchmarkGraphs");
        AutomationProperties.SetAutomationId(_status, "BenchmarkStatus");
        _run.Style = (Style)Application.Current.FindResource("PrimaryButton");
        _switchBest.Style = (Style)Application.Current.FindResource("PrimaryButton");

        // Numbers size to their header/content (never cut off); names, servers and notes share the remaining width.
        foreach (var (header, path, width) in new[]
        {
            ("#", "Rank", 0.0), ("Provider", "Provider", 1.5), ("Servers", "Servers", 1.5), ("Median", "Median", 0.0),
            ("95th %", "P95", 0.0), ("Average", "Average", 0.0), ("Answered", "Success", 0.0), ("Notes", "Note", 1.6),
        })
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = header, Binding = new System.Windows.Data.Binding(path),
                Width = width == 0 ? DataGridLength.Auto : new DataGridLength(width, DataGridLengthUnitType.Star),
                MinWidth = width == 0 ? 0 : 110,
            });
        _grid.SelectionChanged += (_, _) => ShowSelected();

        _run.Click += async (_, _) => await RunAsync();
        _switchBest.Click += async (_, _) => { if (_results.FirstOrDefault(r => r.Ok) is { } best) await SwitchToAsync(best); };
        _useSelected.Click += async (_, _) => { if (_grid.SelectedItem is Row { Result.Ok: true } row) await SwitchToAsync(row.Result); };
        _openGraphs.Click += (_, _) => { if (_graphsRoot is not null) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_graphsRoot}\"")); };
        Closing += (_, _) => _cts?.Cancel();

        // ---- layout ----
        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
            Text = "Measures how quickly each DNS provider answers from this PC — your ISP's servers (eero's automatic setting) and popular public ones — "
                 + $"by looking up {DnsBenchmark.Domains.Length} popular sites several times each. Then you can switch eero to the fastest. "
                 + "Benchmarking only sends DNS lookups; nothing on your network changes until you choose to switch.",
        });
        AutomationProperties.SetAutomationId(_connection, "BenchmarkConnection");
        _connection.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        _connection.Text = ConnectionNote(HistoryStore.ConnectionType());
        root.Children.Add(_connection);
        var bar = new DockPanel();
        DockPanel.SetDock(_run, Dock.Left);
        bar.Children.Add(_run);
        bar.Children.Add(_status);
        root.Children.Add(bar);
        root.Children.Add(_progress);

        var results = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        results.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        results.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        results.Children.Add(_grid);
        Grid.SetColumn(_chart, 1);
        results.Children.Add(_chart);
        root.Children.Add(results);
        root.Children.Add(_recommendation);

        var actions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        foreach (var b in new[] { _switchBest, _useSelected, _openGraphs }) actions.Children.Add(b);
        root.Children.Add(actions);
        root.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 6, 0, 0),
            Text = "Switching DNS makes eero restart every eero a few minutes later, so Wi-Fi and internet drop briefly.",
        }.Also(t => t.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCautionBrush")));

        root.Children.Add(_graphsTitle);
        root.Children.Add(_graphs);
        root.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 10, 0, 0),
            Text = $"Powered by dnspyre {Dnspyre.Version} (MIT License) — {Dnspyre.SourceUrl}",
        }.Also(t => t.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush")));

        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = root };

        _providers = DnsBenchmark.Providers(S.Network);
        _status.Text = $"{_providers.Count} providers ready to test.";
    }

    /// <summary>Explains that results are measured from this PC, and what this PC's connection adds.</summary>
    public static string ConnectionNote(string connection) => connection switch
    {
        "Wi-Fi" => "Measured from this PC, which is on Wi-Fi — not from your eeros. Every result includes this PC's Wi-Fi hop "
                   + "(usually a few milliseconds, and it varies), so the times are higher than your eeros would see. That delay is the same "
                   + "for every provider, so the ranking still holds. For the most accurate numbers, run it from a PC plugged into an eero with a cable.",
        "Ethernet" => "Measured from this PC, which is plugged into the network with a cable — not from your eeros themselves, but "
                      + "close to what they see, since both use the same internet connection.",
        _ => "Measured from this PC, not from your eeros. Your eeros use the same internet connection, so the ranking carries over, "
             + "but if this PC is on Wi-Fi every time includes a few milliseconds for the Wi-Fi hop.",
    };

    private void Busy(bool busy)
    {
        _run.Content = busy ? "Cancel" : "Run Benchmark";
        _progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        _switchBest.IsEnabled = _useSelected.IsEnabled = !busy && _results.Any(r => r.Ok);
    }

    private async Task RunAsync()
    {
        if (_cts is not null) { _cts.Cancel(); return; } // the button is "Cancel" while running

        if (!Dnspyre.IsInstalled && MessageBox.Show(this,
                $"The DNS Benchmark uses dnspyre, an open-source DNS benchmarking tool ({Dnspyre.SourceUrl}).\n\n"
                + $"eero Desktop will download dnspyre {Dnspyre.Version} (about 18 MB) from its official GitHub release, check it against "
                + "the published SHA-256 checksum, and keep it for future benchmarks. Continue?",
                "Download dnspyre", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        _cts = new CancellationTokenSource();
        _results = [];
        Busy(true);
        var progress = new Progress<string>(m => _status.Text = m);
        try
        {
            await S.ReloadNetworkAsync();
            _providers = DnsBenchmark.Providers(S.Network);
            await Dnspyre.EnsureInstalledAsync(progress, _cts.Token);
            _graphsRoot = NewGraphsRoot();
            var repeats = int.TryParse(Environment.GetEnvironmentVariable("EERO_DESKTOP_DNS_REPEATS"), out var r) && r > 0 ? r : 3;
            _results = await DnsBenchmark.RunAsync(_providers, _graphsRoot, repeats, progress, _cts.Token);
            _status.Text = $"Finished — {_results.Count(x => x.Ok)} of {_results.Count} providers answered reliably.";
            AppSettings.Log("DNSBENCH", string.Join("; ", _results.Select(x => $"{x.Provider.Name} {(x.Ok ? $"{x.MedianMs:0} ms" : "failed")}")));
            var connection = HistoryStore.ConnectionType();
            _connection.Text = ConnectionNote(connection);
            HistoryStore.AddDnsRun(new(S.NetworkId, DateTime.UtcNow, connection, DnsBenchmark.Current(S.Network, _providers)?.Name,
                _results.Select(x => new HistoryStore.DnsProviderResult(x.Provider.Name, Round(x.MedianMs), Round(x.P95Ms), Math.Round(x.SuccessRate, 3), x.Ok)).ToList()));
        }
        catch (OperationCanceledException) { _status.Text = "Benchmark cancelled."; }
        catch (Exception ex) { _status.Text = "Benchmark failed: " + ex.Message; }
        finally
        {
            _cts?.Dispose(); _cts = null;
            Render();
            Busy(false);
        }
    }

    private static string NewGraphsRoot()
    {
        var parent = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EeroDesktop", "dns-benchmark");
        Directory.CreateDirectory(parent);
        // Keep the last few runs' graphs only.
        foreach (var old in new DirectoryInfo(parent).GetDirectories().OrderByDescending(d => d.CreationTimeUtc).Skip(4))
            try { old.Delete(recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        var dir = Path.Combine(parent, DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static double Round(double v) => double.IsNaN(v) ? -1 : Math.Round(v, 1); // JSON has no NaN; -1 = no answer

    private static string Ms(double v) => double.IsNaN(v) ? "—" : $"{v:0} ms";

    private void Render()
    {
        var current = DnsBenchmark.Current(S.Network, _providers);
        var rank = 0;
        var rows = _results.Select(r =>
        {
            var notes = new List<string>();
            if (current is not null && r.Provider == current) notes.Add("In use now");
            if (r.Provider.Note.Length > 0) notes.Add(r.Provider.Note);
            if (!r.Ok) notes.Add(r.Error ?? $"Answered only {r.SuccessRate:P0}");
            return new Row(r, r.Ok ? $"{++rank}" : "—", r.Provider.Name, string.Join(", ", r.Provider.Servers),
                Ms(r.MedianMs), Ms(r.P95Ms), Ms(r.MeanMs), r.Runs.Count == 0 ? "—" : $"{r.SuccessRate:P0}", string.Join(" · ", notes));
        }).ToList();
        _grid.ItemsSource = rows;
        _grid.SelectedItem = rows.FirstOrDefault();
        DrawChart();

        var best = _results.FirstOrDefault(r => r.Ok);
        var currentResult = _results.FirstOrDefault(r => r.Provider == current);
        if (best is null) _recommendation.Text = _results.Count == 0 ? "" : "No provider answered reliably — check your internet connection and try again.";
        else if (best.Provider == current) _recommendation.Text = $"You're already using the fastest: {best.Provider.Name} ({Ms(best.MedianMs)} median).";
        else
        {
            var gain = currentResult is { Ok: true } ? currentResult.MedianMs - best.MedianMs : double.NaN;
            var faster = double.IsNaN(gain) ? "" : $" — about {gain:0} ms faster per lookup than {currentResult!.Provider.Name}";
            _recommendation.Text = $"Fastest: {best.Provider.Name} ({Ms(best.MedianMs)} median, {Ms(best.P95Ms)} at the 95th percentile){faster}.";
            // Results from run to run shift by a few ms; a tiny gain isn't worth restarting every eero.
            if (!double.IsNaN(gain) && gain < SmallGainMs)
                _recommendation.Text += $" That's a small difference (under {SmallGainMs:0} ms), so switching probably isn't worth restarting your eeros.";
            if (current?.BlocksAds == true && !best.Provider.BlocksAds) _recommendation.Text += $" {best.Provider.Name} doesn't block ads.";
        }
        _recommendation.Text += AdBlockAdvice(current, currentResult);
        _switchBest.Content = best is null ? "Switch eero to the Fastest"
            : best.Provider == current ? "Already Using the Fastest"
            : $"Switch eero to {best.Provider.Name}";
        _switchBest.IsEnabled = best is not null && best.Provider != current;
        _openGraphs.IsEnabled = _graphsRoot is not null && Directory.Exists(_graphsRoot);
    }

    /// <summary>When ads are blocked by DNS now: which ad-blocking provider is fastest, and what blocking costs vs. the fastest overall.</summary>
    private string AdBlockAdvice(DnsProvider? current, ProviderResult? currentResult)
    {
        if (current?.BlocksAds != true) return "";
        var bestAd = _results.FirstOrDefault(r => r.Ok && r.Provider.BlocksAds);
        if (bestAd is null) return "";
        var text = "\nTo keep blocking ads: ";
        if (bestAd.Provider == current) text += $"{current.Name} is already the fastest ad-blocking option ({Ms(bestAd.MedianMs)}).";
        else
        {
            var gain = currentResult is { Ok: true } ? currentResult.MedianMs - bestAd.MedianMs : double.NaN;
            text += $"{bestAd.Provider.Name} is the fastest ad-blocking option ({Ms(bestAd.MedianMs)}"
                    + (double.IsNaN(gain) ? ")." : $", about {gain:0} ms faster than {current.Name}).");
            if (!double.IsNaN(gain) && gain < SmallGainMs) text += " That's too small to be worth restarting your eeros.";
        }
        if (_results.FirstOrDefault(r => r.Ok) is { } best && !best.Provider.BlocksAds && best.MedianMs < bestAd.MedianMs)
            text += $" Blocking ads costs about {bestAd.MedianMs - best.MedianMs:0} ms per lookup compared with {best.Provider.Name}.";
        return text;
    }

    /// <summary>Horizontal bars: median per provider, with a tick at the 95th percentile. Theme-aware.</summary>
    private void DrawChart()
    {
        _chart.Children.Clear();
        _chart.RowDefinitions.Clear();
        _chart.ColumnDefinitions.Clear();
        var ok = _results.Where(r => r.Ok).ToList();
        if (ok.Count == 0) return;
        _chart.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        _chart.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        const double barMax = 100; // leaves room for the "median / p95 ms" label in the 300-px column
        var max = ok.Max(r => r.P95Ms);
        AddChartRow(new TextBlock { Text = "Median (bar) and 95th percentile (tick)", FontSize = 11, Margin = new Thickness(0, 0, 0, 6) }
            .Also(t => { Grid.SetColumnSpan(t, 2); t.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush"); }), null);
        for (var i = 0; i < ok.Count; i++)
        {
            var r = ok[i];
            var name = new TextBlock { Text = r.Provider.Name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, FontWeight = i == 0 ? FontWeights.SemiBold : FontWeights.Normal };
            var canvas = new Canvas { Height = 22, Margin = new Thickness(6, 2, 0, 2) };
            var barWidth = Math.Max(2, r.MedianMs / max * barMax);
            var rect = new Rectangle { Width = barWidth, Height = 14, RadiusX = 3, RadiusY = 3 };
            rect.SetResourceReference(Shape.FillProperty, i == 0 ? "AccentFillColorDefaultBrush" : "ControlStrongFillColorDefaultBrush");
            Canvas.SetTop(rect, 4);
            canvas.Children.Add(rect);
            var tickX = r.P95Ms / max * barMax;
            var tick = new Rectangle { Width = 2, Height = 20 };
            tick.SetResourceReference(Shape.FillProperty, "TextFillColorSecondaryBrush");
            Canvas.SetLeft(tick, tickX); Canvas.SetTop(tick, 1);
            canvas.Children.Add(tick);
            var label = new TextBlock { Text = $"{r.MedianMs:0} / {r.P95Ms:0} ms", FontSize = 11 };
            Canvas.SetLeft(label, Math.Max(barWidth, tickX) + 6); Canvas.SetTop(label, 3);
            canvas.Children.Add(label);
            AddChartRow(name, canvas);
        }
    }

    private void AddChartRow(UIElement left, UIElement? right)
    {
        _chart.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var row = _chart.RowDefinitions.Count - 1;
        Grid.SetRow(left, row);
        _chart.Children.Add(left);
        if (right is null) return;
        Grid.SetRow(right, row); Grid.SetColumn(right, 1);
        _chart.Children.Add(right);
    }

    /// <summary>dnspyre's own graphs for the selected provider's primary server.</summary>
    private void ShowSelected()
    {
        _useSelected.IsEnabled = _grid.SelectedItem is Row { Result.Ok: true } && _cts is null;
        _graphs.Items.Clear();
        if (_grid.SelectedItem is not Row { Result.GraphsDir: { } dir } row || !Directory.Exists(dir))
        {
            _graphs.Visibility = Visibility.Collapsed;
            _graphsTitle.Text = "";
            return;
        }
        _graphsTitle.Text = $"dnspyre graphs — {row.Provider} ({row.Result.Provider.Servers[0]})";
        foreach (var (title, file) in GraphFiles)
        {
            var path = Path.Combine(dir, file);
            if (!File.Exists(path)) continue;
            var bmp = new BitmapImage();
            bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.UriSource = new Uri(path); bmp.EndInit(); // OnLoad: don't lock the file
            var image = new Image { Source = bmp, Stretch = Stretch.Uniform };
            AutomationProperties.SetName(image, title);
            _graphs.Items.Add(new TabItem
            {
                Header = J.Label(title),
                Content = new Border { Background = Brushes.White, CornerRadius = new CornerRadius(4), Padding = new Thickness(6), Child = image },
            });
        }
        _graphs.SelectedIndex = 0;
        _graphs.Visibility = _graphs.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task SwitchToAsync(ProviderResult target)
    {
        var current = DnsBenchmark.Current(S.Network, _providers);
        var what = target.Provider.IsIsp ? "your ISP's DNS servers (eero automatic)" : $"{target.Provider.Name} ({string.Join(", ", target.Provider.Servers)})";
        var message = $"Switch eero's DNS to {what}?\n\nMedian lookup time: {Ms(target.MedianMs)}.";
        if (current?.BlocksAds == true && !target.Provider.BlocksAds)
            message += $"\n\nYou're using {current.Name} for ad blocking now — {target.Provider.Name} doesn't block ads, so ad blocking turns off.";
        else if (target.Provider.BlocksAds)
            message += $"\n\n{target.Provider.Name} blocks ads and trackers. A few sites may not work properly; you can switch back any time.";
        message += "\n\neero will restart every eero a few minutes after this change. Wi-Fi and internet will drop for everyone for a few minutes.";
        if (MessageBox.Show(this, message, "Switch DNS", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;

        IsEnabled = false;
        try
        {
            var changed = target.Provider.IsIsp ? await S.SetAutomaticDnsAsync() : await S.SetCustomDnsAsync(target.Provider.Servers);
            _status.Text = changed ? $"eero now uses {target.Provider.Name} for DNS. Your eeros will restart in a few minutes."
                                   : $"eero already uses {target.Provider.Name} — nothing was sent.";
            S.Report(_status.Text);
            AppSettings.Log("DNSBENCH", $"Switched DNS to {target.Provider.Name} ({string.Join(", ", target.Provider.Servers)})");
        }
        catch (Exception ex)
        {
            _status.Text = "Couldn't switch DNS: " + ex.Message;
        }
        finally
        {
            IsEnabled = true;
            Render();
        }
    }
}

internal static class FluentExtensions
{
    /// <summary>Small helper for configuring code-built elements inline.</summary>
    public static T Also<T>(this T value, Action<T> configure) { configure(value); return value; }
}
