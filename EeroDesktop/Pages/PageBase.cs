using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using EeroDesktop.Api;

namespace EeroDesktop.Pages;

/// <summary>Base for every main-window page: load hook plus busy/error handling.</summary>
public abstract class PageBase : UserControl
{
    protected static AppState S => AppState.Current;
    protected static EeroClient Api => AppState.Current.Api;

    public abstract string Title { get; }

    /// <summary>Called when the page is shown or the user presses Refresh.</summary>
    public abstract Task LoadAsync();

    protected Window? Owner => Window.GetWindow(this);

    /// <summary>Runs an API action with a status message, disabling the page and reporting errors.</summary>
    protected async Task<bool> Run(string busy, Func<Task> work, string? done = null)
    {
        IsEnabled = false;
        S.Report(busy);
        try
        {
            await work();
            S.Report(done ?? "Ready");
            return true;
        }
        catch (Exception ex)
        {
            S.Report("Error: " + ex.Message);
            MessageBox.Show(Owner!, ex.Message, "eero Desktop", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        finally
        {
            IsEnabled = true;
        }
    }

    protected bool Confirm(string message, string title = "Please Confirm")
        => MessageBox.Show(Owner!, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    protected void ShowJson(string title, JsonNode? node) => JsonWindow.Show(Owner, title, node);
}
