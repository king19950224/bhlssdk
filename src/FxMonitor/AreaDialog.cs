using System.Globalization;
using FxDevice;

namespace FxMonitor;

/// <summary>
/// 净截面积帮助框（厂家「净截面积 ?」那一个）：填公称直径和螺距，按 ISO 898-1 算应力截面积，确定后回填到试件规格。
/// </summary>
internal sealed class AreaDialog : Form
{
    private readonly TextBox _nominal = new() { Width = 90 };
    private readonly TextBox _pitch = new() { Width = 90 };
    private readonly Label _result = new() { AutoSize = true, Font = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold), ForeColor = Color.FromArgb(0, 110, 200) };
    private readonly Button _btnOk = new() { Text = "确定", Width = 84, Height = 30 };
    private readonly Button _btnClose = new() { Text = "关闭", Width = 84, Height = 30 };
    private readonly ToolTip _tip = new();

    public decimal? NominalMm { get; private set; }

    public decimal? PitchMm { get; private set; }

    public decimal? AreaMm2 { get; private set; }

    public AreaDialog(decimal? nominal, decimal? pitch)
    {
        Text = "净截面积";
        Font = new Font("Microsoft YaHei UI", 9.5f);
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(520, 300);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;

        var intro = new Label
        {
            Dock = DockStyle.Top,
            Height = 96,
            Text = "净截面积是受拉伸载荷作用的紧固件那部分的平均截面积，只取决于紧固件的有效长度部分。\n"
                   + "对普通螺栓就是螺纹的应力截面积 As = π/4 × (d − 0.9382P)²（ISO 898-1 / GB/T 3098.1）。\n"
                   + "公称直径填 M 后面的名义值（36、48、64），不是卡尺读数；螺距量 10 个牙的跨距再除以 10。\n"
                   + "螺距填 0 就是光杆截面 π/4 × d²，只有无螺纹的等截面杆才这么算。",
            ForeColor = Color.FromArgb(70, 70, 70),
        };

        var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 4, Padding = new Padding(0, 10, 0, 0) };
        grid.Controls.Add(new Label { Text = "公称直径", AutoSize = true, Margin = new Padding(0, 8, 8, 0) }, 0, 0);
        grid.Controls.Add(Unit(_nominal, "mm"), 1, 0);
        grid.Controls.Add(new Label { Text = "螺距", AutoSize = true, Margin = new Padding(24, 8, 8, 0) }, 2, 0);
        var pitchHelp = new Button { Text = "?", Width = 24, Height = 24, Margin = new Padding(6, 3, 0, 0) };
        _tip.SetToolTip(pitchHelp, "相邻两牙顶的距离。从一个牙顶量到第 11 个牙顶（跨 10 个牙）再除以 10：60 → 6，40 → 4。粗牙对照：M36=4 M42=4.5 M48=5 M56=5.5 M64=6");
        pitchHelp.Click += (_, _) => MessageBox.Show(this,
            "螺距 = 相邻两牙顶的距离。\n\n从一个牙顶量到第 11 个牙顶（跨 10 个牙），再除以 10：量到 60 mm 就是 6，40 mm 就是 4。\n\n"
            + "粗牙对照：M30=3.5  M36=4  M42=4.5  M48=5  M56=5.5  M64=6\n细牙常见 4 / 3 / 2，别默认粗牙，数一下牙。",
            "螺距", MessageBoxButtons.OK, MessageBoxIcon.Information);
        grid.Controls.Add(Unit(_pitch, "mm", pitchHelp), 3, 0);

        var resultRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 14, 0, 0) };
        resultRow.Controls.Add(new Label { Text = "净截面积 = ", AutoSize = true, Margin = new Padding(0, 6, 0, 0) });
        resultRow.Controls.Add(_result);

        var foot = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 40 };
        _btnClose.Margin = new Padding(8, 4, 0, 0);
        _btnOk.Margin = new Padding(8, 4, 0, 0);
        foot.Controls.Add(_btnClose);
        foot.Controls.Add(_btnOk);

        var root = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16, 12, 16, 8) };
        root.Controls.Add(resultRow);
        root.Controls.Add(grid);
        root.Controls.Add(intro);
        root.Controls.Add(foot);
        Controls.Add(root);
        AcceptButton = _btnOk;
        CancelButton = _btnClose;

        _nominal.Text = nominal is { } n ? n.ToString("0.##", CultureInfo.InvariantCulture) : string.Empty;
        _pitch.Text = pitch is { } p ? p.ToString("0.##", CultureInfo.InvariantCulture) : string.Empty;
        _nominal.TextChanged += (_, _) => Compute();
        _pitch.TextChanged += (_, _) => Compute();
        _btnOk.Click += (_, _) =>
        {
            if (!Compute())
            {
                MessageBox.Show(this, "先把公称直径填成正数（螺距可以是 0，表示光杆）。", "净截面积", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            DialogResult = DialogResult.OK;
            Close();
        };
        _btnClose.Click += (_, _) => Close();
        Compute();
    }

    private static Control Unit(TextBox box, string unit, Control? extra = null)
    {
        var p = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 0) };
        p.Controls.Add(box);
        p.Controls.Add(new Label { Text = unit, AutoSize = true, ForeColor = Color.Gray, Margin = new Padding(4, 6, 0, 0) });
        if (extra != null) p.Controls.Add(extra);
        return p;
    }

    private bool Compute()
    {
        NominalMm = null;
        PitchMm = null;
        AreaMm2 = null;
        if (!BoltSpec.TryParseDec(_nominal.Text, out var d) || d <= 0)
        {
            _result.Text = "—";
            return false;
        }

        var pText = _pitch.Text.Trim();
        decimal p = 0;
        if (pText.Length > 0 && (!BoltSpec.TryParseDec(pText, out p) || p < 0 || p >= d))
        {
            _result.Text = "螺距不合理";
            return false;
        }

        var area = p > 0 ? BoltSpec.StressArea(d, p) : (decimal)(Math.PI / 4) * d * d;
        if (area is not { } a || a <= 0)
        {
            _result.Text = "—";
            return false;
        }

        NominalMm = d;
        PitchMm = p;
        AreaMm2 = a;
        _result.Text = $"{a:0.##} mm²" + (p > 0 ? $"　（M{d:0.##}×{p:0.##} 应力截面积）" : "　（光杆截面）");
        return true;
    }
}
