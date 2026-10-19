using FxDevice;
using FxSDK.Models;

namespace FxMonitor;

public sealed class MainForm : Form
{
    private sealed class ChLive
    {
        public ushort Ch;
        public ChannelResult? Last;
        public EchoWave? LastWave;
        /// <summary>最后一帧到达的时刻（UTC）；超过 StaleAfter 没来帧就当这一路没数据，不再挂着旧数。</summary>
        public DateTime LastAt;
        public readonly Queue<decimal> Dt = new();
        // 显示平均用（厂家 TimeAvg 那一套，只影响界面，不影响落库/判定口径）
        public readonly Queue<decimal> Echo = new();
        public readonly Queue<decimal> Temp = new();
        public Dictionary<string, string> Cfg = new();
        public ListViewItem? Row;
        public double? Snr;

        /// <summary>这一帧的状态代码（ok / win / fault:…）。连续记录存这个，不存界面上会变的「无数据 N 秒」。</summary>
        public string StatusCode = "";
    }

    private const int ChannelCount = 48;

    private readonly Dictionary<ushort, ChLive> _channels = new();

    /// <summary>不是本程序在轮询的通道。串口服务器会把回包发给每一路 TCP，这些帧不建行。</summary>
    private readonly Dictionary<ushort, DateTime> _foreign = new();

    private DeviceEngine? _engine;
    private CancellationTokenSource _cts = new();
    private bool _connected;
    private bool _closing;
    private ushort _selected;

    /// <summary>
    /// 接线、标定和口径，和大屏共用 farm.json 与 CalPlan。
    /// 没有 farm.json 时用程序目录的 .ucf，按自由置零口径显示 k·Δt+b；没有标定文件就不显示轴向力。
    /// </summary>
    private CalPlan _plan = CalPlan.Load(null, AppContext.BaseDirectory, _ => { });

