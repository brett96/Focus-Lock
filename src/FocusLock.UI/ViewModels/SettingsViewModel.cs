using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FocusLock.Core.Models;
using FocusLock.Core.Storage;
using FocusLock.UI.Services;
using FocusLock.UI.Views;
using Microsoft.Win32;

namespace FocusLock.UI.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly NavigationService _nav;
    private readonly ServiceClient _client;
    private readonly AppSettings _settings;

    [ObservableProperty] private bool _startWithWindows;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ForceResetCommand))]
    private bool _canReset;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResetMessage))]
    private string _resetMessage = string.Empty;

    public bool HasResetMessage => !string.IsNullOrEmpty(ResetMessage);

    public SettingsViewModel(NavigationService nav, ServiceClient client)
    {
        _nav = nav;
        _client = client;
        _settings = AppSettingsRepository.Load();
        _startWithWindows = _settings.StartWithWindows;
        _ = RefreshStatusAsync();
    }

    private async Task RefreshStatusAsync()
    {
        var status = await _client.GetStatusAsync();
        CanReset = status?.Status != SessionStatus.Active;
    }

    partial void OnStartWithWindowsChanged(bool value)
    {
        _settings.StartWithWindows = value;
        ApplyStartWithWindows(value);
        AppSettingsRepository.Save(_settings);
    }

    private static void ApplyStartWithWindows(bool enable)
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", writable: true);
        if (key is null) return;
        if (enable)
        {
            var exePath = Environment.ProcessPath ?? string.Empty;
            key.SetValue("FocusLock", $"\"{exePath}\"");
        }
        else
        {
            key.DeleteValue("FocusLock", throwOnMissingValue: false);
        }
    }

    [RelayCommand(CanExecute = nameof(CanReset))]
    private async Task ForceResetAsync()
    {
        var confirm = System.Windows.MessageBox.Show(
            "This will forcefully remove all Focus Lock restrictions:\n\n" +
            "• App blocks (IFEO registry entries) will be cleared\n" +
            "• The hosts file will be restored\n" +
            "• Any leftover session state will be cleared\n\n" +
            "Use this if a session ended but apps or sites are still blocked.\n\nContinue?",
            "Reset — Remove All Restrictions",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);

        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        CanReset = false;
        ResetMessage = string.Empty;

        var result = await _client.ForceResetAsync();

        if (result?.Success == true)
            ResetMessage = "Reset complete. All restrictions have been lifted.";
        else
            ResetMessage = $"Error: {result?.ErrorMessage ?? "Could not connect to the Focus Lock service."}";

        CanReset = true;
    }

    [RelayCommand]
    private void Back() => _nav.NavigateToDashboard();
}
