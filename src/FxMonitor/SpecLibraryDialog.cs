using System.Globalization;
using FxDevice;

namespace FxMonitor;

/// <summary>
/// 规格库（只从「现场配置」进）：一种螺栓填一次存进 farm.json，法兰按名字引用做默认规格；
/// 一路一个试件的具体规格在主窗口右侧「试件规格」页填。左边是库里的规格，右边编辑。
/// </summary>
internal sealed class SpecLibraryDialog : Form
{
    private const string NoUcf = "（无）";

    private readonly SpecLibrary _lib;
    private readonly IReadOnlyList<string> _ucfFiles;
    private readonly IReadOnlyDictionary<string, string> _usage;
    private int _current = -1;
    private bool _loading;

    private readonly ListBox _list = new() { Dock = DockStyle.Fill, IntegralHeight = false, Font = new Font("Microsoft YaHei UI", 10f) };
    private readonly TextBox _name = new() { Width = 260 };
    private readonly TextBox _nominal = Box();
    private readonly TextBox _pitch = Box();
    private readonly TextBox _area = Box();
    private readonly ComboBox _grade = new() { DropDownStyle = ComboBoxStyle.DropDown, Width = 88 };
    private readonly TextBox _length = Box();
    private readonly TextBox _clamp = Box();
    private readonly ComboBox _joint = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
    private readonly TextBox _effective = Box();
    private readonly TextBox _rated = Box();
    private readonly ComboBox _ucf = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly TextBox _lower = Box();
    private readonly TextBox _upper = Box();
    private readonly Label _areaHint = Hint();
    private readonly Label _effHint = Hint();
    private readonly Label _preview = new() { AutoSize = true, ForeColor = Color.FromArgb(0, 110, 200), MaximumSize = new Size(600, 0) };
    private readonly Label _status = new() { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(600, 0) };

