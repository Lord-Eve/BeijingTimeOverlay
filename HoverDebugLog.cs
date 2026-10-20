using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace BeijingTimeOverlay;

// No window dependency: repeated snapshots get a one-second heartbeat, while
// transitions are kept immediately so a brief eligibility reset is not lost.
internal sealed class HoverDebugGate
{
    private readonly Dictionary<string, (string State, long At)> _last = new();

    public bool ShouldWrite(string category, string state, long now)
    {
        if (_last.TryGetValue(category, out var last) && last.State == state && now - last.At < 1000)
            return false;
        _last[category] = (state, now);
        return true;
    }
}

internal sealed class HoverDebugLog
{
    private readonly HoverDebugGate _gate = new();
    public bool Enabled { get; } = Environment.GetEnvironmentVariable("BTO_HOVER_DEBUG") == "1";

    public void State(string category, string state, Func<string>? details = null)
    {
        if (!Enabled) return;
        try
        {
            if (!_gate.ShouldWrite(category, state, Environment.TickCount64)) return;
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BeijingTimeOverlay");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "hover-debug.log"),
                $"{DateTimeOffset.Now:O} pid={Environment.ProcessId} {category} {state} {details?.Invoke()}{Environment.NewLine}",
                new UTF8Encoding(false));
        }
        catch
        {
            // Diagnostics must not interrupt the overlay, including when a
            // sampled window disappears or the log directory is unavailable.
        }
    }

    internal static string DescribeWindow(IntPtr window)
    {
        var className = new StringBuilder(256);
        var title = new StringBuilder(512);
        GetClassName(window, className, className.Capacity);
        GetWindowText(window, title, title.Capacity);
        GetWindowThreadProcessId(window, out var processId);
        var processName = "unavailable";
        try
        {
            using var process = Process.GetProcessById((int)processId);
            processName = process.ProcessName;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                         System.ComponentModel.Win32Exception or NotSupportedException) { }

        var rectangle = GetWindowRect(window, out var bounds)
            ? $"[{bounds.Left},{bounds.Top},{bounds.Right},{bounds.Bottom}]" : "unavailable";
        var style = unchecked((uint)(IntPtr.Size == 8
            ? GetWindowLongPtr64(window, -20).ToInt64() : GetWindowLong32(window, -20)));
        var layered = (style & 0x00080000) != 0;
        var layer = "alpha=not-applicable";
        if (layered)
        {
            layer = GetLayeredWindowAttributes(window, out var colorKey, out var alpha, out var flags)
                ? $"layerFlags=0x{flags:X} colorKey=0x{colorKey:X6} rawAlpha={alpha} " +
                  $"alpha={((flags & 2) != 0 ? alpha.ToString() : "not-applicable(LWA_ALPHA-unset)")}"
                : $"alpha=unavailable layeredAttributesError={Marshal.GetLastWin32Error()}";
        }
        return $"hwnd=0x{window.ToInt64():X} class=\"{OneLine(className.ToString())}\" " +
               $"title=\"{OneLine(title.ToString())}\" process=\"{processName}\" processId={processId} rect={rectangle} " +
               $"exStyle=0x{style:X8} WS_EX_TRANSPARENT={(style & 0x20) != 0} " +
               $"WS_EX_LAYERED={layered} WS_EX_TOPMOST={(style & 8) != 0} " +
               $"WS_EX_NOACTIVATE={(style & 0x08000000) != 0} {layer}";
    }

    private static string OneLine(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"")
        .Replace("\r", "\\r").Replace("\n", "\\n");

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder className, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder title, int count);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out WindowRect rectangle);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr window, int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetLayeredWindowAttributes(IntPtr window, out uint colorKey, out byte alpha, out uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect
    {
        public int Left, Top, Right, Bottom;
    }
}
