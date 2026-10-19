using System.Globalization;
using FxDevice;

namespace FxMonitor;

/// <summary>
/// 右侧「硬件调节」页，照厂家那一页做成可直接改的数字框：正向脉冲、反向脉冲、占空比、增益、探头频率、回波宽度。
/// 框里始终显示设备上这一路现在的值（不留空）；改了哪几项，「应用」只写那几项，写完读回核对。
/// 回波窗不在这页（厂家按近似总长算，我们在试件规格页保存时写）；参考声时/温度/阈值只在底下一行显示，改要走置零。
/// </summary>
internal sealed class HardwarePanel : UserControl
{
    private readonly NumericUpDown _forward = Num(0, 15, 0);
    private readonly NumericUpDown _reverse = Num(0, 15, 0);
    private readonly NumericUpDown _duty = Num(1, 100, 0);
    private readonly NumericUpDown _gain = Num(1, 47, 0);
    private readonly ComboBox _freq = new() { DropDownStyle = ComboBoxStyle.DropDown, Width = 88 };
    private readonly NumericUpDown _width = Num(1, 65535, 0);
    private readonly Label _info = new() { AutoSize = true, ForeColor = Ui.Muted, MaximumSize = new Size(310, 0), Margin = new Padding(0, 10, 0, 0) };
    private readonly Button _btnApply = new() { Text = "应用", Width = 84, Height = 30, Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold), Enabled = false, Margin = new Padding(0, 10, 0, 0) };
    private readonly ToolTip _tip = new();

    private bool _loading;
    private Dictionary<string, string> _device = new();

    /// <summary>正在显示的那一路；0 = 没选中。</summary>
    public ushort Channel { get; private set; }

    public event Action? ApplyRequested;

    public HardwarePanel()
    {
        Font = new Font("Microsoft YaHei UI", 9.5f);
        Padding = new Padding(10, 8, 8, 8);
        AutoScroll = true;
        BackColor = Color.White;
        Ui.Primary(_btnApply);

        var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 68));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var r = 0;

        void Row(string label, Control editor, string unit, string tip)
        {
            var lb = new Label { Text = label, AutoSize = true, Margin = new Padding(0, 8, 4, 0) };
            _tip.SetToolTip(lb, tip);
            _tip.SetToolTip(editor, tip);
            editor.Margin = new Padding(0, 4, 0, 4);
            grid.Controls.Add(lb, 0, r);
            grid.Controls.Add(editor, 1, r);
            if (unit.Length > 0) grid.Controls.Add(Ui.Unit(unit), 2, r);

            r++;
        }

        Row("正向脉冲", _forward, "", "激励正向脉冲个数 0～15。");
        Row("反向脉冲", _reverse, "", "激励反向脉冲个数 0～15。");
        Row("占空比", _duty, "1~100", "激励占空比 %。");
        Row("增益", _gain, "", "接收增益档 1～47。只改回波幅值。");
        Row("探头频率", _freq, "MHz", "探头标称频率 MHz，自动法用它解载波周期。");
        Row("回波宽度", _width, "us", "回波窗宽度 µs：设备从回波窗起点起采这么长一段。");

        grid.Controls.Add(_info, 0, r);
        grid.SetColumnSpan(_info, 3);
        r++;
        _tip.SetToolTip(_btnApply, "只写改过的几项，写完逐项读回核对。多路一起改用工具栏的「参数下发…」");
        grid.Controls.Add(_btnApply, 0, r);
        grid.SetColumnSpan(_btnApply, 3);
        Controls.Add(grid);

        foreach (var f in new[] { "1", "2", "2.5", "4", "5" }) _freq.Items.Add(f);
        foreach (var n in new[] { _forward, _reverse, _duty, _gain, _width })
            n.ValueChanged += (_, _) => OnEdited();
        _freq.TextChanged += (_, _) => OnEdited();
        _btnApply.Click += (_, _) => ApplyRequested?.Invoke();
        SetEditable(false);
    }

    private Control[] Editors => new Control[] { _forward, _reverse, _duty, _gain, _freq, _width };

    /// <summary>没选中通道/没读到参数时六个框和「应用」灰掉。</summary>
    private void SetEditable(bool on)
    {
        foreach (var c in Editors) c.Enabled = on;
        if (!on) _btnApply.Enabled = false;
    }

    private static NumericUpDown Num(decimal min, decimal max, int decimals) => new()
    {
        Minimum = min, Maximum = max, DecimalPlaces = decimals, Width = 88, TextAlign = HorizontalAlignment.Left,
    };

    /// <summary>显示设备上这一路的参数。cfg 为空 = 没选中/没读到，框里留原值、页面变灰。</summary>
    public void ShowValues(ushort ch, Dictionary<string, string>? cfg)
    {
        Channel = ch;
        _device = cfg is { } d ? new Dictionary<string, string>(d) : new Dictionary<string, string>();
        var has = ch != 0 && _device.Count > 0;
        _loading = true;
        try
        {
            Set(_forward, "Forward");
            Set(_reverse, "Reverse");
            Set(_duty, "Duty");
            Set(_gain, "Gain");
            Set(_width, "EchoWidth");
            _freq.Text = DeviceEngine.Num(_device, "ProbeFreq") is { } pf ? pf.ToString("0.##", CultureInfo.InvariantCulture) : string.Empty;
            SetEditable(has);
            _info.Text = !has ? "没选中通道或参数没读到" : string.Join("　",
                new[]
                {
                    $"回波窗 {DeviceEngine.Raw(_device, "EchoWin")} ns",
                    $"参考声时 {DeviceEngine.Raw(_device, "RefTime")} ns",
                    $"参考温度 {DeviceEngine.Raw(_device, "RefTemp")} ℃",
                    $"阈值 {DeviceEngine.Raw(_device, "Threshold")}",
                });
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>设备值超出框的常规范围时把范围撑开而不是夹住：夹住了就会被当成"改过"，一按应用写回去。</summary>
    private void Set(NumericUpDown n, string key)
    {
        if (DeviceEngine.Num(_device, key) is not { } v) return;
        if (v < n.Minimum) n.Minimum = v;
        if (v > n.Maximum) n.Maximum = v;
        n.Value = v;
    }

    private void OnEdited()
    {
        if (_loading) return;
        _btnApply.Enabled = Channel != 0 && Collect().Count > 0;
    }

    /// <summary>
    /// 和设备现值不同的项，只写这些。设备快照里没有的项不比较也不写；探头频率不是数字的当没改。
    /// </summary>
    public List<(string Name, decimal Value)> Collect()
    {
        var list = new List<(string, decimal)>();
        void Cmp(string key, decimal value)
        {
            if (DeviceEngine.Num(_device, key) is { } cur && cur != value) list.Add((key, value));
        }

        Cmp("Forward", _forward.Value);
        Cmp("Reverse", _reverse.Value);
        Cmp("Duty", _duty.Value);
        Cmp("Gain", _gain.Value);
        Cmp("EchoWidth", _width.Value);
        if (BoltSpec.TryParseDec(_freq.Text, out var f) && f > 0 && f <= 65) Cmp("ProbeFreq", f);
        return list;
    }

    /// <summary>写设备期间六个框和「应用」灰掉，免得写一半又改。</summary>
    public void SetBusy(bool busy)
    {
        SetEditable(!busy && Channel != 0 && _device.Count > 0);
        _btnApply.Enabled = !busy && Channel != 0 && Collect().Count > 0;
    }
}
