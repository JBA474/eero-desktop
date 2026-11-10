using System.Windows;
using EeroDesktop.Api;

namespace EeroDesktop;

public partial class LoginWindow : Window
{
    private readonly AppState _state = AppState.Current;

    public bool SignedIn { get; private set; }

    public LoginWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => LoginBox.Focus();
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        var login = LoginBox.Text.Trim();
        if (login.Length == 0) { StatusText.Text = "Enter your email or phone number."; return; }

        await Busy("Requesting a code from eero…", async () =>
        {
            _state.Api.Token = null;
            var data = await _state.Api.PostFormAsync("2.2/login", new Dictionary<string, string?> { ["login"] = login });
            var token = data.S("user_token") ?? throw new InvalidOperationException("eero did not return a session token.");
            _state.Api.Token = token;
            StepLogin.Visibility = Visibility.Collapsed;
            StepVerify.Visibility = Visibility.Visible;
            Intro.Text = $"We asked eero to send a code to {login}. Enter it below.";
            StatusText.Text = "";
            CodeBox.Focus();
        });
    }

    private async void Verify_Click(object sender, RoutedEventArgs e)
    {
        var code = CodeBox.Text.Trim();
        if (code.Length == 0) { StatusText.Text = "Enter the code eero sent you."; return; }

        await Busy("Verifying…", async () =>
        {
            await _state.Api.PostFormAsync("2.2/login/verify", new Dictionary<string, string?> { ["code"] = code });
            TokenStore.Save(_state.Api.Token!);
            await _state.LoadAccountAsync();
            SignedIn = true;
            Close();
        });
    }

    private async void Resend_Click(object sender, RoutedEventArgs e)
        => await Busy("Sending a new code…", async () =>
        {
            await _state.Api.PostJsonAsync("2.2/login/resend", new System.Text.Json.Nodes.JsonObject());
            StatusText.Text = "A new code is on its way.";
        });

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        _state.Api.Token = null;
        StepVerify.Visibility = Visibility.Collapsed;
        StepLogin.Visibility = Visibility.Visible;
        Intro.Text = "Enter the email address or phone number on your eero account. eero will send you a verification code.";
        StatusText.Text = "";
    }

    private async Task Busy(string message, Func<Task> work)
    {
        IsEnabled = false;
        StatusText.Text = message;
        try { await work(); }
        catch (Exception ex) { StatusText.Text = ex.Message; }
        finally { IsEnabled = true; }
    }
}
