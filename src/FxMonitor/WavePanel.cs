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

    /// <summary>可选的高亮区间（ns），「找回波窗」里画拟写入的回波窗。</summary>
    public decimal? BandStart { get; set; }
    public decimal? BandEnd { get; set; }

    /// <summary>SDK 判出的主回波区（ns），照厂家画成绿带；空 = 不画。</summary>
    public decimal? EchoRegionStart { get; set; }
    public decimal? EchoRegionEnd { get; set; }

    /// <summary>算法选中的极值辅助点 / 自动法前沿锚点（ns, mV），照厂家画成"+"。</summary>
    public (decimal X, decimal Y)? Mark { get; set; }
    public (decimal X, decimal Y)? Front { get; set; }

    /// <summary>横轴按长度 mm 显示（厂家的画法）：mm = ns × 声速 / 2×10⁶。false = 声时 ns。</summary>
    public bool AxisMm { get; set; }
    public double VelocityMps { get; set; } = 5900;

    /// <summary>右下角黑底标签（厂家放 SNR/DL）。</summary>
    public string? Badge { get; set; }

    public bool ShowLegend { get; set; } = true;

    private Point? _hover;

    /// <summary>用户在波形上点了一下：参数是点中位置的时间（ns）。拖拽平移/框选不算点选。</summary>
    public event Action<decimal>? Picked;

    public event Action? ViewChanged;

    private double _dataX0, _dataX1, _dataY = 100;
    private double _vx0, _vx1, _vy = 100;
    private bool _userView;
    private Rectangle _plot;

    private Point _down;
    private bool _moved;
    private bool _panning;
    private bool _boxing;
    private Rectangle _box;

    private static readonly Brush BandBrush = new SolidBrush(Color.FromArgb(40, 240, 130, 0));
    private static readonly Brush RegionBrush = new SolidBrush(Color.FromArgb(28, 0, 160, 60));
    private static readonly Pen RegionPen = new(Color.FromArgb(0, 150, 60), 1f) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
    private static readonly Pen MarkPen = new(Color.FromArgb(0, 120, 50), 1.4f);
    private static readonly Pen FrontPen = new(Color.FromArgb(200, 60, 60), 1.4f);
    private static readonly Pen HoverPen = new(Color.FromArgb(120, 120, 120), 1f) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot };
    private static readonly Brush BadgeBrush = new SolidBrush(Color.FromArgb(215, 30, 30, 30));
    private static readonly Brush BadgeText = new SolidBrush(Color.FromArgb(120, 230, 120));
    private static readonly Brush HoverBack = new SolidBrush(Color.FromArgb(235, 255, 251, 214));
    private static readonly Brush BoxBrush = new SolidBrush(Color.FromArgb(40, 47, 128, 237));
    private static readonly Pen GridPen = new(Color.FromArgb(235, 235, 235));
    private static readonly Pen AxisPen = new(Color.FromArgb(200, 200, 200));
    private static readonly Pen RefPen = new(Color.FromArgb(170, 170, 185));
    private static readonly Pen LivePen = new(Color.FromArgb(0, 150, 60), 1.6f);
    private static readonly Pen BoxPen = new(Color.FromArgb(47, 128, 237), 1f) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
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
        BackColor = Ui.Window;
        TabStop = true;
    }

    public bool HasCustomView => _userView;

    public void ResetView()
    {
        _userView = false;
        Invalidate();
        ViewChanged?.Invoke();
    }

    public void ZoomAroundCenter(double factor)
    {
        if (!MeasureData()) return;
        EnsureView();
        ZoomX(factor, (_vx0 + _vx1) / 2);
        ZoomY(factor);
        Invalidate();
        ViewChanged?.Invoke();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.Clear(BackColor);
        var rc = new Rectangle(36, 8, Width - 46, Height - 28);
        _plot = Rectangle.Empty;
        if (rc.Width < 50 || rc.Height < 50) return;

        g.FillRectangle(Brushes.White, rc);
        g.DrawRectangle(AxisPen, rc);

        if (!MeasureData())
        {
            var msg = "无数据";
            var size = g.MeasureString(msg, Font);
            g.DrawString(msg, Font, TextBrush, rc.Left + (rc.Width - size.Width) / 2, rc.Top + (rc.Height - size.Height) / 2);
            return;
        }

        var x0 = _userView ? _vx0 : _dataX0;
        var x1 = _userView ? _vx1 : _dataX1;
        var yMax = _userView ? _vy : _dataY;
        if (x1 <= x0 || yMax <= 0) return;
        _plot = rc;

        float Px(double ns) => (float)(rc.Left + (ns - x0) / (x1 - x0) * rc.Width);
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
            var ns = x0 + (x1 - x0) * i / 10;
            var label = AxisMm ? ToMm(ns).ToString("0.0") + (i == 10 ? " mm" : string.Empty) : ns.ToString("0") + (i == 10 ? " ns" : string.Empty);
            var w = g.MeasureString(label, small).Width;
            var x = rc.Left + rc.Width * i / 10f - w / 2;
            x = Math.Max(rc.Left, Math.Min(x, rc.Right - w));
            g.DrawString(label, small, TextBrush, x, rc.Bottom + 4);
        }

        g.DrawString(yMax.ToString("0"), small, TextBrush, 2, rc.Top);
        g.DrawString("0", small, TextBrush, 2, Py(0) - 8);
        g.DrawString((-yMax).ToString("0"), small, TextBrush, 2, rc.Bottom - 12);

        if (BandStart is { } bs && BandEnd is { } be && be > bs)
        {
            var bx1 = Math.Max(rc.Left, Px((double)bs));
            var bx2 = Math.Min(rc.Right, Px((double)be));
            if (bx2 > bx1) g.FillRectangle(BandBrush, bx1, rc.Top, bx2 - bx1, rc.Height);
        }

        // 主回波区（厂家的绿带）
        if (EchoRegionStart is { } rs && EchoRegionEnd is { } re && re > rs)
        {
            var gx1 = Math.Max(rc.Left, Px((double)rs));
            var gx2 = Math.Min(rc.Right, Px((double)re));
            if (gx2 > gx1)
            {
                g.FillRectangle(RegionBrush, gx1, rc.Top, gx2 - gx1, rc.Height);
                g.DrawLine(RegionPen, gx1, rc.Top, gx1, rc.Bottom);
                g.DrawLine(RegionPen, gx2, rc.Top, gx2, rc.Bottom);
            }
        }

        var state = g.Save();
        g.SetClip(rc);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        if (ShowRef && Ref != null && Ref.Length > 1) DrawWave(g, RefPen, Ref, RefStart, RefClock, Px, Py, x0, x1);
        if (Live != null && Live.Length > 1) DrawWave(g, LivePen, Live, LiveStart, LiveClock, Px, Py, x0, x1);
        if (Mark is { } mkp) DrawCross(g, MarkPen, Px((double)mkp.X), Py((double)mkp.Y));
        if (Front is { } fr) DrawCross(g, FrontPen, Px((double)fr.X), Py((double)fr.Y));
        g.Restore(state);

        if (Marker.HasValue && (double)Marker.Value >= x0 && (double)Marker.Value <= x1)
        {
            var mk = Marker.Value;
            var x = Px((double)mk);
            g.DrawLine(MarkerPen, x, rc.Top, x, rc.Bottom);
            var text = AxisMm ? $"检出 {ToMm((double)mk):F2} mm / {mk:F1} ns" : $"检出 {mk:F1} ns";
            var tw = g.MeasureString(text, small).Width;
            // 图例占着顶上一行，标签放它下面一行，免得叠字
            g.DrawString(text, small, TextBrush, Math.Min(x + 3, rc.Right - tw - 2), rc.Top + (ShowLegend ? 18 : 2));
        }

        if (ShowLegend) DrawLegend(g, small, rc);

        if (!string.IsNullOrEmpty(Badge))
        {
            var bw = g.MeasureString(Badge, Font).Width + 12;
            var bh = Font.Height + 6;
            var bx = rc.Right - bw - 6;
            var by = rc.Bottom - bh - 6;
            g.FillRectangle(BadgeBrush, bx, by, bw, bh);
            g.DrawString(Badge, Font, BadgeText, bx + 6, by + 3);
        }

        if (_hover is { } hv && rc.Contains(hv) && !_panning && !_boxing)
        {
            var ns = x0 + (hv.X - rc.Left) / (double)rc.Width * (x1 - x0);
            g.DrawLine(HoverPen, hv.X, rc.Top, hv.X, rc.Bottom);
            var amp = SampleAt(ns);
            var text = (AxisMm ? $"长度 {ToMm(ns):F2} mm　声时 {ns:F0} ns" : $"声时 {ns:F1} ns")
                       + (amp is { } a ? $"　幅值 {a} mV" : string.Empty);
            var size = g.MeasureString(text, small);
            var tx = hv.X + 12 + size.Width > rc.Right ? hv.X - 12 - size.Width : hv.X + 12;
            var ty = Math.Max(rc.Top + 2, Math.Min(hv.Y - size.Height - 4, rc.Bottom - size.Height - 2));
            g.FillRectangle(HoverBack, tx - 3, ty - 1, size.Width + 6, size.Height + 2);
            g.DrawRectangle(AxisPen, tx - 3, ty - 1, size.Width + 6, size.Height + 2);
            g.DrawString(text, small, Brushes.Black, tx, ty);
        }

        if (_boxing && _box.Width > 4 && _box.Height > 4)
        {
            var box = Normalize(_box);
            box.Intersect(rc);
            if (box.Width > 0 && box.Height > 0)
            {
                g.FillRectangle(BoxBrush, box);
                g.DrawRectangle(BoxPen, box);
            }
        }
    }

    /// <summary>ns → mm：声程是来回一趟，长度 = t × v / 2。</summary>
    private double ToMm(double ns) => ns * VelocityMps / 2_000_000.0;

    private static void DrawCross(Graphics g, Pen pen, float x, float y)
    {
        g.DrawLine(pen, x - 5, y, x + 5, y);
        g.DrawLine(pen, x, y - 5, x, y + 5);
    }

    /// <summary>实时波形在某一时刻的采样值（取最近的点）；没有实时波形就空。</summary>
    private int? SampleAt(double ns)
    {
        if (Live == null || Live.Length == 0 || LiveClock == 0) return null;
        var i = (int)Math.Round((ns - LiveStart) / LiveClock);
        return i >= 0 && i < Live.Length ? Live[i] : null;
    }

    private void DrawLegend(Graphics g, Font small, Rectangle rc)
    {
        var items = new List<(Pen? Pen, Brush? Fill, string Text)>();
        if (ShowRef && Ref is { Length: > 1 }) items.Add((RefPen, null, "参考回波"));
        if (Live is { Length: > 1 }) items.Add((LivePen, null, "实时回波"));
        if (EchoRegionStart is { } && EchoRegionEnd is { }) items.Add((null, RegionBrush, "主回波区"));
        if (Marker.HasValue) items.Add((MarkerPen, null, "检出点"));
        if (items.Count == 0) return;

        float x = rc.Right - 8;
        float y = rc.Top + 4;
        for (var i = items.Count - 1; i >= 0; i--)
        {
            var (pen, fill, text) = items[i];
            var w = g.MeasureString(text, small).Width;
            x -= w;
            g.DrawString(text, small, TextBrush, x, y);
            x -= 18;
            if (fill != null) g.FillRectangle(fill, x, y + 3, 14, 8);
            else if (pen != null) g.DrawLine(pen, x, y + 7, x + 14, y + 7);
            x -= 10;
        }
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        if (!Focused) Focus();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (_plot.Width <= 0 || !_plot.Contains(e.Location)) return;
        Focus();
        _down = e.Location;
        _moved = false;
        if (e.Button == MouseButtons.Left)
        {
            _panning = true;
            Cursor = Cursors.SizeAll;
        }
        else if (e.Button == MouseButtons.Right)
        {
            _boxing = true;
            _box = new Rectangle(e.Location, Size.Empty);
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover is { })
        {
            _hover = null;
            Invalidate();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_panning && !_boxing)
        {
            // 悬停读数（厂家有）：只在有波形时画
            _hover = _plot.Width > 0 && _plot.Contains(e.Location) ? e.Location : null;
            Invalidate();
            return;
        }

        if (Math.Abs(e.X - _down.X) + Math.Abs(e.Y - _down.Y) > 4) _moved = true;
        if (_panning && _moved)
        {
            EnsureView();
            var dx = e.X - _down.X;
            var dy = e.Y - _down.Y;
            _down = e.Location;
            var span = _vx1 - _vx0;
            _vx0 -= dx / (double)_plot.Width * span;
            _vx1 = _vx0 + span;
            _vy = Math.Max(20, _vy + dy / (double)_plot.Height * 2 * _vy);
            Invalidate();
        }
        else if (_boxing)
        {
            _box = new Rectangle(_down, new Size(e.X - _down.X, e.Y - _down.Y));
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_panning)
        {
            _panning = false;
            Cursor = Cursors.Default;
            if (_moved) ViewChanged?.Invoke();
            else if (e.Button == MouseButtons.Left && _plot.Contains(e.Location) && ViewX1 > ViewX0)
            {
                var ns = ViewX0 + (e.X - _plot.Left) / (double)_plot.Width * (ViewX1 - ViewX0);
                Picked?.Invoke((decimal)ns);
            }
        }

        if (_boxing)
        {
            _boxing = false;
            if (_moved && _box.Width != 0 && _box.Height != 0) ApplyBoxZoom(Normalize(_box));
            _box = Rectangle.Empty;
            Invalidate();
            ViewChanged?.Invoke();
        }
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button == MouseButtons.Left) ResetView();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_plot.Width <= 0 || !MeasureData()) return;
        EnsureView();
        var factor = e.Delta > 0 ? 0.8 : 1.25;
        if (ModifierKeys == Keys.Control) ZoomY(factor);
        else
        {
            var pivot = _plot.Contains(e.Location)
                ? ViewX0 + (e.X - _plot.Left) / (double)_plot.Width * (ViewX1 - ViewX0)
                : (ViewX0 + ViewX1) / 2;
            ZoomX(factor, pivot);
        }

        Invalidate();
        ViewChanged?.Invoke();
    }

    private double ViewX0 => _userView ? _vx0 : _dataX0;
    private double ViewX1 => _userView ? _vx1 : _dataX1;

    private bool MeasureData()
    {
        var hasLive = Live != null && Live.Length > 1;
        var hasRef = ShowRef && Ref != null && Ref.Length > 1;
        if (!hasLive && !hasRef) return false;

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

        if (xMax <= xMin) return false;
        _dataX0 = xMin;
        _dataX1 = xMax;
        var peak = 100;
        if (hasLive) peak = Math.Max(peak, Live!.Max(v => Math.Abs((int)v)));
        if (hasRef) peak = Math.Max(peak, Ref!.Max(v => Math.Abs((int)v)));
        _dataY = (peak / 100 + 1) * 100;
        return true;
    }

    private void EnsureView()
    {
        if (_userView) return;
        _vx0 = _dataX0;
        _vx1 = _dataX1;
        _vy = _dataY;
        _userView = true;
    }

    private void ZoomX(double factor, double pivot)
    {
        var span = Math.Max(40, (_vx1 - _vx0) * factor);
        var dataSpan = Math.Max(40, _dataX1 - _dataX0);
        span = Math.Min(span, dataSpan * 8);
        var t = (_vx1 - _vx0) > 0 ? (pivot - _vx0) / (_vx1 - _vx0) : 0.5;
        t = Math.Clamp(t, 0, 1);
        _vx0 = pivot - t * span;
        _vx1 = _vx0 + span;
    }

    private void ZoomY(double factor) => _vy = Math.Clamp(_vy * factor, 20, Math.Max(100, _dataY * 8));

    private void ApplyBoxZoom(Rectangle box)
    {
        box.Intersect(_plot);
        if (box.Width < 8 || box.Height < 8 || _plot.Width <= 0) return;
        EnsureView();
        var x0 = ViewX0 + (box.Left - _plot.Left) / (double)_plot.Width * (ViewX1 - ViewX0);
        var x1 = ViewX0 + (box.Right - _plot.Left) / (double)_plot.Width * (ViewX1 - ViewX0);
        var yTop = _vy - (box.Top - _plot.Top) / (double)_plot.Height * 2 * _vy;
        var yBot = _vy - (box.Bottom - _plot.Top) / (double)_plot.Height * 2 * _vy;
        _vx0 = Math.Min(x0, x1);
        _vx1 = Math.Max(x0, x1);
        _vy = Math.Max(20, Math.Max(Math.Abs(yTop), Math.Abs(yBot)));
        _userView = true;
    }

    private static Rectangle Normalize(Rectangle r)
    {
        var x = Math.Min(r.Left, r.Right);
        var y = Math.Min(r.Top, r.Bottom);
        return new Rectangle(x, y, Math.Abs(r.Width), Math.Abs(r.Height));
    }

    private static void DrawWave(Graphics g, Pen pen, short[] data, uint start, ushort clock,
        Func<double, float> px, Func<double, float> py, double x0, double x1)
    {
        if (clock == 0 || data.Length < 2) return;
        var i0 = Math.Max(0, (int)Math.Floor((x0 - start) / clock) - 1);
        var i1 = Math.Min(data.Length - 1, (int)Math.Ceiling((x1 - start) / clock) + 1);
        if (i1 - i0 < 1) return;
        var pts = new PointF[i1 - i0 + 1];
        for (var i = i0; i <= i1; i++)
            pts[i - i0] = new PointF(px(start + (double)i * clock), py(data[i]));
        g.DrawLines(pen, pts);
    }
}
