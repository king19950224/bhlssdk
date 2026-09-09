
namespace FxMonitor;

public sealed class WavePanel : Control
{
    public short[]? Live { get; set; }
    public uint LiveStart { get; set; }
    public ushort LiveClock { get; set; } = 10;

    public short[]? Ref { get; set; }
    public uint RefStart { get; set; }
    public ushort RefClock { get; set; } = 10;
    public decimal? Marker { get; set; }

    public bool ShowRef { get; set; } = true;

    private static readonly Pen GridPen = new(Color.FromArgb(235, 235, 235));
    private static readonly Pen AxisPen = new(Color.FromArgb(200, 200, 200));
    private static readonly Pen RefPen = new(Color.FromArgb(170, 170, 185));
    private static readonly Pen LivePen = new(Color.FromArgb(0, 150, 60), 1.6f);
    private static readonly Pen MarkerPen = CreateMarkerPen();
    private static readonly Brush TextBrush = new SolidBrush(Color.FromArgb(120, 120, 120));

    private static Pen CreateMarkerPen()
    {
        var p = new Pen(Color.FromArgb(240, 130, 0), 1.2f);
        p.DashStyle = System.Drawing.Drawing2D.DashStyle.Dash;
        return p;
    }

    public WavePanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Color.White;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.Clear(SystemColors.Control);
        var rc = new Rectangle(10, 8, Width - 20, Height - 30);
        if (rc.Width < 50 || rc.Height < 50) return;

        g.FillRectangle(Brushes.White, rc);
        g.DrawRectangle(AxisPen, rc);

        var hasLive = Live != null && Live.Length > 1;
        var hasRef = ShowRef && Ref != null && Ref.Length > 1;
        if (!hasLive && !hasRef)
        {
            var msg = "无数据";
            var size = g.MeasureString(msg, Font);
            g.DrawString(msg, Font, TextBrush, rc.Left + (rc.Width - size.Width) / 2, rc.Top + (rc.Height - size.Height) / 2);
            return;
        }

        var xMin = double.MaxValue;
        var xMax = double.MinValue;
        if (hasLive)
        {
            xMin = Math.Min(xMin, LiveStart);
            xMax = Math.Max(xMax, LiveStart + (double)(Live!.Length - 1) * LiveClock);
        }

        if (hasRef)
        {
            xMin = Math.Min(xMin, RefStart);
            xMax = Math.Max(xMax, RefStart + (double)(Ref!.Length - 1) * RefClock);
        }

        if (xMax <= xMin) return;

        var peak = 100;
        if (hasLive) peak = Math.Max(peak, Live!.Max(v => Math.Abs((int)v)));
        if (hasRef) peak = Math.Max(peak, Ref!.Max(v => Math.Abs((int)v)));
        var yMax = (peak / 100 + 1) * 100;

        float Px(double ns) => (float)(rc.Left + (ns - xMin) / (xMax - xMin) * rc.Width);
        float Py(double v) => (float)(rc.Top + (yMax - v) / (2.0 * yMax) * rc.Height);

        for (var i = 1; i < 10; i++)
        {
            var x = rc.Left + rc.Width * i / 10f;
            g.DrawLine(GridPen, x, rc.Top, x, rc.Bottom);
        }

        for (var i = 1; i < 4; i++)
        {
            var y = rc.Top + rc.Height * i / 4f;
            g.DrawLine(i == 2 ? AxisPen : GridPen, rc.Left, y, rc.Right, y);
        }

        using var small = new Font(Font.FontFamily, 7.5f);
        for (var i = 0; i <= 10; i += 2)
        {
            var ns = xMin + (xMax - xMin) * i / 10;
            var label = ns.ToString("0") + (i == 10 ? " ns" : string.Empty);
            var w = g.MeasureString(label, small).Width;
            var x = rc.Left + rc.Width * i / 10f - w / 2;
            x = Math.Max(rc.Left, Math.Min(x, rc.Right - w));
            g.DrawString(label, small, TextBrush, x, rc.Bottom + 4);
        }

        g.DrawString(yMax.ToString(), small, TextBrush, rc.Left + 3, rc.Top + 2);
        g.DrawString("0", small, TextBrush, rc.Left + 3, Py(0) - 14);
        g.DrawString((-yMax).ToString(), small, TextBrush, rc.Left + 3, rc.Bottom - 15);

        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        if (hasRef) DrawWave(g, RefPen, Ref!, RefStart, RefClock, Px, Py);
        if (hasLive) DrawWave(g, LivePen, Live!, LiveStart, LiveClock, Px, Py);

        if (Marker.HasValue && (double)Marker.Value >= xMin && (double)Marker.Value <= xMax)
        {
            var mk = Marker.Value;
            var x = Px((double)mk);
            g.DrawLine(MarkerPen, x, rc.Top, x, rc.Bottom);
            g.DrawString($"声时 {mk:F1}", small, TextBrush, x + 3, rc.Top + 2);
        }
    }

    private static void DrawWave(Graphics g, Pen pen, short[] data, uint start, ushort clock,
        Func<double, float> px, Func<double, float> py)
    {
        var pts = new PointF[data.Length];
        for (var i = 0; i < data.Length; i++)
        {
            pts[i] = new PointF(px(start + (double)i * clock), py(data[i]));
        }

        g.DrawLines(pen, pts);
    }
}
