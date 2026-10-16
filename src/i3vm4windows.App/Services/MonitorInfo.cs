using System.Runtime.InteropServices;
using i3vm4windows.App.Interop;
using i3vm4windows.Core;

namespace i3vm4windows.App.Services;

/// <summary>
/// A connected display, identified by its Win32 monitor handle and device
/// name, with the screen rectangle of its work area (excluding the taskbar).
/// </summary>
internal sealed record MonitorInfo(IntPtr Handle, Rect WorkArea, bool IsPrimary, string DeviceName)
{
    /// <summary>Enumerates all currently connected displays.</summary>
    public static List<MonitorInfo> Enumerate()
    {
        var monitors = new List<MonitorInfo>();

        Win32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new Win32.MONITORINFOEX { cbSize = Marshal.SizeOf<Win32.MONITORINFOEX>() };
            if (Win32.GetMonitorInfo(monitor, ref info))
            {
                var work = info.rcWork;
                monitors.Add(new MonitorInfo(
                    monitor,
                    new Rect(work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top),
                    (info.dwFlags & Win32.MONITORINFOF_PRIMARY) != 0,
                    info.szDevice));
            }

            return true;
        }, IntPtr.Zero);

        return monitors;
    }
}
