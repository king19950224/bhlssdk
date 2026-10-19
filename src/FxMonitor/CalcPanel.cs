using FxDevice;

namespace FxMonitor;

/// <summary>
/// 右侧「计算参数」页，照厂家那一页：温度通道、温度平滑（厂家的温度 T 系数）、温度补偿三选一、结果计算三选一 + 标定文件。
/// 温度通道是这一路自己的（farm.json tempFrom）；温度平滑和温度补偿是全局的（SDK 就是全局的），标出来。
/// 结果计算：标定文件 = 用 .ucf 的斜率算力；材料系数 = 按试件规格的理论斜率。载荷标定是厂家的试验模式，产出的就是 .ucf，
/// 我们用厂家软件做完拿文件过来选，这里只作说明不可点。
/// </summary>
internal sealed class CalcPanel : UserControl
{
    private readonly NumericUpDown _tempCh = new() { Minimum = 0, Maximum = 48, Width = 70, TextAlign = HorizontalAlignment.Left };
    private readonly ComboBox _tau = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
    private readonly RadioButton[] _tc = { Seg("不使用"), Seg("材料系数"), Seg("自定义系数") };
    private readonly NumericUpDown _coef = new()
    {
        DecimalPlaces = 8, Increment = 0.000001M, Minimum = 0.00000001M, Maximum = 0.01M, Value = 0.000101M, Width = 110,
        ThousandsSeparator = false, Visible = false, Margin = new Padding(0, 4, 0, 4),
    };
    private readonly RadioButton[] _calc = { Seg("材料系数"), Seg("载荷标定"), Seg("标定文件") };
    private readonly TextBox _file = new() { ReadOnly = true, Width = 168, BackColor = Ui.Window, BorderStyle = BorderStyle.FixedSingle };
    private readonly Button _btnBrowse = new() { Text = "浏览", AutoSize = true, Height = 26 };
    private readonly Label _source = new() { AutoSize = true, ForeColor = Ui.Muted, MaximumSize = new Size(310, 0), Margin = new Padding(0, 6, 0, 0) };
    private readonly ToolTip _tip = new();
    private readonly System.Windows.Forms.Timer _tempTimer = new() { Interval = 700 };
    private readonly System.Windows.Forms.Timer _coefTimer = new() { Interval = 700 };
    private bool _loading;

    public ushort Channel { get; private set; }

    /// <summary>温度通道改了：(这一路, 借哪一路；0 = 用本路温度)。</summary>
    public event Action<ushort, ushort>? TempChannelChanged;

    /// <summary>温度平滑档位改了（TauChoices 的下标）。</summary>
    public event Action<int>? TauChanged;

    /// <summary>温度补偿改了：模式 0/1/2 和自定义系数。</summary>
    public event Action<ushort, decimal?>? TcChanged;

    /// <summary>结果计算切到「材料系数」（解除标定文件）。</summary>
    public event Action? UseTheoryRequested;

    /// <summary>「浏览」或切到「标定文件」：选一份 .ucf 给这一路。</summary>
    public event Action? BrowseUcfRequested;

