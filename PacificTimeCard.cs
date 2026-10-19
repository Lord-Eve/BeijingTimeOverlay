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

    public void UpdateTime(DateTimeOffset utc, TimeZoneInfo zone)
    {
        var next = PacificTimeText.FromUtc(utc, zone);
        if (next == _text) return;
        _text = next;
        AccessibleDescription = $"洛杉矶 {_text.Date} {_text.Time} {_text.Zone}";
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
}
