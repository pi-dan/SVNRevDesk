using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using SVNRevDesk.Core.Services;
using SVNRevDesk.Desktop;
using SVNRevDesk.Desktop.Services;
using SVNRevDesk.Desktop.ViewModels;
using SVNRevDesk.Desktop.Views;

namespace SVNRevDesk.UiSmokeTests;

internal static class WinMergeComparisonTests
{
    public static async Task RunAsync(string output, string workingCopy)
    {
        var directory = Path.Combine(output, "default-winmerge");
        Directory.CreateDirectory(directory);
        var legacyIni = Path.Combine(directory, "WinMerge.ini");
        var ini = Path.Combine(directory, "Tools", "WinMerge", "WinMerge.ini");
        File.WriteAllText(legacyIni, "[WinMerge]\r\nSettings/MainLeft=2769\r\nSettings/MainTop=4294967193\r\n"
            + "Settings/MainRight=4233\r\nSettings/MainBottom=926\r\nSettings/MainMax=1\r\nSettings/TabSize=7\r\n", Encoding.Unicode);

        void VerifyScreen(Rect monitor, Rect workArea)
        {
            WinMergeWindowPlacement.Prepare(directory, monitor, workArea);
            if (File.Exists(legacyIni)) throw new InvalidOperationException("WinMerge settings were not migrated to the tool directory.");
            var restored = ReadBounds(ini);
            restored.Offset(workArea.Left - monitor.Left, workArea.Top - monitor.Top);
            if (!workArea.Contains(restored) || restored.Width > 1200 || restored.Height > 780
                || ReadInt(ini, "Settings/MainMax") != 0 || ReadInt(ini, "Settings/TabSize") != 7)
                throw new InvalidOperationException("WinMerge placement does not fit the work area or changed an unrelated preference.");
        }

        VerifyScreen(new Rect(0, 0, 1920, 1080), new Rect(0, 0, 1920, 1040));
        if (ReadBounds(ini).Size != new Size(1200, 780)) throw new InvalidOperationException("1080p default size changed.");
        VerifyScreen(new Rect(0, 0, 1280, 720), new Rect(0, 0, 1280, 680));
        VerifyScreen(new Rect(-1920, -1080, 1920, 1080), new Rect(-1872, -1040, 1872, 1040));

        // Exercise the actual default workflow, not only the saved dimensions. The
        // temporary working copy and tool installation never touch the user's data.
        var owner = new MainWindow(new UserSettings(directory) { CloseBehavior = WindowCloseBehavior.Exit });
        var model = (MainViewModel)owner.DataContext;
        Process? comparisonProcess = null;
        var localFile = Path.Combine(workingCopy, "equal.txt");
        var originalContent = File.ReadAllBytes(localFile);
        try
        {
            File.AppendAllText(localFile, "\nWinMerge comparison verification\n");
            await model.RunAsync("Open comparison fixture", token => model.OpenAsync(workingCopy, true, token));
            var entry = model.Entries.Single(item => item.Name == "equal.txt");
            var builtInWindows = Application.Current.Windows.OfType<ComparisonWindow>().Count();
            var actions = new ClientActions(owner, model);
            await model.RunAsync("Compare BASE", token => actions.ExecuteAsync("compare-base", [entry], token));
            comparisonProcess = FindProcess(Path.Combine(directory, "Tools", "WinMerge", "WinMergeU.exe"))
                ?? throw new InvalidOperationException("Default file comparison did not launch WinMerge.");
            if (!await Task.Run(() => comparisonProcess.WaitForInputIdle(15000)))
                throw new InvalidOperationException("WinMerge startup timed out.");
            var placement = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
            // Input-idle can occur during early initialization, before the main frame exists.
            var ready = false;
            for (var attempt = 0; attempt < 100 && !comparisonProcess.HasExited; attempt++)
            {
                comparisonProcess.Refresh();
                if (comparisonProcess.MainWindowHandle != IntPtr.Zero
                    && GetWindowPlacement(comparisonProcess.MainWindowHandle, ref placement)) { ready = true; break; }
                await Task.Delay(100);
            }
            if (!ready) throw new InvalidOperationException("Unable to inspect the real WinMerge window after startup.");
            var (monitor, workArea) = WinMergeWindowPlacement.GetMonitorAreas(owner);
            var bounds = placement.NormalPosition.ToRect();
            bounds.Offset(workArea.Left - monitor.Left, workArea.Top - monitor.Top);
            if (!workArea.Contains(bounds) || bounds.Width > 1200 || bounds.Height > 780 || placement.ShowCommand != 1
                || model.IsBusy || Application.Current.Windows.OfType<ComparisonWindow>().Count() != builtInWindows)
                throw new InvalidOperationException("Default comparison did not finish its progress or fit the real screen.");
            if (ReadInt(ini, "Settings/TabSize") != 7) throw new InvalidOperationException("WinMerge preferences were lost.");
            if (File.Exists(legacyIni)) throw new InvalidOperationException("Actual comparison recreated the application-root INI.");
            Console.WriteLine("PASS: default file comparison launches WinMerge directly, finishes progress, fits 1080p/smaller/secondary screens, and preserves preferences.");
        }
        finally
        {
            if (comparisonProcess != null)
            {
                if (!comparisonProcess.HasExited)
                {
                    comparisonProcess.CloseMainWindow();
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    try { await comparisonProcess.WaitForExitAsync(timeout.Token); }
                    catch (OperationCanceledException) { comparisonProcess.Kill(entireProcessTree: true); await comparisonProcess.WaitForExitAsync(); }
                }
                comparisonProcess.Dispose();
            }
            model.Dispose();
            owner.Close();
            File.WriteAllBytes(localFile, originalContent);
        }
    }

    private static Process? FindProcess(string executable)
    {
        foreach (var process in Process.GetProcessesByName("WinMergeU"))
        {
            try { if (process.MainModule?.FileName.Equals(executable, StringComparison.OrdinalIgnoreCase) == true) return process; }
            catch (System.ComponentModel.Win32Exception) { }
            process.Dispose();
        }
        return null;
    }

    private static Rect ReadBounds(string path)
    {
        var left = ReadInt(path, "Settings/MainLeft");
        var top = ReadInt(path, "Settings/MainTop");
        return new(left, top, ReadInt(path, "Settings/MainRight") - left, ReadInt(path, "Settings/MainBottom") - top);
    }

    private static int ReadInt(string path, string key) =>
        int.Parse(File.ReadAllLines(path).Single(line => line.StartsWith(key + "=", StringComparison.Ordinal))[(key.Length + 1)..]);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public readonly Rect ToRect() => new(Left, Top, Right - Left, Bottom - Top);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPlacement
    {
        public int Length, Flags, ShowCommand;
        public NativePoint MinimumPosition, MaximumPosition;
        public NativeRect NormalPosition;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowPlacement(IntPtr window, ref WindowPlacement placement);
}
