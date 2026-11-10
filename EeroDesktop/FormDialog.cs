using System.Windows;
using System.Windows.Controls;

namespace EeroDesktop;

/// <summary>Small code-built dialog for entering a few values (text, choice, checkbox).</summary>
public sealed class FormDialog : Window
{
    private abstract record Field(string Key, string Label);
    private sealed record TextField(string Key, string Label, string Value, bool Required) : Field(Key, Label);
    private sealed record ChoiceField(string Key, string Label, IReadOnlyList<string> Options, string? Value) : Field(Key, Label);
    private sealed record CheckField(string Key, string Label, bool Value) : Field(Key, Label);

    private readonly List<Field> _fields = [];
    private readonly Dictionary<string, Control> _inputs = [];
    private readonly string _intro;

    public FormDialog(Window? owner, string title, string intro = "")
    {
        Owner = owner;
        Title = title;
        _intro = intro;
        Width = 440;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
    }

    public FormDialog Text(string key, string label, string? value = null, bool required = false)
    { _fields.Add(new TextField(key, label, value ?? "", required)); return this; }

    public FormDialog Choice(string key, string label, IReadOnlyList<string> options, string? value = null)
    { _fields.Add(new ChoiceField(key, label, options, value)); return this; }

    public FormDialog Check(string key, string label, bool value = false)
    { _fields.Add(new CheckField(key, label, value)); return this; }

    public string Get(string key) => _inputs[key] switch
    {
        TextBox t => t.Text.Trim(),
        ComboBox c => c.SelectedItem as string ?? c.Text,
        CheckBox b => b.IsChecked == true ? "true" : "false",
        _ => "",
    };

    public bool GetBool(string key) => _inputs[key] is CheckBox { IsChecked: true };

    /// <summary>Shows the dialog; returns true when the user pressed OK with all required fields filled.</summary>
    public bool Prompt()
    {
        var panel = new StackPanel { Margin = new Thickness(18) };
        if (!string.IsNullOrEmpty(_intro))
            panel.Children.Add(new TextBlock { Text = _intro, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });

        foreach (var f in _fields)
        {
            Control input;
            switch (f)
            {
                case TextField t:
                    panel.Children.Add(new TextBlock { Text = EeroDesktop.Api.J.Label(t.Label), Margin = new Thickness(0, 6, 0, 3) });
                    input = new TextBox { Text = t.Value, Padding = new Thickness(4, 3, 4, 3) };
                    break;
                case ChoiceField c:
                    panel.Children.Add(new TextBlock { Text = EeroDesktop.Api.J.Label(c.Label), Margin = new Thickness(0, 6, 0, 3) });
                    var combo = new ComboBox { ItemsSource = c.Options, IsEditable = false };
                    combo.SelectedItem = c.Value is not null && c.Options.Contains(c.Value) ? c.Value : c.Options.FirstOrDefault();
                    input = combo;
                    break;
                case CheckField b:
                    input = new CheckBox { Content = b.Label, IsChecked = b.Value, Margin = new Thickness(0, 8, 0, 0) };
                    break;
                default:
                    continue;
            }
            _inputs[f.Key] = input;
            panel.Children.Add(input);
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        ok.Click += (_, _) =>
        {
            var missing = _fields.OfType<TextField>().FirstOrDefault(t => t.Required && string.IsNullOrWhiteSpace(Get(t.Key)));
            if (missing is not null)
            {
                MessageBox.Show(this, $"{missing.Label} is required.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            DialogResult = true;
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        Content = panel;

        Loaded += (_, _) => _inputs.Values.FirstOrDefault()?.Focus();
        return ShowDialog() == true;
    }
}
