using System.Text.Json.Nodes;
using System.Windows;
using EeroDesktop.Api;

namespace EeroDesktop.Pages;

public partial class WifiPage : PageBase
{
    private JsonNode? _guest;

    public WifiPage() => InitializeComponent();

    public override string Title => "Wi-Fi & Guest";

    public override async Task LoadAsync()
    {
        await Run("Loading Wi-Fi settings…", async () =>
        {
            await S.ReloadNetworkAsync();
            _guest = S.TryRes("guestnetwork") is { } g ? await Api.GetAsync(g) : S.Network.At("guest_network");
        });
        NameBox.Text = S.Network.S("name") ?? "";
        PasswordBox.Text = S.Network.S("password") ?? "";
        GuestEnabled.IsChecked = _guest.B("enabled");
        GuestNameBox.Text = _guest.S("name") ?? "";
        GuestPasswordBox.Text = _guest.S("password") ?? "";
        PasswordBox.IsRevealed = GuestPasswordBox.IsRevealed = false; // passwords start hidden every time the page loads
    }

    private async void SaveName_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0) return;
        if (!Confirm($"Rename your Wi-Fi to \"{name}\"? Wi-Fi devices will need to reconnect.", "Change Network Name")) return;
        var oldName = S.Network.S("name") ?? "";
        var url = S.NetworkUrl;
        if (await Run("Saving network name…", () => Api.PutFormAsync(url, new Dictionary<string, string?> { ["name"] = name }), "Network name saved.") && oldName.Length > 0)
            UndoService.RecordRequest($"Renamed Wi-Fi {oldName} to {name}", $"Rename the Wi-Fi back to {oldName}", $"Renamed Wi-Fi {name} back to {oldName}",
                $"Rename the Wi-Fi to {name} again", new("PUT", url, Form: new() { ["name"] = oldName }), new("PUT", url, Form: new() { ["name"] = name }));
        await LoadAsync();
    }

    private async void SavePassword_Click(object sender, RoutedEventArgs e)
    {
        var pw = PasswordBox.Text;
        if (pw.Length < 8) { MessageBox.Show(Owner!, "Wi-Fi passwords must be at least 8 characters.", "Password", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        if (!Confirm("Change the Wi-Fi password? Every Wi-Fi device will need the new password to reconnect.", "Change Password")) return;
        await Run("Saving password…", () => Api.PutFormAsync(S.Res("password"), new Dictionary<string, string?> { ["password"] = pw }), "Wi-Fi password saved.");
        await LoadAsync();
    }

    private void CopyPassword_Click(object sender, RoutedEventArgs e)
    {
        if (PasswordBox.Text.Length == 0) return;
        Clipboard.SetText(PasswordBox.Text);
        S.Report("Password copied to clipboard.");
    }

    private async void Generate_Click(object sender, RoutedEventArgs e)
    {
        JsonNode? data = null;
        if (await Run("Generating a password…", async () => data = await Api.PostJsonAsync("2.2/util/generatepassword", new JsonObject())))
        {
            PasswordBox.Text = data.S() ?? "";
            PasswordBox.IsRevealed = true; // show what was generated
            S.Report("Generated a password. Press Save Password to apply it.");
        }
    }

    private async void SaveGuest_Click(object sender, RoutedEventArgs e)
    {
        var url = S.TryRes("guestnetwork") ?? S.NetworkUrl + "/guestnetwork";
        var name = GuestNameBox.Text.Trim();
        if (name.Length == 0) { S.Report("Enter a guest network name."); return; }
        var oldName = _guest.S("name") ?? name;
        var wasOn = _guest.B("enabled");
        var on = GuestEnabled.IsChecked == true;
        if (await Run("Saving guest network…", () => Api.PutFormAsync(url, new Dictionary<string, string?>
            {
                ["name"] = name,
                ["enabled"] = EeroClient.Bool(on),
            }), "Guest network saved.") && (oldName != name || wasOn != on))
        {
            string Describe(string n, bool e) => $"guest network {n} ({(e ? "on" : "off")})";
            UndoService.RecordRequest($"Changed the {Describe(oldName, wasOn)} to {Describe(name, on)}", $"Put the {Describe(oldName, wasOn)} back",
                $"Put the {Describe(oldName, wasOn)} back", $"Change it to {Describe(name, on)} again",
                new("PUT", url, Form: new() { ["name"] = oldName, ["enabled"] = EeroClient.Bool(wasOn) }),
                new("PUT", url, Form: new() { ["name"] = name, ["enabled"] = EeroClient.Bool(on) }));
        }
        await LoadAsync();
    }

    private async void SaveGuestPassword_Click(object sender, RoutedEventArgs e)
    {
        var pw = GuestPasswordBox.Text;
        if (pw.Length < 8) { MessageBox.Show(Owner!, "Wi-Fi passwords must be at least 8 characters.", "Guest Password", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var url = _guest.S("resources.password") ?? (S.TryRes("guestnetwork") ?? S.NetworkUrl + "/guestnetwork") + "/password";
        await Run("Saving guest password…", () => Api.PutFormAsync(url, new Dictionary<string, string?> { ["password"] = pw }), "Guest password saved.");
        await LoadAsync();
    }

    private async void RemoveGuestPassword_Click(object sender, RoutedEventArgs e)
    {
        if (!Confirm("Remove the guest password? Anyone nearby will be able to join the guest network.", "Open Guest Network")) return;
        var url = _guest.S("resources.password") ?? (S.TryRes("guestnetwork") ?? S.NetworkUrl + "/guestnetwork") + "/password";
        await Run("Removing guest password…", () => Api.DeleteAsync(url), "Guest network is now open.");
        await LoadAsync();
    }
}
