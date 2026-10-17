using System.Windows;

namespace SVNRevDesk.Desktop;

public partial class App : Application
{
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        // Windows shutdown/logoff must exit instead of hiding the application in the tray.
        if (MainWindow is MainWindow window) window.PrepareForSystemShutdown();
        base.OnSessionEnding(e);
    }
}
