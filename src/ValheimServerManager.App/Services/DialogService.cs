using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.Storage.Pickers;
using ValheimServerManager.App.Localization;
using ValheimServerManager.Core.Servers;

namespace ValheimServerManager.App.Services;

public interface IDialogService
{
    /// <summary>Asks for confirmation. Null button texts mean "Confirm" and "Cancel" in the UI language.</summary>
    Task<bool> ConfirmAsync(string title, string message, string? primaryText = null, string? closeText = null, bool destructive = false);

    /// <summary>Shows a message. A null button text means "OK" in the UI language.</summary>
    Task AlertAsync(string title, string message, string? closeText = null);

    Task<string?> PromptAsync(string title, string message, string placeholder, string initialValue = "");

    /// <summary>Three-way choice. Returns 1 (primary), 2 (secondary) or 0 (cancel). A null close text means "Cancel".</summary>
    Task<int> ChooseAsync(string title, string message, string primaryText, string secondaryText, string? closeText = null);

    Task<bool> ShowChecksAsync(string title, string intro, IReadOnlyList<StartCheckItem> checks, bool canProceed, string proceedText);
}

public sealed class DialogService : IDialogService
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    private static XamlRoot Root =>
        App.GetService<MainWindow>().Content?.XamlRoot ?? throw new InvalidOperationException("Window not loaded yet.");

    public async Task<bool> ConfirmAsync(string title, string message, string? primaryText = null, string? closeText = null, bool destructive = false)
    {
        var dialog = Create(title, Text(message));
        dialog.PrimaryButtonText = primaryText ?? ShellStrings.Dialog_Confirm;
        dialog.CloseButtonText = closeText ?? ShellStrings.Dialog_Cancel;
        dialog.DefaultButton = destructive ? ContentDialogButton.Close : ContentDialogButton.Primary;
        if (destructive)
        {
            dialog.PrimaryButtonStyle = (Style)Application.Current.Resources["AccentButtonStyle"];
        }

        return await ShowAsync(dialog) == ContentDialogResult.Primary;
    }

    public async Task AlertAsync(string title, string message, string? closeText = null)
    {
        var dialog = Create(title, Text(message));
        dialog.CloseButtonText = closeText ?? ShellStrings.Dialog_Ok;
        dialog.DefaultButton = ContentDialogButton.Close;
        await ShowAsync(dialog);
    }

    public async Task<string?> PromptAsync(string title, string message, string placeholder, string initialValue = "")
    {
        var box = new TextBox { PlaceholderText = placeholder, Text = initialValue, Margin = new Thickness(0, 12, 0, 0) };
        var panel = new StackPanel();
        panel.Children.Add(Text(message));
        panel.Children.Add(box);
        var dialog = Create(title, panel);
        dialog.PrimaryButtonText = ShellStrings.Dialog_Ok;
        dialog.CloseButtonText = ShellStrings.Dialog_Cancel;
        dialog.DefaultButton = ContentDialogButton.Primary;
        return await ShowAsync(dialog) == ContentDialogResult.Primary ? box.Text.Trim() : null;
    }

    public async Task<int> ChooseAsync(string title, string message, string primaryText, string secondaryText, string? closeText = null)
    {
        var dialog = Create(title, Text(message));
        dialog.PrimaryButtonText = primaryText;
        dialog.SecondaryButtonText = secondaryText;
        dialog.CloseButtonText = closeText ?? ShellStrings.Dialog_Cancel;
        dialog.DefaultButton = ContentDialogButton.Primary;
        return await ShowAsync(dialog) switch
        {
            ContentDialogResult.Primary => 1,
            ContentDialogResult.Secondary => 2,
            _ => 0,
        };
    }

    public async Task<bool> ShowChecksAsync(string title, string intro, IReadOnlyList<StartCheckItem> checks, bool canProceed, string proceedText)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(Text(intro));
        foreach (var check in checks.Where(c => c.Level != CheckLevel.Info).OrderByDescending(c => c.Level))
        {
            panel.Children.Add(new InfoBar
            {
                IsOpen = true,
                IsClosable = false,
                Severity = check.Level == CheckLevel.Blocker ? InfoBarSeverity.Error : InfoBarSeverity.Warning,
                Message = check.Message,
            });
        }

        var dialog = Create(title, new ScrollViewer { Content = panel, MaxHeight = 420 });
        dialog.CloseButtonText = canProceed ? ShellStrings.Dialog_Cancel : ShellStrings.Dialog_Close;
        if (canProceed)
        {
            dialog.PrimaryButtonText = proceedText;
            dialog.DefaultButton = ContentDialogButton.Close;
        }

        return await ShowAsync(dialog) == ContentDialogResult.Primary;
    }

    private static ContentDialog Create(string title, object content) => new()
    {
        Title = title,
        Content = content,
        XamlRoot = Root,
        Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
        RequestedTheme = (App.GetService<MainWindow>().Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default,
    };

    private static TextBlock Text(string message) => new()
    {
        Text = message,
        TextWrapping = TextWrapping.Wrap,
        IsTextSelectionEnabled = true,
    };

    private async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        // WinUI allows a single open ContentDialog per window.
        await _gate.WaitAsync();
        try
        {
            foreach (var open in VisualTreeHelper.GetOpenPopupsForXamlRoot(dialog.XamlRoot))
            {
                if (open.Child is ContentDialog)
                {
                    return ContentDialogResult.None;
                }
            }

            return await dialog.ShowAsync();
        }
        finally
        {
            _gate.Release();
        }
    }
}

public interface IPickerService
{
    Task<string?> PickFolderAsync();

    Task<string?> PickFileAsync(params string[] extensions);
}

public sealed class PickerService : IPickerService
{
    public async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker(App.GetService<MainWindow>().AppWindow.Id);
        var result = await picker.PickSingleFolderAsync();
        return result?.Path;
    }

    public async Task<string?> PickFileAsync(params string[] extensions)
    {
        var picker = new FileOpenPicker(App.GetService<MainWindow>().AppWindow.Id);
        foreach (var ext in extensions.DefaultIfEmpty("*"))
        {
            picker.FileTypeFilter.Add(ext);
        }

        var result = await picker.PickSingleFileAsync();
        return result?.Path;
    }
}
