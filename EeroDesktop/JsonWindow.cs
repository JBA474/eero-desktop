using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EeroDesktop.Api;

namespace EeroDesktop;

/// <summary>Read-only viewer for raw API JSON.</summary>
public static class JsonWindow
{
    public static void Show(Window? owner, string title, JsonNode? node)
    {
        var box = new TextBox
        {
            Text = node.Pretty(),
            IsReadOnly = true,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8),
        };
        var w = new Window
        {
            Title = title,
            Owner = owner,
            Width = 720,
            Height = 640,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Content = box,
        };
        w.Show();
    }
}
