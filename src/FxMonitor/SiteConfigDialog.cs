using System.Diagnostics;
using System.Globalization;
using FxDevice;

namespace FxMonitor;

/// <summary>
/// 现场配置：不用手改 farm.json。连上设备后，扫到的通道（有没有信号、置没置零）列在这里，逐路选法兰、填螺栓号；
/// 每个法兰选标定文件、填额定值；法兰的名字/螺栓数/编号也在这改。保存写回 exe 旁的 farm.json，
/// 找得到大屏程序目录就同时写进大屏的 wwwroot/farm.json（大屏盯着这个文件，保存即生效），标定文件也一起拷过去。
/// </summary>
internal sealed class SiteConfigDialog : Form
{
    /// <summary>主窗体扫到的一路通道：置没置零、信噪比。</summary>
    public sealed record Detected(ushort Ch, bool Zeroed, double? Snr, bool Weak);

    private const string NoUcf = "（无）";
    private const string NoSpec = "（无）";
    private const string SameSpec = "同法兰";
    private const string FlangeNone = "（不接入）";
    private const string FlangeGuard = "巡检位（只判有没有探头）";

    private readonly string _ownDir;
    private readonly bool _connected;
    private readonly IReadOnlyDictionary<ushort, Detected> _detected;
    private SiteConfig _cfg;
    private string _sourcePath = string.Empty;
    private string _copyNote = string.Empty;

