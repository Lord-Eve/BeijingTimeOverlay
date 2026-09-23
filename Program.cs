using System.Runtime.InteropServices;
using System.Threading;

namespace BeijingTimeOverlay;

internal static class Program
{
    internal const int ShowClockMessage = 0x8001;
    private const int SwShownNoActivate = 4;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpShowWindow = 0x0040;
    private static readonly IntPtr HwndTopmost = new(-1);

    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, @"Local\BeijingTimeOverlay", out var createdNew);
        if (!createdNew)
        {
            if (ActivateExistingClock())
            {
                return;
            }

            // A previous process can keep the mutex while failing before it
            // creates a form. Continue with a recovery instance instead of
            // trapping the user behind an "already running" dialog.
        }

        try
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new ClockForm());
        }
        catch (Exception exception)
        {
            var logPath = Path.Combine(Path.GetTempPath(), "BeijingTimeOverlay-startup-error.txt");
            try
            {
                File.WriteAllText(logPath, exception.ToString());
            }
            catch
            {
                // Preserve the visible error even if the diagnostic log cannot be written.
            }

            MessageBox.Show(
                $"北京时间浮窗启动失败。\n\n诊断日志：{logPath}\n\n{exception.Message}",
                "北京时间浮窗",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private static bool ActivateExistingClock()
    {
        var handle = FindWindow(null, "北京时间");
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        ShowWindow(handle, SwShownNoActivate);
        SetWindowPos(
            handle,
            HwndTopmost,
            0,
            0,
            0,
            0,
            SwpNoActivate | SwpNoMove | SwpNoSize | SwpShowWindow);
        PostMessage(handle, ShowClockMessage, IntPtr.Zero, IntPtr.Zero);
        return true;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr handle, int command);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr handle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);
}
