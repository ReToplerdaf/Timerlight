using System.Runtime.InteropServices;

namespace Timerlight;

/// <summary>Win32 entry points used by the tray widget.</summary>
internal static class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo plii);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>
    /// Time since the last keyboard or mouse input anywhere in the session.
    /// Returns <see cref="TimeSpan.Zero"/> when Windows declines to answer.
    /// </summary>
    internal static TimeSpan GetIdleTime()
    {
        var info = new LastInputInfo { cbSize = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info))
        {
            return TimeSpan.Zero;
        }

        // Both values come from GetTickCount, so unchecked unsigned subtraction
        // stays correct across the ~49-day wrap.
        uint now = unchecked((uint)Environment.TickCount);
        uint elapsed = unchecked(now - info.dwTime);
        return TimeSpan.FromMilliseconds(elapsed);
    }
}
