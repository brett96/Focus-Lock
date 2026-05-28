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
    private readonly AppSettings _settings;

    [ObservableProperty] private bool _startWithWindows;

    public SettingsViewModel(NavigationService nav)
    {
        _nav = nav;
        _settings = AppSettingsRepository.Load();
        _startWithWindows = _settings.StartWithWindows;
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

    [RelayCommand]
    private void Back() => _nav.NavigateTo(new DashboardPage());
}
