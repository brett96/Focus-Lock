using System.Windows;
using System.Windows.Forms;
using Application = System.Windows.Application;

namespace FocusLock.UI.Services;

/// <summary>
/// Manages the system tray icon and minimise-to-tray behaviour.
/// Must be created on the STA UI thread.
/// </summary>
public sealed class TrayManager : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Window _window;

    public TrayManager(Window window)
    {
        _window = window;

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Focus Lock", null, (_, _) => ShowWindow());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());

        _icon = new NotifyIcon
        {
            Text = "Focus Lock",
            Icon = SystemIcons.Shield,
            ContextMenuStrip = menu,
            Visible = true
        };

        _icon.DoubleClick += (_, _) => ShowWindow();
    }

    public void ShowBalloon(string title, string message) =>
        _icon.ShowBalloonTip(3000, title, message, ToolTipIcon.Info);

    public void UpdateTooltip(string text)
    {
        // NotifyIcon.Text max is 63 characters.
        _icon.Text = text.Length > 63 ? text[..63] : text;
    }

    private void ShowWindow()
    {
        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private static void ExitApp() =>
        Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown());

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
