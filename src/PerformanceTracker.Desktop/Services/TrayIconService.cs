using System.IO;
using System.Windows.Resources;
using Forms = System.Windows.Forms;

namespace PerformanceTracker.Desktop.Services;

public sealed class TrayIconService : IDisposable
{
    private static readonly Uri AppIconUri = new("pack://application:,,,/Assets/performance-tracker.ico", UriKind.Absolute);
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
            Icon = CreateAppIcon(),
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

    private static global::System.Drawing.Icon CreateAppIcon()
    {
        StreamResourceInfo? iconResource = System.Windows.Application.GetResourceStream(AppIconUri);
        if (iconResource is null)
        {
            return global::System.Drawing.SystemIcons.Application;
        }

        using Stream resourceStream = iconResource.Stream;
        using MemoryStream iconStream = new();
        resourceStream.CopyTo(iconStream);
        iconStream.Position = 0;

        using global::System.Drawing.Icon icon = new(iconStream);
        return (global::System.Drawing.Icon)icon.Clone();
    }
}
