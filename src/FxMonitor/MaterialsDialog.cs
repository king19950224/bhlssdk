using System.Globalization;
using FxDevice;

namespace FxMonitor;

/// <summary>
/// 材料库：等级 → 屈服 / 声速 / 弹性模量 / 声弹因子 / 温度系数。厂家 u_spec 那一套，存 farm.json 顶层 materials[]。
/// 空库打开时先把内置默认列出来，改过保存就成了现场自己的常数。
/// </summary>
internal sealed class MaterialsDialog : Form
{
    private readonly SpecLibrary _lib;
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false,
        RowHeadersVisible = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        EditMode = DataGridViewEditMode.EditOnEnter,
        BackgroundColor = Color.White,
        BorderStyle = BorderStyle.FixedSingle,
        MultiSelect = false,
    };

    private readonly Button _btnAdd = new() { Text = "添加等级", AutoSize = true };
    private readonly Button _btnDel = new() { Text = "删除选中", AutoSize = true };
    private readonly Button _btnReset = new() { Text = "恢复内置默认", AutoSize = true };
    private readonly Button _btnOk = new() { Text = "确定", Width = 88, Height = 30 };
    private readonly Button _btnCancel = new() { Text = "取消", Width = 84, Height = 30 };
    private readonly Label _hint = new()
    {
        AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(720, 0),
        Text = "声速/弹模/声弹因子只影响回波窗和没有标定文件时的理论斜率；温度系数默认是 SDK 碳钢的 0.00010269，改了就按改的。厂家 M36 10.9 理论值 0.864 kN/ns 对应声弹因子约 1.97。",
    };

    public MaterialsDialog(SpecLibrary lib)
    {
        _lib = lib;
        Text = "材料库";
        Font = new Font("Microsoft YaHei UI", 9.5f);
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(760, 380);
        MinimumSize = new Size(640, 300);
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;

        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "grade", HeaderText = "等级", FillWeight = 14 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "yield", HeaderText = "屈服 Rp0.2 MPa", FillWeight = 18 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "velocity", HeaderText = "声速 m/s", FillWeight = 16 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "elastic", HeaderText = "弹性模量 GPa", FillWeight = 16 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "acoustic", HeaderText = "声弹因子 L_ae", FillWeight = 16 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "tc", HeaderText = "温度系数 /℃", FillWeight = 20 });
        _grid.DataError += (_, e) => e.ThrowException = false;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(12, 10, 12, 10) };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(_grid, 0, 0);
        _hint.Margin = new Padding(0, 6, 0, 0);
        root.Controls.Add(_hint, 0, 1);
        var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Padding = new Padding(0, 6, 0, 0) };
        foreach (var b in new[] { _btnAdd, _btnDel, _btnReset }) b.Margin = new Padding(0, 0, 6, 0);
        bar.Controls.Add(_btnAdd);
        bar.Controls.Add(_btnDel);
        bar.Controls.Add(_btnReset);
        var spacer = new Panel { Width = 40, Height = 1 };
        bar.Controls.Add(spacer);
        _btnOk.Margin = new Padding(0, 0, 6, 0);
        bar.Controls.Add(_btnOk);
        bar.Controls.Add(_btnCancel);
        root.Controls.Add(bar, 0, 2);
        Controls.Add(root);
        AcceptButton = _btnOk;
        CancelButton = _btnCancel;

        Fill(_lib.Materials.Count > 0 ? _lib.Materials : MaterialSpec.Defaults);
        _btnAdd.Click += (_, _) => _grid.Rows.Add("", "940", "5900", "206", "2", MaterialSpec.DefaultTempCoefficient.ToString("0.########", CultureInfo.InvariantCulture));
        _btnDel.Click += (_, _) => { if (_grid.CurrentRow is { } r) _grid.Rows.Remove(r); };
        _btnReset.Click += (_, _) => Fill(MaterialSpec.Defaults);
        _btnOk.Click += (_, _) => Save();
        _btnCancel.Click += (_, _) => Close();
    }

    private void Fill(IEnumerable<MaterialSpec> mats)
    {
        _grid.Rows.Clear();
        foreach (var m in mats)
        {
            // 温度系数不留空：没单独填的就显示 SDK 默认值（和厂家一样每格都有数）
            _grid.Rows.Add(m.Grade, D(m.YieldMpa), D(m.Velocity), D(m.ElasticGpa), D(m.Acoustic),
                m.EffectiveTempCoefficient.ToString("0.########", CultureInfo.InvariantCulture));
        }
    }

    private static string D(decimal v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    private void Save()
    {
        _grid.EndEdit();
        var list = new List<MaterialSpec>();
        var errors = new List<string>();
        foreach (DataGridViewRow r in _grid.Rows)
        {
            var grade = (r.Cells["grade"].Value as string ?? "").Trim();
            if (grade.Length == 0)
            {
                errors.Add($"第 {r.Index + 1} 行没有等级");
                continue;
            }

            if (list.Any(x => string.Equals(x.Grade, grade, StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add($"等级 {grade} 重复");
                continue;
            }

            var m = new MaterialSpec { Grade = grade };
            if (!Req(r, "yield", $"{grade} 的屈服", out var y, errors) || !Req(r, "velocity", $"{grade} 的声速", out var v, errors)
                || !Req(r, "elastic", $"{grade} 的弹性模量", out var e, errors) || !Req(r, "acoustic", $"{grade} 的声弹因子", out var a, errors))
                continue;
            m.YieldMpa = y;
            m.Velocity = v;
            m.ElasticGpa = e;
            m.Acoustic = a;
            var tcText = (r.Cells["tc"].Value as string ?? "").Trim();
            if (tcText.Length > 0)
            {
                // 和默认值一样就不单独存，farm.json 里少一个字段；不一样才是现场自己的系数
                if (BoltSpec.TryParseDec(tcText, out var tc) && tc > 0) m.TempCoefficient = tc == MaterialSpec.DefaultTempCoefficient ? null : tc;
                else errors.Add($"{grade} 的温度系数不是数");
            }

            list.Add(m);
        }

        if (errors.Count > 0)
        {
            MessageBox.Show(this, string.Join("\n", errors.Select(x => "• " + x)), "材料库", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _lib.Materials.Clear();
        _lib.Materials.AddRange(list);
        DialogResult = DialogResult.OK;
        Close();
    }

    private static bool Req(DataGridViewRow r, string col, string label, out decimal value, List<string> errors)
    {
        value = 0;
        var t = (r.Cells[col].Value as string ?? "").Trim();
        if (BoltSpec.TryParseDec(t, out var v) && v > 0)
        {
            value = v;
            return true;
        }

        errors.Add($"{label}要是正数");
        return false;
    }
}