    private readonly Button _btnNew = new() { Text = "新建", Width = 60 };
    private readonly Button _btnCopy = new() { Text = "复制", Width = 60 };
    private readonly Button _btnDel = new() { Text = "删除", Width = 60 };
    private readonly Button _btnSave = new() { Text = "保存", Width = 96, Height = 32, Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold) };
    private readonly Button _btnCancel = new() { Text = "取消", Width = 84, Height = 32 };
    private readonly ToolTip _tip = new();

    public SpecLibraryDialog(SpecLibrary lib, IReadOnlyList<string> ucfFiles, IReadOnlyDictionary<string, string>? usage = null)
    {
        _lib = lib;
        _ucfFiles = ucfFiles;
        _usage = usage ?? new Dictionary<string, string>();

        Text = "试件规格库";
        Font = new Font("Microsoft YaHei UI", 9.5f);
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(900, 600);
        MinimumSize = new Size(820, 540);
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;

        BuildUi();
        RefreshGrades();
        _ucf.Items.Add(NoUcf);
        foreach (var f in _ucfFiles) _ucf.Items.Add(f);
        foreach (var j in new[] { JointKind.BoltNut, JointKind.StudNuts, JointKind.Tapped }) _joint.Items.Add(BoltSpec.JointName(j));

        RefreshList();
        if (_lib.Specs.Count == 0)
        {
            _lib.Specs.Add(new BoltSpec { Name = _lib.UniqueName("新规格") });
            RefreshList();
        }

        _list.SelectedIndex = 0;

        _list.SelectedIndexChanged += (_, _) => OnSelect();
        _btnNew.Click += (_, _) => NewSpec(copy: false);
        _btnCopy.Click += (_, _) => NewSpec(copy: true);
        _btnDel.Click += (_, _) => DeleteSpec();
        _btnSave.Click += (_, _) => Save();
        _btnCancel.Click += (_, _) => Close();
        foreach (var t in new[] { _nominal, _pitch, _area, _length, _clamp, _effective, _rated, _lower, _upper })
            t.TextChanged += (_, _) => RefreshPreview();
        _name.TextChanged += (_, _) =>
        {
            if (_loading || _current < 0) return;
            _list.Items[_current] = _name.Text.Trim().Length > 0 ? _name.Text.Trim() : "（未命名）";
        };
        _grade.TextChanged += (_, _) => RefreshPreview();
        _joint.SelectedIndexChanged += (_, _) => RefreshPreview();
        _ucf.SelectedIndexChanged += (_, _) => RefreshPreview();
    }

    private static TextBox Box() => new() { Width = 88 };

    private static Label Hint() => new() { AutoSize = true, ForeColor = Color.Gray, Margin = new Padding(4, 8, 0, 0) };

    private void BuildUi()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Padding = new Padding(12, 10, 12, 10) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // 左：库
        var left = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 0, 10, 0) };
        var leftTitle = new Label { Text = "规格库（全场共用，存 farm.json）", Dock = DockStyle.Top, Height = 24, ForeColor = Color.Gray };
        var leftBar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 34, WrapContents = false, Padding = new Padding(0, 4, 0, 0) };
        foreach (var b in new[] { _btnNew, _btnCopy, _btnDel }) b.Margin = new Padding(0, 0, 4, 0);
        leftBar.Controls.Add(_btnNew);
        leftBar.Controls.Add(_btnCopy);
        leftBar.Controls.Add(_btnDel);
        left.Controls.Add(_list);
        left.Controls.Add(leftBar);
        left.Controls.Add(leftTitle);
        root.Controls.Add(left, 0, 0);

        // 右：编辑
        var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 4 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        var r = 0;

        void Field(string label, Control editor, string tip, int col, int row)
        {
            var lb = new Label { Text = label, AutoSize = true, Margin = new Padding(0, 8, 6, 0) };
            _tip.SetToolTip(lb, tip);
            _tip.SetToolTip(editor, tip);
            editor.Margin = new Padding(0, 4, 10, 4);
            grid.Controls.Add(lb, col, row);
            grid.Controls.Add(editor, col + 1, row);
        }

        void Pair(string l1, Control e1, string t1, string l2, Control e2, string t2)
        {
            Field(l1, e1, t1, 0, r);
            Field(l2, e2, t2, 2, r);
            r++;
        }

        Field("名称", _name, "这种螺栓的叫法，如 M36×4 10.9 夹持160。法兰和通道按它引用，改名后引用跟着改。", 0, r);
        grid.SetColumnSpan(_name, 3);
        r++;
        Pair("公称直径", WithUnit(_nominal, "mm"), "M 后面的名义整数，不是卡尺读数。要量就量螺纹牙顶，向上取到标准规格。",
            "螺距", WithUnit(_pitch, "mm"), "量 10 个牙的跨距除以 10。M36 粗牙 4，M64 粗牙 6。不要默认粗牙。");
        Pair("净截面积", WithUnit(_area, "mm²", _areaHint), "空着就按 ISO 898-1 由直径和螺距算（和厂家对话框一致）；填了以你填的为准。",
            "材料等级", _grade, "等级带出屈服/声速/弹模/声弹因子/温度系数。常数在主窗体「试件规格」页「材料规格 ?」的材料库里改。");
        Pair("近似总长", WithUnit(_length, "mm"), "螺栓总长。只用来放回波窗：回波窗 = 总长×2×10⁶/声速，向下取整到 1 µs（和厂家同一公式）。",
            "夹持长度", WithUnit(_clamp, "mm"), "被夹法兰的总厚度，现场能直接量的那个数。");
        Pair("连接方式", _joint, "决定有效长度里头和螺母各算多少：螺栓+螺母 = 夹持+0.9d；双头螺柱 = 夹持+0.8d；拧入螺孔 = 夹持+1.0d（VDI 2230 份额）。",
            "有效长度", WithUnit(_effective, "mm", _effHint), "空着就按夹持长度+连接方式算；填了以你填的为准。理论斜率跟它成反比。");
        Pair("额定预紧力", WithUnit(_rated, "kN"), "这种螺栓的工艺额定。法兰没单独填额定时用它判偏低/松动。",
            "标定文件", _ucf, "这种螺栓的载荷标定 .ucf。有文件用文件斜率，理论值只作对照。");
        Pair("下限", WithUnit(_lower, "kN"), "显示范围下限，不参与判定。",
            "上限", WithUnit(_upper, "kN"), "显示范围上限，不参与判定。");
        _preview.Margin = new Padding(0, 6, 0, 0);
        grid.Controls.Add(_preview, 0, r);
        grid.SetColumnSpan(_preview, 4);
        r++;

        var right = new Panel { Dock = DockStyle.Fill };
        right.Controls.Add(grid);
        root.Controls.Add(right, 1, 0);

        var foot = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, AutoSize = true };
        _btnSave.Margin = new Padding(8, 6, 0, 0);
        _btnCancel.Margin = new Padding(8, 6, 0, 0);
        _status.Margin = new Padding(8, 12, 0, 0);
        foot.Controls.Add(_btnSave);
        foot.Controls.Add(_btnCancel);
        foot.Controls.Add(_status);
        root.Controls.Add(foot, 0, 1);
        root.SetColumnSpan(foot, 2);

        Controls.Add(root);
        CancelButton = _btnCancel;
    }

    private static Control WithUnit(TextBox box, string unit, Label? hint = null)
    {
        var p = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        box.Margin = new Padding(0, 0, 4, 0);
        p.Controls.Add(box);
        p.Controls.Add(new Label { Text = unit, AutoSize = true, Margin = new Padding(0, 6, 0, 0), ForeColor = Color.Gray });
        if (hint != null) p.Controls.Add(hint);
        return p;
    }

    private void RefreshGrades()
    {
        var keep = _grade.Text;
        _grade.Items.Clear();
        foreach (var g in _lib.Grades()) _grade.Items.Add(g);
        _grade.Text = keep;
    }

    private void RefreshList()
    {
        _loading = true;
        _list.Items.Clear();
        foreach (var s in _lib.Specs) _list.Items.Add(s.Name.Trim().Length > 0 ? s.Name.Trim() : "（未命名）");
        _loading = false;
    }

    private void OnSelect()
    {
        if (_loading) return;
        var next = _list.SelectedIndex;
        if (next == _current) return;
        if (_current >= 0 && _current < _lib.Specs.Count && !Commit(_current, out var err))
        {
            MessageBox.Show(this, err, "试件规格", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _loading = true;
            _list.SelectedIndex = _current;
            _loading = false;
            return;
        }

        _current = next;
        LoadSpec(next);
    }

    private void LoadSpec(int index)
    {
        _loading = true;
        try
        {
            if (index < 0 || index >= _lib.Specs.Count)
            {
                foreach (var t in new[] { _name, _nominal, _pitch, _area, _length, _clamp, _effective, _rated, _lower, _upper }) t.Text = string.Empty;
                _preview.Text = string.Empty;
                return;
            }

            var s = _lib.Specs[index];
            _name.Text = s.Name;
            _nominal.Text = Dec(s.NominalMm);
            _pitch.Text = Dec(s.PitchMm);
            _area.Text = Dec(s.AreaMm2);
            _grade.Text = string.IsNullOrWhiteSpace(s.Grade) ? "10.9" : s.Grade;
            _length.Text = Dec(s.LengthMm);
            _clamp.Text = Dec(s.ClampMm);
            _joint.SelectedIndex = s.Joint switch { JointKind.StudNuts => 1, JointKind.Tapped => 2, _ => 0 };
            _effective.Text = Dec(s.EffectiveMm);
            _rated.Text = Dec(s.RatedKn);
            _lower.Text = Dec(s.LowerKn);
            _upper.Text = Dec(s.UpperKn);
            _ucf.SelectedItem = s.Ucf is { Length: > 0 } u && _ucf.Items.Contains(u) ? u : NoUcf;
            if (s.Ucf is { Length: > 0 } u2 && !_ucf.Items.Contains(u2))
            {
                _ucf.Items.Add(u2);   // 配置里写了但目录里没有：也列出来，保存时提示
                _ucf.SelectedItem = u2;
            }
        }
        finally
        {
            _loading = false;
        }

        RefreshPreview();
    }

    private static string Dec(decimal? v) => v is { } n ? n.ToString("0.##", CultureInfo.InvariantCulture) : string.Empty;

    /// <summary>把编辑框读回一条规格；填得不像数就报错，不猜。</summary>
    private bool Collect(out BoltSpec spec, out string error)
    {
        spec = new BoltSpec();
        error = string.Empty;
        spec.Name = _name.Text.Trim();
        spec.Grade = string.IsNullOrWhiteSpace(_grade.Text) ? "10.9" : _grade.Text.Trim();
        spec.Joint = _joint.SelectedIndex switch { 1 => JointKind.StudNuts, 2 => JointKind.Tapped, _ => JointKind.BoltNut };
        spec.Ucf = _ucf.SelectedItem is string u && u != NoUcf ? u : null;
        if (!Opt(_nominal, "公称直径", out var nominal, out error)) return false;
        if (!Opt(_pitch, "螺距", out var pitch, out error)) return false;
        if (!Opt(_area, "净截面积", out var area, out error)) return false;
        if (!Opt(_length, "近似总长", out var length, out error)) return false;
        if (!Opt(_clamp, "夹持长度", out var clamp, out error)) return false;
        if (!Opt(_effective, "有效长度", out var effective, out error)) return false;
        if (!Opt(_rated, "额定预紧力", out var rated, out error)) return false;
        if (!Opt(_lower, "下限", out var lower, out error)) return false;
        if (!Opt(_upper, "上限", out var upper, out error)) return false;
        spec.NominalMm = nominal;
        spec.PitchMm = pitch;
        spec.AreaMm2 = area;
        spec.LengthMm = length;
        spec.ClampMm = clamp;
        spec.EffectiveMm = effective;
        spec.RatedKn = rated;
        spec.LowerKn = lower;
        spec.UpperKn = upper;
        return true;
    }

    private static bool Opt(TextBox box, string label, out decimal? value, out string error)
    {
        value = null;
        error = string.Empty;
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

    private bool Commit(int index, out string error)
    {
        if (!Collect(out var spec, out error)) return false;
        if (spec.Name.Length == 0)
        {
            error = "规格要有名字";
            return false;
        }

        var dup = _lib.Specs.Select((s, i) => (s, i)).FirstOrDefault(x => x.i != index && string.Equals(x.s.Name.Trim(), spec.Name, StringComparison.OrdinalIgnoreCase));
        if (dup.s is { })
        {
            error = $"已经有一条叫「{spec.Name}」的规格";
            return false;
        }

        var target = _lib.Specs[index];
        var oldName = target.Name;
        target.Name = spec.Name;
        target.NominalMm = spec.NominalMm;
        target.PitchMm = spec.PitchMm;
        target.AreaMm2 = spec.AreaMm2;
        target.Grade = spec.Grade;
        target.LengthMm = spec.LengthMm;
        target.ClampMm = spec.ClampMm;
        target.Joint = spec.Joint;
        target.EffectiveMm = spec.EffectiveMm;
        target.RatedKn = spec.RatedKn;
        target.LowerKn = spec.LowerKn;
        target.UpperKn = spec.UpperKn;
        target.Ucf = spec.Ucf;
        if (!string.Equals(oldName, target.Name, StringComparison.Ordinal)) Renamed?.Invoke(oldName, target.Name);
        return true;
    }

    /// <summary>改了规格名：调用方（现场配置）把法兰/通道上的引用跟着改。</summary>
    public event Action<string, string>? Renamed;

    private void RefreshPreview()
    {
        if (_loading) return;
        if (!Collect(out var s, out var err))
        {
            _preview.Text = err;
            return;
        }

        var m = _lib.MaterialFor(s.Grade);
        _areaHint.Text = s.AreaMm2 is null && BoltSpec.StressArea(s.NominalMm, s.PitchMm) is { } asv ? $"按螺纹 {asv:0.##}" : string.Empty;
        _effHint.Text = s.EffectiveMm is null && s.DerivedEffective is { } de ? $"按夹持 {de:0.##}" : string.Empty;

        var bits = new List<string>();
        if (s.ResolvedArea is { } a) bits.Add($"As {a:0.##} mm²");
        if (s.ResolvedEffective is { } le) bits.Add($"Le {le:0.##} mm");
        if (s.ExpectedEchoNs(m) is { } t) bits.Add($"预期回波 {t:0} ns → 回波窗 {s.EchoWinNs(m):0}");
        if (s.TheoreticalK(m) is { } k) bits.Add($"理论斜率 {k:F4} kN/ns");
        if (s.YieldKn(m) is { } y) bits.Add($"屈服 {y:0} kN，70% ≈ {y * 0.7m:0}");
        bits.Add($"材料 {m.Grade}：{m.YieldMpa:0} MPa · {m.Velocity:0} m/s · {m.ElasticGpa:0} GPa · L_ae {m.Acoustic:0.##}");
        if (s.Ucf is { Length: > 0 }) bits.Add("有标定文件，力值用文件斜率，理论只作对照");
        _preview.Text = string.Join("　", bits);
    }

    private void NewSpec(bool copy)
    {
        if (_current >= 0 && _current < _lib.Specs.Count && !Commit(_current, out var err))
        {
            MessageBox.Show(this, err, "试件规格", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        BoltSpec s;
        if (copy && _current >= 0 && _current < _lib.Specs.Count)
        {
            s = _lib.Specs[_current].Clone();
            s.Name = _lib.UniqueName(s.Name + " 副本");
        }
        else
        {
            s = new BoltSpec { Name = _lib.UniqueName("新规格") };
        }

        _lib.Specs.Add(s);
        RefreshList();
        _current = -1;
        _list.SelectedIndex = _lib.Specs.Count - 1;
        _name.Focus();
        _name.SelectAll();
    }

    private void DeleteSpec()
    {
        if (_current < 0 || _current >= _lib.Specs.Count) return;
        var s = _lib.Specs[_current];
        if (_usage.TryGetValue(s.Name, out var where) && where.Length > 0)
        {
            MessageBox.Show(this, $"「{s.DisplayName}」还被 {where} 引用着，先把它们改成别的规格。", "删除规格", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (MessageBox.Show(this, $"删除规格「{s.DisplayName}」？", "删除规格", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        _lib.Specs.RemoveAt(_current);
        var next = Math.Min(_current, _lib.Specs.Count - 1);
        _current = -1;
        RefreshList();
        if (next >= 0) _list.SelectedIndex = next;
        else LoadSpec(-1);
    }

    private void Save()
    {
        if (_current >= 0 && _current < _lib.Specs.Count && !Commit(_current, out var err))
        {
            MessageBox.Show(this, err, "试件规格", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var missing = _lib.Specs.Where(s => s.Ucf is { Length: > 0 } u && !_ucfFiles.Contains(u, StringComparer.OrdinalIgnoreCase)).Select(s => $"{s.DisplayName}：{s.Ucf}").ToList();
        if (missing.Count > 0)
        {
            MessageBox.Show(this, "这些规格引用的标定文件在程序目录和大屏目录里都找不到：\n" + string.Join("\n", missing), "试件规格", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        DialogResult = DialogResult.OK;
        Close();
    }
}