    private readonly TextBox _ipBox = new() { Width = 150, Text = "192.168.0.7:23", BorderStyle = BorderStyle.FixedSingle };
    private readonly Button _btnConnect = new() { Text = "连接", Width = 88, Height = 30 };
    private readonly Button _btnScan = new() { Text = "扫描通道", Width = 88, Height = 30, Enabled = false };
    private readonly Button _btnFind = new() { Text = "找回波窗", Width = 88, Height = 30, Enabled = false };
    private readonly ToolTip _tip = new() { AutoPopDelay = 20000, InitialDelay = 400, ReshowDelay = 200 };
    private bool _scanning;
    private List<ushort> _unheard = new();
    private List<string> _weakSkipped = new();
    private List<ushort> _wiredAbsent = new();
    private readonly Label _led = new()
    {
        AutoSize = true, Text = "●", Font = new Font("Microsoft YaHei UI", 11f),
        ForeColor = Ui.Muted, Margin = new Padding(8, 5, 2, 0),
    };
    private readonly Label _lblState = new() { AutoSize = true, Text = "未连接", ForeColor = Ui.Muted };
    private readonly Button _btnParams = new() { Text = "参数下发...", AutoSize = true, MinimumSize = new Size(92, 30), Enabled = false };
    private readonly Button _btnZero = new() { Text = "通道置零...", AutoSize = true, MinimumSize = new Size(92, 30), Enabled = false };
    // 现场配置不要求连着设备：没连也能改法兰/标定/额定；连着时扫到的通道会列出来供逐路接线
    private readonly Button _btnConfig = new() { Text = "现场配置...", AutoSize = true, MinimumSize = new Size(92, 30) };
    // 右侧三页，照厂家：试件规格 / 硬件调节 / 计算参数；页签上方一行小结（厂家：通道 · 温度补偿 · 结果计算）
    private readonly Panel _rightHost = new() { Dock = DockStyle.Right, Width = 340 };
    private readonly Label _lblSummary = new() { Dock = DockStyle.Top, Height = 26, AutoEllipsis = true, ForeColor = Ui.Muted, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(10, 4, 8, 0), BackColor = Color.White };
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly SpecimenPanel _specimen = new() { Dock = DockStyle.Fill };
    private readonly HardwarePanel _hw = new() { Dock = DockStyle.Fill };
    private readonly CalcPanel _calc = new() { Dock = DockStyle.Fill };
    // 打开业主大屏网页；本机大屏服务没在跑、exe 又在手边（同一个文件夹发布）就先把它拉起来
    private readonly Button _btnDash = new() { Text = "打开大屏", AutoSize = true, MinimumSize = new Size(92, 30) };
    private readonly ComboBox _cmbAvg = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 72, Margin = new Padding(0, 3, 0, 0) };
    private readonly Button _btnWaveReset = new() { Text = "复位", Width = 52, Height = 26 };
    private readonly Button _btnWaveIn = new() { Text = "+", Width = 28, Height = 26 };
    private readonly Button _btnWaveOut = new() { Text = "−", Width = 28, Height = 26 };
    private readonly Label _lblWaveHint = new() { AutoSize = true, ForeColor = Ui.Muted, Text = "滚轮放大 · 拖拽平移 · 右键框选 · 双击复位" };
    private static readonly FxSDK.Utility.TempTauProfile[] TauChoices =
    {
        FxSDK.Utility.TempTauProfile.Direct, FxSDK.Utility.TempTauProfile.Fast, FxSDK.Utility.TempTauProfile.Balanced,
        FxSDK.Utility.TempTauProfile.Stable, FxSDK.Utility.TempTauProfile.Conservative,
    };
    private static readonly int[] AvgChoices = { 1, 4, 8, 16 };

    /// <summary>卡片和列表按最近几帧平均显示（1 = 逐帧）。厂家软件 TimeAvg=16；我们按通道轮询帧率低，默认 8。</summary>
    private int _avgFrames = 8;
    private readonly ListView _list = new()
    {
        View = View.Details,
        FullRowSelect = true,
        HideSelection = false,
        Dock = DockStyle.Fill,
        Font = new Font("Microsoft YaHei UI", 10.5f),
        MultiSelect = false,
        HeaderStyle = ColumnHeaderStyle.Nonclickable,
        BorderStyle = BorderStyle.None,
        BackColor = Color.White,
        Scrollable = true,
    };
    private int[] _colPref = Array.Empty<int>();
    private bool _fittingCols;
    private bool _placing;

    private SplitContainer _split = null!;

    private readonly Label[] _cardValues = new Label[6];
    // 顺序和叫法照厂家顶栏：轴向力 → 温度 → 回波声时 → 伸长量 → 声时差 → 标准差
    private static readonly string[] CardTitles = { "轴向力 kN", "温度 ℃", "回波声时 ns", "伸长量 μm", "声时差 ns", "标准差 ns" };

    private readonly WavePanel _wave = new() { Dock = DockStyle.Fill, AxisMm = true };
    private readonly CheckBox _chkRef = new() { Text = "参考回波", Checked = true, AutoSize = true };
    private readonly ComboBox _cmbAxis = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 82, Margin = new Padding(0, 3, 8, 0) };
    private readonly Label _lblLog = new() { AutoSize = false, AutoEllipsis = true, ForeColor = Ui.Muted, Text = "" };
    private readonly System.Windows.Forms.Timer _linkTimer = new() { Interval = 2000 };

    // 波形下面一排，照厂家：通道 / 测量模式 / 上一个 下一个 / 停止 / 测回
    private readonly Label _lblChan = new() { AutoSize = true, Text = "通道 --", Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold), Margin = new Padding(4, 7, 8, 0) };
    private readonly ComboBox _cmbMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 76, Margin = new Padding(0, 3, 10, 0), Enabled = false };
    private readonly Button _btnPrev = new() { Text = "◀", Width = 34, Height = 28 };
    private readonly Button _btnNext = new() { Text = "▶", Width = 34, Height = 28 };
    private readonly Button _btnPause = new() { Text = "停止", Width = 56, Height = 28, Enabled = false };
    private readonly Button _btnRecord = new() { Text = "测回", Width = 64, Height = 28, Enabled = false, Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold) };
    private bool _modeUiLock;
    private bool _paused;

    /// <summary>连接还在、但这么久没有任一通道的测量帧，就重新打开监测。正常大约 0.5 秒一帧，短空白会自己恢复。</summary>
    private static readonly TimeSpan StallAfter = TimeSpan.FromSeconds(15);

    /// <summary>
    /// TCP 还在而帧停了，先重发监测命令（两帧，一两秒）；连续这么多次都没帧（约 1 分钟）才重连整条链路。
    /// 重连要在待机下重读每一路参数，无线丢包严重时一次要 30–60 秒。
    /// </summary>
    private const int ReopenAfterStrikes = 3;
    private DateTime _monitorSinceUtc;
    private int _stallStrikes;
    private bool _reviveBusy;

    // 手动测量结果（厂家「测回」记下来的那张表），exe 旁 data\手动测量结果.csv
    private readonly ManualRecords _records = ManualRecords.Load(ManualRecords.PathFor(AppContext.BaseDirectory));
    private readonly Panel _recPanel = new() { Dock = DockStyle.Bottom, Height = RecPanelHeight, Padding = new Padding(10, 0, 10, 4), BackColor = Color.White };
    private readonly FlowLayoutPanel _recExtra = new() { Dock = DockStyle.Top, Height = 32, WrapContents = false, Padding = new Padding(0, 1, 0, 0) };
    private readonly DataGridView _recGrid = new()
    {
        Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = true, BackgroundColor = Color.White, BorderStyle = BorderStyle.None,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None, ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
        EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2, AllowUserToResizeRows = false,
    };
    private readonly ComboBox _cmbRecSpec = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120, Margin = new Padding(0, 3, 10, 0) };
    private readonly DateTimePicker _dtFrom = new() { Format = DateTimePickerFormat.Short, Width = 104, Margin = new Padding(0, 3, 4, 0) };
    private readonly DateTimePicker _dtTo = new() { Format = DateTimePickerFormat.Short, Width = 104, Margin = new Padding(0, 3, 10, 0) };
    private readonly Button _btnRecExport = new() { Text = "导出…", AutoSize = true, Height = 26 };
    private readonly Button _btnRecDelete = new() { Text = "删除选中", AutoSize = true, Height = 26 };
    private readonly Label _lblRecCount = new() { AutoSize = true, ForeColor = Ui.Muted, Margin = new Padding(12, 7, 0, 0) };
    private readonly Button _btnRecToggle = new() { Text = "收起", AutoSize = true, Height = 26 };
    private readonly CheckBox _chkTrace = new() { Text = "连续记录", AutoSize = true, Margin = new Padding(0, 3, 8, 0) };
    private readonly Label _lblTrace = new() { AutoSize = true, ForeColor = Ui.Muted, Text = "已记录 0 帧", Margin = new Padding(0, 6, 12, 0) };
    private readonly Button _btnTraceExport = new() { Text = "导出Excel…", AutoSize = true, Height = 26 };
    private readonly ComboBox _cmbTraceRun = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 168, Margin = new Padding(0, 3, 8, 0) };
    private readonly Button _btnTraceToday = new() { Text = "从今天开始", AutoSize = true, Height = 26 };
    private readonly Button _btnTraceNew = new() { Text = "从现在开始", AutoSize = true, Height = 26 };
    private TraceStore? _trace;
    private bool _traceOn = true;
    private int _traceRetainDays = TraceStore.DefaultRetainDays;
    private bool _traceBroken;
    private string _shownTraceError = "";
    private bool _recCollapsed;
    private bool _recUiLock;
    private const int RecPanelHeight = 188;
    private const int RecPanelCollapsedHeight = 42;

    private sealed class Settings
    {
        public string? Endpoint { get; set; }

        /// <summary>大屏程序目录（现场配置对话框里选的）；本机跑着大屏时会自动找到，不用填。</summary>
        public string? DashboardDir { get; set; }

        /// <summary>大屏网页地址。不填 = 本机 http://localhost:5080/；大屏在别的机器上就填它的地址。</summary>
        public string? DashboardUrl { get; set; }

        /// <summary>卡片/列表显示按最近几帧平均：1 / 4 / 8 / 16。</summary>
        public int? DisplayAvgFrames { get; set; }

        /// <summary>波形横轴按长度 mm（厂家的画法，默认）还是声时 ns。</summary>
        public bool? WaveAxisMm { get; set; }

        /// <summary>底部手动测量结果表是否收起。</summary>
        public bool? RecordsCollapsed { get; set; }

        /// <summary>测量时自动把每一帧读数写入 data\连续记录.db。缺省为开。</summary>
        public bool? ContinuousRecord { get; set; }

        /// <summary>连续记录保留天数。不填是 14。改完要重新打开程序。</summary>
        public int? TraceRetainDays { get; set; }
    }

    private string? _dashboardDir;
    private string? _dashboardUrl;

    /// <summary>当前这条连接连的是哪个地址。连上之后地址栏只读，方案和配置都按这个地址走。</summary>
    private string _connectedEndpoint = string.Empty;

    private string ActiveEndpoint() =>
        !string.IsNullOrWhiteSpace(_connectedEndpoint) ? _connectedEndpoint : _ipBox.Text.Trim();
    private const string LocalDashboardUrl = "http://localhost:5080/";

    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static string SettingsPath => Path.Combine(AppContext.BaseDirectory, "settings.json");

    public MainForm(string? endpoint, bool autoConnect, bool demo = false)
    {
        LoadSettings();
        if (endpoint != null) _ipBox.Text = endpoint;

        Text = $"螺栓预紧力监视器 v{Application.ProductVersion}";
        Font = new Font("Microsoft YaHei UI", 9f);
        BackColor = Ui.Window;
        DoubleBuffered = true;
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(1440, 860);
        MinimumSize = new Size(1180, 720);
        StartPosition = FormStartPosition.CenterScreen;

        BuildUi();
        BuildRecordsUi();

        _btnConnect.Click += async (_, _) => await ToggleAsync();
        _list.SelectedIndexChanged += (_, _) =>
        {
            if (_list.SelectedItems.Count > 0 && _list.SelectedItems[0].Tag is ushort ch)
            {
                if (_selected == ch) return;
                if (_specimen.Dirty && _specimen.Channel != 0
                    && MessageBox.Show(this, $"CH{_specimen.Channel} 的试件规格改了还没保存，切换通道就丢了。继续？", "试件规格",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                {
                    // 留在原来那一路
                    var keep = _list.Items.Cast<ListViewItem>().FirstOrDefault(i => i.Tag is ushort t && t == _selected);
                    if (keep != null) { keep.Selected = true; keep.Focused = true; }
                    return;
                }

                _wave.ResetView();
                _selected = ch;
                UpdateParams();
                RefreshSelected();
                RefreshSpecimenPanel(force: true);
            }
        };
        _specimen.SaveRequested += async () => await SaveSpecimenAsync();
        _specimen.OpenConfigRequested += () => ShowConfigDialog();
        _specimen.OpenMaterialsRequested += () => EditMaterials();
        _hw.ApplyRequested += async () => await ApplyHardwareAsync();
        _calc.TempChannelChanged += (ch, from) => SetTempChannel(ch, from);
        _calc.TauChanged += i => ApplyTau(TauChoices[i]);
        // 换档记操作日志；自定义系数一格格拨动不记（和原来工具栏上一样）
        _calc.TcChanged += (mode, coef) => ApplyTc(mode, coef, log: mode != _plan.TcMode);
        _calc.UseTheoryRequested += () => BindUcf(_calc.Channel, null);
        _calc.BrowseUcfRequested += () => BrowseUcf(_calc.Channel);
        _chkRef.CheckedChanged += (_, _) =>
        {
            _wave.ShowRef = _chkRef.Checked;
            _wave.Invalidate();
        };
        _btnParams.Click += (_, _) => ShowParamsDialog();
        _btnZero.Click += (_, _) => ShowZeroDialog();
        _btnConfig.Click += (_, _) => ShowConfigDialog();
        _btnDash.Click += async (_, _) => await OpenDashboardAsync();
        _btnScan.Click += async (_, _) => await RescanAsync();
        _btnFind.Click += async (_, _) => await ShowEchoWindowDialogAsync();
        foreach (var n in AvgChoices) _cmbAvg.Items.Add(n == 1 ? "不平均" : $"{n} 帧");
        _cmbAvg.SelectedIndex = Math.Max(0, Array.IndexOf(AvgChoices, _avgFrames));
        _cmbAvg.SelectedIndexChanged += (_, _) => OnAvgChanged();
        _btnWaveReset.Click += (_, _) => _wave.ResetView();
        _btnWaveIn.Click += (_, _) => _wave.ZoomAroundCenter(0.8);
        _btnWaveOut.Click += (_, _) => _wave.ZoomAroundCenter(1.25);
        _wave.ViewChanged += () => _lblWaveHint.Text = _wave.HasCustomView
            ? "已缩放，点复位或双击回到全貌"
            : "滚轮放大 · 拖拽平移 · 右键框选 · 双击复位";
        _cmbAxis.Items.AddRange(new object[] { "长度 mm", "声时 ns" });
        _cmbAxis.SelectedIndex = _wave.AxisMm ? 0 : 1;
        _cmbAxis.SelectedIndexChanged += (_, _) =>
        {
            _wave.AxisMm = _cmbAxis.SelectedIndex == 0;
            _wave.Invalidate();
            SaveSettings();
        };
        _cmbMode.Items.AddRange(new object[] { "自动", "位置", "阈值" });
        _cmbMode.SelectedIndexChanged += async (_, _) => { if (!_modeUiLock) await ChangeMeasModeAsync(); };
        _btnPrev.Click += (_, _) => SelectAdjacent(-1);
        _btnNext.Click += (_, _) => SelectAdjacent(1);
        _btnPause.Click += async (_, _) => await TogglePauseAsync();
        _btnRecord.Click += (_, _) => RecordCurrent();
        _cmbRecSpec.SelectedIndexChanged += (_, _) => { if (!_recUiLock) RefreshRecordGrid(); };
        _dtFrom.ValueChanged += (_, _) => { if (!_recUiLock) RefreshRecordGrid(); };
        _dtTo.ValueChanged += (_, _) => { if (!_recUiLock) RefreshRecordGrid(); };
        _btnRecExport.Click += (_, _) => ExportRecords();
        _btnRecDelete.Click += (_, _) => DeleteSelectedRecords();
        _btnRecToggle.Click += (_, _) => SetRecordsCollapsed(!_recCollapsed, save: true);
        _recGrid.CellEndEdit += (_, e) => OnRecordNoteEdited(e.RowIndex);
        _chkTrace.CheckedChanged += (_, _) =>
        {
            if (_traceBroken) return;
            _traceOn = _chkTrace.Checked;
            SaveSettings();
            UpdateTraceLabel();
            OpsLog.Append(_traceOn ? "打开连续记录" : "关闭连续记录");
        };
        _btnTraceExport.Click += async (_, _) => await ExportTraceExcelAsync();
        _btnTraceToday.Click += (_, _) => StartTraceToday();
        _btnTraceNew.Click += (_, _) => StartTraceSeparate();
        TryOpenTrace();
        LoadRecordsUi();

        LoadPlan();
        _linkTimer.Tick += (_, _) =>
        {
            UpdateTraceLabel();
            if (_connected && _engine != null && !_engine.IsConnected && !_reviveBusy)
            {
                _lblState.Text = "连接已断开：" + (_engine.LastDisconnect ?? "未知原因");
                ColorState(Ui.Danger);
            }

            // 帧停了就没有 Absorb。靠这个定时器把超时的行标成无数据。
            // 停按钮灰着 = 正在扫波/改参数/开着置零或参数对话框，流是故意停的，不要标无数据，也不要自动重连。
            if (!_connected || _scanning || !_btnPause.Enabled) return;
            foreach (var c in _channels.Values)
            {
                if (IsStale(c)) UpdateRow(c);
            }

            if (_channels.TryGetValue(_selected, out var sel) && IsStale(sel)) RefreshSelected();
            MaybeReviveStalled();
        };
        _linkTimer.Start();

        if (autoConnect) Shown += async (_, _) => await ToggleAsync();
        if (demo) FillDemoWave();
    }

    private void FillDemoWave()
    {
        const int n = 600;
        var live = new short[n];
        var reff = new short[n];
        for (var i = 0; i < n; i++)
        {
            var t = (i - 300) / 60.0;
            var env = Math.Exp(-t * t / 8);
            live[i] = (short)(env * 5200 * Math.Sin(i * 0.55));
            reff[i] = (short)(env * 5200 * Math.Sin(i * 0.55 + 0.7));
        }

        _wave.Live = live;
        _wave.LiveStart = 24000;
        _wave.LiveClock = 10;
        _wave.Ref = reff;
        _wave.RefStart = 24000;
        _wave.RefClock = 10;
        _wave.Marker = 27020m;
        _lblState.Text = "演示模式";
        ColorState(Ui.Muted);
    }

    private static readonly string[] TauNames = TauChoices.Select(CalPlan.TempTauName).ToArray();

    /// <summary>「计算参数」页和页签上方小结跟着当前通道 + 全局口径刷新（不触发写入）。</summary>
    private void SyncTcUi()
    {
        var ch = _selected;
        var tempFrom = ch != 0 && _plan.TempFrom.TryGetValue(ch, out var f) ? f : (ushort)0;
        var cal = ch != 0 ? _plan.CalFor(ch) : null;
        var usesFile = cal is { Points: > 0 };
        var file = usesFile ? cal!.FileName : string.Empty;
        var source = ch == 0 ? string.Empty : $"来源：{_plan.CalSourceFor(ch)}" + (cal is { } c ? $"　斜率 {c.K:F4} kN/ns" : string.Empty);
        _calc.ShowCalc(ch, tempFrom, TauNames, Math.Max(0, Array.IndexOf(TauChoices, _plan.TempTau)), _plan.TcMode, _plan.TempCoefficient, usesFile, file, source);
        var tc = _plan.TcMode == 0 ? "不使用" : _plan.TcMode == 2 ? "自定义系数" : "材料系数";
        var calc = cal is null ? "无" : usesFile ? "标定文件" : "材料系数";
        _lblSummary.Text = ch == 0
            ? $"温度补偿 {tc}"
            : $"通道 CH{ch}　温度补偿 {tc}　结果计算 {calc}";
        _lblSummary.ForeColor = ch != 0 && cal is null ? Ui.Warn : Ui.Muted;
    }

    private void ApplyTau(FxSDK.Utility.TempTauProfile tau)
    {
        if (tau == _plan.TempTau) return;
        _plan = _plan.WithTempTau(tau);
        _engine?.SetTempTau(tau);
        foreach (var c in _channels.Values) { c.Dt.Clear(); c.Temp.Clear(); }
        var paths = FarmPaths();
        if (paths.Count == 0)
        {
            _lblLog.Text = "没有 farm.json，温度平滑只对当前窗口有效，重启后恢复默认";
        }
        else
        {
            try
            {
                foreach (var p in paths) SiteConfig.PatchTempTau(p, CalPlan.TempTauKey(tau));
                _lblLog.Text = $"温度平滑：{CalPlan.TempTauName(tau)}";
            }
            catch (Exception ex)
            {
                _lblLog.Text = "温度平滑写入 farm.json 失败：" + ex.Message;
            }
        }

        OpsLog.Append($"温度平滑改为 {CalPlan.TempTauName(tau)}");
    }

    private void OnAvgChanged()
    {
        if (_cmbAvg.SelectedIndex < 0) return;
        _avgFrames = AvgChoices[_cmbAvg.SelectedIndex];
        SaveSettings();
        foreach (var c in _channels.Values) UpdateRow(c);
        if (_selected != 0) RefreshSelected();
    }

    /// <summary>
    /// 显示用的数：按最近 _avgFrames 帧平均的声时差 / 回波声时 / 温度。1 帧就是当前帧。
    /// 只在这一处平均，列表和卡片同一个数；健康判定也用它（更稳，不会因末位抖动来回跳状态词）。
    /// </summary>
    private (decimal? Dt, decimal? Echo, decimal? Temp) Displayed(ChLive c)
    {
        var dt = ShownDelta(c);
        var echo = c.Last?.EchoTime;
        var temp = c.Last?.SpTemp ?? c.Last?.RawTemp;
        if (_avgFrames <= 1) return (dt, echo, temp);
        return (Avg(c.Dt, dt), Avg(c.Echo, echo), Avg(c.Temp, temp));

        decimal? Avg(Queue<decimal> q, decimal? fallback)
        {
            if (q.Count == 0) return fallback;
            var take = Math.Min(_avgFrames, q.Count);
            return q.Skip(q.Count - take).Average();
        }
    }

    private void ApplyTc(ushort mode, decimal? coef, bool log)
    {
        if (mode == _plan.TcMode && coef == _plan.TempCoefficient) return;
        _plan = _plan.WithTc(mode, coef);
        _engine?.SetTempComp(mode, coef);
        SyncTcUi();
        // 老帧的声时差是按旧口径算的，不能和新口径的一起平均
        foreach (var c in _channels.Values) c.Dt.Clear();
        foreach (var c in _channels.Values) UpdateRow(c);
        if (_selected != 0) RefreshSelected();
        SaveTcToFarm(mode, coef);
        if (log) OpsLog.Append($"温度补偿改为 {_plan.TcText}");
        _lblLog.Text = $"温度补偿：{_plan.TcText}";
    }

    /// <summary>
    /// 需要同步修改的 farm.json：exe 旁 wwwroot 里的、exe 旁的、大屏目录里的，不往上级目录找。
    /// 从 bin 里运行（开发）时，同时写仓库里的 src\BoltPreload.Monitor\wwwroot\farm.json。
    /// </summary>
    private List<string> FarmPaths()
    {
        var paths = new List<string>();
        void Add(string? p)
        {
            if (string.IsNullOrWhiteSpace(p) || !File.Exists(p)) return;
            p = Path.GetFullPath(p);
            if (!paths.Exists(x => string.Equals(x, p, StringComparison.OrdinalIgnoreCase)))
                paths.Add(p);
        }

        if (_dashboardDir is { Length: > 0 })
            Add(Path.Combine(_dashboardDir, "wwwroot", "farm.json"));
        Add(Path.Combine(AppContext.BaseDirectory, "wwwroot", "farm.json"));
        Add(Path.Combine(AppContext.BaseDirectory, "farm.json"));

        if (AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (var i = 0; i < 6 && dir != null; i++, dir = dir.Parent)
                Add(Path.Combine(dir.FullName, "src", "BoltPreload.Monitor", "wwwroot", "farm.json"));
        }

        return paths;
    }

    private void SaveTcToFarm(ushort tcMode, decimal? coef)
    {
        var paths = FarmPaths();
        if (paths.Count == 0)
        {
            _lblLog.Text = "没有 farm.json，温补只对当前窗口有效，重启后恢复默认";
            return;
        }

        try
        {
            foreach (var p in paths) SiteConfig.PatchTempComp(p, tcMode, coef);
        }
        catch (Exception ex)
        {
            _lblLog.Text = "温补写入 farm.json 失败：" + ex.Message;
        }
    }

    private static Label Cap(string text) => new()
    {
        Text = text, AutoSize = true, ForeColor = Ui.Muted, Margin = new Padding(10, 8, 4, 0),
    };

    private void BuildUi()
    {
        var top = new Panel { Dock = DockStyle.Top, Height = 76, Padding = new Padding(12, 6, 12, 0), BackColor = Color.White };
        var row1 = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 34, WrapContents = false, BackColor = Color.White };
        row1.Controls.Add(new Label { Text = "设备", AutoSize = true, ForeColor = Ui.Muted, Margin = new Padding(0, 8, 4, 0) });
        _ipBox.Margin = new Padding(0, 4, 6, 0);
        _ipBox.Height = 24;
        _btnConnect.Margin = new Padding(0, 1, 4, 0);
        row1.Controls.Add(_ipBox);
        row1.Controls.Add(_btnConnect);
        foreach (var b in new[] { _btnScan, _btnFind, _btnZero, _btnParams })
            b.Margin = new Padding(0, 1, 6, 0);
        _tip.SetToolTip(_btnScan, "重新扫描 48 路，找出接了探头的通道。换插探头后点一下");
        _tip.SetToolTip(_btnFind, "扫整段波形，点回波起点写入回波窗");
        row1.Controls.Add(Ui.VSep());
        row1.Controls.Add(_btnScan);
        row1.Controls.Add(_btnFind);
        row1.Controls.Add(Ui.VSep());
        row1.Controls.Add(_btnZero);
        row1.Controls.Add(_btnParams);
        row1.Controls.Add(Ui.VSep());
        _lblState.Margin = new Padding(2, 8, 0, 0);
        row1.Controls.Add(_led);
        row1.Controls.Add(_lblState);

        var row2 = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 34, WrapContents = false, BackColor = Color.White };
        foreach (var b in new[] { _btnConfig, _btnDash })
            b.Margin = new Padding(0, 1, 6, 0);
        _tip.SetToolTip(_btnConfig, "接线、法兰结构、法兰级标定文件/额定值、规格库。保存写 farm.json，大屏一起生效。单路的标定文件在右侧「计算参数」页选");
        _tip.SetToolTip(_btnDash, "打开业主大屏网页。本机服务没在跑时会提示启动");
        row2.Controls.Add(_btnConfig);
        row2.Controls.Add(_btnDash);
        row2.Controls.Add(Ui.VSep());
        row2.Controls.Add(Cap("显示平均"));
        _cmbAvg.Margin = new Padding(0, 3, 0, 0);
        _tip.SetToolTip(_cmbAvg, "本窗口显示平均，不影响大屏和历史库");
        row2.Controls.Add(_cmbAvg);

        top.Controls.Add(row2);
        top.Controls.Add(row1);

        _list.Columns.Add("通道", S(56));
        // 位置 = farm.json 接线表里这一路装在哪个法兰的几号螺栓；没有 farm.json 时这一列是空的
        _list.Columns.Add("位置", S(118));
        _list.Columns.Add("轴向力kN", S(86));
        _list.Columns.Add("声时差ns", S(84));
        _list.Columns.Add("温度℃", S(72));
        _list.Columns.Add("信噪比dB", S(78));
        _list.Columns.Add("状态", S(84));
        foreach (var i in new[] { 2, 3, 4, 5 }) _list.Columns[i].TextAlign = HorizontalAlignment.Right;
        _colPref = _list.Columns.Cast<ColumnHeader>().Select(c => c.Width).ToArray();
        Ui.Smooth(_list);

        // 右侧三页（厂家：试件规格 / 硬件调节 / 计算参数），页签上方一行小结
        _rightHost.Width = S(352);
        _rightHost.BackColor = Color.White;
        _tabs.Padding = new Point(14, 4);
        TabPage Page(string title, Control body)
        {
            var p = new TabPage(title) { BackColor = Color.White, UseVisualStyleBackColor = false };
            p.Controls.Add(body);
            return p;
        }
        _tabs.TabPages.Add(Page("试件规格", _specimen));
        _tabs.TabPages.Add(Page("硬件调节", _hw));
        _tabs.TabPages.Add(Page("计算参数", _calc));
        _rightHost.Controls.Add(_tabs);
        _rightHost.Controls.Add(_lblSummary);

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 26, Padding = new Padding(12, 4, 10, 2), BackColor = Color.White };
        _lblLog.Dock = DockStyle.Fill;
        _lblLog.AutoEllipsis = true;
        bottom.Controls.Add(_lblLog);

        var waveBar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 30, WrapContents = false, Padding = new Padding(4, 1, 0, 0) };
        foreach (var b in new[] { _btnWaveReset, _btnWaveIn, _btnWaveOut })
            b.Margin = new Padding(0, 1, 4, 0);
        _tip.SetToolTip(_btnWaveReset, "坐标回到整段波形（也可用双击）");
        _tip.SetToolTip(_btnWaveIn, "放大");
        _tip.SetToolTip(_btnWaveOut, "缩小");
        _tip.SetToolTip(_wave, "滚轮放大 · 拖拽平移 · 右键框选 · 双击复位 · 悬停看读数");
        waveBar.Controls.Add(_btnWaveReset);
        waveBar.Controls.Add(_btnWaveIn);
        waveBar.Controls.Add(_btnWaveOut);
        waveBar.Controls.Add(Cap("横轴"));
        _tip.SetToolTip(_cmbAxis, "厂家按长度 mm 画（长度 = 声时×声速/2，声速取试件材料）；也可以切回声时 ns");
        waveBar.Controls.Add(_cmbAxis);
        _chkRef.Margin = new Padding(0, 3, 8, 0);
        waveBar.Controls.Add(_chkRef);
        _lblWaveHint.Margin = new Padding(4, 6, 0, 0);
        _lblWaveHint.ForeColor = Ui.Muted;
        _lblWaveHint.Text = string.Empty;
        waveBar.Controls.Add(_lblWaveHint);

        // 波形下面一排（厂家：通道 / 测量模式 … 停止 / 测回）
        var waveBar2 = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 34, WrapContents = false, Padding = new Padding(4, 2, 0, 0) };
        waveBar2.Controls.Add(_lblChan);
        var capMode = Cap("模式");
        capMode.Margin = new Padding(0, 8, 4, 0);
        waveBar2.Controls.Add(capMode);
        _tip.SetToolTip(_cmbMode, "测量模式：0 自动 / 1 位置 / 2 阈值。改的是设备存储区参数，要已置零的通道才能改");
        waveBar2.Controls.Add(_cmbMode);
        foreach (var b in new[] { _btnPrev, _btnNext, _btnPause, _btnRecord })
            b.Margin = new Padding(0, 1, 4, 0);
        _tip.SetToolTip(_btnPrev, "上一路（列表里往上选一行）");
        _tip.SetToolTip(_btnNext, "下一路（列表里往下选一行）");
        _tip.SetToolTip(_btnPause, "停止测量（设备待机，波形和数字停住）；再点继续");
        _tip.SetToolTip(_btnRecord, "把当前这一路此刻的读数记成一行（厂家「测回」），存进底部的手动测量结果表。连续记录是另一件事，在表上方勾选，测量时自动存");
        waveBar2.Controls.Add(_btnPrev);
        waveBar2.Controls.Add(_btnNext);
        waveBar2.Controls.Add(_btnPause);
        waveBar2.Controls.Add(_btnRecord);

        var cards = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 88,
            ColumnCount = 6,
            Padding = new Padding(8, 6, 8, 2),
            BackColor = Ui.Window,
        };
        for (var i = 0; i < 6; i++)
        {
            cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100 / 6f));
            var frame = new Panel
            {
                Dock = DockStyle.Fill, Margin = new Padding(5, 2, 5, 2), Padding = new Padding(1),
                BackColor = i == 0 ? Color.FromArgb(170, 204, 232) : Ui.Line,
            };
            var host = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Card };
            var title = new Label
            {
                Text = CardTitles[i],
                Dock = DockStyle.Top,
                Height = 20,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Ui.Muted,
                Font = new Font("Microsoft YaHei UI", 8.5f),
            };
            var value = new Label
            {
                Text = "--",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Microsoft YaHei UI", i == 0 ? 20f : 14f, FontStyle.Bold),
                ForeColor = i == 0 ? Ui.Accent : Ui.Text,
            };
            _cardValues[i] = value;
            host.Controls.Add(value);
            host.Controls.Add(title);
            if (i == 0) host.Controls.Add(new Panel { Dock = DockStyle.Left, Width = 3, BackColor = Ui.Accent });
            if (i == 1) _tip.SetToolTip(host, "与厂家同一公式：开温补时先把回波声时归一到参考温度，再减参考声时");
            frame.Controls.Add(host);
            cards.Controls.Add(frame, i, 0);
        }

        _split = new SplitContainer
        {
            Size = new Size(1200, 600),
            Dock = DockStyle.Fill,
            SplitterWidth = 6,
            TabStop = false,
            FixedPanel = FixedPanel.None,
            Panel1MinSize = 0,
            Panel2MinSize = 0,
            BackColor = Ui.Window,
        };
        _split.Panel1.Padding = new Padding(10, 6, 0, 6);
        _split.Panel1.BackColor = Ui.Window;
        _split.Panel2.Padding = new Padding(2, 2, 6, 4);
        _split.Panel2.BackColor = Ui.Window;
        var listHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(1), BackColor = Ui.Line };
        var listInner = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
        listInner.Controls.Add(_list);
        listHost.Controls.Add(listInner);
        _split.Panel1.Controls.Add(listHost);
        // 后加的先停靠：右侧页签占右边，波形下的按钮行贴底，波形工具条贴顶，波形填中间。
        // 六个卡片不放这里，否则会和页签抢宽度，默认窗口下卡片被挤窄、「测回」被裁掉。
        _split.Panel2.Controls.Add(_wave);
        _split.Panel2.Controls.Add(waveBar);
        _split.Panel2.Controls.Add(waveBar2);
        _split.Panel2.Controls.Add(_rightHost);

        _list.ClientSizeChanged += (_, _) => FitListColumns();
        _split.SizeChanged += (_, _) => PlaceSplitter();

        // 后加的先停靠：工具栏最上，状态栏和记录在下，卡片横贯全宽，剩下的给左右拆分
        Controls.Add(_split);
        Controls.Add(cards);
        Controls.Add(_recPanel);
        Controls.Add(bottom);
        Controls.Add(top);
        StyleButtons();
    }

    /// <summary>底部「手动测量结果」表（厂家同名那一块）：试件/时间筛选、导出、删除、收起。</summary>
    private void BuildRecordsUi()
    {
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36, WrapContents = false, Padding = new Padding(0, 4, 0, 0) };
        bar.Controls.Add(new Label { Text = "记录", AutoSize = true, Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold), Margin = new Padding(0, 6, 10, 0) });
        _chkTrace.Checked = _traceOn;
        _chkTrace.Margin = new Padding(0, 3, 8, 0);
        _cmbTraceRun.Margin = new Padding(0, 3, 8, 0);
        _lblTrace.Margin = new Padding(0, 6, 10, 0);
        foreach (var b in new[] { _btnTraceNew, _btnTraceExport, _btnRecToggle }) b.Margin = new Padding(0, 1, 6, 0);
        bar.Controls.Add(_chkTrace);
        bar.Controls.Add(_cmbTraceRun);
        bar.Controls.Add(_lblTrace);
        bar.Controls.Add(_btnTraceNew);
        bar.Controls.Add(_btnTraceExport);
        bar.Controls.Add(Ui.VSep());
        _lblRecCount.Margin = new Padding(0, 7, 8, 0);
        bar.Controls.Add(_lblRecCount);
        bar.Controls.Add(_btnRecToggle);

        foreach (var b in new[] { _btnRecExport, _btnRecDelete, _btnTraceToday }) b.Margin = new Padding(0, 1, 6, 0);
        _recExtra.Controls.Add(Cap("试件"));
        _recExtra.Controls.Add(_cmbRecSpec);
        _recExtra.Controls.Add(Cap("时间"));
        _recExtra.Controls.Add(_dtFrom);
        _recExtra.Controls.Add(new Label { Text = "–", AutoSize = true, Margin = new Padding(0, 7, 4, 0) });
        _recExtra.Controls.Add(_dtTo);
        _recExtra.Controls.Add(_btnRecExport);
        _recExtra.Controls.Add(_btnRecDelete);
        _recExtra.Controls.Add(_btnTraceToday);

        _tip.SetToolTip(_btnRecExport, "把当前筛出来的行导出成 CSV（Excel 直接打开）");
        _tip.SetToolTip(_btnRecDelete, "删掉选中的行（文件里也删）");
        _tip.SetToolTip(_chkTrace, $"测量进行时，每一帧写入当前这一段。换工况用「从现在开始」，之前的段还在，导出时选哪一段就出哪一段。记录保留 {_traceRetainDays} 天，更早的自动删除；天数在 settings.json 的 TraceRetainDays，要长期保存请导出 Excel。");
        _tip.SetToolTip(_cmbTraceRun, "导出哪一段。正在写入的是当前段。选「扭到500牛·m」就只导出这一段，不会把之前的一起带上。");
        _tip.SetToolTip(_btnTraceToday, "把今天 0 点以来已经记下的行收成一段。同一天再点不会重开，也不会把后来「从现在开始」的段并回去。");
        _tip.SetToolTip(_btnTraceNew, "从这一刻另开一段，比如「扭到500牛·m」。只记接下来的数据，之前的留在上一段。");
        _tip.SetToolTip(_btnTraceExport, "把下拉框里这一段、再按上面的起止日期，导出成一张表：每一行一个时刻，同一通道的温度、回波声时、声时差、伸长量、轴向力、参考声时、参考温度、标准差、信噪比、状态挨在一起，再接下一个通道。");

        _recGrid.Font = new Font("Microsoft YaHei UI", 9f);
        _recGrid.ColumnHeadersDefaultCellStyle.Font = new Font("Microsoft YaHei UI", 9f);
        Ui.Grid(_recGrid);
        void Col(string name, string header, int width, bool numeric = false, bool editable = false)
        {
            var c = new DataGridViewTextBoxColumn
            {
                Name = name, HeaderText = header, Width = S(width), ReadOnly = !editable, SortMode = DataGridViewColumnSortMode.NotSortable,
            };
            if (numeric) c.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            _recGrid.Columns.Add(c);
        }

        Col("time", "采集时间", 150);
        Col("ch", "通道", 48, numeric: true);
        Col("spec", "试件", 96);
        Col("group", "分组", 118);
        Col("temp", "温度(℃)", 74, numeric: true);
        Col("echo", "回波声时(ns)", 104, numeric: true);
        Col("dt", "声时差(ns)", 88, numeric: true);
        Col("elong", "伸长量(μm)", 88, numeric: true);
        Col("force", "U1轴向力(kN)", 100, numeric: true);
        Col("std", "标准差(ns)", 80, numeric: true);
        Col("note", "备注", 200, editable: true);
        _recGrid.Columns["note"]!.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        _recGrid.Columns["note"]!.MinimumWidth = S(120);
        _recGrid.Columns["note"]!.ToolTipText = "双击可填工况（比如加载级 500 / 1000），随行一起存";

        // 后加的先停靠：常驻一行（连续记录 + 展开），展开后才露出筛选和表
        _recPanel.Controls.Add(_recGrid);
        _recPanel.Controls.Add(_recExtra);
        _recPanel.Controls.Add(bar);
    }

    private int S(int px) => (int)Math.Round(px * DeviceDpi / 96.0);

    /// <summary>左侧通道表要放下七列：通道 / 位置 / 轴向力 / 声时差 / 温度 / 信噪比 / 状态。按列宽合计摆拆分条，窗口不够再按比例收列，避免表头被裁成「温」。 </summary>
    private int ListPanelPreferred()
    {
        var cols = _colPref.Length > 0 ? _colPref.Sum() : _list.Columns.Cast<ColumnHeader>().Sum(c => c.Width);
        return cols + _split.Panel1.Padding.Horizontal + 4 + SystemInformation.VerticalScrollBarWidth;
    }

    /// <summary>
    /// 拆分条：通道表拿到能放下七列的宽度，多出来的给波形；窗口变窄时先收表，波形至少留一截能放下「测回」那一排。
    /// 右侧页签宽度不动。卡片已经横贯在拆分条上面，不占这里的宽度。
    /// </summary>
    private void PlaceSplitter()
    {
        if (_placing || !_split.IsHandleCreated || _split.Width <= 0) return;
        _placing = true;
        try
        {
            var splitter = _split.SplitterWidth;
            var width = _split.Width;
            var available = width - splitter;
            if (available < 80) return;

            var right = Math.Max(0, _rightHost.Width);
            var listFloor = 300;
            var panel2Floor = right + S(460);
            if (listFloor + panel2Floor > available)
            {
                panel2Floor = Math.Max(right + S(200), available - listFloor);
                if (listFloor + panel2Floor > available)
                    listFloor = Math.Max(S(160), available - panel2Floor);
            }

            // 先把下限放开才能挪拆分条，挪完再按实际宽度卡回去
            _split.Panel1MinSize = 0;
            _split.Panel2MinSize = 0;
            var maxList = width - panel2Floor - splitter;
            if (maxList < listFloor) listFloor = Math.Max(0, maxList);
            var dist = Math.Clamp(ListPanelPreferred(), listFloor, Math.Max(listFloor, maxList));
            dist = Math.Clamp(dist, 0, Math.Max(0, available));
            if (_split.SplitterDistance != dist) _split.SplitterDistance = dist;

            var panel2Left = width - _split.SplitterDistance - splitter;
            if (listFloor > 0 && listFloor <= _split.SplitterDistance && panel2Floor <= panel2Left)
            {
                _split.Panel1MinSize = listFloor;
                _split.Panel2MinSize = panel2Floor;
            }

            FitListColumns();
        }
        catch (InvalidOperationException)
        {
        }
        catch (ArgumentOutOfRangeException)
        {
        }
        finally
        {
            _placing = false;
        }
    }

    private void FitListColumns()
    {
        if (_fittingCols || _list.Columns.Count == 0 || _list.ClientSize.Width <= 0) return;
        var pref = _colPref.Length == _list.Columns.Count
            ? _colPref
            : _list.Columns.Cast<ColumnHeader>().Select(c => c.Width).ToArray();
        var need = pref.Sum();
        var avail = _list.ClientSize.Width;
        _fittingCols = true;
        try
        {
            if (avail >= need)
            {
                for (var i = 0; i < pref.Length - 1; i++) _list.Columns[i].Width = pref[i];
                _list.Columns[pref.Length - 1].Width = Math.Max(pref[^1], avail - (need - pref[^1]));
                return;
            }

            var scale = (double)avail / need;
            var used = 0;
            for (var i = 0; i < pref.Length; i++)
            {
                var w = i == pref.Length - 1
                    ? Math.Max(S(48), avail - used)
                    : Math.Max(S(36), (int)Math.Round(pref[i] * scale));
                _list.Columns[i].Width = w;
                used += w;
            }
        }
        finally
        {
            _fittingCols = false;
        }
    }

    private void StyleButtons()
    {
        Ui.Primary(_btnConnect);
        Ui.Secondary(_btnScan);
        Ui.Secondary(_btnFind);
        Ui.Secondary(_btnZero);
        Ui.Secondary(_btnParams);
        Ui.Secondary(_btnConfig);
        Ui.Secondary(_btnDash);
        Ui.Quiet(_btnWaveReset);
        Ui.Quiet(_btnWaveIn);
        Ui.Quiet(_btnWaveOut);
        Ui.Quiet(_btnPrev);
        Ui.Quiet(_btnNext);
        Ui.Secondary(_btnPause);
        Ui.Primary(_btnRecord);
        Ui.Secondary(_btnRecExport);
        Ui.DangerBtn(_btnRecDelete);
        Ui.Secondary(_btnRecToggle);
        Ui.Secondary(_btnTraceExport);
        Ui.Secondary(_btnTraceToday);
        Ui.Secondary(_btnTraceNew);
    }

    private void StyleConnect()
    {
        if (_btnConnect.Text == "断开") Ui.DangerBtn(_btnConnect);
        else Ui.Primary(_btnConnect);
    }

    private void StylePause()
    {
        if (_btnPause.Text == "继续") Ui.Primary(_btnPause);
        else Ui.Secondary(_btnPause);
    }

    private void ColorState(Color color)
    {
        _lblState.ForeColor = color;
        _led.ForeColor = color;
    }

    private void PaintListRow(ListViewItem row, Color status, Color? force = null)
    {
        var zebra = row.Index % 2 == 0 ? Color.White : Ui.Zebra;
        row.UseItemStyleForSubItems = false;
        row.BackColor = zebra;
        foreach (ListViewItem.ListViewSubItem s in row.SubItems)
        {
            s.BackColor = zebra;
            s.ForeColor = Ui.Text;
        }
        row.SubItems[6].ForeColor = status;
        if (force is { } f) row.SubItems[2].ForeColor = f;
    }

    private async Task ToggleAsync()
    {
        if (_connected)
        {
            await DisconnectAsync();
            return;
        }

        _btnConnect.Enabled = false;
        ColorState(Ui.Muted);
        // 连哪台设备就用哪台的接线/标定（farm.json 里一个风场很多台）
        LoadPlan();
        try
        {
            _cts = new CancellationTokenSource();
            _engine = new DeviceEngine();
            _engine.OnLog += s => Post(() => _lblLog.Text = s);
            _engine.OnMeasurement += m => Post(() => Absorb(m));

            _lblState.Text = "正在连接...";
            var opt = CurrentDeviceOptions();
            await _engine.OpenAsync(opt, _cts.Token);
            if (!_engine.IsConnected)
            {
                throw new InvalidOperationException("设备已断开连接，请检查 IP 或设备是否被其他程序占用");
            }

            _connectedEndpoint = opt.Endpoint;
            _ipBox.ReadOnly = true;

            SaveSettings();
            await YieldDashboardAsync();
            if (!await ScanAndStartAsync())
            {
                try { if (_engine != null) await _engine.CloseAsync(); } catch { }
                _engine = null;
                await ReleaseDashboardAsync();
                return;
            }

            _connected = true;
            _btnConnect.Text = "断开";
            StyleConnect();
            _btnParams.Enabled = true;
            _btnZero.Enabled = true;
            _btnScan.Enabled = true;
            _btnFind.Enabled = true;
            _btnPause.Enabled = true;
            _btnRecord.Enabled = true;
            _cmbMode.Enabled = true;
            ShowMonitorState();
            RefreshSpecimenPanel();
        }
        catch (Exception ex)
        {
            _lblState.Text = "连接失败：" + ex.Message;
            ColorState(Ui.Danger);
            if (_engine != null)
            {
                try { await _engine.CloseAsync(); } catch { }
            }

            _engine = null;
            await ReleaseDashboardAsync();
        }
        finally
        {
            _btnConnect.Enabled = true;
        }
    }

    private async Task<bool> ScanAndStartAsync()
    {
        _scanning = true;
        try
        {
            ColorState(Ui.Muted);
            _lblState.Text = $"正在扫描 {ChannelCount} 个通道...";
            var scan = await _engine!.ScanProbesAsync(ChannelCount,
                text => Post(() => _lblState.Text = text),
                _cts.Token);
            // 列表只收这次有探头的通道。接线表里的号没有信号就不占一行。
            // 信号弱：没有 farm 时列入；有接线表时只有表内的才列入，表外多半是串扰。
            var configured = _plan.HasFarm ? _plan.WantedChannels.ToHashSet() : new HashSet<ushort>();
            var probes = scan.Where(s => s.HasProbe || (s.Suspect && (!_plan.HasFarm || configured.Contains(s.Channel)))).Select(s => s.Channel).ToList();
            _weakSkipped = scan.Where(s => s.Suspect && !probes.Contains(s.Channel)).Select(s => $"CH{s.Channel}（{s.SnrAvg:0} dB）").ToList();
            _wiredAbsent = configured.Where(c => !probes.Contains(c)).OrderBy(c => c).ToList();
            _unheard = scan.Where(s => !s.Heard).Select(s => s.Channel).ToList();

            _channels.Clear();
            _foreign.Clear();
            _list.Items.Clear();
            _selected = 0;
            if (probes.Count == 0)
            {
                if (_unheard.Count >= ChannelCount && await OfferRebootAsync())
                {
                    return false;
                }

                return true;
            }

            _lblState.Text = "正在读取通道参数...";
            var config = await _engine.StartStreamAsync(probes, _cts.Token);
            if (config.Values.Any(v => v.Count == 0))
            {
                await Task.Delay(2000, _cts.Token);
                _lblState.Text = "正在重新读取通道参数...";
                await _engine.StopStreamAsync();
                config = await _engine.StartStreamAsync(probes, _cts.Token);
            }

            foreach (var kv in config.OrderBy(k => k.Key))
            {
                var live = new ChLive { Ch = kv.Key, Cfg = kv.Value, Row = NewRow(kv.Key) };
                var s = scan.First(x => x.Channel == kv.Key);
                live.Snr = s.SnrAvg;
                live.Row.SubItems[5].Text = s.SnrAvg.ToString("0");
                if (s.Suspect)
                {
                    live.Row.SubItems[6].Text = "信号弱";
                    PaintListRow(live.Row, Ui.Warn);
                }

                _channels[kv.Key] = live;
            }

            if (_list.Items.Count > 0)
            {
                _list.Items[0].Selected = true;
            }

            _monitorSinceUtc = DateTime.UtcNow;
            _stallStrikes = 0;
            return true;
        }
        finally
        {
            _scanning = false;
        }
    }

    private async Task<bool> OfferRebootAsync()
    {
        var answer = MessageBox.Show(this,
            "扫描期间设备没有上报任何通道的数据，设备可能已经卡住。\n\n是否向设备发送重启命令？重启大约需要 1 分钟，完成后请重新点“连接”。",
            "设备无响应", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (answer != DialogResult.Yes) return false;

        try
        {
            await _engine!.RebootDeviceAsync();
            _lblState.Text = "已发送重启命令，请约 1 分钟后重新连接";
            ColorState(Ui.Warn);
        }
        catch (Exception ex)
        {
            _lblState.Text = "发送重启命令失败：" + ex.Message;
            ColorState(Ui.Danger);
        }

        return true;
    }

    private void ResetToDisconnected()
    {
        _engine = null;
        _connected = false;
        _paused = false;
        _btnPause.Text = "停止";
        StylePause();
        _btnConnect.Text = "连接";
        StyleConnect();
        _btnParams.Enabled = false;
        _btnZero.Enabled = false;
        _btnScan.Enabled = false;
        _btnFind.Enabled = false;
        _btnPause.Enabled = false;
        _btnRecord.Enabled = false;
        _cmbMode.Enabled = false;
        _ipBox.ReadOnly = false;
        _connectedEndpoint = string.Empty;
        _hw.SetBusy(false);
        _wave.Badge = null;
        _wave.Invalidate();
        RefreshSpecimenPanel();
    }

    /// <summary>
    /// 置零、参数下发这类模态对话框自己会停流。停按钮一灰，定时器就不再把“帧停了”当成断线去重连，
    /// 也不会把各行标成无数据。各路的最后一帧时间拨到现在，对话框开得久一点也不立刻超时。
    /// </summary>
    private void HoldDevice()
    {
        SetDeviceButtons(false);
        var now = DateTime.UtcNow;
        _monitorSinceUtc = now;
        _stallStrikes = 0;
        foreach (var c in _channels.Values)
        {
            if (c.Last != null) c.LastAt = now;
        }
    }

    private void ReleaseDevice()
    {
        if (_connected) SetDeviceButtons(true);
    }

    /// <summary>会话操作（扫描、找回波窗、改测量模式）期间锁住其它设备按钮，免得两拨命令同时下去。</summary>
    private void SetDeviceButtons(bool enabled)
    {
        _btnScan.Enabled = enabled;
        _btnConnect.Enabled = enabled;
        _btnParams.Enabled = enabled;
        _btnZero.Enabled = enabled;
        _btnFind.Enabled = enabled;
        _btnPause.Enabled = enabled;
        _cmbMode.Enabled = enabled;
        _hw.SetBusy(!enabled);
    }

    /// <summary>「上一个 / 下一个」：在列表里挪一行，选中即切换右边显示的那一路。</summary>
    private void SelectAdjacent(int delta)
    {
        if (_list.Items.Count == 0) return;
        var idx = _list.SelectedIndices.Count > 0 ? _list.SelectedIndices[0] : -1;
        var next = idx < 0 ? 0 : Math.Clamp(idx + delta, 0, _list.Items.Count - 1);
        var item = _list.Items[next];
        item.Selected = true;
        item.Focused = true;
        _list.EnsureVisible(next);
        _list.Focus();
    }

    /// <summary>「停止 / 继续」（厂家的停止）：停下来设备进待机，波形和数字停住；继续就恢复监测。</summary>
    private async Task TogglePauseAsync()
    {
        if (_engine is not { IsConnected: true } eng || _scanning) return;
        var monitored = _channels.Keys.OrderBy(c => c).ToArray();
        _btnPause.Enabled = false;
        try
        {
            if (!_paused)
            {
                await eng.StopStreamAsync();
                _paused = true;
                _btnPause.Text = "继续";
                StylePause();
                _lblState.Text = "已停止测量（设备待机）。点「继续」恢复";
                ColorState(Ui.Warn);
                OpsLog.Append("停止测量");
            }
            else
            {
                await ResumeAfterSessionOpAsync(monitored);
                OpsLog.Append("继续测量");
                ShowMonitorState();
            }
        }
        catch (Exception ex)
        {
            _lblState.Text = (_paused ? "恢复测量失败：" : "停止失败：") + ex.Message;
            ColorState(Ui.Danger);
        }
        finally
        {
            _btnPause.Enabled = _connected;
        }
    }

    /// <summary>波形下的「测量模式」下拉：改设备存储区的 MeasMode（要已置零的通道），写完读回核对，恢复监测。</summary>
    private async Task ChangeMeasModeAsync()
    {
        var mode = _cmbMode.SelectedIndex;
        if (mode < 0) return;
        if (_engine is not { IsConnected: true } eng || _scanning || !_channels.TryGetValue(_selected, out var live))
        {
            SyncModeUi();
            return;
        }

        var ch = live.Ch;
        var cur = DeviceEngine.Num(live.Cfg, "MeasMode");
        if (cur == mode) return;
        var label = ChannelLabel(ch);
        if (!DeviceEngine.IsZeroed(live.Cfg))
        {
            MessageBox.Show(this, $"{label} 还没置零。测量模式是设备存储区参数，只有置过零的通道才能改：先「通道置零」。",
                "测量模式", MessageBoxButtons.OK, MessageBoxIcon.Information);
            SyncModeUi();
            return;
        }

        var name = (string)_cmbMode.Items[mode]!;
        if (MessageBox.Show(this, $"将把 {label} 的测量模式改为「{name}」（存储区整块重写，参考声时/参考温度按现值原样写回）。是否继续？",
                "测量模式", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
        {
            SyncModeUi();
            return;
        }

        var monitored = _channels.Keys.OrderBy(c => c).ToArray();
        SetDeviceButtons(false);
        try
        {
            await eng.StopStreamAsync();
            var rows = await eng.WriteRegistersAsync(ch, new[] { ("MeasMode", (decimal)mode) }, CancellationToken.None);
            var row = rows.FirstOrDefault(r => r.Name == "MeasMode" && !r.Implicit);
            if (row is { Verdict: Verdict.StoredOk })
            {
                foreach (var kv in eng.Snapshot(ch)) live.Cfg[kv.Key] = kv.Value;
                live.Dt.Clear();
                OpsLog.Append($"{label} 测量模式 {row.BeforeValue} -> {row.ReadBack}");
                _ = NotifyDashboardResyncAsync("桌面端改了测量模式");
                _lblLog.Text = $"{label} 测量模式已改为「{name}」";
            }
            else
            {
                MessageBox.Show(this, $"{label} 测量模式没改上：{row?.Detail ?? row?.Verdict.ToString() ?? "无结果"}", "测量模式",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            await ResumeAfterSessionOpAsync(monitored);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "改测量模式失败：" + ex.Message, "测量模式", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetDeviceButtons(true);
            UpdateParams();
            foreach (var c in _channels.Values) UpdateRow(c);
            RefreshSelected();
            ShowMonitorState();
        }
    }

    /// <summary>下拉框跟着设备上这一路的测量模式走，不触发写入。</summary>
    private void SyncModeUi()
    {
        _modeUiLock = true;
        try
        {
            var mode = _channels.TryGetValue(_selected, out var live) ? DeviceEngine.Num(live.Cfg, "MeasMode") : null;
            _cmbMode.SelectedIndex = mode is { } m && m >= 0 && m <= 2 ? (int)m : -1;
            _lblChan.Text = _selected == 0 ? "通道 --" : $"通道 CH{_selected}";
        }
        finally
        {
            _modeUiLock = false;
        }
    }

    private async Task RescanAsync()
    {
        if (_engine == null || !_engine.IsConnected || _scanning) return;
        SetDeviceButtons(false);
        var keep = true;
        try
        {
            keep = await ScanAndStartAsync();
            if (keep)
            {
                UpdateParams();
                ShowMonitorState();
            }
        }
        catch (Exception ex)
        {
            _lblState.Text = "扫描失败：" + ex.Message;
            ColorState(Ui.Danger);
        }
        finally
        {
            if (keep)
            {
                SetDeviceButtons(true);
            }
            else
            {
                try { if (_engine != null) await _engine.CloseAsync(); } catch { }
                await ReleaseDashboardAsync();
                ResetToDisconnected();
                _btnConnect.Enabled = true;
            }
        }
    }

    /// <summary>找回波窗（厂家"扫波"）：对选中的通道扫整段波形，在波形上点回波起点，写入回波窗。</summary>
    private async Task ShowEchoWindowDialogAsync()
    {
        if (_engine == null || !_engine.IsConnected || _scanning) return;
        var monitored = _channels.Keys.OrderBy(c => c).ToArray();
        if (monitored.Length == 0 || !_channels.TryGetValue(_selected, out var live))
        {
            MessageBox.Show(this, "请先在列表里选中一个在监测的通道。", "找回波窗", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var ch = live.Ch;
        var label = ChannelLabel(ch);
        SetDeviceButtons(false);
        bool touched;
        decimal? written;
        using (var dlg = new EchoWindowDialog(_engine, ch, label, new Dictionary<string, string>(live.Cfg)))
        {
            dlg.ShowDialog(this);
            touched = dlg.Touched;
            written = dlg.WrittenEchoWin;
        }

        try
        {
            if (written is { })
            {
                var snap = _engine.Snapshot(ch);
                foreach (var kv in snap) live.Cfg[kv.Key] = kv.Value;
                live.Dt.Clear();
                _lblLog.Text = $"{label} 回波窗已写为 {written:0} ns";
            }

            if (touched) await ResumeAfterSessionOpAsync(monitored);
        }
        finally
        {
            SetDeviceButtons(true);
            UpdateParams();
            foreach (var c in _channels.Values) UpdateRow(c);
            ShowMonitorState();
        }
    }

    private async Task ResumeAfterSessionOpAsync(IReadOnlyList<ushort> monitored)
    {
        if (_engine == null || monitored.Count == 0) return;
        try
        {
            await _engine.ResumeMonitorAsync(monitored, CancellationToken.None);
            // 任何会话操作结束都恢复了监测，"已停止"状态随之结束
            _paused = false;
            _btnPause.Text = "停止";
            StylePause();
            _monitorSinceUtc = DateTime.UtcNow;
            _stallStrikes = 0;
        }
        catch (Exception ex)
        {
            _lblState.Text = "恢复监测失败：" + ex.Message + "，可点“扫描通道”重试";
            ColorState(Ui.Danger);
        }
    }

    /// <summary>
    /// 连接还在时设备有时不再发测量帧，界面仍显示已连接，连续记录就空一段。
    /// 停满 <see cref="StallAfter"/> 就重新打开监测；再停一次，或 TCP 已经断了，就原样重连，不把设备交还给大屏。
    /// </summary>
    private void MaybeReviveStalled()
    {
        if (_reviveBusy || _paused || !_btnPause.Enabled || _engine == null || _channels.Count == 0) return;
        var last = _monitorSinceUtc;
        foreach (var c in _channels.Values)
        {
            if (c.LastAt > last) last = c.LastAt;
        }

        if (last == default || DateTime.UtcNow - last < StallAfter) return;
        // TCP 已断只能重连；TCP 还在就先重发监测命令，连着 ReopenAfterStrikes 次都救不回来再重连
        var reopen = !_engine.IsConnected || _stallStrikes >= ReopenAfterStrikes;
        _stallStrikes++;
        _reviveBusy = true;
        _monitorSinceUtc = DateTime.UtcNow;
        _ = ReviveStreamAsync(reopen);
    }

    private async Task ReviveStreamAsync(bool reopen)
    {
        var channels = _channels.Keys.OrderBy(c => c).ToArray();
        SetDeviceButtons(false);
        try
        {
            if (reopen)
            {
                var why = _engine is { IsConnected: false } ? "TCP 已断" : $"重发监测命令 {_stallStrikes - 1} 次仍没有帧";
                _lblState.Text = "测量中断，正在重新连接…";
                ColorState(Ui.Warn);
                OpsLog.Append($"测量中断（{why}），自动重连");
                await ReopenLinkAsync(channels);
            }
            else
            {
                _lblState.Text = $"测量帧停了，重新下发监测命令（第 {_stallStrikes} 次）…";
                ColorState(Ui.Warn);
                OpsLog.Append($"测量帧停了，重新下发监测命令（第 {_stallStrikes} 次，TCP 还在，不重连）");
                await _engine!.ResumeMonitorAsync(channels, _cts.Token);
            }

            _monitorSinceUtc = DateTime.UtcNow;
            ShowMonitorState();
        }
        catch (Exception ex)
        {
            _lblState.Text = "测量中断后没有恢复：" + ex.Message;
            ColorState(Ui.Danger);
            OpsLog.Append("测量中断后没有恢复：" + ex.Message);
            _monitorSinceUtc = DateTime.UtcNow;
            _stallStrikes = Math.Max(_stallStrikes, 1);
        }
        finally
        {
            _reviveBusy = false;
            if (_connected) SetDeviceButtons(true);
        }
    }

    private DeviceOptions CurrentDeviceOptions() => new()
    {
        Endpoint = ActiveEndpoint(),
        Address = 1,
        Resume = "multi",
        Gap = 250,
        TcMode = _plan.TcMode,
        TempCoefficient = _plan.TempCoefficient,
        TempTau = _plan.TempTau,
        TempFrom = _plan.TempFrom,
        Anchors = AnchorStore.Open(AppContext.BaseDirectory, ActiveEndpoint()),
    };

    /// <summary>TCP 断了或重新打开监测无效时，换一条连接继续原来的通道。不结束大屏的维护。</summary>
    private async Task ReopenLinkAsync(ushort[] channels)
    {
        try { if (_engine != null) await _engine.CloseAsync(); } catch { }
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        _cts = new CancellationTokenSource();
        _engine = new DeviceEngine();
        _engine.OnLog += s => Post(() => _lblLog.Text = s);
        _engine.OnMeasurement += m => Post(() => Absorb(m));
        var opt = CurrentDeviceOptions();
        await _engine.OpenAsync(opt, _cts.Token);
        if (!_engine.IsConnected)
            throw new InvalidOperationException("设备没有连上");
        _connectedEndpoint = opt.Endpoint;
        var config = await _engine.StartStreamAsync(channels, _cts.Token);
        foreach (var kv in config)
        {
            if (_channels.TryGetValue(kv.Key, out var live) && kv.Value.Count > 0)
                live.Cfg = kv.Value;
        }
    }

    private void ShowMonitorState()
    {
        if (!_connected) return;
        if (_paused)
        {
            _lblState.Text = "已停止测量（设备待机）。点「继续」恢复";
            ColorState(Ui.Warn);
            return;
        }

        if (_channels.Count == 0)
        {
            var empty = _unheard.Count >= ChannelCount
                ? "设备没有上报任何通道数据，请检查设备状态（必要时重启设备）后点“扫描通道”重试"
                : "未发现探头，请检查探头连接后点“扫描通道”";
            var absent = WiredAbsentNote();
            if (absent.Length > 0) empty += "。" + absent;
            _lblState.Text = empty;
            ColorState(Ui.Warn);
            _tip.SetToolTip(_lblState, empty);
            return;
        }

        var names = string.Join("、", _channels.Keys.OrderBy(c => c).Select(c => "CH" + c));
        var unread = _channels.Values.Where(c => c.Cfg.Count == 0).Select(c => "CH" + c.Ch).ToList();
        var zeroed = _channels.Values.Count(c => DeviceEngine.IsZeroed(c.Cfg));
        var text = $"监测中，{_channels.Count} 个通道（{names}）· 已置零 {zeroed}";
        if (unread.Count > 0) text += $"，{string.Join("、", unread)} 参数未读到";
        if (_unheard.Count > 0) text += $"，扫描时 CH{FormatChannels(_unheard)} 未响应";
        if (unread.Count > 0 || _unheard.Count > 0) text += "，可点“扫描通道”重试";
        if (_weakSkipped.Count > 0) text += $"；{string.Join("、", _weakSkipped)} 有微弱信号，未纳入";
        var wiredAbsent = WiredAbsentNote();
        if (wiredAbsent.Length > 0) text += "；" + wiredAbsent;
        var foreign = _foreign.Where(kv => DateTime.UtcNow - kv.Value < TimeSpan.FromMinutes(2)).Select(kv => kv.Key).OrderBy(c => c).ToList();
        if (foreign.Count > 0) text += $"；另有程序在轮询 CH{FormatChannels(foreign)}（大屏巡检？），其回包已忽略";
        var warn = unread.Count > 0 || _unheard.Count > 0 || foreign.Count > 0 || _weakSkipped.Count > 0 || _wiredAbsent.Count > 0;
        _lblState.Text = warn
            ? $"已连接 {_channels.Count} 路 · 已置零 {zeroed} · 有提示"
            : $"已连接 {_channels.Count} 路 · 已置零 {zeroed}";
        ColorState(warn ? Ui.Warn : Ui.Ok);
        _tip.SetToolTip(_lblState, text);
        _tip.SetToolTip(_led, text);
    }

    private string WiredAbsentNote()
    {
        return _wiredAbsent.Count == 0 ? string.Empty : $"接线表里的 CH{FormatChannels(_wiredAbsent)} 这次没探头，未列入";
    }

    private static string FormatChannels(IReadOnlyList<ushort> channels)
    {
        var sorted = channels.Distinct().OrderBy(c => c).ToList();
        var parts = new List<string>();
        for (var i = 0; i < sorted.Count;)
        {
            var j = i;
            while (j + 1 < sorted.Count && sorted[j + 1] == sorted[j] + 1) j++;
            parts.Add(j > i ? $"{sorted[i]}-{sorted[j]}" : sorted[i].ToString());
            i = j + 1;
        }

        return string.Join("、", parts);
    }

    private ListViewItem NewRow(ushort ch)
    {
        var row = new ListViewItem($"CH{ch}") { Tag = ch };
        for (var i = 1; i < _list.Columns.Count; i++)
        {
            row.SubItems.Add("--");
        }

        row.SubItems[1].Text = _plan.Describe(ch);
        _list.Items.Add(row);
        PaintListRow(row, Ui.Muted);
        return row;
    }

    /// <summary>如"CH3（塔筒顶段法兰 #24）"，用于对话框标题和操作记录。</summary>
    private string ChannelLabel(ushort ch)
    {
        var where = _plan.Describe(ch);
        return where.Length > 0 ? $"CH{ch}（{where}）" : $"CH{ch}";
    }

    private async Task DisconnectAsync()
    {
        _btnConnect.Enabled = false;
        try
        {
            _cts.Cancel();
            if (_engine != null)
            {
                _lblState.Text = "正在恢复监测模式...";
                try { await _engine.CloseAsync(); } catch { }
            }
        }
        finally
        {
            ResetToDisconnected();
            await ReleaseDashboardAsync();
            _btnConnect.Enabled = true;
            _lblState.Text = "已断开";
            ColorState(Ui.Muted);
        }
    }

    /// <summary>置零已写入、监测还没恢复：换上新参考声时，平均队列从空开始，避免掺进置零前的声时差。</summary>
    private void ApplyZero(ushort ch)
    {
        if (_engine is null || !_channels.TryGetValue(ch, out var c)) return;
        var snap = _engine.Snapshot(ch);
        foreach (var kv in snap) c.Cfg[kv.Key] = kv.Value;
        c.Dt.Clear();
        c.Echo.Clear();
        c.Temp.Clear();
    }

    private void ShowZeroDialog()
    {
        if (_engine == null || !_engine.IsConnected)
        {
            MessageBox.Show(this, "请先连接设备", "通道置零", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var monitored = _channels.Keys.OrderBy(c => c).ToArray();
        if (monitored.Length == 0)
        {
            // 连上了但一个探头都没扫到时按钮仍是亮的；置零要拿实时波形当基线，没有在测的通道就没法做
            MessageBox.Show(this, "当前没有在监测的通道，无法置零。\n请检查探头连接后点“扫描通道”。",
                "通道置零", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var zeroed = new HashSet<ushort>();
        HoldDevice();
        try
        {
            using (var dlg = new ZeroDialog(_engine, monitored, _selected == 0 ? monitored[0] : _selected, ChannelLabel, _plan.Mode))
            {
                dlg.Committed += ApplyZero;
                dlg.ShowDialog(this);
                zeroed = dlg.ZeroedChannels;
            }

            foreach (var ch in zeroed)
            {
                if (!_channels.TryGetValue(ch, out var c)) continue;
                var snap = _engine.Snapshot(ch);
                foreach (var kv in snap)
                {
                    c.Cfg[kv.Key] = kv.Value;
                }
            }

            if (zeroed.Count > 0)
            {
                UpdateParams();
                RefreshSelected();
                foreach (var c in _channels.Values) UpdateRow(c);
                _ = NotifyDashboardResyncAsync($"桌面端已对 {string.Join("、", zeroed.OrderBy(c => c).Select(c => "CH" + c))} 置零");
            }
        }
        finally
        {
            ReleaseDevice();
        }

        // 置零对话框关闭时自己恢复了监测，"已停止"随之结束
        _paused = false;
        _btnPause.Text = "停止";
        StylePause();
        ShowMonitorState();
    }

    /// <summary>
    /// 现场配置：扫到的通道带着"有没有信号、置没置零"进对话框，人只需要选法兰、填螺栓号、选标定文件、填额定。
    /// 保存后重读 farm.json，列表的"位置"列和力值口径立刻按新配置走；大屏那边盯着文件自己重载。
    /// </summary>
    private void ShowConfigDialog()
    {
        // 扫描结果属于当前连着的那台设备；地址栏已经改成别的地址就不能拿这台的探头去配那台
        var connected = _engine is { IsConnected: true } && !_scanning && SiteConfig.SameHost(_connectedEndpoint, _ipBox.Text);
        var detected = connected
            ? _channels.Values.OrderBy(c => c.Ch).Select(c => new SiteConfigDialog.Detected(
                c.Ch,
                DeviceEngine.IsZeroed(c.Cfg),
                c.Snr,
                c.Snr is { } s && s < DeviceEngine.ProbeSnrDb)).ToList()
            : new List<SiteConfigDialog.Detected>();

        SiteConfigDialog dlg;
        try
        {
            dlg = new SiteConfigDialog(ActiveEndpoint(), connected, detected, AppContext.BaseDirectory, _dashboardDir);
        }
        catch (Exception)
        {
            return;   // farm.json 读不了，对话框自己已经提示过
        }

        using (dlg)
        {
            var result = dlg.ShowDialog(this);
            if (dlg.DashboardDir != _dashboardDir)
            {
                _dashboardDir = dlg.DashboardDir;
                SaveSettings();
            }

            if (result != DialogResult.OK) return;
            OpsLog.Append($"现场配置保存：{dlg.Summary}");
            _lblLog.Text = "现场配置已保存" + (dlg.DashboardDir is not null ? "，大屏自动重载" : "");
        }

        // 地址栏可能在对话框里改成了别的设备；按对话框里的地址重选方案
        LoadPlan();
    }

    // ── 右侧「试件规格 / 计算参数」页：一路一个试件，照厂家那一页 ──────────────────────────

    /// <summary>farm.json 读成可编辑模型；读不了给人看原因并返回空。</summary>
    private SiteConfig? LoadSiteConfig(out List<string> paths)
    {
        paths = FarmPaths();
        if (paths.Count == 0)
        {
            MessageBox.Show(this, "没有 farm.json。先用「现场配置」把探头接到法兰上，试件规格存在里面。", "试件规格",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return null;
        }

        try
        {
            return SiteConfig.Load(File.ReadAllText(paths[0]), ActiveEndpoint());
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"{paths[0]}\n读不了：{ex.Message}", "试件规格", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return null;
        }
    }

    /// <summary>校验、写回所有该一起改的 farm.json（先写临时文件再改名，大屏盯着它）。</summary>
    private bool SaveSiteConfig(SiteConfig cfg, IReadOnlyList<string> paths, string title)
    {
        var errors = cfg.Validate();
        if (errors.Count > 0)
        {
            MessageBox.Show(this, string.Join("\n", errors.Select(e => "• " + e)), "配置还不能保存", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        try
        {
            var json = cfg.ToJson();
            foreach (var p in paths)
            {
                var tmp = p + ".tmp";
                File.WriteAllText(tmp, json, new System.Text.UTF8Encoding(false));
                File.Move(tmp, p, overwrite: true);
            }

            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"写入 farm.json 失败：{ex.Message}", title, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    /// <summary>
    /// 把选中通道的试件显示到右侧页：通道自己的规格 → 法兰默认（标注"沿用"）→ 空表单。
    /// 有没保存的改动时不动表单（除非 force），免得一帧数据进来把人填的东西冲掉。
    /// </summary>
    private void RefreshSpecimenPanel(bool force = false)
    {
        if (_specimen.Dirty && !force && _specimen.Channel == _selected) return;
        var ch = _selected;
        var spec = ch != 0 ? _plan.SpecFor(ch) : null;
        var own = ch != 0 && _plan.HasOwnSpec(ch);
        var fi = ch != 0 ? _plan.FlangeOf(ch) : -1;
        var groupName = fi >= 0 ? _plan.Flanges[fi].Name : null;
        var groups = _plan.Flanges.Select(f => f.Name).ToList();
        var pos = ch != 0 ? _plan.Describe(ch) : string.Empty;
        var defaultName = pos.Length > 0 ? pos : ch == 0 ? string.Empty : $"CH{ch}";
        _specimen.ShowSpecimen(ch, spec, own, groupName, groups, _plan.Library.Grades(), defaultName);
        _specimen.ShowCalibration(ch != 0 ? _plan.CalSourceFor(ch) : null);
        SyncTcUi();
    }

    /// <summary>试件页「材料规格 ?」：开材料库改这一等级（和别的等级）的常数，确定即写 farm.json。</summary>
    private void EditMaterials()
    {
        if (LoadSiteConfig(out var paths) is not { } cfg) return;
        using (var dlg = new MaterialsDialog(cfg.Library))
        {
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
        }

        if (!SaveSiteConfig(cfg, paths, "材料库")) return;
        OpsLog.Append($"材料库保存：{string.Join("、", cfg.Library.Materials.Select(m => $"{m.Grade}（{m.YieldMpa:0} MPa · {m.Velocity:0} m/s · E{m.ElasticGpa:0} · L_ae {m.Acoustic:0.###}）"))}");
        _lblLog.Text = $"材料库已保存，{cfg.Library.Materials.Count} 个等级";
        LoadPlan();
    }

    /// <summary>「计算参数」页改温度通道：写进 farm.json 这台设备的 tempFrom；LoadPlan 里连着设备就让 SDK 按新表配（不发命令）。</summary>
    private void SetTempChannel(ushort ch, ushort from)
    {
        if (ch == 0) return;
        if (LoadSiteConfig(out var paths) is not { } cfg) { SyncTcUi(); return; }
        if (!cfg.Wires.Any(w => w.Ch == ch && w.FlangeIndex != SiteConfig.Wire.None))
        {
            MessageBox.Show(this, $"CH{ch} 还没接到任何法兰上，温度通道存不下来：先在「试件规格」的分组里选法兰并保存。", "温度通道",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            SyncTcUi();
            return;
        }

        if (from == 0) cfg.TempFrom.Remove(ch);
        else cfg.TempFrom[ch] = from;
        if (!SaveSiteConfig(cfg, paths, "温度通道")) { SyncTcUi(); return; }
        OpsLog.Append(from == 0 ? $"CH{ch} 温度通道：用本路" : $"CH{ch} 温度通道：借 CH{from}");
        _lblLog.Text = from == 0 ? $"CH{ch} 用本路温度" : $"CH{ch} 用 CH{from} 的温度";
        LoadPlan();
    }

    /// <summary>「结果计算 → 标定文件 / 浏览」：挑一份 .ucf，拷到程序目录（有大屏目录也拷一份），绑到这一路。</summary>
    private void BrowseUcf(ushort ch)
    {
        if (ch == 0) { SyncTcUi(); return; }
        using var dlg = new OpenFileDialog
        {
            Filter = "标定文件 (*.ucf)|*.ucf|所有文件 (*.*)|*.*",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Title = $"给 {ChannelLabel(ch)} 选标定文件",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) { SyncTcUi(); return; }

        Ucf.Cal cal;
        try
        {
            cal = Ucf.Load(dlg.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"这不是能用的标定文件：{ex.Message}", "标定文件", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            SyncTcUi();
            return;
        }

        var name = Path.GetFileName(dlg.FileName);
        try
        {
            foreach (var dir in new[] { AppContext.BaseDirectory, _dashboardDir }.Where(d => d is { Length: > 0 } && Directory.Exists(d)))
            {
                var dst = Path.Combine(dir!, name);
                if (!string.Equals(Path.GetFullPath(dst), Path.GetFullPath(dlg.FileName), StringComparison.OrdinalIgnoreCase))
                    File.Copy(dlg.FileName, dst, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "拷贝标定文件失败：" + ex.Message, "标定文件", MessageBoxButtons.OK, MessageBoxIcon.Error);
            SyncTcUi();
            return;
        }

        BindUcf(ch, name, $"{cal.K:F4} kN/ns，{cal.Points} 点");
    }

    /// <summary>
    /// 把标定文件绑到这一路：通道有自己的规格就写在规格上，否则写在所属法兰上（法兰上的对整段法兰生效）。
    /// name 为空 = 解除绑定、改按材料系数（理论斜率）算。
    /// </summary>
    private void BindUcf(ushort ch, string? name, string? detail = null)
    {
        if (ch == 0) { SyncTcUi(); return; }
        if (LoadSiteConfig(out var paths) is not { } cfg) { SyncTcUi(); return; }
        var wire = cfg.Wires.FirstOrDefault(w => w.Ch == ch && w.FlangeIndex >= 0);
        if (wire is null)
        {
            MessageBox.Show(this, $"CH{ch} 还没接到任何法兰上：先在「试件规格」的分组里选法兰并保存。", "结果计算", MessageBoxButtons.OK, MessageBoxIcon.Information);
            SyncTcUi();
            return;
        }

        var own = wire.SpecName is { Length: > 0 } sn ? cfg.Library.Find(sn) : null;
        var flange = cfg.Flanges[wire.FlangeIndex];
        // 规格上的文件：所有用这一规格、又没在法兰上另配文件的通道一起变；法兰上的：整段法兰一起
        var where = own is { } ? $"规格「{own.DisplayName}」（用这一规格的通道一起）" : $"法兰「{flange.Name}」（整段法兰一起）";
        if (name is null)
        {
            var cur = own is { } ? own.Ucf : flange.Ucf;
            if (string.IsNullOrEmpty(cur)) { SyncTcUi(); return; }
            if (MessageBox.Show(this, $"改为按材料系数算：解除 {where} 的标定文件 {cur}，力值改用试件规格算出的理论斜率。是否继续？", "结果计算",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
            {
                SyncTcUi();
                return;
            }
        }

        if (own is { }) own.Ucf = name;
        else flange.Ucf = name;
        if (!SaveSiteConfig(cfg, paths, "结果计算")) { SyncTcUi(); return; }
        OpsLog.Append(name is null ? $"{where} 解除标定文件，改按材料系数" : $"{where} 标定文件 -> {name}（{detail}）");
        _lblLog.Text = name is null ? $"{where}：按材料系数算" : $"{where}：标定文件 {name}";
        LoadPlan();
    }

    /// <summary>「硬件调节」页「应用」：只写改过的几项，写完读回核对，恢复监测。</summary>
    private async Task ApplyHardwareAsync()
    {
        var ch = _hw.Channel;
        if (ch == 0 || _engine is not { IsConnected: true } eng || _scanning || !_channels.TryGetValue(ch, out var live)) return;
        var items = _hw.Collect();
        if (items.Count == 0) return;
        foreach (var (n, v) in items)
        {
            if (DeviceEngine.Validate(n, v) is { } bad)
            {
                MessageBox.Show(this, $"{ParamCatalog.CnOf(n)}：{bad}", "硬件调节", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }

        var label = ChannelLabel(ch);
        var monitored = _channels.Keys.OrderBy(c => c).ToArray();
        SetDeviceButtons(false);
        _hw.SetBusy(true);
        try
        {
            await eng.StopStreamAsync();
            var rows = await eng.WriteRegistersAsync(ch, items, CancellationToken.None);
            foreach (var kv in eng.Snapshot(ch)) live.Cfg[kv.Key] = kv.Value;
            var wrote = rows.Where(r => !r.Implicit).ToList();
            var bad = wrote.Where(r => r.Verdict != Verdict.StoredOk).ToList();
            OpsLog.Append($"{label} 硬件调节：" + string.Join("，", wrote.Select(r => $"{r.Cn} {r.BeforeValue}->{r.Wrote}（{(r.Verdict == Verdict.StoredOk ? "成功" : r.Detail is { Length: > 0 } ? r.Detail : r.Verdict.ToString())}）")));
            if (bad.Count > 0)
            {
                MessageBox.Show(this, "有参数没写上：\n" + string.Join("\n", bad.Select(r => $"• {r.Cn}：{(r.Detail is { Length: > 0 } ? r.Detail : r.Verdict.ToString())}")),
                    "硬件调节", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            else
            {
                _lblLog.Text = $"{label} 已应用：{string.Join("，", wrote.Select(r => $"{r.Cn} {r.ReadBack}"))}";
            }

            if (wrote.Any(r => r.Verdict == Verdict.StoredOk))
            {
                live.Dt.Clear();
                _ = NotifyDashboardResyncAsync("桌面端改了硬件参数");
            }

            await ResumeAfterSessionOpAsync(monitored);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "应用失败：" + ex.Message, "硬件调节", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _hw.SetBusy(false);
            SetDeviceButtons(true);
            UpdateParams();
            foreach (var c in _channels.Values) UpdateRow(c);
            RefreshSelected();
            ShowMonitorState();
        }
    }

    /// <summary>
    /// 保存这一路的试件：写成规格库里一条以名称命名的规格（名称唯一），把这一路绑到它上面（channelSpecs）；
    /// 分组改了就把这一路挪到对应法兰。规格被其它通道或法兰共用时不改原条目，另存一条。
    /// 近似总长变了且设备连着，按厂家公式算出回波窗写进这一路。
    /// </summary>
    private async Task SaveSpecimenAsync()
    {
        var ch = _specimen.Channel;
        if (ch == 0) return;
        if (!_specimen.TryCollect(out var spec, out var err))
        {
            MessageBox.Show(this, err, "试件规格", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // 保存前这一路表单里显示的总长（自己的或沿用法兰默认的）：没改就不碰设备上的回波窗
        var lengthBefore = _plan.SpecFor(ch)?.LengthMm;
        if (LoadSiteConfig(out var paths) is not { } cfg) return;
        var wire = cfg.Wires.FirstOrDefault(w => w.Ch == ch && w.FlangeIndex >= 0);
        var groupIndex = _specimen.SelectedGroup is { } gname ? cfg.Flanges.FindIndex(f => f.Name == gname) : -1;
        if (wire is null)
        {
            if (groupIndex < 0)
            {
                MessageBox.Show(this, $"CH{ch} 还没接到任何法兰上：先在「分组」里选一个法兰（没有法兰就去「现场配置」建）。", "试件规格",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            wire = new SiteConfig.Wire { Ch = ch, FlangeIndex = groupIndex };
            cfg.Wires.Add(wire);
            cfg.Wires.Sort((a, b) => a.Ch.CompareTo(b.Ch));
        }
        else if (groupIndex >= 0 && groupIndex != wire.FlangeIndex)
        {
            wire.FlangeIndex = groupIndex;
            wire.Bolt = null;   // 换了法兰，原来的螺栓号不再成立
        }

        var lib = cfg.Library;
        var own = wire.SpecName is { Length: > 0 } sn ? lib.Find(sn) : null;
        bool Shared(BoltSpec s) => cfg.Wires.Any(w => w.Ch != ch && string.Equals(w.SpecName, s.Name, StringComparison.OrdinalIgnoreCase))
                                   || cfg.Flanges.Any(f => string.Equals(f.SpecName, s.Name, StringComparison.OrdinalIgnoreCase));

        var byName = lib.Find(spec.Name);
        BoltSpec target;
        if (own is { } && !Shared(own))
        {
            // 这一路自己独有的一条：就地改（改名也行，但不能撞别人）
            if (byName is { } && !ReferenceEquals(byName, own))
            {
                MessageBox.Show(this, $"已经有一条叫「{spec.Name}」的规格（{Describe(cfg, byName)}），试件名称必须唯一。", "试件规格", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            target = own;
        }
        else if (byName is { } && !Shared(byName) && (own is null || !ReferenceEquals(byName, own)))
        {
            // 库里有一条同名但没人用的（预设）：拿来当这一路的
            target = byName;
        }
        else if (byName is { })
        {
            MessageBox.Show(this, $"「{spec.Name}」已被 {Describe(cfg, byName)} 使用，试件名称必须唯一，换个名字。", "试件规格",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        else
        {
            target = new BoltSpec();
            lib.Specs.Add(target);
        }

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
        // 这一路还没有自己的规格时，沿用当时正在用的标定文件。已经有规格且文件被清掉（材料系数）则保持为空。
        if (string.IsNullOrWhiteSpace(spec.Ucf) && own is null && _plan.CalFor(ch) is { Points: > 0 } cal)
            spec.Ucf = cal.FileName;
        target.Ucf = spec.Ucf;
        wire.SpecName = target.Name;

        if (!SaveSiteConfig(cfg, paths, "试件规格")) return;
        OpsLog.Append($"CH{ch} 试件规格保存：{target.Summary(cfg.Library.MaterialFor(target.Grade))}，分组 {cfg.Flanges[wire.FlangeIndex].Name}");
        _specimen.MarkSaved();
        _lblLog.Text = $"CH{ch} 试件规格已保存：{target.DisplayName}";
        LoadPlan();

        // 近似总长决定回波窗（厂家用法）：总长变了、设备连着、这一路在测，就按新总长写回波窗
        if (spec.LengthMm is { } len && len != lengthBefore && _engine is { IsConnected: true } && !_scanning && _channels.TryGetValue(ch, out var live)
            && spec.EchoWinNs(_plan.Library.MaterialFor(spec.Grade)) is { } win
            && DeviceEngine.Num(live.Cfg, "EchoWin") != win)
        {
            await WriteEchoWinAsync(ch, win);
        }
    }

    private static string Describe(SiteConfig cfg, BoltSpec s)
    {
        var who = new List<string>();
        who.AddRange(cfg.Wires.Where(w => string.Equals(w.SpecName, s.Name, StringComparison.OrdinalIgnoreCase)).Select(w => $"CH{w.Ch}"));
        who.AddRange(cfg.Flanges.Where(f => string.Equals(f.SpecName, s.Name, StringComparison.OrdinalIgnoreCase)).Select(f => $"法兰「{f.Name}」"));
        return who.Count > 0 ? string.Join("、", who) : "规格库预设";
    }

    /// <summary>按试件总长算出的回波窗写进这一路（试件规格保存时总长变了会走到这里）。</summary>
    private async Task WriteEchoWinAsync(ushort ch, decimal win)
    {
        if (_engine is not { IsConnected: true } eng) return;
        var monitored = _channels.Keys.OrderBy(c => c).ToArray();
        var label = ChannelLabel(ch);
        SetDeviceButtons(false);
        try
        {
            await eng.StopStreamAsync();
            var rows = await eng.WriteRegistersAsync(ch, new[] { ("EchoWin", win) }, CancellationToken.None);
            var row = rows.FirstOrDefault(r => r.Name == "EchoWin" && !r.Implicit);
            if (row is { Verdict: Verdict.StoredOk })
            {
                if (_channels.TryGetValue(ch, out var c))
                {
                    foreach (var kv in eng.Snapshot(ch)) c.Cfg[kv.Key] = kv.Value;
                    c.Dt.Clear();
                }

                OpsLog.Append($"{label} 按试件总长写入回波窗 {row.BeforeValue} -> {row.ReadBack}");
                _ = NotifyDashboardResyncAsync("桌面端按试件规格写了回波窗");
                _lblLog.Text = $"{label} 回波窗已写为 {row.ReadBack} ns";
            }
            else
            {
                MessageBox.Show(this, $"{label} 回波窗没写上：{row?.Detail ?? row?.Verdict.ToString() ?? "无结果"}", "写入回波窗",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            if (monitored.Length > 0) await ResumeAfterSessionOpAsync(monitored);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "写入回波窗失败：" + ex.Message, "写入回波窗", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetDeviceButtons(true);
            UpdateParams();
            foreach (var c in _channels.Values) UpdateRow(c);
            ShowMonitorState();
        }
    }
    /// <summary>
    /// 打开大屏网页。地址取 settings.json 的 DashboardUrl，没填用本机 5080。
    /// 本机打不开时：装了服务就启动服务，没装就运行旁边的「安装大屏服务.cmd」（需要管理员权限）。
    /// </summary>
    private async Task OpenDashboardAsync()
    {
        _btnDash.Enabled = false;
        try
        {
            var url = string.IsNullOrWhiteSpace(_dashboardUrl) ? LocalDashboardUrl : _dashboardUrl!.Trim();
            var isLocal = url.Contains("localhost", StringComparison.OrdinalIgnoreCase) || url.Contains("127.0.0.1");
            if (!await DashboardAliveAsync(url) && isLocal)
            {
                if (DashboardServiceInstalled())
                {
                    var go = MessageBox.Show(this, "本机的大屏服务没在运行。现在启动它？（需要管理员权限，会弹出确认）",
                        "打开大屏", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (go != DialogResult.Yes) return;
                    var code = await RunElevatedAsync("sc.exe", "start BoltPreloadMonitor", null);
                    if (code is null) return;
                    // sc.exe 的退出码就是 Win32 错误码；1056 = 已经在运行（存活检测那两秒它刚好起来了）
                    if (code is not (0 or 1056))
                    {
                        var hint = code switch
                        {
                            1058 => "服务被设为“禁用”（发布脚本中途断掉会留下这个状态）。重新运行一次「安装大屏服务.cmd」，或在 services.msc 里把「螺栓预紧力大屏服务」改回自动。",
                            1053 or 1067 => "服务起来又退出了，请看大屏程序目录 logs 文件夹里今天的日志。",
                            1060 => "服务不存在，请运行「安装大屏服务.cmd」。",
                            _ => "请看大屏程序目录 logs 文件夹里今天的日志。",
                        };
                        OpsLog.Append($"启动大屏服务失败：sc start 返回 {code}");
                        MessageBox.Show(this, $"启动大屏服务失败（sc start 返回 {code}）。\n\n{hint}", "打开大屏", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }

                    OpsLog.Append("启动大屏服务（sc start BoltPreloadMonitor）");
                }
                else
                {
                    var installer = FindDashboardFile("安装大屏服务.cmd");
                    if (installer is null)
                    {
                        MessageBox.Show(this, $"本机 {url} 没有大屏在运行，旁边也没找到「安装大屏服务.cmd」。\n\n大屏在别的机器上的话，在 settings.json 里填 DashboardUrl（如 http://192.168.1.20:5080/）。",
                            "打开大屏", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }

                    var go = MessageBox.Show(this, $"本机还没有安装大屏服务。现在安装并启动？（需要管理员权限，会弹出确认）\n\n{installer}",
                        "打开大屏", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (go != DialogResult.Yes) return;
                    var code = await RunElevatedAsync("cmd.exe", $"/c \"\"{installer}\" nobrowser < nul\"", Path.GetDirectoryName(installer));
                    if (code is null) return;
                    if (code != 0)
                    {
                        OpsLog.Append($"安装大屏服务失败：脚本退出码 {code}");
                        MessageBox.Show(this, $"安装脚本没有成功（退出码 {code}）。\n\n请右键「安装大屏服务.cmd」以管理员身份运行一次，看它的提示。", "打开大屏", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }

                    OpsLog.Append($"安装并启动大屏服务：{installer}");
                }

                _lblLog.Text = "大屏服务启动中……";
                for (var i = 0; i < 30 && !await DashboardAliveAsync(url); i++) await Task.Delay(500);
                if (!await DashboardAliveAsync(url))
                {
                    _lblLog.Text = "大屏服务还没响应";
                    MessageBox.Show(this, "大屏服务还没有响应，请看大屏程序目录 logs 文件夹里今天的日志。", "打开大屏", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                _lblLog.Text = "大屏服务已启动";
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "打开大屏失败：" + ex.Message, "打开大屏", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _btnDash.Enabled = true;
        }
    }

    /// <summary>大屏网页服务在不在：问两次 /api/state，各 2.5 秒。刚起来那几秒它在连设备、开库，第一次没回不算死。</summary>
    private static async Task<bool> DashboardAliveAsync(string url)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(2500) };
                using var r = await http.GetAsync(new Uri(new Uri(url), "/api/state"));
                if (r.IsSuccessStatusCode) return true;
            }
            catch
            {
                // 连不上/超时：再试一次
            }

            if (attempt == 0) await Task.Delay(500);
        }

        return false;
    }

    /// <summary>本机有没有装大屏的 Windows 服务（sc query 的退出码：0 有，1060 没有）。</summary>
    private static bool DashboardServiceInstalled()
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("sc.exe", "query BoltPreloadMonitor")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            });
            if (p is null) return false;
            if (!p.WaitForExit(3000) && !p.HasExited) try { p.Kill(); } catch { }
            return p.HasExited && p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>大屏目录里的某个文件：优先自己旁边（同一个文件夹发布），其次现场配置里选过的大屏目录。</summary>
    private string? FindDashboardFile(string name)
    {
        foreach (var dir in new[] { AppContext.BaseDirectory, _dashboardDir })
        {
            if (dir is null) continue;
            var path = Path.Combine(dir, name);
            if (File.Exists(path)) return path;
        }

        return null;
    }

    /// <summary>
    /// 以管理员身份运行一个命令并等它结束（UAC 弹窗），返回它的退出码；用户在 UAC 上取消、或一分钟还没结束返回空。
    /// 窗口隐藏，不闪黑框。退出码一定要看：sc start 在服务被禁用时返回 1058、不存在返回 1060，命令"跑完了"不等于"起来了"
    /// </summary>
    private async Task<int?> RunElevatedAsync(string file, string args, string? workDir)
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file, args)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                WorkingDirectory = workDir ?? AppContext.BaseDirectory,
            });
            if (p is null) return null;
            var exited = await Task.Run(() => p.WaitForExit(60_000));
            if (!exited)
            {
                _lblLog.Text = "命令一分钟没有结束";
                return null;
            }

            return p.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            _lblLog.Text = "已取消（需要管理员权限）";
            return null;
        }
    }

    private void ShowParamsDialog()
    {
        if (_engine == null || !_engine.IsConnected)
        {
            MessageBox.Show(this, "请先连接设备", "参数下发", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var monitored = _channels.Keys.OrderBy(c => c).ToArray();
        HoldDevice();
        try
        {
            using (var dlg = new ParamsDialog(_engine, monitored, _selected))
            {
                dlg.ShowDialog(this);

                foreach (var kv in dlg.DeviceValues)
                {
                    if (!_channels.TryGetValue(kv.Key, out var c)) continue;
                    foreach (var item in kv.Value)
                    {
                        if (item.Value == "-") continue;
                        c.Cfg[item.Key] = item.Value;
                    }

                    // 参数变了，显示平均队列里的旧帧不再可比，从头攒
                    if (dlg.ChangedChannels.Contains(kv.Key)) c.Dt.Clear();
                }

                if (dlg.WroteAnything) _ = NotifyDashboardResyncAsync("桌面端下发了参数");
            }
        }
        finally
        {
            ReleaseDevice();
        }

        // 参数对话框关闭时自己恢复了监测，"已停止"随之结束
        _paused = false;
        _btnPause.Text = "停止";
        StylePause();
        UpdateParams();
        foreach (var c in _channels.Values) UpdateRow(c);
        ShowMonitorState();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        var wa = Screen.FromControl(this).WorkingArea;
        MinimumSize = new Size(Math.Min(MinimumSize.Width, wa.Width), Math.Min(MinimumSize.Height, wa.Height));
        if (Width > wa.Width || Height > wa.Height)
        {
            Size = new Size(Math.Min(Width, wa.Width), Math.Min(Height, wa.Height));
            Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Top + (wa.Height - Height) / 2);
        }

        PlaceSplitter();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        PlaceSplitter();
    }

    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        if ((_connected || _scanning) && !_closing)
        {
            e.Cancel = true;
            _closing = true;
            Hide();
            try { await DisconnectAsync(); } catch { }
            Close();
            return;
        }

        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _trace?.Dispose();
        _trace = null;
        base.OnFormClosed(e);
    }

    private void Post(Action a)
    {
        if (IsDisposed || Disposing) return;
        try { BeginInvoke(a); } catch (ObjectDisposedException) { }
    }

    private void Absorb(ChannelResult m)
    {
        if (_scanning) return;
        if (!_channels.TryGetValue(m.Channel, out var c))
        {
            // 不是本程序在轮询的通道：是另一路 TCP 客户端（大屏）的回包串过来了。记一下、状态栏提一句，不建行
            var first = !_foreign.ContainsKey(m.Channel);
            _foreign[m.Channel] = DateTime.UtcNow;
            if (first) ShowMonitorState();
            return;
        }

        c.Last = m;
        c.LastAt = DateTime.UtcNow;
        _stallStrikes = 0;
        if (m.EchoWave != null && m.EchoWave.WaveData != null && m.EchoWave.WaveData.Count > 0)
        {
            c.LastWave = m.EchoWave;
            c.Snr = DeviceEngine.Snr(m.EchoWave) ?? c.Snr;
        }

        if (ShownDelta(c) is { } dt)
        {
            c.Dt.Enqueue(dt);
            while (c.Dt.Count > 20) c.Dt.Dequeue();
        }

        if (m.EchoTime is { } echo)
        {
            c.Echo.Enqueue(echo);
            while (c.Echo.Count > 20) c.Echo.Dequeue();
        }

        if ((m.SpTemp ?? m.RawTemp) is { } temp)
        {
            c.Temp.Enqueue(temp);
            while (c.Temp.Count > 20) c.Temp.Dequeue();
        }

        UpdateRow(c);
        if (_selected == 0) _selected = m.Channel;
        if (m.Channel == _selected) RefreshSelected();
        CaptureTrace(c);
    }

    /// <summary>轴向力显示值：厂家 measVal = k·Δt+b。和大屏主数字同一套 CalPlan.ClampKn。</summary>
    private string ForceText(ushort ch, decimal? deltaTof)
        => deltaTof is { } dt && _plan.ClampKn(ch, dt) is { } f ? f.ToString("F2") : "--";

    private string ForceTip(ushort ch, decimal? deltaTof)
    {
        if (deltaTof is not { } dt) return "没有声时差，算不出轴向力";
        if (_plan.CalFor(ch) is not { } cal) return "没有标定文件也没有试件规格，不显示轴向力";
        var clamp = dt * cal.K + cal.B;
        return $"轴向力 = {cal.K:F4}×{dt:F2} + {cal.B:F2} = {clamp:F2} kN（{_plan.CalSourceFor(ch)}）";
    }

    /// <summary>与厂家同一套：farm.json tcMode&gt;0 时温补后再减参考声时；关掉则就是回波 − 参考。</summary>
    private decimal? ShownDelta(ChLive c)
    {
        var temp = DeviceEngine.CompTemp(c.Last);
        return DeviceEngine.DeltaTof(c.Cfg, c.Last?.EchoTime, temp, _plan.TcMode, _plan.TempCoefficient)
               ?? c.Last?.DeltaTof;
    }

    /// <summary>右侧「硬件调节」页跟着当前通道设备上的参数走；测量模式下拉和「计算参数」页一起刷。</summary>
    private void UpdateParams()
    {
        var has = _channels.TryGetValue(_selected, out var c) && c.Cfg.Count > 0;
        _hw.ShowValues(has ? _selected : (ushort)0, has ? c!.Cfg : null);
        SyncModeUi();
        SyncTcUi();
    }

    /// <summary>这一路多久没来帧就算"无数据"。轮询几十路时每路几秒一帧，60 秒足够。</summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(60);

    private static bool IsStale(ChLive c) => c.Last != null && c.LastAt != default && DateTime.UtcNow - c.LastAt > StaleAfter;

    /// <summary>信噪比低于噪声门。数字照常显示，状态列注明探头无信号。大屏用文字顶掉数字。</summary>
    private static bool NoSignal(ChLive c) => c.Snr is { } s && s < DeviceEngine.NoiseSnrDb;

    /// <summary>预期回波比窗尾还晚才算不符。起点晚于预期回波不算，杆长算出的预期偏早。</summary>
    private bool EchoOutsideWindow(ChLive c)
    {
        if (_plan.ExpectedEchoNs(c.Ch) is not { } exp) return false;
        if (DeviceEngine.Num(c.Cfg, "EchoWin") is not { } win || win <= 0) return false;
        var width = (DeviceEngine.Num(c.Cfg, "EchoWidth") ?? 15m) * 1000m;
        return exp > win + width;
    }

    private void UpdateRow(ChLive c)
    {
        if (c.Row == null || c.Last == null) return;
        c.Row.SubItems[1].Text = _plan.Describe(c.Ch) + (_plan.HasOwnSpec(c.Ch) && _plan.SpecFor(c.Ch) is { } sp ? $" · {sp.DisplayName}" : string.Empty);
        c.Row.SubItems[5].Text = c.Snr.HasValue ? c.Snr.Value.ToString("0") : "--";

        if (IsStale(c))
        {
            var ago = (int)(DateTime.UtcNow - c.LastAt).TotalSeconds;
            c.Row.SubItems[2].Text = "--";
            c.Row.SubItems[3].Text = "--";
            c.Row.SubItems[4].Text = "--";
            c.StatusCode = "stale";
            c.Row.SubItems[6].Text = ago >= 3600 ? $"无数据 {ago / 3600} 小时" : ago >= 120 ? $"无数据 {ago / 60} 分钟" : $"无数据 {ago} 秒";
            PaintListRow(c.Row, Ui.Muted);
            return;
        }

        var (dt, _, temp) = Displayed(c);
        var normal = c.Last.StatusCode == MeasStatusCode.Normal;
        var weak = c.Snr is { } snr && snr < DeviceEngine.ProbeSnrDb;

        // 状态词和大屏同一份口径（FxDevice.Health.Judge：失配 → 无信号 → 异常 → 未标定 → 基线可疑 → 未配额定 → 松动/偏低/正常），
        // 同一颗螺栓两边不光是同一个数，也是同一个词。在位只按这一帧的信噪比粗判（大屏是多帧统计），灰区不下结论
        var mismatch = Health.IsBaselineMismatch(dt);
        var presence = c.Snr is { } s2 ? (s2 < DeviceEngine.NoiseSnrDb ? "none" : s2 >= DeviceEngine.ProbeSnrDb ? "probe" : "unknown") : "unknown";
        var estKn = !mismatch && dt is { } d ? _plan.EstKn(c.Ch, d) : null;
        // 能进列表的都是这次扫到过探头的。信噪比掉下去算无信号，不算未接入。
        var health = Health.Judge(normal, mismatch, presence, _plan, c.Ch, estKn, seenProbe: true);
        // 数字照常显示（和厂家软件一致），无信号/窗不符只在状态列注明
        var winOff = health != Health.NoProbe && EchoOutsideWindow(c);
        c.StatusCode = winOff ? "win"
            : health == Health.Fault ? "fault:" + c.Last.StatusCode
            : weak && health is Health.Ok or Health.Unrated or Health.Uncal ? health + "+weak"
            : health;
        c.Row.SubItems[2].Text = ForceText(c.Ch, dt);
        c.Row.SubItems[3].Text = dt?.ToString("F2") ?? "--";
        c.Row.SubItems[4].Text = temp?.ToString("F2") ?? "--";
        c.Row.SubItems[6].Text = winOff ? "回波窗不符"   // 规格说回波在窗外，抓到的不是这颗螺栓的回波，数字不可信
            : health == Health.Fault ? DeviceEngine.DescribeStatus(c.Last.StatusCode)   // 设备给的具体原因比"检测异常"有用
            : Health.Text(health) + (weak && health is Health.Ok or Health.Unrated or Health.Uncal ? " · 信号弱" : string.Empty);
        var statusColor = health is Health.Loose or Health.Fault or Health.NoProbe or Health.Shift ? Ui.Danger
            : winOff || weak || health is Health.Warn or Health.BadBase ? Ui.Warn
            : health is Health.Uncal or Health.Unrated ? Ui.Muted
            : Ui.Ok;
        PaintListRow(c.Row, statusColor);
    }

    private void RefreshSelected()
    {
        if (!_channels.TryGetValue(_selected, out var c) || c.Last == null)
        {
            foreach (var v in _cardValues) v.Text = "--";
            _cardValues[0].ForeColor = Ui.Accent;
            _tip.SetToolTip(_cardValues[0], _selected == 0 ? "" : $"CH{_selected} 还没有收到这一路的测量帧");
            _wave.Live = null;
            _wave.EchoRegionStart = null;
            _wave.EchoRegionEnd = null;
            _wave.Mark = null;
            _wave.Front = null;
            _wave.Badge = null;
            _wave.Invalidate();
            SyncModeUi();
            return;
        }

        var m = c.Last;

        var (dt, echo, temp) = Displayed(c);
        if (IsStale(c))
        {
            // 久无数据：六个卡片全空，波形照画
            foreach (var v in _cardValues) v.Text = "--";
            _cardValues[0].ForeColor = Ui.Accent;
            _tip.SetToolTip(_cardValues[0], $"CH{c.Ch} 已 {(int)(DateTime.UtcNow - c.LastAt).TotalSeconds} 秒没有收到数据");
        }
        else
        {
            // 有帧就显示数（和厂家软件一致），探头无信号只在悬停提示里注明，人看波形自己判断
            _cardValues[0].Text = ForceText(c.Ch, dt);
            // 试件规格里的下限/上限只管显示：出了范围数字变红（厂家那一套），判定仍按额定值
            var force = dt is { } d0 ? _plan.ClampKn(c.Ch, d0) : null;
            var spec = _plan.SpecFor(c.Ch);
            var outOfRange = force is { } f && spec is { } && ((spec.LowerKn is { } lo && f < lo) || (spec.UpperKn is { } up && f > up));
            _cardValues[0].ForeColor = outOfRange ? Ui.Danger : Ui.Accent;
            _tip.SetToolTip(_cardValues[0], ForceTip(c.Ch, dt) + (_avgFrames > 1 ? $"（最近 {_avgFrames} 帧平均）" : string.Empty)
                + (outOfRange ? $"\n超出试件规格的显示范围 {spec!.LowerKn:0.#}～{spec.UpperKn:0.#} kN" : string.Empty)
                + (NoSignal(c) ? $"\n探头无信号（信噪比 {c.Snr:0} dB，低于噪声门 {DeviceEngine.NoiseSnrDb:0} dB）：波形里没有像样的回波，这个数可能是噪声算出来的" : string.Empty));
            _cardValues[1].Text = temp?.ToString("F2") ?? "--";
            _cardValues[2].Text = echo?.ToString("F2") ?? "--";
            _cardValues[3].Text = dt.HasValue
                ? (dt.Value * Ucf.UmPerNs).ToString("F2")
                : "--";
            _cardValues[4].Text = dt?.ToString("F2") ?? "--";
            _cardValues[5].Text = StdDev(c.Dt)?.ToString("F2") ?? "--";
        }

        var lw = c.LastWave;
        if (lw != null && lw.WaveData != null && lw.WaveData.Count > 0)
        {
            _wave.Live = lw.WaveData.ToArray();
            _wave.LiveStart = lw.EchoStart;
            _wave.LiveClock = lw.ClockInt;
            // 厂家右下角的 SNR/DL：用 SDK 同一个算法（能量法信噪比 + 5σ 检测阈值）算这一帧
            try
            {
                var (snr, dl) = FxSDK.Utility.SignalProcessor.CalcSnrAuto(lw.WaveData.Select(v => (double)v).ToArray());
                _wave.Badge = $"SNR:{snr:F1}dB / DL:{dl:0}mV";
            }
            catch (Exception)
            {
                _wave.Badge = null;
            }

            // SDK 判出的主回波区和辅助点（EchoAssist 里是插值信号的索引，换成 ns）
            var a = m.Assist;
            decimal? At(decimal? idx) => idx is { } i ? lw.EchoStart + i * lw.ClockInt : null;
            _wave.EchoRegionStart = At(a?.Sx);
            _wave.EchoRegionEnd = At(a?.Ex);
            _wave.Mark = a?.Mx is { } mx && a.My is { } my ? (At(mx)!.Value, my) : null;
            _wave.Front = a?.Fx is { } fx && a.Fy is { } fy ? (At(fx)!.Value, fy) : null;
        }

        var rw = _engine?.RefWave(c.Ch);
        if (rw != null && rw.WaveData != null && rw.WaveData.Count > 0)
        {
            _wave.Ref = rw.WaveData.ToArray();
            _wave.RefStart = rw.EchoStart;
            _wave.RefClock = rw.ClockInt;
        }
        else
        {
            _wave.Ref = null;
        }

        // 横轴按长度画时用这一路试件材料的声速；没规格按碳钢 5900
        var mat = _plan.SpecFor(c.Ch) is { } sp2 ? _plan.Library.MaterialFor(sp2.Grade) : null;
        _wave.VelocityMps = mat is { Velocity: > 0 } ? (double)mat.Velocity : 5900;
        _wave.Marker = m.EchoTime;
        _wave.Invalidate();
        SyncModeUi();
    }

    // ── 连续记录（每一帧读数，不含波形，data\连续记录.db）────────────────────────────────────

    private void TryOpenTrace()
    {
        try
        {
            _trace = new TraceStore(TraceStore.PathFor(AppContext.BaseDirectory), _traceRetainDays);
            UpdateTraceLabel();
        }
        catch (Exception ex)
        {
            _traceBroken = true;
            _chkTrace.Enabled = false;
            _cmbTraceRun.Enabled = false;
            _btnTraceToday.Enabled = false;
            _btnTraceNew.Enabled = false;
            _btnTraceExport.Enabled = false;
            _lblTrace.ForeColor = Ui.Danger;
            _lblTrace.Text = "连续记录没能打开";
            _tip.SetToolTip(_lblTrace, ex.Message);
            _lblLog.Text = "连续记录数据库打不开：" + ex.Message;
            OpsLog.Append("连续记录数据库打不开：" + ex.Message);
            return;
        }

        ReloadTraceRuns(_trace.CurrentRunId);
    }

    private void UpdateTraceLabel()
    {
        if (_traceBroken || _trace == null) return;
        var err = _trace.LastError;
        if (err.Length > 0)
        {
            _lblTrace.ForeColor = Ui.Danger;
            _lblTrace.Text = "写入失败";
            _tip.SetToolTip(_lblTrace, err);
            if (err != _shownTraceError)
            {
                _shownTraceError = err;
                _lblLog.Text = "连续记录写入失败：" + err;
                OpsLog.Append("连续记录写入失败：" + err);
            }

            return;
        }

        if (_shownTraceError.Length > 0) _shownTraceError = "";
        _lblTrace.ForeColor = Ui.Muted;
        _tip.SetToolTip(_lblTrace, $"{_trace.Path}\n保留 {_trace.RetainDays} 天，更早的自动删除");
        var keep = $"保留 {_trace.RetainDays} 天";
        if (!_traceOn)
        {
            _lblTrace.Text = $"未在记录 · {keep}";
            return;
        }

        var stat = _trace.CurrentStat();
        var n = stat.Frames;
        var dropped = _trace.Dropped;
        _lblTrace.Text = dropped > 0 ? $"{n:N0} 帧，丢弃 {dropped:N0} · {keep}" : $"{n:N0} 帧 · {keep}";
    }

    /// <summary>把这一帧的原始读数丢进连续记录。界面上的多帧平均不进库，波形不记。</summary>
    private void CaptureTrace(ChLive c)
    {
        if (!_traceOn || _trace == null || _paused || c.Last == null) return;
        try
        {
            var dt = ShownDelta(c);
            var echo = c.Last.EchoTime;
            var temp = c.Last.SpTemp ?? c.Last.RawTemp;
            var spec = _plan.SpecFor(c.Ch);
            var pos = _plan.Describe(c.Ch);
            var fi = _plan.FlangeOf(c.Ch);
            var status = c.StatusCode.Length > 0 ? c.StatusCode : Health.Fault;
            _trace.Enqueue(new TraceSample
            {
                Ts = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
                Ch = c.Ch,
                Specimen = spec?.Name is { Length: > 0 } name ? name : pos.Length > 0 ? pos : $"CH{c.Ch}",
                Group = fi >= 0 ? _plan.Flanges[fi].Name : string.Empty,
                Temp = D(temp),
                Echo = D(echo),
                Delta = D(dt),
                Elong = dt is { } d1 ? (double)(d1 * Ucf.UmPerNs) : null,
                Kn = dt is { } d2 && _plan.ClampKn(c.Ch, d2) is { } kn ? (double)kn : null,
                RefNs = D(DeviceEngine.Num(c.Cfg, "RefTime")),
                RefTemp = D(DeviceEngine.Num(c.Cfg, "RefTemp")),
                Std = D(StdDev(c.Dt)),
                Snr = c.Snr,
                Status = status,
                RunId = _trace.CurrentRunId,
            });
        }
        catch (Exception ex)
        {
            _lblLog.Text = "连续记录没记上：" + ex.Message;
        }

        static double? D(decimal? v) => v is { } d ? (double)d : null;
    }

    private void StartTraceToday()
    {
        if (_trace == null) return;
        var name = $"今天 {DateTime.Today:yyyy-MM-dd}";
        var runs = _trace.ListRuns();
        var today = runs.FirstOrDefault(r => r.Name == name);
        var current = runs.FirstOrDefault(r => r.Open);
        if (today != null && current?.Id == today.Id)
        {
            _lblLog.Text = $"已经在记「{name}」，接着测就行。换工况请用「从现在开始」。";
            return;
        }

        if (today != null)
        {
            var where = current is null ? "当前没有正在记的段" : $"现在正在记「{current.Name}」";
            if (MessageBox.Show(this,
                    $"{where}。\n回到「{name}」继续记？用「从现在开始」另开的段不会被并进去。",
                    "从今天开始", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                return;
            try
            {
                var run = _trace.Resume(today.Id);
                ReloadTraceRuns(run.Id);
                SetTraceDatesToToday();
                UpdateTraceLabel();
                OpsLog.Append($"连续记录回到：{run.Name}");
                _lblLog.Text = $"回到「{run.Name}」，接着记";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "从今天开始", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            return;
        }

        if (MessageBox.Show(this,
                $"从今天 0 点起另开一段「{name}」。\n今天已经记下的行会归到这一段，更早的留在原来的段里。",
                "从今天开始", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
            return;
        try
        {
            var run = _trace.BeginToday();
            ReloadTraceRuns(run.Id);
            SetTraceDatesToToday();
            UpdateTraceLabel();
            OpsLog.Append($"连续记录从今天开始：{run.Name}");
            _lblLog.Text = $"正在记录「{run.Name}」";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "从今天开始", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void StartTraceSeparate()
    {
        if (_trace == null) return;
        var name = AskRunName();
        if (name == null) return;
        try
        {
            var run = _trace.BeginSeparate(name);
            ReloadTraceRuns(run.Id);
            SetTraceDatesToToday();
            UpdateTraceLabel();
            OpsLog.Append($"连续记录从现在开始：{run.Name}");
            _lblLog.Text = $"从现在起记录「{run.Name}」。导出时下拉框选这一段，之前的不会一起出来。";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "从现在开始", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ReloadTraceRuns(long selectId)
    {
        if (_trace == null) return;
        _recUiLock = true;
        try
        {
            _cmbTraceRun.Items.Clear();
            foreach (var r in _trace.ListRuns()) _cmbTraceRun.Items.Add(r);
            var pick = _cmbTraceRun.Items.Cast<TraceRun>().FirstOrDefault(r => r.Id == selectId);
            if (pick != null) _cmbTraceRun.SelectedItem = pick;
        }
        finally
        {
            _recUiLock = false;
        }
    }

    private void SetTraceDatesToToday()
    {
        _recUiLock = true;
        try
        {
            _dtFrom.Value = DateTime.Today;
            _dtTo.Value = DateTime.Today;
        }
        finally
        {
            _recUiLock = false;
        }

        RefreshRecordGrid();
    }

    private string? AskRunName()
    {
        using var form = new Form
        {
            Text = "从现在开始",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(420, 148),
            Font = new Font("Microsoft YaHei UI", 9f),
        };
        var label = new Label
        {
            Text = "写下这一段的工况，例如「扭到500牛·m」。从这一刻起才记进这一段；之前的留在上一段，导出时选这一段就不会带上。",
            AutoSize = false,
            Bounds = new Rectangle(12, 10, 396, 52),
        };
        var box = new TextBox
        {
            Text = "",
            Bounds = new Rectangle(12, 68, 396, 24),
        };
        var ok = new Button { Text = "开始", DialogResult = DialogResult.OK, Bounds = new Rectangle(244, 106, 76, 28) };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Bounds = new Rectangle(332, 106, 76, 28) };
        form.Controls.Add(label);
        form.Controls.Add(box);
        form.Controls.Add(ok);
        form.Controls.Add(cancel);
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        if (form.ShowDialog(this) != DialogResult.OK) return null;
        var name = box.Text.Trim();
        if (name.Length == 0) name = $"现在 {DateTime.Now:HH:mm}";
        return name;
    }

    private async Task ExportTraceExcelAsync()
    {
        if (_trace == null)
        {
            MessageBox.Show(this, "连续记录数据库没打开，导不出来。", "导出Excel", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (_cmbTraceRun.SelectedItem is not TraceRun run)
        {
            MessageBox.Show(this, "先在下拉框里选要导出的那一段。", "导出Excel", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var from = _dtFrom.Value.Date;
        var to = _dtTo.Value.Date.AddDays(1);
        if (to <= from)
        {
            MessageBox.Show(this, "结束日期要比开始日期晚。", "导出Excel", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        TraceSpan span;
        UseWaitCursor = true;
        try
        {
            span = await Task.Run(() => _trace.Count(from, to, run.Id));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "读连续记录失败：" + ex.Message, "导出Excel", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        finally
        {
            UseWaitCursor = false;
        }

        if (span.Frames == 0)
        {
            MessageBox.Show(this, $"「{run.Name}」在这个日期范围内没有连续记录。", "导出Excel",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (span.LiveWaves > 1_048_575)
        {
            MessageBox.Show(this,
                $"这个日期范围内按时刻展开约 {span.LiveWaves:N0} 行（{span.Frames:N0} 条测量），Excel 一个工作表最多约 104 万行。请把上面的日期范围缩短后再导出。",
                "导出Excel", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (span.Frames > 30_000)
        {
            var ask = MessageBox.Show(this,
                $"将导出 {span.Frames:N0} 条测量。行数比较多，生成时请稍等。继续？",
                "导出Excel", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (ask != DialogResult.OK) return;
        }

        var end = to.AddDays(-1);
        using var dlg = new SaveFileDialog
        {
            Filter = "Excel 工作簿|*.xlsx",
            FileName = from.Date == end.Date ? $"连续记录-{from:yyyyMMdd}.xlsx" : $"连续记录-{from:yyyyMMdd}-{end:yyyyMMdd}.xlsx",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        _btnTraceExport.Enabled = false;
        UseWaitCursor = true;
        _lblLog.Text = "正在导出连续记录…";
        try
        {
            var path = dlg.FileName;
            var progress = new Progress<string>(s => _lblLog.Text = s);
            var result = await Task.Run(() => _trace.ExportExcel(path, from, to, run.Id, progress));
            _lblLog.Text = $"已导出 Excel：{result.Path}";
            OpsLog.Append($"导出连续记录 {result.Frames} 行 {result.Channels} -> {result.Path}");
            MessageBox.Show(this,
                $"已导出 {result.Frames:N0} 行。每一行是一个时刻，同一通道的各项挨在一起。\n\n{result.Path}",
                "导出Excel", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            _lblLog.Text = "导出 Excel 失败：" + ex.Message;
            MessageBox.Show(this, "导出失败：" + ex.Message, "导出Excel", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _btnTraceExport.Enabled = true;
            UseWaitCursor = false;
        }
    }

    // ── 手动测量结果（厂家「测回」）───────────────────────────────────────────────────────────

    /// <summary>「测回」：把当前这一路此刻显示的读数记成一行，追加进 data\手动测量结果.csv。</summary>
    private void RecordCurrent()
    {
        if (!_channels.TryGetValue(_selected, out var c) || c.Last == null)
        {
            MessageBox.Show(this, "请先在列表里选中一个有数据的通道。", "测回", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (IsStale(c))
        {
            MessageBox.Show(this, $"CH{c.Ch} 已 {(int)(DateTime.UtcNow - c.LastAt).TotalSeconds} 秒没有收到数据，没有可记的读数。", "测回",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var (dt, echo, temp) = Displayed(c);
        var spec = _plan.SpecFor(c.Ch);
        var pos = _plan.Describe(c.Ch);
        var fi = _plan.FlangeOf(c.Ch);
        var r = new ManualRecord
        {
            Time = DateTime.Now,
            Ch = c.Ch,
            Specimen = spec?.Name is { Length: > 0 } n ? n : pos.Length > 0 ? pos : $"CH{c.Ch}",
            Group = fi >= 0 ? _plan.Flanges[fi].Name : string.Empty,
            TempC = temp,
            EchoNs = echo,
            DeltaNs = dt,
            ElongUm = dt is { } d1 ? d1 * Ucf.UmPerNs : null,
            AxialKn = dt is { } d2 ? _plan.ClampKn(c.Ch, d2) : null,
            StdNs = StdDev(c.Dt),
        };
        try
        {
            _records.Append(r);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "记录写不进文件：" + ex.Message + "\n\n" + _records.Path, "测回", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        OpsLog.Append($"测回 CH{c.Ch} {r.Specimen}：温度 {Fmt(r.TempC)} ℃，回波声时 {Fmt(r.EchoNs)} ns，声时差 {Fmt(r.DeltaNs)} ns，轴向力 {Fmt(r.AxialKn)} kN");
        RefreshRecordSpecChoices();
        RefreshRecordGrid(selectLast: true);
        if (_recCollapsed) SetRecordsCollapsed(false, save: false);
        _lblLog.Text = $"已记录 CH{c.Ch} {r.Specimen}（{r.Time:HH:mm:ss}），共 {_records.Items.Count} 行";
    }

    private static string Fmt(decimal? v) => v is { } n ? n.ToString("0.00") : "--";

    private void LoadRecordsUi()
    {
        _recUiLock = true;
        try
        {
            var earliest = _records.Items.Count > 0 ? _records.Items.Min(r => r.Time).Date : DateTime.Today;
            if (_trace?.EarliestLocal() is { } first && first.Date < earliest) earliest = first.Date;
            _dtFrom.Value = earliest;
            _dtTo.Value = DateTime.Today;
        }
        finally
        {
            _recUiLock = false;
        }

        RefreshRecordSpecChoices();
        RefreshRecordGrid();
        SetRecordsCollapsed(_recCollapsed, save: false);
        if (_records.FileUnreadable)
            _lblLog.Text = "手动测量结果文件读不了，新的测回会接着往下记；改备注和删行先不覆盖原文件";
        else if (_records.KeptLines > 0)
            _lblLog.Text = $"手动测量结果有 {_records.KeptLines} 行读不懂，改备注或删行时这些行会原样留在文件里";
    }

    private void RefreshRecordSpecChoices()
    {
        _recUiLock = true;
        try
        {
            var keep = _cmbRecSpec.SelectedItem as string;
            _cmbRecSpec.Items.Clear();
            _cmbRecSpec.Items.Add("全部");
            foreach (var n in _records.Items.Select(r => r.Specimen).Where(s => s.Length > 0).Distinct().OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
                _cmbRecSpec.Items.Add(n);
            _cmbRecSpec.SelectedItem = keep is { } && _cmbRecSpec.Items.Contains(keep) ? keep : "全部";
        }
        finally
        {
            _recUiLock = false;
        }
    }

    private IEnumerable<ManualRecord> FilteredRecords()
    {
        var spec = _cmbRecSpec.SelectedItem as string;
        var from = _dtFrom.Value.Date;
        var to = _dtTo.Value.Date.AddDays(1);
        return _records.Items.Where(r => r.Time >= from && r.Time < to && (spec is null || spec == "全部" || r.Specimen == spec));
    }

    private void RefreshRecordGrid(bool selectLast = false)
    {
        _recGrid.SuspendLayout();
        _recGrid.Rows.Clear();
        foreach (var r in FilteredRecords().OrderBy(r => r.Time))
        {
            var i = _recGrid.Rows.Add(r.Time.ToString("yyyy-MM-dd HH:mm:ss.fff"), r.Ch, r.Specimen, r.Group,
                Fmt(r.TempC), Fmt(r.EchoNs), Fmt(r.DeltaNs), Fmt(r.ElongUm), Fmt(r.AxialKn), Fmt(r.StdNs), r.Note);
            _recGrid.Rows[i].Tag = r;
        }

        _recGrid.ResumeLayout();
        _lblRecCount.Text = $"数据行数 {_recGrid.Rows.Count}" + (_recGrid.Rows.Count != _records.Items.Count ? $" / 共 {_records.Items.Count}" : string.Empty);
        if (selectLast && _recGrid.Rows.Count > 0)
        {
            _recGrid.ClearSelection();
            var last = _recGrid.Rows[_recGrid.Rows.Count - 1];
            last.Selected = true;
            _recGrid.FirstDisplayedScrollingRowIndex = last.Index;
        }
    }

    private void OnRecordNoteEdited(int rowIndex)
    {
        if (rowIndex < 0 || rowIndex >= _recGrid.Rows.Count || _recGrid.Rows[rowIndex].Tag is not ManualRecord r) return;
        var note = Convert.ToString(_recGrid.Rows[rowIndex].Cells["note"].Value)?.Trim() ?? string.Empty;
        if (note == r.Note) return;
        r.Note = note;
        try
        {
            _records.Save();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "备注写不进文件：" + ex.Message, "手动测量结果", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void DeleteSelectedRecords()
    {
        var picked = _recGrid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Tag).OfType<ManualRecord>().ToList();
        if (picked.Count == 0)
        {
            MessageBox.Show(this, "先在表里选中要删的行。", "手动测量结果", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (MessageBox.Show(this, $"删除选中的 {picked.Count} 行？文件里也会删掉。", "手动测量结果", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        foreach (var r in picked) _records.Items.Remove(r);
        try
        {
            _records.Save();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "删除后写文件失败：" + ex.Message, "手动测量结果", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        OpsLog.Append($"删除手动测量结果 {picked.Count} 行");
        RefreshRecordSpecChoices();
        RefreshRecordGrid();
    }

    private void ExportRecords()
    {
        var rows = FilteredRecords().OrderBy(r => r.Time).ToList();
        if (rows.Count == 0)
        {
            MessageBox.Show(this, "当前筛选下没有数据。", "导出", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dlg = new SaveFileDialog
        {
            Filter = "CSV 表格（Excel 直接打开）|*.csv",
            FileName = $"手动测量结果-{DateTime.Now:yyyyMMdd-HHmm}.csv",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            ManualRecords.Export(rows, dlg.FileName);
            _lblLog.Text = $"已导出 {rows.Count} 行：{dlg.FileName}";
            OpsLog.Append($"导出手动测量结果 {rows.Count} 行 -> {dlg.FileName}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "导出失败：" + ex.Message, "导出", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SetRecordsCollapsed(bool collapsed, bool save)
    {
        _recCollapsed = collapsed;
        _recPanel.Height = collapsed ? RecPanelCollapsedHeight : RecPanelHeight;
        _recGrid.Visible = !collapsed;
        _recExtra.Visible = !collapsed;
        _btnRecToggle.Text = collapsed ? "展开" : "收起";
        if (save) SaveSettings();
    }

    private static decimal? StdDev(IReadOnlyCollection<decimal> xs)
    {
        if (xs.Count < 2) return null;
        var avg = xs.Average();
        var sum = xs.Sum(x => (x - avg) * (x - avg));
        return (decimal)Math.Sqrt((double)(sum / xs.Count));
    }

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            var s = System.Text.Json.JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath), JsonOptions);
            if (s != null && !string.IsNullOrWhiteSpace(s.Endpoint)) _ipBox.Text = s.Endpoint;
            if (s != null && !string.IsNullOrWhiteSpace(s.DashboardDir) && Directory.Exists(s.DashboardDir)) _dashboardDir = s.DashboardDir;
            if (s != null && !string.IsNullOrWhiteSpace(s.DashboardUrl)) _dashboardUrl = s.DashboardUrl;
            if (s?.DisplayAvgFrames is { } n && AvgChoices.Contains(n)) _avgFrames = n;
            if (s?.WaveAxisMm is { } mm) _wave.AxisMm = mm;
            if (s?.RecordsCollapsed is { } rc) _recCollapsed = rc;
            if (s?.ContinuousRecord is { } traceOn) _traceOn = traceOn;
            if (s?.TraceRetainDays is { } days && days is >= 1 and <= 3650) _traceRetainDays = days;
        }
        catch (Exception)
        {
        }
    }

    private void SaveSettings()
    {
        try
        {
            var s = new Settings
            {
                Endpoint = _ipBox.Text.Trim(), DashboardDir = _dashboardDir, DashboardUrl = _dashboardUrl,
                DisplayAvgFrames = _avgFrames, WaveAxisMm = _wave.AxisMm, RecordsCollapsed = _recCollapsed,
                ContinuousRecord = _traceOn, TraceRetainDays = _traceRetainDays,
            };
            File.WriteAllText(SettingsPath, System.Text.Json.JsonSerializer.Serialize(s, JsonOptions));
        }
        catch (Exception ex)
        {
            OpsLog.Append("设置没能保存：" + ex.Message);
        }
    }

    /// <summary>
    /// 读 exe 旁的 farm.json（和大屏那份一样）与程序目录的 .ucf。farm.json 里一个风场有很多台设备，
    /// 按地址栏里的 IP 找到"我连的是哪台"，用它那台的接线/标定/额定/口径，列表多一列"位置"；
    /// 找不到对应设备（或没有 farm.json）就退回"程序目录最新 .ucf + 自由置零口径"的老行为，并在标签上说明。
    /// 连接时会按当时的地址再选一次。
    /// </summary>
    private void LoadPlan()
    {
        var notes = new List<string>();
        var farmRoot = CalPlan.FindFarm(AppContext.BaseDirectory, notes.Add);
        var farm = FarmPlan.Load(farmRoot, AppContext.BaseDirectory, notes.Add);
        var ep = ActiveEndpoint();
        var dev = farm.ForEndpoint(ep);
        if (dev is not null)
        {
            _plan = dev.Plan;
            _planLabel = $"F{dev.Turbine:00} 机组";
            _planTurbine = dev.Turbine;
            if (farm.Devices.Count > 1) notes.Add($"farm.json 有 {farm.Devices.Count} 台设备，按地址 {ep} 选中 {dev.Label}");
        }
        else
        {
            _plan = CalPlan.Load(null, AppContext.BaseDirectory, notes.Add);
            _planLabel = string.Empty;
            _planTurbine = 0;
            if (farm.HasFarm && farm.Devices.Count > 0)
            {
                notes.Add($"farm.json 里没有地址为 {ep} 的设备（有：{string.Join("、", farm.Devices.Select(d => d.Label))}），按无 farm.json 处理");
            }
        }

        foreach (var n in notes) OpsLog.Append(n);
        ShowPlan();
        SyncTcUi();
        if (_engine is { IsConnected: true } eng)
        {
            // 连着的时候改了现场配置：温度平滑和温度借用不用重连就生效
            eng.SetTempComp(_plan.TcMode, _plan.TempCoefficient);
            eng.SetTempTau(_plan.TempTau);
            _ = eng.SetTempFromAsync(_plan.TempFrom);
        }

        foreach (var c in _channels.Values) UpdateRow(c);
        RefreshSelected();
        RefreshSpecimenPanel(force: !_specimen.Dirty);
    }

    /// <summary>"F01 机组"：当前方案属于哪台机组，写在窗口标题里（厂家用工位软件的项目名）。</summary>
    private string _planLabel = string.Empty;

    /// <summary>当前方案的机组号（farm.json 里这台设备的 turbine），通知大屏重读基线时用；0 = 不知道。</summary>
    private int _planTurbine;
    private CancellationTokenSource? _dashHoldCts;
    private Task? _dashHold;
    private bool _dashHeld;
    private const int DashHoldMinutes = 5;

    /// <summary>桌面端要占这台设备。大屏让出连接，断开桌面端后再接回去。</summary>
    private async Task YieldDashboardAsync()
    {
        await StopDashHoldLoopAsync();
        var (ok, why) = await SetDashboardMaintenanceAsync(DashHoldMinutes);
        if (!ok)
        {
            _lblLog.Text = "未能让出大屏：" + why;
            MessageBox.Show(this,
                "大屏没有让出这台设备。\n" + why + "\n\n两边同时连着时，写参数可能互相打断。需要时请到大屏主机上点「现场维护」。",
                "打开桌面端", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _dashHeld = true;
        _lblLog.Text = "大屏已让出这台设备，断开后它会自己接回去";
        _dashHoldCts = new CancellationTokenSource();
        var token = _dashHoldCts.Token;
        _dashHold = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(2), token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                await SetDashboardMaintenanceAsync(DashHoldMinutes);
            }
        }, token);
    }

    private async Task StopDashHoldLoopAsync()
    {
        if (_dashHoldCts != null)
        {
            try { _dashHoldCts.Cancel(); } catch (ObjectDisposedException) { }
        }

        if (_dashHold != null)
        {
            try { await _dashHold; } catch { }
            _dashHold = null;
        }

        _dashHoldCts?.Dispose();
        _dashHoldCts = null;
    }

    /// <summary>桌面端不再占设备。先停续期，再让大屏恢复连接并重读基线。</summary>
    private async Task ReleaseDashboardAsync()
    {
        await StopDashHoldLoopAsync();

        if (!_dashHeld) return;
        _dashHeld = false;
        var (ok, _) = await SetDashboardMaintenanceAsync(0);
        if (ok) await NotifyDashboardResyncAsync("桌面端已断开，交还给大屏");
    }

    private async Task<(bool Ok, string Why)> SetDashboardMaintenanceAsync(int minutes)
    {
        var url = string.IsNullOrWhiteSpace(_dashboardUrl) ? LocalDashboardUrl : _dashboardUrl!.Trim();
        var turbine = _planTurbine > 0 ? _planTurbine.ToString() : "null";
        var body = $"{{\"turbine\":{turbine},\"minutes\":{minutes},\"note\":\"桌面端已连接\"}}";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            using var r = await http.PostAsync(new Uri(new Uri(url), "/api/maintenance"), content);
            if (r.IsSuccessStatusCode) return (true, "");
            if (r.StatusCode == System.Net.HttpStatusCode.Forbidden)
                return (false, $"{url} 只接受大屏主机本机调用（HTTP 403）。大屏在别的电脑上时，请到那台机器上点「现场维护」");
            if (r.StatusCode == System.Net.HttpStatusCode.BadRequest)
                return (false, "大屏不认得这台机组（farm.json 里没有对应设备）");
            return (false, $"HTTP {(int)r.StatusCode}");
        }
        catch (TaskCanceledException)
        {
            return (false, $"{url} 超时（大屏断开设备超过 15 秒还没应答）");
        }
        catch (Exception ex)
        {
            return (false, $"{url} 连不上：{ex.Message}");
        }
    }

    /// <summary>置零或下发参数后通知大屏重读基线。失败不处理，大屏每 10 分钟也会自己重读。</summary>
    private async Task NotifyDashboardResyncAsync(string why)
    {
        var url = string.IsNullOrWhiteSpace(_dashboardUrl) ? LocalDashboardUrl : _dashboardUrl!.Trim();
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var q = $"/api/resync?why={Uri.EscapeDataString(why)}" + (_planTurbine > 0 ? $"&turbine={_planTurbine}" : string.Empty);
            using var r = await http.PostAsync(new Uri(new Uri(url), q), null);
            _lblLog.Text = r.IsSuccessStatusCode ? $"已通知大屏重读基线（{why}）" : $"大屏未接受通知（HTTP {(int)r.StatusCode}），它会在 10 分钟内自行重读";
        }
        catch
        {
            _lblLog.Text = $"大屏 {url} 未响应，它会在 10 分钟内自行重读基线";
        }
    }

    private void ShowPlan()
    {
        var ver = $"螺栓预紧力监视器 v{Application.ProductVersion}";
        Text = _planLabel.Length > 0 ? $"{ver} — {_planLabel}" : ver;
    }
}