    public CalcPanel()
    {
        Font = new Font("Microsoft YaHei UI", 9.5f);
        Padding = new Padding(10, 8, 8, 8);
        AutoScroll = true;
        BackColor = Color.White;
        Ui.Secondary(_btnBrowse);

        var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 68));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var r = 0;

        void Row(string label, Control editor, string tip)
        {
            var lb = new Label { Text = label, AutoSize = true, Margin = new Padding(0, 8, 4, 0) };
            _tip.SetToolTip(lb, tip);
            _tip.SetToolTip(editor, tip);
            editor.Margin = new Padding(0, 4, 0, 4);
            grid.Controls.Add(lb, 0, r);
            grid.Controls.Add(editor, 1, r);
            r++;
        }

        var tempRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        _tempCh.Margin = new Padding(0, 0, 8, 0);
        tempRow.Controls.Add(_tempCh);
        tempRow.Controls.Add(new Label { Text = "温度平滑", AutoSize = true, ForeColor = Ui.Muted, Margin = new Padding(0, 6, 4, 0) });
        tempRow.Controls.Add(_tau);
        Row("温度通道", tempRow, "温度通道：0 = 用本路探头的温度；填别的通道号 = 借那一路的温度（本路探头没温感或 Pt1000 坏了时用）。温度平滑是全局的档位。");

        Row("温度补偿", Segments(_tc), "不使用 = 回波声时 − 参考声时；材料系数 = 按材料温度系数先把声时归到参考温度再减；自定义 = 用自己填的系数。全局，两个软件同一套。");
        Row("", _coef, "自定义温补系数 /℃。厂家项目常见 0.000101。");

        var calcBox = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0) };
        calcBox.Controls.Add(Segments(_calc));
        var fileRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 0) };
        _file.Margin = new Padding(0, 0, 6, 0);
        fileRow.Controls.Add(_file);
        fileRow.Controls.Add(_btnBrowse);
        calcBox.Controls.Add(fileRow);
        Row("结果计算", calcBox, "标定文件 = 用 .ucf 的斜率算轴向力；材料系数 = 按试件规格算理论斜率。有 .ucf 时永远用文件，理论只作对照。");

        grid.Controls.Add(_source, 0, r);
        grid.SetColumnSpan(_source, 2);
        Controls.Add(grid);

        _calc[1].Enabled = false;
        _tip.SetToolTip(_calc[1], "载荷标定是厂家软件的试验模式（接拉力机同步采力），做完得到 .ucf 标定文件——拿文件到这里选「标定文件」即可。");
        _tip.SetToolTip(_btnBrowse, "选一份 .ucf 给这一路（会拷到程序目录，写进 farm.json）");

        // 拨箭头/敲数字停下 0.7 s 就存（厂家是改了即生效）；离开框、回车立刻存
        _tempTimer.Tick += (_, _) => { _tempTimer.Stop(); CommitTemp(); };
        _coefTimer.Tick += (_, _) => { _coefTimer.Stop(); if (!_loading && _tc[2].Checked) TcChanged?.Invoke(2, _coef.Value); };
        _tempCh.ValueChanged += (_, _) => { if (!_loading) { _tempTimer.Stop(); _tempTimer.Start(); } };
        _tempCh.Leave += (_, _) => { _tempTimer.Stop(); CommitTemp(); };
        _tempCh.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { _tempTimer.Stop(); CommitTemp(); e.SuppressKeyPress = true; } };
        _tau.SelectedIndexChanged += (_, _) => { if (!_loading && _tau.SelectedIndex >= 0) TauChanged?.Invoke(_tau.SelectedIndex); };
        for (var i = 0; i < 3; i++)
        {
            var mode = (ushort)i;
            _tc[i].CheckedChanged += (_, _) =>
            {
                if (_loading || !_tc[mode].Checked) return;
                _coef.Visible = mode == 2;
                TcChanged?.Invoke(mode, mode == 2 ? _coef.Value : null);
            };
        }

        _coef.ValueChanged += (_, _) =>
        {
            if (_loading || !_tc[2].Checked) return;
            _coefTimer.Stop();
            _coefTimer.Start();
        };
        _coef.Leave += (_, _) => { if (!_loading && _tc[2].Checked) { _coefTimer.Stop(); TcChanged?.Invoke(2, _coef.Value); } };
        // 用 CheckedChanged 而不是 Click：键盘方向键切过来也算选了
        _calc[0].CheckedChanged += (_, _) => { if (!_loading && _calc[0].Checked && !string.IsNullOrEmpty(_file.Text)) UseTheoryRequested?.Invoke(); };
        _calc[2].CheckedChanged += (_, _) => { if (!_loading && _calc[2].Checked && string.IsNullOrEmpty(_file.Text)) BrowseUcfRequested?.Invoke(); };
        _btnBrowse.Click += (_, _) => BrowseUcfRequested?.Invoke();
        SetChannelParts(false);
    }

    /// <summary>温度通道、结果计算是这一路自己的，没选中通道就灰掉；温度平滑、温度补偿是全局的，随时能改。</summary>
    private void SetChannelParts(bool on)
    {
        _tempCh.Enabled = on;
        _calc[0].Enabled = on;
        _calc[2].Enabled = on;
        _btnBrowse.Enabled = on;
    }

    private ushort _shownTemp;

    private void CommitTemp()
    {
        if (_loading || Channel == 0) return;
        var v = (ushort)_tempCh.Value;
        if (v == _shownTemp) return;
        if (v == Channel)
        {
            _tempCh.Value = _shownTemp;
            return;
        }

        _shownTemp = v;
        TempChannelChanged?.Invoke(Channel, v);
    }

    private static RadioButton Seg(string text) => new()
    {
        Text = text, AutoSize = true, Appearance = Appearance.Button, FlatStyle = FlatStyle.Flat,
        Padding = new Padding(6, 1, 6, 1), Margin = new Padding(0, 0, 2, 0), Height = 26, TextAlign = ContentAlignment.MiddleCenter,
    };

    private static Control Segments(RadioButton[] rbs)
    {
        var p = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0), BackColor = Ui.Window, Padding = new Padding(2) };
        foreach (var rb in rbs)
        {
            rb.FlatAppearance.BorderSize = 0;
            rb.FlatAppearance.CheckedBackColor = Color.White;
            rb.BackColor = Ui.Window;
            rb.CheckedChanged += (_, _) => rb.ForeColor = rb.Checked ? Ui.Accent : Ui.Text;
            p.Controls.Add(rb);
        }

        return p;
    }

    /// <summary>刷新这一路的显示。tauNames 是全局档位名；usesFile = 现在按标定文件算，fileName 是文件名（没有为空）。</summary>
    public void ShowCalc(ushort ch, ushort tempFrom, IReadOnlyList<string> tauNames, int tauIndex, ushort tcMode, decimal? coef,
        bool usesFile, string fileName, string sourceText)
    {
        _tempTimer.Stop();
        _coefTimer.Stop();
        _loading = true;
        try
        {
            Channel = ch;
            _shownTemp = tempFrom;
            _tempCh.Value = Math.Min(_tempCh.Maximum, tempFrom);
            if (_tau.Items.Count != tauNames.Count)
            {
                _tau.Items.Clear();
                foreach (var n in tauNames) _tau.Items.Add(n);
            }

            _tau.SelectedIndex = tauIndex >= 0 && tauIndex < _tau.Items.Count ? tauIndex : -1;
            var m = Math.Clamp((int)tcMode, 0, 2);
            _tc[m].Checked = true;
            _coef.Visible = m == 2;
            if (coef is { } c) _coef.Value = Math.Min(_coef.Maximum, Math.Max(_coef.Minimum, c));
            _calc[usesFile ? 2 : 0].Checked = true;
            _file.Text = fileName;
            _source.Text = sourceText;
            SetChannelParts(ch != 0);
        }
        finally
        {
            _loading = false;
        }
    }
}
