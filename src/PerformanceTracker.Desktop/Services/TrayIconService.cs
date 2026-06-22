using Forms = System.Windows.Forms;

namespace PerformanceTracker.Desktop.Services;

public sealed class TrayIconService : IDisposable
{
    private Forms.NotifyIcon? _notifyIcon;

    public void Initialize(Action showWindow, Action exitApplication)
    {
        if (_notifyIcon is not null)
        {
            return;
        }

        Forms.ContextMenuStrip menu = new();
        menu.Items.Add("Open Dashboard", null, (_, _) => showWindow());
        menu.Items.Add("Exit", null, (_, _) => exitApplication());

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = global::System.Drawing.SystemIcons.Application,
            Text = "Performance Tracker",
            Visible = true,
            ContextMenuStrip = menu
        };

        _notifyIcon.DoubleClick += (_, _) => showWindow();
    }

    public void Dispose()
    {
        if (_notifyIcon is null)
        {
            return;
        }

        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _notifyIcon = null;
    }
}
