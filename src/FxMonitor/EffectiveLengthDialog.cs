using System.Globalization;
using FxDevice;

namespace FxMonitor;

/// <summary>
/// 有效长度帮助框（厂家「有效长度 ?」）：填夹持长度和连接方式，按头/螺母/旋合螺纹的份额算出受力长度，确定后回填。
/// </summary>
internal sealed class EffectiveLengthDialog : Form
{
    private readonly TextBox _clamp = new() { Width = 90 };
    private readonly TextBox _nominal = new() { Width = 90 };
    private readonly ComboBox _joint = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    private readonly Label _result = new() { AutoSize = true, Font = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold), ForeColor = Color.FromArgb(0, 110, 200) };
    private readonly Button _btnOk = new() { Text = "确定", Width = 84, Height = 30 };
    private readonly Button _btnClose = new() { Text = "关闭", Width = 84, Height = 30 };

    public decimal? ClampMm { get; private set; }

    public decimal? NominalMm { get; private set; }

    public JointKind Joint { get; private set; }

    public decimal? EffectiveMm { get; private set; }

    public EffectiveLengthDialog(decimal? clamp, decimal? nominal, JointKind joint)
    {
        Text = "有效长度";
        Font = new Font("Microsoft YaHei UI", 9.5f);
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(520, 320);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;

        var intro = new Label
        {
            Dock = DockStyle.Top,
            Height = 104,
            Text = "有效长度是螺栓真正受拉的那段：被夹法兰的总厚度（夹持长度），再加头和螺母里参与变形的份额。\n"
                   + "按 VDI 2230 的弹性长度份额：螺栓头 0.5d、螺母 0.4d、旋合螺纹 0.5d——\n"
                   + "螺栓 + 螺母 = 夹持 + 0.9d；双头螺柱两端螺母 = 夹持 + 0.8d；螺栓拧入螺孔 = 夹持 + 1.0d。\n"
                   + "理论斜率跟有效长度成反比，填错一成力值就错一成；有载荷标定文件时它只影响对照值。",
            ForeColor = Color.FromArgb(70, 70, 70),
        };

        var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(0, 10, 0, 0) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        grid.Controls.Add(new Label { Text = "夹持长度", AutoSize = true, Margin = new Padding(0, 8, 8, 0) }, 0, 0);
        grid.Controls.Add(Unit(_clamp, "mm"), 1, 0);
        grid.Controls.Add(new Label { Text = "公称直径", AutoSize = true, Margin = new Padding(0, 8, 8, 0) }, 0, 1);
        grid.Controls.Add(Unit(_nominal, "mm"), 1, 1);
        grid.Controls.Add(new Label { Text = "连接方式", AutoSize = true, Margin = new Padding(0, 8, 8, 0) }, 0, 2);
        _joint.Margin = new Padding(0, 4, 0, 0);
        grid.Controls.Add(_joint, 1, 2);

        var resultRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 14, 0, 0) };
        resultRow.Controls.Add(new Label { Text = "有效长度 = ", AutoSize = true, Margin = new Padding(0, 6, 0, 0) });
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

        foreach (var j in new[] { JointKind.BoltNut, JointKind.StudNuts, JointKind.Tapped }) _joint.Items.Add(BoltSpec.JointName(j));
        _joint.SelectedIndex = joint switch { JointKind.StudNuts => 1, JointKind.Tapped => 2, _ => 0 };
        _clamp.Text = clamp is { } c ? c.ToString("0.##", CultureInfo.InvariantCulture) : string.Empty;
        _nominal.Text = nominal is { } n ? n.ToString("0.##", CultureInfo.InvariantCulture) : string.Empty;
        _clamp.TextChanged += (_, _) => Compute();
        _nominal.TextChanged += (_, _) => Compute();
        _joint.SelectedIndexChanged += (_, _) => Compute();
        _btnOk.Click += (_, _) =>
        {
            if (!Compute())
            {
                MessageBox.Show(this, "夹持长度和公称直径都要填成正数。", "有效长度", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            DialogResult = DialogResult.OK;
            Close();
        };
        _btnClose.Click += (_, _) => Close();
        Compute();
    }

    private static Control Unit(TextBox box, string unit)
    {
        var p = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 0) };
        p.Controls.Add(box);
        p.Controls.Add(new Label { Text = unit, AutoSize = true, ForeColor = Color.Gray, Margin = new Padding(4, 6, 0, 0) });
        return p;
    }

    private bool Compute()
    {
        ClampMm = null;
        NominalMm = null;
        EffectiveMm = null;
        Joint = _joint.SelectedIndex switch { 1 => JointKind.StudNuts, 2 => JointKind.Tapped, _ => JointKind.BoltNut };
        if (!BoltSpec.TryParseDec(_clamp.Text, out var c) || c <= 0 || !BoltSpec.TryParseDec(_nominal.Text, out var d) || d <= 0)
        {
            _result.Text = "—";
            return false;
        }

        ClampMm = c;
        NominalMm = d;
        var f = BoltSpec.JointFactor(Joint);
        EffectiveMm = c + f * d;
        _result.Text = $"{EffectiveMm:0.##} mm　（{c:0.##} + {f:0.#}×{d:0.##}）";
        return true;
    }
}
