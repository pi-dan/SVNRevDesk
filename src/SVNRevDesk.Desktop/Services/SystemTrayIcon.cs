using System.Drawing;
using System.Windows;
using Forms = System.Windows.Forms;

namespace SVNRevDesk.Desktop.Services;

internal sealed class SystemTrayIcon : IDisposable
{
    private readonly Icon icon;
    private readonly Forms.ContextMenuStrip menu;
    private readonly Forms.NotifyIcon notification;
    private bool disposed;

    public SystemTrayIcon(Action restoreWindow, Action exitApplication)
    {
        var resource = Application.GetResourceStream(new Uri("pack://application:,,,/SVNRevDesk;component/Assets/AppIcon.ico"))
            ?? throw new InvalidOperationException("无法读取软件图标。");
        using (resource.Stream)
        using (var source = new Icon(resource.Stream))
            icon = (Icon)source.Clone();

        menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开 SVNRevDesk", null, (_, _) => restoreWindow());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出软件", null, (_, _) => exitApplication());
        notification = new Forms.NotifyIcon
        {
            Icon = icon,
            Text = $"SVNRevDesk · {AppVersion.Current} — 双击显示主窗口",
            ContextMenuStrip = menu
        };
        notification.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) restoreWindow();
        };
    }

    public void Show()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        notification.Visible = true;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        notification.Visible = false;
        notification.Dispose();
        menu.Dispose();
        icon.Dispose();
    }
}