    private readonly NumericUpDown _turbine = new() { Minimum = 1, Maximum = 999, Width = 70 };
    private readonly TextBox _endpoint = new() { Width = 170 };
    private readonly TextBox _dashDir = new() { Width = 520 };
    private readonly Button _btnBrowse = new() { Text = "浏览…", AutoSize = true };
    private readonly Label _lblDash = new() { AutoSize = true, ForeColor = Color.DimGray };
    private readonly Label _lblSource = new() { AutoSize = true, ForeColor = Color.DimGray };
    private readonly DataGridView _flanges = NewGrid();
    private readonly DataGridView _wires = NewGrid();
    private readonly NumericUpDown _addCh = new() { Minimum = 1, Maximum = 48, Width = 60 };
    private readonly Label _status = new() { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(640, 0) };
    private readonly Button _btnSave = new() { Text = "保存并应用", Width = 120, Height = 34, Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold) };
    private readonly Button _btnExport = new() { Text = "另存为…", Width = 96, Height = 34 };
    private readonly Button _btnCancel = new() { Text = "取消", Width = 84, Height = 34 };

    /// <summary>保存后大屏目录（用户浏览选的），主窗体记到 settings.json。</summary>
    public string? DashboardDir => _dashDir.Text.Trim().Length > 0 ? _dashDir.Text.Trim() : null;

    /// <summary>保存后的摘要，主窗体写操作记录、提示用。</summary>
    public string Summary { get; private set; } = string.Empty;

    public SiteConfigDialog(string endpoint, bool connected, IReadOnlyList<Detected> detected, string ownDir, string? dashboardDir)
    {
        _ownDir = ownDir;
        _connected = connected;
        _detected = detected.ToDictionary(d => d.Ch);

        Text = "现场配置";
        Font = new Font("Microsoft YaHei UI", 9.5f);
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(980, 720);
        MinimumSize = new Size(860, 600);
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = true;
        MinimizeBox = false;
        ShowInTaskbar = false;

        _dashDir.Text = dashboardDir ?? DetectDashboardDir(ownDir) ?? string.Empty;
        _cfg = LoadConfig(endpoint);

        BuildUi();
        FillFromConfig();

        _btnBrowse.Click += (_, _) => BrowseDashboard();
        _dashDir.TextChanged += (_, _) => DescribeDashboard();
        _btnSave.Click += (_, _) => Save();
        _btnExport.Click += (_, _) => Export();
        _btnCancel.Click += (_, _) => Close();
    }

    // ── 读配置 ───────────────────────────────────────────────────────────────

    /// <summary>大屏那份是业主看到的，优先读它；没有就读 exe 旁的；都没有就是一份空配置。</summary>
    private SiteConfig LoadConfig(string endpoint)
    {
        var candidates = new List<string>();
        if (DashboardFarmPath() is { } dp) candidates.Add(dp);
        candidates.Add(Path.Combine(_ownDir, "farm.json"));
        candidates.Add(Path.Combine(_ownDir, "wwwroot", "farm.json"));
        foreach (var p in candidates)
        {
            if (!File.Exists(p)) continue;
            try
            {
                var cfg = SiteConfig.Load(File.ReadAllText(p), endpoint);
                _sourcePath = p;
                _copyNote = DifferNote(p, candidates);
                return cfg;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"{p}\n读不了：{ex.Message}\n\n请先修好这个文件再来配置，以免把它覆盖掉。", "现场配置",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                throw;
            }
        }

        _sourcePath = string.Empty;
        return SiteConfig.Load(null, endpoint);
    }

    /// <summary>选中的这份和旁边另一份内容不同就说一声。保存会按编辑结果把写到的那几份改成一样。</summary>
    private static string DifferNote(string chosen, IEnumerable<string> candidates)
    {
        string chosenText;
        try
        {
            chosenText = File.ReadAllText(chosen);
        }
        catch (Exception)
        {
            return string.Empty;
        }

        foreach (var p in candidates)
        {
            if (!File.Exists(p)) continue;
            if (string.Equals(Path.GetFullPath(p), Path.GetFullPath(chosen), StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                if (!string.Equals(File.ReadAllText(p), chosenText, StringComparison.Ordinal))
                    return $"另有一份 {p} 内容和这份不一致";
            }
            catch (Exception)
            {
                // 读不了的那份留给打开它的人
            }
        }

        return string.Empty;
    }

    private string? DashboardFarmPath()
    {
        var dir = DashboardDir;
        return dir is null ? null : Path.Combine(dir, "wwwroot", "farm.json");
    }

    /// <summary>
    /// 大屏就在这台机器上跑的话，直接找到它的目录，保存时顺手写过去；没在跑但 exe 就在自己旁边（两个程序发布在同一个文件夹）也算找到。
    /// </summary>
    private static string? DetectDashboardDir(string ownDir)
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("BoltPreload.Monitor"))
            {
                try
                {
                    var path = p.MainModule?.FileName;
                    if (path is not null) return Path.GetDirectoryName(path);
                }
                catch
                {
                    // 拿不到路径（权限/位数不同）就算了
                }
            }
        }
        catch
        {
        }

        return File.Exists(Path.Combine(ownDir, "BoltPreload.Monitor.exe")) ? ownDir : null;
    }

    private bool SameFolderAsDashboard => DashboardDir is { } d
        && string.Equals(Path.GetFullPath(d).TrimEnd('\\', '/'), Path.GetFullPath(_ownDir).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    // ── 界面 ─────────────────────────────────────────────────────────────────

    private static DataGridView NewGrid() => new()
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

    private void BuildUi()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(12, 8, 12, 8) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 42));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 58));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // 设备
        var head = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 6 };
        head.Controls.Add(new Label { Text = "机组号", AutoSize = true, Margin = new Padding(0, 7, 4, 0) }, 0, 0);
        head.Controls.Add(_turbine, 1, 0);
        head.Controls.Add(new Label { Text = "设备地址", AutoSize = true, Margin = new Padding(18, 7, 4, 0) }, 2, 0);
        head.Controls.Add(_endpoint, 3, 0);
        head.Controls.Add(new Label { Text = "大屏程序目录", AutoSize = true, Margin = new Padding(0, 7, 4, 0) }, 0, 1);
        head.SetColumnSpan(_dashDir, 3);
        head.Controls.Add(_dashDir, 1, 1);
        head.Controls.Add(_btnBrowse, 4, 1);
        head.SetColumnSpan(_lblDash, 5);
        head.Controls.Add(_lblDash, 1, 2);
        head.SetColumnSpan(_lblSource, 5);
        head.Controls.Add(_lblSource, 1, 3);
        root.Controls.Add(head, 0, 0);

        // 法兰
        var flangeBox = new GroupBox { Text = "法兰（全场统一的结构；标定文件和额定值是这台设备上的）", Dock = DockStyle.Fill, Padding = new Padding(8, 4, 8, 6) };
        var flangeBar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 34, WrapContents = false, Padding = new Padding(0, 4, 0, 0) };
        var btnAddFlange = new Button { Text = "添加法兰", AutoSize = true };
        var btnDelFlange = new Button { Text = "删除选中法兰", AutoSize = true };
        var btnLib = new Button { Text = "规格库…", AutoSize = true, Margin = new Padding(18, 3, 3, 3) };
        btnAddFlange.Click += (_, _) => AddFlange();
        btnDelFlange.Click += (_, _) => DeleteFlange();
        btnLib.Click += (_, _) => OpenLibrary();
        flangeBar.Controls.Add(btnAddFlange);
        flangeBar.Controls.Add(btnDelFlange);
        flangeBar.Controls.Add(btnLib);
        var libHint = new Label
        {
            Text = "试件规格：一种螺栓在规格库里填一次（直径/螺距/夹持/材料），法兰选默认，个别通道装的不是这种螺栓就在接线表里单独选。",
            AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(10, 7, 0, 0),
        };
        flangeBar.Controls.Add(libHint);
        flangeBox.Controls.Add(_flanges);
        flangeBox.Controls.Add(flangeBar);
        root.Controls.Add(flangeBox, 0, 1);

        _flanges.Columns.Add(new DataGridViewTextBoxColumn { Name = "name", HeaderText = "法兰名称", FillWeight = 30 });
        _flanges.Columns.Add(new DataGridViewTextBoxColumn { Name = "bolts", HeaderText = "螺栓数", FillWeight = 12 });
        _flanges.Columns.Add(new DataGridViewTextBoxColumn { Name = "top", HeaderText = "正上方是几号", FillWeight = 14 });
        _flanges.Columns.Add(new DataGridViewCheckBoxColumn { Name = "cw", HeaderText = "顺时针编号", FillWeight = 12 });
        _flanges.Columns.Add(new DataGridViewComboBoxColumn { Name = "spec", HeaderText = "试件规格", FillWeight = 26, FlatStyle = FlatStyle.Flat, ToolTipText = "这个法兰默认装的哪种螺栓（规格库）。没有标定文件时按它的理论斜率算力，额定也可以从它带出来" });
        _flanges.Columns.Add(new DataGridViewComboBoxColumn { Name = "ucf", HeaderText = "标定文件", FillWeight = 30, FlatStyle = FlatStyle.Flat });
        _flanges.Columns.Add(new DataGridViewTextBoxColumn { Name = "rated", HeaderText = "额定预紧力 kN", FillWeight = 16 });
        _flanges.CellValueChanged += (_, e) =>
        {
            if (e.RowIndex < 0) return;
            if (_flanges.Columns[e.ColumnIndex].Name == "name") RefreshFlangeChoices();
            if (_flanges.Columns[e.ColumnIndex].Name == "ucf") DescribeUcf(e.RowIndex);
        };
        _flanges.CurrentCellDirtyStateChanged += (_, _) => { if (_flanges.IsCurrentCellDirty) _flanges.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        _flanges.DataError += (_, e) => e.ThrowException = false;

        // 接线
        var wireBox = new GroupBox { Text = "通道接线（哪路探头在哪个法兰的几号螺栓上）", Dock = DockStyle.Fill, Padding = new Padding(8, 4, 8, 6) };
        var wireBar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 34, WrapContents = false, Padding = new Padding(0, 4, 0, 0) };
        wireBar.Controls.Add(new Label { Text = "没扫到的通道手工加：CH", AutoSize = true, Margin = new Padding(0, 7, 2, 0) });
        wireBar.Controls.Add(_addCh);
        var btnAddCh = new Button { Text = "添加通道", AutoSize = true, Margin = new Padding(6, 0, 0, 0) };
        btnAddCh.Click += (_, _) => AddWire((ushort)_addCh.Value);
        wireBar.Controls.Add(btnAddCh);
        var hint = new Label
        {
            Text = "螺栓号不清楚可以先空着（图上不写编号）。现场对号：脱开一颗探头，大屏上对应通道的牌子会闪。",
            AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(18, 7, 0, 0),
        };
        wireBar.Controls.Add(hint);
        wireBox.Controls.Add(_wires);
        wireBox.Controls.Add(wireBar);
        root.Controls.Add(wireBox, 0, 2);

        _wires.Columns.Add(new DataGridViewTextBoxColumn { Name = "ch", HeaderText = "通道", FillWeight = 10, ReadOnly = true });
        _wires.Columns.Add(new DataGridViewTextBoxColumn { Name = "probe", HeaderText = "探头（本次扫描）", FillWeight = 26, ReadOnly = true });
        _wires.Columns.Add(new DataGridViewComboBoxColumn { Name = "flange", HeaderText = "接在哪个法兰", FillWeight = 40, FlatStyle = FlatStyle.Flat });
        _wires.Columns.Add(new DataGridViewTextBoxColumn { Name = "bolt", HeaderText = "螺栓号", FillWeight = 10 });
        _wires.Columns.Add(new DataGridViewComboBoxColumn { Name = "spec", HeaderText = "规格", FillWeight = 22, FlatStyle = FlatStyle.Flat, ToolTipText = "这一路装的不是法兰默认那种螺栓时单独选（实验室一路一种）。选了就按它自己的标定/理论/额定算" });
        // 温度借用（厂家 UseTempChannel）不在这张表里：选中一路在右侧「计算参数」页填温度通道，同一个 tempFrom
        _wires.CurrentCellDirtyStateChanged += (_, _) => { if (_wires.IsCurrentCellDirty) _wires.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        _wires.DataError += (_, e) => e.ThrowException = false;

        // 底栏
        var foot = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 4, Padding = new Padding(0, 6, 0, 0) };
        foot.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _status.Margin = new Padding(0, 8, 8, 0);
        foot.Controls.Add(_status, 0, 0);
        foot.Controls.Add(_btnExport, 1, 0);
        foot.Controls.Add(_btnSave, 2, 0);
        foot.Controls.Add(_btnCancel, 3, 0);
        root.Controls.Add(foot, 0, 3);

        Controls.Add(root);
        AcceptButton = null;
        CancelButton = _btnCancel;
    }

    private void FillFromConfig()
    {
        _turbine.Value = Math.Clamp(_cfg.Turbine, 1, 999);
        _endpoint.Text = _cfg.Endpoint;
        DescribeDashboard();
        _lblSource.Text = _sourcePath.Length > 0
            ? $"读取自 {_sourcePath}{(_cfg.IsExisting ? "" : "（这台设备还没配过，按新设备添加）")}"
              + (_copyNote.Length > 0 ? "。" + _copyNote : "")
            : "没有找到 farm.json，这是一份新配置";

        var ucfCol = (DataGridViewComboBoxColumn)_flanges.Columns["ucf"]!;
        ucfCol.Items.Clear();
        ucfCol.Items.Add(NoUcf);
        foreach (var f in UcfFiles()) ucfCol.Items.Add(f);
        RefreshSpecChoices();

        _flanges.Rows.Clear();
        foreach (var f in _cfg.Flanges)
        {
            var ucf = f.Ucf is { Length: > 0 } u ? u : NoUcf;
            if (ucf != NoUcf && !ucfCol.Items.Contains(ucf)) ucfCol.Items.Add(ucf);   // 配置里写了但目录里没有：也列出来，校验时再说
            var spec = f.SpecName is { Length: > 0 } sn && _cfg.Library.Find(sn) is { } found ? found.Name : NoSpec;
            _flanges.Rows.Add(f.Name, f.BoltCount.ToString(), f.TopBolt.ToString(), f.Clockwise, spec, ucf, f.Rated > 0 ? f.Rated.ToString("0.##") : "");
        }

        RefreshFlangeChoices();

        // 接线表：配置里有的 ∪ 本次扫到的；扫到但没配的先列成"不接入"，让人一眼看到还有哪几路没归属
        var wires = _cfg.Wires.ToList();
        foreach (var d in _detected.Values.OrderBy(d => d.Ch))
        {
            if (wires.All(w => w.Ch != d.Ch)) wires.Add(new SiteConfig.Wire { Ch = d.Ch, FlangeIndex = SiteConfig.Wire.None });
        }

        _wires.Rows.Clear();
        foreach (var w in wires.OrderBy(w => w.Ch)) AddWireRow(w);
        for (var i = 0; i < _flanges.Rows.Count; i++) DescribeUcf(i);
    }

    private IEnumerable<string> UcfFiles()
    {
        var dirs = new List<string> { _ownDir };
        if (DashboardDir is { } d && Directory.Exists(d)) dirs.Add(d);
        return dirs.Where(Directory.Exists)
            .SelectMany(d => Directory.GetFiles(d, "*.ucf").Select(Path.GetFileName))
            .Where(n => n is not null).Select(n => n!)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
    }

    private string FlangeChoice(int index) => index >= 0 && index < _flanges.Rows.Count
        ? $"{index + 1}. {(_flanges.Rows[index].Cells["name"].Value as string ?? "").Trim()}"
        : index == SiteConfig.Wire.Guard ? FlangeGuard : FlangeNone;

    private int FlangeIndexOf(string? choice)
    {
        if (choice == FlangeGuard) return SiteConfig.Wire.Guard;
        if (string.IsNullOrEmpty(choice) || choice == FlangeNone) return SiteConfig.Wire.None;
        var dot = choice.IndexOf('.');
        return dot > 0 && int.TryParse(choice[..dot], out var n) ? n - 1 : SiteConfig.Wire.None;
    }

    /// <summary>法兰改了名/增删了行，接线表下拉框里的选项跟着变，每行按法兰下标保住原选择。</summary>
    private void RefreshFlangeChoices()
    {
        var col = (DataGridViewComboBoxColumn)_wires.Columns["flange"]!;
        var current = _wires.Rows.Cast<DataGridViewRow>().Select(r => FlangeIndexOf(r.Cells["flange"].Value as string)).ToList();
        col.Items.Clear();
        col.Items.Add(FlangeNone);
        col.Items.Add(FlangeGuard);
        for (var i = 0; i < _flanges.Rows.Count; i++) col.Items.Add(FlangeChoice(i));
        for (var r = 0; r < _wires.Rows.Count; r++)
        {
            var idx = current[r];
            if (idx >= _flanges.Rows.Count) idx = SiteConfig.Wire.None;
            _wires.Rows[r].Cells["flange"].Value = FlangeChoice(idx);
        }
    }

    /// <summary>规格库变了（改名/增删）：两张表的规格下拉框选项跟着刷，每行保住原选择。</summary>
    private void RefreshSpecChoices()
    {
        var names = _cfg.Library.Specs.Select(s => s.Name.Trim()).Where(n => n.Length > 0).ToList();
        var fcol = (DataGridViewComboBoxColumn)_flanges.Columns["spec"]!;
        var fkeep = _flanges.Rows.Cast<DataGridViewRow>().Select(r => r.Cells["spec"].Value as string).ToList();
        fcol.Items.Clear();
        fcol.Items.Add(NoSpec);
        foreach (var n in names) fcol.Items.Add(n);
        for (var r = 0; r < _flanges.Rows.Count; r++)
            _flanges.Rows[r].Cells["spec"].Value = fkeep[r] is { } k && fcol.Items.Contains(k) ? k : NoSpec;

        var wcol = (DataGridViewComboBoxColumn)_wires.Columns["spec"]!;
        var wkeep = _wires.Rows.Cast<DataGridViewRow>().Select(r => r.Cells["spec"].Value as string).ToList();
        wcol.Items.Clear();
        wcol.Items.Add(SameSpec);
        foreach (var n in names) wcol.Items.Add(n);
        for (var r = 0; r < _wires.Rows.Count; r++)
            _wires.Rows[r].Cells["spec"].Value = wkeep[r] is { } k && wcol.Items.Contains(k) ? k : SameSpec;
    }

    /// <summary>规格库对话框：编辑库本身；改了名把两张表里的引用一起改。</summary>
    private void OpenLibrary()
    {
        var usage = SpecUsage();
        using var dlg = new SpecLibraryDialog(_cfg.Library, UcfFiles().ToList(), usage);
        var renames = new List<(string From, string To)>();
        dlg.Renamed += (from, to) => renames.Add((from, to));
        dlg.ShowDialog(this);
        // 取消也可能已经改了内存里的库（对话框直接编辑 _cfg.Library）；按现状刷新下拉框，改名的引用跟着走
        foreach (var (from, to) in renames)
        {
            foreach (DataGridViewRow r in _flanges.Rows)
                if (string.Equals(r.Cells["spec"].Value as string, from, StringComparison.OrdinalIgnoreCase)) r.Cells["spec"].Value = to;
            foreach (DataGridViewRow r in _wires.Rows)
                if (string.Equals(r.Cells["spec"].Value as string, from, StringComparison.OrdinalIgnoreCase)) r.Cells["spec"].Value = to;
        }

        RefreshSpecChoices();
        _status.Text = $"规格库 {_cfg.Library.Specs.Count} 条，材料 {_cfg.Library.Materials.Count} 条";
        _status.ForeColor = Color.DimGray;
    }

    /// <summary>每条规格被哪些法兰/通道引用着（删规格前拦一下）。</summary>
    private Dictionary<string, string> SpecUsage()
    {
        var usage = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        void Add(string? name, string who)
        {
            if (string.IsNullOrEmpty(name) || name == NoSpec || name == SameSpec) return;
            if (!usage.TryGetValue(name, out var list)) usage[name] = list = new List<string>();
            list.Add(who);
        }

        foreach (DataGridViewRow r in _flanges.Rows) Add(r.Cells["spec"].Value as string, $"法兰「{(r.Cells["name"].Value as string ?? "").Trim()}」");
        foreach (DataGridViewRow r in _wires.Rows) Add(r.Cells["spec"].Value as string, r.Cells["ch"].Value as string ?? "");
        return usage.ToDictionary(kv => kv.Key, kv => string.Join("、", kv.Value), StringComparer.OrdinalIgnoreCase);
    }

    private void AddWireRow(SiteConfig.Wire w)
    {
        var probe = _detected.TryGetValue(w.Ch, out var d)
            ? (d.Zeroed ? "有信号 · 已置零" : "有信号 · 未置零") + (d.Weak ? "（信号弱）" : "") + (d.Snr is { } s ? $"  {s:0} dB" : "")
            : _connected ? "本次扫描没检测到" : "—";
        // 按通道号插到该在的位置（按字符串排会把 CH10 排到 CH2 前面）
        var at = _wires.Rows.Cast<DataGridViewRow>().TakeWhile(r => (ushort)r.Tag! < w.Ch).Count();
        var specCol = (DataGridViewComboBoxColumn)_wires.Columns["spec"]!;
        if (!specCol.Items.Contains(SameSpec)) specCol.Items.Add(SameSpec);
        var specChoice = w.SpecName is { Length: > 0 } sn && _cfg.Library.Find(sn) is { } found ? found.Name : SameSpec;
        if (!specCol.Items.Contains(specChoice)) specCol.Items.Add(specChoice);
        var newRow = new DataGridViewRow();
        newRow.CreateCells(_wires, $"CH{w.Ch}", probe, FlangeChoice(w.FlangeIndex), w.Bolt?.ToString() ?? "", specChoice);
        _wires.Rows.Insert(at, newRow);
        var row = at;
        _wires.Rows[row].Tag = w.Ch;
        if (_detected.TryGetValue(w.Ch, out var dd) && !dd.Zeroed) _wires.Rows[row].Cells["probe"].Style.ForeColor = Color.DarkOrange;
        if (!_detected.ContainsKey(w.Ch) && _connected) _wires.Rows[row].Cells["probe"].Style.ForeColor = Color.Gray;
    }

    private void AddWire(ushort ch)
    {
        if (_wires.Rows.Cast<DataGridViewRow>().Any(r => (ushort)r.Tag! == ch))
        {
            _status.Text = $"CH{ch} 已经在表里";
            return;
        }

        AddWireRow(new SiteConfig.Wire { Ch = ch });
        _status.Text = $"已添加 CH{ch}，请选它接在哪个法兰";
    }

    // 法兰表格和 _cfg.Flanges 一一对应（同序）：增删都两边一起动，保存时按行号把表格里的值写回同一个对象，
    // 对象里带着的原 JSON（kind、marks、plannedChannels）才能原样保留
    private void AddFlange()
    {
        var f = new SiteConfig.Flange { Name = $"法兰 {_flanges.Rows.Count + 1}", BoltCount = 96, TopBolt = 1, Clockwise = true };
        _cfg.Flanges.Add(f);
        _flanges.Rows.Add(f.Name, f.BoltCount.ToString(), f.TopBolt.ToString(), f.Clockwise, NoSpec, NoUcf, "");
        RefreshFlangeChoices();
    }

    private void DeleteFlange()
    {
        if (_flanges.CurrentRow is not { } row) return;
        var idx = row.Index;
        var used = _wires.Rows.Cast<DataGridViewRow>().Where(r => FlangeIndexOf(r.Cells["flange"].Value as string) == idx).Select(r => r.Cells["ch"].Value).ToList();
        if (used.Count > 0)
        {
            MessageBox.Show(this, $"这个法兰上还接着 {string.Join("、", used)}，先把它们改到别的法兰或“不接入”。", "删除法兰", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // 后面法兰的下标前移一位，接线表里的选择跟着挪
        var remap = _wires.Rows.Cast<DataGridViewRow>().Select(r => FlangeIndexOf(r.Cells["flange"].Value as string)).Select(i => i > idx ? i - 1 : i).ToList();
        _flanges.Rows.RemoveAt(idx);
        _cfg.Flanges.RemoveAt(idx);
        var col = (DataGridViewComboBoxColumn)_wires.Columns["flange"]!;
        col.Items.Clear();
        col.Items.Add(FlangeNone);
        col.Items.Add(FlangeGuard);
        for (var i = 0; i < _flanges.Rows.Count; i++) col.Items.Add(FlangeChoice(i));
        for (var r = 0; r < _wires.Rows.Count; r++) _wires.Rows[r].Cells["flange"].Value = FlangeChoice(remap[r]);
    }

    private void BrowseDashboard()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "选大屏程序（BoltPreload.Monitor.exe）所在的目录，保存时把 farm.json 和标定文件一起写过去",
            UseDescriptionForTitle = true,
            SelectedPath = DashboardDir ?? _ownDir,
        };
        if (dlg.ShowDialog(this) == DialogResult.OK) _dashDir.Text = dlg.SelectedPath;
    }

    private void DescribeDashboard()
    {
        var dir = DashboardDir;
        if (dir is null)
        {
            _lblDash.Text = "没找到大屏程序：保存只写本目录的 farm.json，请把它和标定文件拷到大屏目录（大屏程序目录\\wwwroot\\farm.json）";
            _lblDash.ForeColor = Color.DarkOrange;
            return;
        }

        var exe = Path.Combine(dir, "BoltPreload.Monitor.exe");
        var running = Process.GetProcessesByName("BoltPreload.Monitor").Length > 0;
        if (File.Exists(exe))
        {
            _lblDash.Text = running ? "大屏在这台机器上运行中：保存后它自动重载，页面自动刷新" : "找到大屏程序（未在运行）：保存后写入它的 wwwroot\\farm.json，下次启动生效";
            _lblDash.ForeColor = Color.ForestGreen;
        }
        else
        {
            _lblDash.Text = "这个目录里没有 BoltPreload.Monitor.exe，请重新选";
            _lblDash.ForeColor = Color.Firebrick;
        }
    }

    /// <summary>选了哪份标定文件就把它的斜率亮出来：文件里不含螺栓规格，斜率差几倍一眼能看出选错了。</summary>
    private void DescribeUcf(int row)
    {
        var name = _flanges.Rows[row].Cells["ucf"].Value as string;
        if (string.IsNullOrEmpty(name) || name == NoUcf) return;
        var path = FindUcf(name);
        if (path is null)
        {
            _status.Text = $"{name}：本目录和大屏目录里都没有这个文件";
            _status.ForeColor = Color.Firebrick;
            return;
        }

        try
        {
            var cal = Ucf.Load(path);
            _status.Text = $"{name}：{cal.K:F4} {cal.Unit}/ns，{cal.Points} 点";
            _status.ForeColor = Color.DimGray;
        }
        catch (Exception ex)
        {
            _status.Text = $"{name} 读不了：{ex.Message}";
            _status.ForeColor = Color.Firebrick;
        }
    }

    private string? FindUcf(string name)
    {
        foreach (var dir in new[] { _ownDir, DashboardDir })
        {
            if (dir is null) continue;
            var p = Path.Combine(dir, name);
            if (File.Exists(p)) return p;
        }

        return null;
    }

    // ── 收集与保存 ───────────────────────────────────────────────────────────

    /// <summary>把表格里填的读回模型；填得不像数的地方直接报错，不猜。</summary>
    private List<string> Collect()
    {
        var errors = new List<string>();
        _flanges.EndEdit();
        _wires.EndEdit();

        _cfg.Turbine = (int)_turbine.Value;
        _cfg.Endpoint = _endpoint.Text.Trim();

        // 表格行和 _cfg.Flanges 同序同数（增删时两边一起动）
        for (var i = 0; i < _flanges.Rows.Count && i < _cfg.Flanges.Count; i++)
        {
            var r = _flanges.Rows[i];
            var f = _cfg.Flanges[i];
            var name = (r.Cells["name"].Value as string ?? "").Trim();
            f.Name = name;
            if (!int.TryParse((r.Cells["bolts"].Value as string ?? "").Trim(), out var bolts)) errors.Add($"第 {i + 1} 个法兰的螺栓数不是整数");
            if (!int.TryParse((r.Cells["top"].Value as string ?? "").Trim(), out var top)) errors.Add($"第 {i + 1} 个法兰的“正上方是几号”不是整数");
            f.BoltCount = bolts;
            f.TopBolt = top;
            f.Clockwise = r.Cells["cw"].Value is true;
            var ucf = r.Cells["ucf"].Value as string;
            f.Ucf = string.IsNullOrEmpty(ucf) || ucf == NoUcf ? null : ucf;
            var spec = r.Cells["spec"].Value as string;
            f.SpecName = string.IsNullOrEmpty(spec) || spec == NoSpec ? null : spec;
            var ratedTxt = (r.Cells["rated"].Value as string ?? "").Trim();
            if (ratedTxt.Length == 0) f.Rated = 0;
            else if (decimal.TryParse(ratedTxt, NumberStyles.Number, CultureInfo.InvariantCulture, out var rated)) f.Rated = rated;
            else errors.Add($"法兰「{name}」的额定预紧力不是数");
        }

        var wires = new List<SiteConfig.Wire>();
        foreach (DataGridViewRow r in _wires.Rows)
        {
            var ch = (ushort)r.Tag!;
            var idx = FlangeIndexOf(r.Cells["flange"].Value as string);
            if (idx == SiteConfig.Wire.None) continue;
            var boltTxt = (r.Cells["bolt"].Value as string ?? "").Trim();
            int? bolt = null;
            if (boltTxt.Length > 0)
            {
                if (int.TryParse(boltTxt, out var b)) bolt = b;
                else errors.Add($"CH{ch} 的螺栓号不是整数");
            }

            var specChoice = r.Cells["spec"].Value as string;
            var specName = idx >= 0 && !string.IsNullOrEmpty(specChoice) && specChoice != SameSpec ? specChoice : null;
            wires.Add(new SiteConfig.Wire { Ch = ch, FlangeIndex = idx, Bolt = idx >= 0 ? bolt : null, SpecName = specName });
        }

        _cfg.Wires.Clear();
        _cfg.Wires.AddRange(wires);
        // 温度借用在「计算参数」页改，这里原样保留；接线里已经没有的通道其借用关系一起去掉（被借的一路不在轮询里校验会拦）
        var polled = wires.Select(w => w.Ch).ToHashSet();
        foreach (var ch in _cfg.TempFrom.Keys.Where(ch => !polled.Contains(ch)).ToList()) _cfg.TempFrom.Remove(ch);
        foreach (var ch in _cfg.TempFrom.Where(kv => !polled.Contains(kv.Value)).Select(kv => kv.Key).ToList()) _cfg.TempFrom.Remove(ch);
        return errors;
    }

    private bool Prepare(out string json)
    {
        json = string.Empty;
        var errors = Collect();
        errors.AddRange(_cfg.Validate());
        foreach (var f in _cfg.Flanges.Where(f => f.Ucf is { Length: > 0 }))
        {
            if (FindUcf(f.Ucf!) is null) errors.Add($"法兰「{f.Name}」的标定文件 {f.Ucf} 在本目录和大屏目录里都找不到");
        }

        if (!_cfg.Wires.Any(w => w.FlangeIndex >= 0))
        {
            errors.Add("没有一路通道接在法兰上——至少把一路探头接到某个法兰，大屏才有东西测");
        }

        if (errors.Count > 0)
        {
            MessageBox.Show(this, string.Join("\n", errors.Select(e => "• " + e)), "配置还不能保存", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        json = _cfg.ToJson();
        return true;
    }

    private void Save()
    {
        if (!Prepare(out var json)) return;

        var written = new List<string>();
        try
        {
            // 和大屏同一目录时只写 wwwroot\farm.json。根目录已经有一份就一起改。
            var ownPath = Path.Combine(_ownDir, "farm.json");
            var ownWww = Path.Combine(_ownDir, "wwwroot", "farm.json");
            if (!SameFolderAsDashboard || File.Exists(ownPath))
            {
                WriteAtomic(ownPath, json);
                written.Add(ownPath);
            }

            if (File.Exists(ownWww) || Directory.Exists(Path.GetDirectoryName(ownWww)))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ownWww)!);
                WriteAtomic(ownWww, json);
                written.Add(ownWww);
            }

            if (DashboardDir is { } dash && Directory.Exists(dash))
            {
                var www = Path.Combine(dash, "wwwroot");
                Directory.CreateDirectory(www);
                var dashPath = Path.Combine(www, "farm.json");
                WriteAtomic(dashPath, json);
                written.Add(dashPath);
                // 标定文件也得在大屏 exe 旁，不然它那边是"未标定"。同名文件要覆盖：现场用新标定换掉旧文件时，大屏不能留着旧斜率。
                var ucfNames = _cfg.Flanges.Select(f => f.Ucf)
                    .Concat(_cfg.Library.Specs.Select(s => s.Ucf))
                    .Where(u => u is { Length: > 0 })
                    .Distinct(StringComparer.OrdinalIgnoreCase);
                foreach (var name in ucfNames)
                {
                    var src = FindUcf(name!);
                    if (src is null) continue;
                    CopyCal(src, Path.Combine(dash, name!), written);
                    CopyCal(src, Path.Combine(_ownDir, name!), written);
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"写文件失败：{ex.Message}\n\n已写入：{(written.Count > 0 ? string.Join("\n", written) : "无")}", "现场配置",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var wired = _cfg.Wires.Where(w => w.FlangeIndex >= 0).ToList();
        Summary = $"F{_cfg.Turbine:00} {_cfg.Endpoint}：接线 {string.Join("、", wired.Select(w => $"CH{w.Ch}→{_cfg.Flanges[w.FlangeIndex].Name}{(w.Bolt is { } b ? $"#{b}" : "")}"))}"
                  + string.Concat(_cfg.Flanges.Where(f => f.Ucf is { Length: > 0 } || f.Rated > 0 || f.SpecName is { Length: > 0 })
                      .Select(f => $"；{f.Name} 标定 {f.Ucf ?? "无"} 额定 {(f.Rated > 0 ? f.Rated + " kN" : "未配")}"
                                   + (f.SpecName is { Length: > 0 } ? $" 规格 {f.SpecName}" : "")))
                  + string.Concat(wired.Where(w => w.SpecName is { Length: > 0 }).Select(w => $"；CH{w.Ch} 单独规格 {w.SpecName}"))
                  + (_cfg.TempFrom.Count > 0 ? $"；温度借用 {string.Join("、", _cfg.TempFrom.OrderBy(kv => kv.Key).Select(kv => $"CH{kv.Key}←CH{kv.Value}"))}" : string.Empty)
                  + $"。写入 {string.Join("、", written)}";
        DialogResult = DialogResult.OK;
        Close();
    }

    private void Export()
    {
        if (!Prepare(out var json)) return;
        using var dlg = new SaveFileDialog { Filter = "farm.json|farm.json|JSON|*.json", FileName = "farm.json", Title = "另存 farm.json（拷到大屏目录的 wwwroot 下）" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            WriteAtomic(dlg.FileName, json);
            _status.Text = $"已另存到 {dlg.FileName}";
            _status.ForeColor = Color.ForestGreen;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"写文件失败：{ex.Message}", "另存为", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>源和目标是同一个文件就不动。不是同一个就覆盖，避免大屏留着同名旧标定。</summary>
    private static void CopyCal(string src, string dst, List<string> written)
    {
        if (string.Equals(Path.GetFullPath(src), Path.GetFullPath(dst), StringComparison.OrdinalIgnoreCase)) return;
        File.Copy(src, dst, overwrite: true);
        written.Add(dst);
    }

    /// <summary>先写临时文件再改名：大屏盯着这个文件，不能让它读到半截。</summary>
    private static void WriteAtomic(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content, new System.Text.UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }
}
