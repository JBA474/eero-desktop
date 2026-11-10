using System.Windows;
using System.Windows.Controls;
using EeroDesktop.Api;
using EeroDesktop.Pages;

namespace EeroDesktop;

public partial class MainWindow : Window
{
    private readonly AppState _state = AppState.Current;
    private readonly List<PageBase> _pages;
    private bool _loading;

    public bool SignedOut { get; private set; }

    public MainWindow()
    {
        InitializeComponent();
        FitToWorkArea();
        _pages =
        [
            new OverviewPage(),
            new EerosPage(),
            new DevicesPage(),
            new ProfilesPage(),
            new WifiPage(),
            new AdvancedPage(),
            new PortsPage(),
            new ActivityPage(),
            new HistoryPage(),
            new UpdatesPage(),
            new ConsolePage(),
            new SettingsPage(),
        ];
        Nav.ItemsSource = _pages.Select(p => p.Title).ToList();
        _state.StatusChanged += OnStatus;
        UndoService.Changed += OnUndoChanged;
        Loaded += OnLoaded;
        Closed += (_, _) => { _state.StatusChanged -= OnStatus; UndoService.Changed -= OnUndoChanged; };
    }

    private void OnUndoChanged() => Dispatcher.BeginInvoke(UpdateUndo);

    /// <summary>Shows "Undo Last Change" when the selected network has a saved change to put back.</summary>
    private void UpdateUndo()
    {
        var entry = _state.Selected is null ? null : UndoService.For(_state.NetworkId);
        UndoButton.Visibility = entry is null ? Visibility.Collapsed : Visibility.Visible;
        UndoButton.ToolTip = entry is null ? null : $"{entry.UndoText}\n(last change: {entry.Done}, {J.Ago(entry.Utc.ToLocalTime())})";
    }

    private async void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_state.Selected is null || UndoService.For(_state.NetworkId) is not { } entry) return;
        var message = $"Your last change on {_state.Selected.Name} ({J.Ago(entry.Utc.ToLocalTime())}):\n{entry.Done}.\n\nUndo will: {entry.UndoText}.";
        if (entry.RestartsAll) message += "\n\neero will restart every eero a few minutes after this change. Wi-Fi and internet will drop for everyone for a few minutes.";
        if (MessageBox.Show(this, message, "Undo Last Change", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;

        UndoButton.IsEnabled = false;
        StatusText.Text = "Undoing: " + entry.UndoText + "…";
        try
        {
            var changed = await UndoService.UndoAsync(entry);
            if (!changed) UndoService.Forget(entry.NetworkId); // nothing to put back any more
            StatusText.Text = changed ? $"Undone: {entry.UndoText}." + (entry.RestartsAll ? " Your eeros will restart in a few minutes." : "")
                                      : "The network already has that setting — nothing was sent.";
            UpdateHealth();
            if (PageHost.Content is PageBase page) await page.LoadAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Couldn't undo: " + ex.Message;
            MessageBox.Show(this, ex.Message, "Undo Last Change", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { UndoButton.IsEnabled = true; UpdateUndo(); }
    }

    /// <summary>Keeps the window (and its title bar) on screen on small or high-scaling displays.</summary>
    private void FitToWorkArea()
    {
        var wa = SystemParameters.WorkArea;
        Width = Math.Min(Width, wa.Width - 32);
        Height = Math.Min(Height, wa.Height - 32);
        MinWidth = Math.Min(MinWidth, Width);
        MinHeight = Math.Min(MinHeight, Height);
    }

    private void OnStatus(string message) => Dispatcher.Invoke(() => StatusText.Text = message);

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        AccountText.Text = _state.Account.S("email.value") ?? _state.Account.S("phone.value") ?? _state.Account.S("name") ?? "";
        if (_state.Networks.Count == 0)
        {
            StatusText.Text = "This eero account has no networks.";
            return;
        }
        _loading = true;
        NetworkCombo.ItemsSource = _state.Networks;
        NetworkCombo.SelectedIndex = 0;
        _loading = false;
        await SelectNetworkAsync(_state.Networks[0]);
        Nav.SelectedIndex = 0;
    }

    private async Task SelectNetworkAsync(NetworkRef network)
    {
        try
        {
            StatusText.Text = $"Loading {network.Name}…";
            await _state.SelectNetworkAsync(network);
            UpdateHealth();
            UpdateUndo();
            StatusText.Text = "Ready";
            if (PageHost.Content is PageBase page) await page.LoadAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Error: " + ex.Message;
            if (ex is EeroApiException { IsUnauthorized: true }) SignOut();
        }
    }

    private void UpdateHealth()
    {
        var internet = _state.Network.S("health.internet.status");
        var mesh = _state.Network.S("health.eero_network.status");
        // Green / yellow / red light beside each status; the text says the same thing.
        HealthText.Inlines.Clear();
        if (internet is null) return;
        void Add(string label, string? status, bool first)
        {
            if (!first) HealthText.Inlines.Add(new System.Windows.Documents.Run("    "));
            HealthText.Inlines.Add(new System.Windows.Documents.InlineUIContainer(StatusLights.Light(status, 12)) { BaselineAlignment = BaselineAlignment.Center });
            HealthText.Inlines.Add(new System.Windows.Documents.Run($"{label}: {J.Title(status ?? "unknown")}"));
        }
        Add("Internet", internet, true);
        Add("eero Network", mesh, false);
        System.Windows.Automation.AutomationProperties.SetName(HealthText, $"Internet: {J.Title(internet)}, eero Network: {J.Title(mesh ?? "unknown")}");
        HealthText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
    }

    private async void NetworkCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || NetworkCombo.SelectedItem is not NetworkRef n) return;
        await SelectNetworkAsync(n);
    }

    private async void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Nav.SelectedIndex < 0) return;
        var page = _pages[Nav.SelectedIndex];
        PageHost.Content = page;
        if (_state.Selected is null) return;
        try { await page.LoadAsync(); }
        catch (Exception ex) { StatusText.Text = "Error: " + ex.Message; }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_state.Selected is null) return;
        await SelectNetworkAsync(_state.Selected);
    }

    private void SignOut_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Sign out of eero on this computer?", "Sign Out", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        _ = _state.Api.PostFormAsync("2.2/logout", new Dictionary<string, string?>()).ContinueWith(_ => { });
        SignOut();
    }

    private void SignOut()
    {
        _state.SignOut();
        SignedOut = true;
        Close();
    }
}
