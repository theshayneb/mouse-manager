namespace KeyShare;

/// <summary>
/// A shrinking ring around the cursor for half a second, so it's easy to find
/// after switching with the switch key. Click-through and never takes focus.
/// </summary>
internal sealed class CursorLocator : Form
{
    private const int Size0 = 160;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 15 };
    private readonly long _start = Environment.TickCount64;
    private const int DurationMs = 600;

    public static void ShowAtCursor()
    {
        try
        {
            Native.GetCursorPos(out var p);
            new CursorLocator(p).Show();
        }
        catch (Exception ex)
        {
            Log.Error("Cursor locator", ex);
        }
    }

    private CursorLocator(Native.POINT p)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.Magenta;
        TransparencyKey = Color.Magenta;
        Bounds = new Rectangle(p.X - Size0 / 2, p.Y - Size0 / 2, Size0, Size0);
        DoubleBuffered = true;
        _timer.Tick += (_, _) =>
        {
            if (Environment.TickCount64 - _start > DurationMs) { _timer.Stop(); Close(); }
            else Invalidate();
        };
        _timer.Start();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // transparent to clicks, layered, tool window, no activation, topmost
            cp.ExStyle |= 0x20 | 0x80000 | 0x80 | 0x08000000 | 0x8;
            return cp;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        float t = Math.Clamp((Environment.TickCount64 - _start) / (float)DurationMs, 0f, 1f);
        float r = (Size0 / 2f - 6) * (1 - t) + 10 * t;
        float c = Size0 / 2f;
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None; // keeps the edge off the magenta key
        using var outer = new Pen(Color.Black, 7);
        using var inner = new Pen(Color.FromArgb(255, 200, 0), 4);
        e.Graphics.DrawEllipse(outer, c - r, c - r, 2 * r, 2 * r);
        e.Graphics.DrawEllipse(inner, c - r, c - r, 2 * r, 2 * r);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}
