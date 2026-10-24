using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace BeijingTimeOverlay;

// Display-only owned window. It must never activate or intercept taskbar clicks.
internal sealed class PacificTimeCard : Form
{
    private Font _labelFont = null!;
    private Font _timeFont = null!;
    private int _dpi;
    private PacificTimeText _text;

    public PacificTimeCard()
    {
        Text = "太平洋时间预览";
        AccessibleName = Text;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        DoubleBuffered = true;
        BackColor = Color.FromArgb(37, 38, 42);
        ConfigureDpi(96);
    }

    protected override bool ShowWithoutActivation => true;

    public void ShowFor(Form clock)
    {
        TopMost = clock.TopMost;
        Show(clock);
        SynchronizeWindowOrder(clock);
    }

    public void SynchronizeWindowOrder(Form clock)
    {
        if (!IsHandleCreated || !clock.IsHandleCreated) return;
        var topMost = clock.TopMost;
        if (TopMost != topMost) TopMost = topMost;

        // WinForms can replace a hidden tool window's native owner. Compare
        // native state as well as managed properties: Visible can remain true
        // while an unowned, non-topmost preview sits underneath normal apps.
        var ownerChanged = GetWindow(Handle, 4) != clock.Handle;
        if (ownerChanged)
        {
            if (IntPtr.Size == 8) SetWindowLongPtr64(Handle, -8, clock.Handle);
            else SetWindowLong32(Handle, -8, clock.Handle.ToInt32());
        }
        var style = IntPtr.Size == 8 ? GetWindowLongPtr64(Handle, -20).ToInt64() : GetWindowLong32(Handle, -20);
        if (ownerChanged || ((style & 0x08) != 0) != topMost)
        {
            SetWindowPos(Handle, new IntPtr(topMost ? -1 : -2), 0, 0, 0, 0,
                0x0001 | 0x0002 | 0x0010); // NOSIZE | NOMOVE | NOACTIVATE
        }
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= 0x08000000 | 0x80 | 0x80000 | 0x20;
            return parameters;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        SetLayeredWindowAttributes(Handle, 0, 255, 2);
    }

    public void ConfigureDpi(int dpi)
    {
        dpi = dpi > 0 ? dpi : 96;
        if (_dpi == dpi) return;
        _dpi = dpi;
        _labelFont?.Dispose();
        _timeFont?.Dispose();
        _labelFont = new Font("Segoe UI", 12 * dpi / 96f, FontStyle.Regular, GraphicsUnit.Pixel);
        _timeFont = new Font("Segoe UI", 26 * dpi / 96f, FontStyle.Regular, GraphicsUnit.Pixel);
        ClientSize = OverlayGeometry.PacificCardSize(dpi);
        Invalidate();
    }

    public void UpdateTime(DateTimeOffset utc, TimeZoneInfo zone, TimeZoneInfo beijingZone)
    {
        var next = PacificTimeText.FromUtc(utc, zone, beijingZone);
        if (next == _text) return;
        _text = next;
        AccessibleDescription = $"洛杉矶 {_text.Date} {_text.Time} {_text.Zone} {_text.WeekdayComparison}";
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var scale = _dpi / 96f;
        e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        using var border = new Pen(Color.FromArgb(83, 88, 98), Math.Max(1, scale));
        e.Graphics.DrawRectangle(border, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        using var muted = new SolidBrush(Color.FromArgb(186, 195, 208));
        using var accent = new SolidBrush(Color.FromArgb(144, 204, 255));
        using var foreground = new SolidBrush(Color.White);
        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            FormatFlags = StringFormatFlags.NoWrap,
        };
        var width = ClientSize.Width - 28 * scale;
        void Draw(string text, Font font, Brush brush, float y) => e.Graphics.DrawString(text, font, brush,
            new RectangleF(14 * scale, y * scale, width, font.GetHeight(e.Graphics)), format);
        Draw("太平洋时间 · 洛杉矶", _labelFont, accent, 12);
        Draw(_text.Time ?? string.Empty, _timeFont, foreground, 33);
        Draw($"{_text.Date}  ·  {_text.Zone}", _labelFont, muted, 78);
        Draw(_text.WeekdayComparison, _labelFont, muted, 98);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _labelFont?.Dispose();
            _timeFont?.Dispose();
        }
        base.Dispose(disposing);
    }

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr window, uint color, byte alpha, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr window, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(IntPtr window, int index, int value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
