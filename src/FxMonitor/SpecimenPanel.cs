using System.Globalization;
using FxDevice;

namespace FxMonitor;

/// <summary>
/// 主窗口右侧「试件规格」页，和厂家那一页一样的七项：名称（唯一）、近似总长、材料规格、有效长度、净截面积、分组、下限/上限，
/// 每个"?"是对应的帮助/计算框。选中哪一路显示哪一路（一路一个试件），改完按「保存」写进 farm.json。
/// 近似总长照厂家的用法直接决定回波窗：保存时总长变了，主窗体按 总长×2×10⁶/声速 算出回波窗写进设备，不另设按钮。
/// 公称直径/螺距/夹持长度/连接方式藏在帮助框里，随试件一起存，下次还能改。
/// </summary>
internal sealed class SpecimenPanel : UserControl
{
    private readonly TextBox _name = new() { Width = 132 };
    private readonly Label _unique = Ui.Chip("唯一");
    private readonly TextBox _length = Box();
    private readonly ComboBox _grade = new() { DropDownStyle = ComboBoxStyle.DropDown, Width = 100 };
    private readonly TextBox _effective = Box();
    private readonly TextBox _area = Box();
    private readonly ComboBox _group = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 132 };
    private readonly TextBox _lower = new() { Width = 58 };
    private readonly TextBox _upper = new() { Width = 58 };
    private readonly TextBox _rated = new() { Width = 84 };
    private readonly Label _cal = new() { AutoSize = true, MaximumSize = new Size(240, 36), ForeColor = Color.DimGray, Text = "无" };
    private readonly Button _btnGradeHelp = Help();
    private readonly Button _btnEffHelp = Help();
    private readonly Button _btnAreaHelp = Help();
    private readonly Button _btnGroupHelp = Help();
    private readonly Button _btnSave = new() { Text = "保存", Width = 84, Height = 30, Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold), Enabled = false };
    private readonly ToolTip _tip = new();

    private bool _loading;

    /// <summary>正在编辑的那一路；0 = 没选中通道。</summary>
    public ushort Channel { get; private set; }

    /// <summary>工作副本：帮助框改的隐藏字段（直径/螺距/夹持/连接方式）放在这里，界面框只显示算出来的数。</summary>
    public BoltSpec Working { get; private set; } = new();

    /// <summary>改过还没保存。</summary>
    public bool Dirty { get; private set; }

    public event Action? SaveRequested;

    public event Action? OpenConfigRequested;

    public event Action? OpenMaterialsRequested;

    public SpecimenPanel()
    {
        Font = new Font("Microsoft YaHei UI", 9.5f);
        Padding = new Padding(10, 8, 8, 8);
        AutoScroll = false;
        BackColor = Color.White;
        Ui.Primary(_btnSave);

        var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 68));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var r = 0;

        void Row(string label, Control editor, Control? extra, string tip)
        {
            var lb = new Label { Text = label, AutoSize = true, Margin = new Padding(0, 5, 4, 0) };
            _tip.SetToolTip(lb, tip);
            _tip.SetToolTip(editor, tip);
            editor.Margin = new Padding(0, 2, 0, 2);
            grid.Controls.Add(lb, 0, r);
            grid.Controls.Add(editor, 1, r);
            if (extra != null)
            {
                extra.Margin = new Padding(4, 4, 0, 0);
                grid.Controls.Add(extra, 2, r);
            }

            r++;
        }

        Row("名称", _name, _unique, "这颗试件的叫法，同一台设备上不能重名。");
        Row("近似总长", Unit(_length, "mm"), null, "螺栓总长。保存时按 总长×2×10⁶/声速 算出回波窗写进设备（和厂家一样）。");
        Row("材料规格", _grade, _btnGradeHelp, "性能等级，带出屈服/声速/弹模/声弹因子/温度系数。点 ? 打开材料库看/改这些常数。");
        Row("有效长度", Unit(_effective, "mm"), _btnEffHelp, "真正受拉的那段长度。点 ? 按夹持长度 + 连接方式算；也可以直接填。");
        Row("净截面积", Unit(_area, "mm²"), _btnAreaHelp, "应力截面积 As。点 ? 按公称直径 + 螺距算（ISO 898-1）；也可以直接填。");
        Row("分组", _group, _btnGroupHelp, "这颗试件在哪个法兰。改法兰点 ?。");
        Row("额定", Unit(_rated, "kN"), null, "这一路螺栓的工艺额定。用来判偏低/松动。空着表示未配，只显示变化量。");

        var limits = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 4) };
        limits.Controls.Add(_lower);
        limits.Controls.Add(new Label { Text = "上限", AutoSize = true, Margin = new Padding(10, 6, 4, 0) });
        limits.Controls.Add(_upper);
        Row("下限", limits, null, "轴向力显示范围（kN）：出了范围卡片上的数字变红。");
        Row("标定", _cal, null, "这一路正在用的标定。换文件在「计算参数」。这一路还没有自己的规格时，保存会把法兰上的标定文件写进新规格。");

        // 保存钉在底部：窗口矮的时候上面的格子滚动，按钮不被卷走
        var foot = new Panel { Dock = DockStyle.Bottom, Height = 36 };
        _btnSave.Location = new Point(0, 4);
        foot.Controls.Add(_btnSave);
        var scroller = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        grid.Dock = DockStyle.None;
        grid.AutoSize = true;
        grid.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        scroller.Controls.Add(grid);
        scroller.Resize += (_, _) =>
        {
            var width = Math.Max(1, scroller.ClientSize.Width);
            if (grid.Width != width) grid.Width = width;
        };
        Controls.Add(scroller);
        Controls.Add(foot);

        _tip.SetToolTip(_btnGradeHelp, "看/改这一等级的声速、弹模、声弹因子、温度系数、屈服");
        _tip.SetToolTip(_btnEffHelp, "按夹持长度和连接方式算有效长度");
        _tip.SetToolTip(_btnAreaHelp, "按公称直径和螺距算净截面积");
        _tip.SetToolTip(_btnGroupHelp, "打开现场配置改法兰/接线");

        foreach (var t in new[] { _name, _length, _effective, _area, _lower, _upper, _rated })
            t.TextChanged += (_, _) => OnEdited();
        _grade.TextChanged += (_, _) => OnEdited();
        _group.SelectedIndexChanged += (_, _) => OnEdited();
        _effective.TextChanged += (_, _) => { if (!_loading) Working.EffectiveMm = ParseOrNull(_effective.Text); };
        _area.TextChanged += (_, _) => { if (!_loading) Working.AreaMm2 = ParseOrNull(_area.Text); };

        _btnAreaHelp.Click += (_, _) =>
        {
            using var dlg = new AreaDialog(Working.NominalMm, Working.PitchMm);
            if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
            Working.NominalMm = dlg.NominalMm;
            Working.PitchMm = dlg.PitchMm is { } p && p > 0 ? p : null;
            Working.AreaMm2 = dlg.PitchMm is { } p2 && p2 > 0 ? null : dlg.AreaMm2;   // 有螺距按公式算，光杆直接存面积
            _loading = true;
            _area.Text = Dec(dlg.AreaMm2);
            _loading = false;
            OnEdited();
        };
        _btnEffHelp.Click += (_, _) =>
        {
            using var dlg = new EffectiveLengthDialog(Working.ClampMm, Working.NominalMm, Working.Joint);
            if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
            Working.ClampMm = dlg.ClampMm;
            Working.NominalMm ??= dlg.NominalMm;
            Working.Joint = dlg.Joint;
            Working.EffectiveMm = null;   // 按夹持算，不覆盖
            _loading = true;
            _effective.Text = Dec(dlg.EffectiveMm);
            _loading = false;
            OnEdited();
        };
        _btnGradeHelp.Click += (_, _) => OpenMaterialsRequested?.Invoke();
        _btnGroupHelp.Click += (_, _) => OpenConfigRequested?.Invoke();
        _btnSave.Click += (_, _) => SaveRequested?.Invoke();
    }

    private static TextBox Box() => new() { Width = 84 };

    private static Button Help() => Ui.Help();

    private static Control Unit(TextBox box, string unit)
    {
        var p = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        box.Margin = new Padding(0, 0, 0, 0);
        p.Controls.Add(box);
        p.Controls.Add(Ui.Unit(unit));
        return p;
    }

    private static string Dec(decimal? v) => v is { } n ? n.ToString("0.##", CultureInfo.InvariantCulture) : string.Empty;

    private static decimal? ParseOrNull(string text) => BoltSpec.TryParseDec(text, out var v) && v > 0 ? v : null;

    /// <summary>
    /// 显示一路的试件。spec 为空 = 这一路还没有规格（按空表单，名称预填位置/通道号）；
    /// own = false 表示现在显示的是法兰默认那条，一保存就成为这一路自己的。
    /// </summary>
    public void ShowSpecimen(ushort ch, BoltSpec? spec, bool own, string? groupName, IEnumerable<string> groups,
        IEnumerable<string> grades, string defaultName)
    {
        _loading = true;
        try
        {
            Channel = ch;
            Working = spec?.Clone() ?? new BoltSpec();
            if (!own || Working.Name.Trim().Length == 0) Working.Name = defaultName;
            _name.Text = Working.Name;
            _length.Text = Dec(Working.LengthMm);
            _grade.Items.Clear();
            foreach (var g in grades) _grade.Items.Add(g);
            _grade.Text = string.IsNullOrWhiteSpace(Working.Grade) ? "10.9" : Working.Grade;
            _effective.Text = Dec(Working.ResolvedEffective);
            _area.Text = Dec(Working.ResolvedArea);
            _group.Items.Clear();
            foreach (var g in groups) _group.Items.Add(g);
            _group.SelectedItem = groupName is { } gn && _group.Items.Contains(gn) ? gn : null;
            _lower.Text = Dec(Working.LowerKn);
            _upper.Text = Dec(Working.UpperKn);
            _rated.Text = Dec(Working.RatedKn);
            Dirty = false;
            _btnSave.Enabled = false;
            Enabled = ch != 0;
        }
        finally
        {
            _loading = false;
        }
    }

    public string? SelectedGroup => _group.SelectedItem as string;

    /// <summary>显示这一路当前的标定来源。不进保存的规格字段。</summary>
    public void ShowCalibration(string? text)
    {
        _cal.Text = string.IsNullOrWhiteSpace(text) ? "无" : text;
    }

    /// <summary>把表单读回一条规格。填得不像数就报错。</summary>
    public bool TryCollect(out BoltSpec spec, out string error)
    {
        error = string.Empty;
        spec = Working.Clone();
        spec.Name = _name.Text.Trim();
        spec.Grade = string.IsNullOrWhiteSpace(_grade.Text) ? "10.9" : _grade.Text.Trim();
        if (spec.Name.Length == 0)
        {
            error = "名称不能为空";
            return false;
        }

        if (!Opt(_length, "近似总长", out var length, ref error)) return false;
        if (!Opt(_effective, "有效长度", out var effective, ref error)) return false;
        if (!Opt(_area, "净截面积", out var area, ref error)) return false;
        if (!Opt(_lower, "下限", out var lower, ref error)) return false;
        if (!Opt(_upper, "上限", out var upper, ref error)) return false;
        if (!Opt(_rated, "额定预紧力", out var rated, ref error)) return false;
        spec.LengthMm = length;
        // 有效长度/净截面积：和按公式算出来的一样就不存覆盖值，保留直径/螺距/夹持这些来源；不一样就是人手填的
        spec.EffectiveMm = effective is { } e && (spec.DerivedEffective is not { } de || Math.Abs(de - e) > 0.05m) ? e : null;
        spec.AreaMm2 = area is { } a && (BoltSpec.StressArea(spec.NominalMm, spec.PitchMm) is not { } sa || Math.Abs(sa - a) > 0.05m) ? a : null;
        spec.LowerKn = lower;
        spec.UpperKn = upper;
        spec.RatedKn = rated is { } r && r > 0 ? r : null;
        if (lower is { } lo && upper is { } up && lo > up)
        {
            error = "下限大于上限";
            return false;
        }

        return true;
    }

    private static bool Opt(TextBox box, string label, out decimal? value, ref string error)
    {
        value = null;
        var t = box.Text.Trim();
        if (t.Length == 0) return true;
        if (!BoltSpec.TryParseDec(t, out var v) || v < 0)
        {
            error = $"{label}不是数：{t}";
            return false;
        }

        value = v;
        return true;
    }

    private void OnEdited()
    {
        if (_loading) return;
        Dirty = true;
        _btnSave.Enabled = Channel != 0;
    }

    public void MarkSaved()
    {
        Dirty = false;
        _btnSave.Enabled = false;
    }
}
