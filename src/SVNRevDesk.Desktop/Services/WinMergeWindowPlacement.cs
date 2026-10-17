using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using SVNRevDesk.Core.Comparison;

namespace SVNRevDesk.Desktop.Services;

public static class WinMergeWindowPlacement
{
    public static (Rect Monitor, Rect WorkArea) GetMonitorAreas(Window owner)
    {
        var monitor = MonitorFromWindow(new WindowInteropHelper(owner).Handle, 2); // Nearest monitor, or primary before showing the owner.
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return (info.Monitor.ToRect(), info.WorkArea.ToRect());
    }

    public static void Prepare(string applicationDirectory, Rect monitor, Rect workArea)
    {
        if (workArea.Width <= 0 || workArea.Height <= 0) throw new ArgumentException("屏幕可用区域无效。", nameof(workArea));
        // WinMerge stores physical pixels. Cap the default at 1200 x 780 and leave
        // space around it, including on smaller screens and with display scaling.
        var width = (int)Math.Min(1200, workArea.Width * 0.85);
        var height = (int)Math.Min(780, workArea.Height * 0.85);
        // WINDOWPLACEMENT uses workspace coordinates, excluding top/left taskbars.
        var left = (int)(monitor.Left + (workArea.Width - width) / 2);
        var top = (int)(monitor.Top + (workArea.Height - height) / 2);
        var path = WinMergeExecutable.PrepareSettingsPath(applicationDirectory);
        if (!File.Exists(path)) File.WriteAllText(path, "[WinMerge]\r\n", Encoding.Unicode);

        Write("Settings/MainLeft", left);
        Write("Settings/MainTop", top);
        Write("Settings/MainRight", left + width);
        Write("Settings/MainBottom", top + height);
        Write("Settings/MainMax", 0);

        void Write(string key, int value)
        {
            if (!WritePrivateProfileString("WinMerge", key, value.ToString(CultureInfo.InvariantCulture), path))
                throw new IOException("无法保存 WinMerge 窗口位置。", new Win32Exception(Marshal.GetLastWin32Error()));
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public readonly Rect ToRect() => new(Left, Top, Right - Left, Bottom - Top);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor, WorkArea;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WritePrivateProfileString(string section, string key, string value, string path);
}
