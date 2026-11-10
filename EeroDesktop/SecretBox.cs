using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace EeroDesktop;

/// <summary>
/// Password field that is masked (●●●●) by default, with a Show/Hide button to switch to readable text.
/// Exposes the value as <see cref="Text"/> either way, so callers don't care which mode is showing.
/// </summary>
public sealed class SecretBox : UserControl
{
    private readonly PasswordBox _masked = new() { VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBox _plain = new() { Visibility = Visibility.Collapsed, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly Button _toggle = new() { Content = "Show", Margin = new Thickness(8, 0, 0, 0), MinWidth = 64 };
    private bool _syncing;

    public SecretBox()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(_masked);
        grid.Children.Add(_plain);
        Grid.SetColumn(_toggle, 1);
        grid.Children.Add(_toggle);
        Content = grid;

        _masked.PasswordChanged += (_, _) => Sync(() => _plain.Text = _masked.Password);
        _plain.TextChanged += (_, _) => Sync(() => _masked.Password = _plain.Text);
        _toggle.Click += (_, _) => IsRevealed = !IsRevealed;

        // Stable UI Automation ids (used by the end-to-end tests): <Name>Masked, <Name>Plain, <Name>Toggle.
        Loaded += (_, _) =>
        {
            AutomationProperties.SetAutomationId(_masked, Name + "Masked");
            AutomationProperties.SetAutomationId(_plain, Name + "Plain");
            AutomationProperties.SetAutomationId(_toggle, Name + "Toggle");
            AutomationProperties.SetName(_toggle, IsRevealed ? "Hide" : "Show");
        };
    }

    public string Text
    {
        get => _masked.Password;
        set { _masked.Password = value ?? ""; _plain.Text = value ?? ""; }
    }

    /// <summary>True while the password is shown as plain text.</summary>
    public bool IsRevealed
    {
        get => _plain.Visibility == Visibility.Visible;
        set
        {
            var hadFocus = _masked.IsKeyboardFocusWithin || _plain.IsKeyboardFocusWithin;
            _plain.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            _masked.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
            _toggle.Content = value ? "Hide" : "Show";
            AutomationProperties.SetName(_toggle, (string)_toggle.Content);
            _toggle.ToolTip = value ? "Hide the password" : "Show the password as plain text";
            if (hadFocus) { if (value) _plain.Focus(); else _masked.Focus(); }
        }
    }

    private void Sync(Action update)
    {
        if (_syncing) return;
        _syncing = true;
        try { update(); } finally { _syncing = false; }
    }
}
