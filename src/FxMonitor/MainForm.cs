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
        public readonly Queue<decimal> Dt = new();
        public Dictionary<string, string> Cfg = new();
        public ListViewItem? Row;
        public double? Snr;
    }

    private const int ChannelCount = 48;

    private readonly Dictionary<ushort, ChLive> _channels = new();
    private DeviceEngine? _engine;
    private CancellationTokenSource _cts = new();
    private bool _connected;
    private bool _closing;
    private ushort _selected;

    /// <summary>
    /// 接线、标定与口径：和大屏同一份 farm.json（放在 exe 旁）、同一套 CalPlan——同一颗螺栓两边算出来必须是同一个数。
    /// 没有 farm.json 时只有程序目录的 .ucf 兜底，口径无从得知，按自由置零口径显示 k·Δt+b 并在标签上说明。
    /// 没有任何标定文件就不显示夹紧力（没有出处的数不上界面）。
    /// </summary>
    private CalPlan _plan = CalPlan.Load(null, AppContext.BaseDirectory, _ => { });

    private readonly TextBox _ipBox = new() { Width = 150, Text = "192.168.0.7:23" };
    private readonly Button _btnConnect = new() { Text = "连接", Width = 88, Height = 30 };
    private readonly Button _btnScan = new() { Text = "扫描通道", Width = 88, Height = 30, Enabled = false };
    private readonly ToolTip _tip = new();
    private bool _scanning;
    private List<ushort> _unheard = new();
    private readonly Label _lblState = new() { AutoSize = true, Text = "未连接", ForeColor = Color.Gray };
    private readonly Label _lblCal = new() { AutoSize = true, Text = "标定：未加载标定文件（不显示夹紧力，请加载 .ucf）", ForeColor = Color.DarkOrange };
    private readonly Button _btnCal = new() { Text = "标定文件...", AutoSize = true, MinimumSize = new Size(92, 30) };
    private readonly Button _btnParams = new() { Text = "参数下发...", AutoSize = true, MinimumSize = new Size(92, 30), Enabled = false };
    private readonly Button _btnZero = new() { Text = "通道置零...", AutoSize = true, MinimumSize = new Size(92, 30), Enabled = false };
    // 现场配置不要求连着设备：没连也能改法兰/标定/额定；连着时扫到的通道会列出来供逐路接线
    private readonly Button _btnConfig = new() { Text = "现场配置...", AutoSize = true, MinimumSize = new Size(92, 30) };
    // 打开业主大屏网页；本机大屏服务没在跑、exe 又在手边（同一个文件夹发布）就先把它拉起来
    private readonly Button _btnDash = new() { Text = "打开大屏", AutoSize = true, MinimumSize = new Size(92, 30) };
    private readonly ListView _list = new()
    {
        View = View.Details,
        FullRowSelect = true,
        HideSelection = false,
        Dock = DockStyle.Fill,
        Font = new Font("Microsoft YaHei UI", 10.5f),
        MultiSelect = false,
        HeaderStyle = ColumnHeaderStyle.Nonclickable,
    };

    private SplitContainer _split = null!;
    private SplitContainer _leftStack = null!;

    private readonly ListView _params = new()
    {
        View = View.Details,
        FullRowSelect = true,
        Dock = DockStyle.Fill,
        Font = new Font("Microsoft YaHei UI", 9.5f),
        MultiSelect = false,
        HeaderStyle = ColumnHeaderStyle.Nonclickable,
        Scrollable = true,
    };

    private static readonly (string Key, string Cn)[] ParamOrder =
    {
        ("ProbeFreq", "探头频率 MHz"),
        ("EchoWin", "回波窗 ns"),
        ("EchoWidth", "回波宽度 us"),
        ("Duty", "激励占空比 %"),
        ("Forward", "正向脉冲数"),
        ("Reverse", "反向脉冲数"),
        ("Gain", "增益档"),
        ("MeasMode", "测量模式"),
        ("Threshold", "检波阈值"),
        ("RefTime", "参考声时 ns"),
        ("RefTemp", "参考温度 ℃"),
        ("LEchoWave", "参考波形"),
    };

    private readonly Label[] _cardValues = new Label[6];
    private static readonly string[] CardTitles = { "夹紧力 kN", "声时差 ns", "回波声时 ns", "温度 ℃", "伸长量 um", "标准差 ns" };

    private readonly WavePanel _wave = new() { Dock = DockStyle.Fill };
    private readonly CheckBox _chkRef = new() { Text = "显示参考回波", Checked = true, AutoSize = true };
    private readonly Label _lblLog = new() { AutoSize = true, ForeColor = Color.Gray, Text = "" };
    private readonly System.Windows.Forms.Timer _linkTimer = new() { Interval = 2000 };

    private sealed class Settings
    {
        public string? Endpoint { get; set; }

        /// <summary>大屏程序目录（现场配置对话框里选的）；本机跑着大屏时会自动找到，不用填。</summary>
        public string? DashboardDir { get; set; }

        /// <summary>大屏网页地址。不填 = 本机 http://localhost:5080/；大屏在别的机器上就填它的地址。</summary>
        public string? DashboardUrl { get; set; }
    }

    private string? _dashboardDir;
    private string? _dashboardUrl;

    /// <summary>当前这条连接连的是哪个地址（地址栏随时可以改，扫描结果只属于这一台）。</summary>
    private string _connectedEndpoint = string.Empty;
    private const string LocalDashboardUrl = "http://localhost:5080/";

    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static string SettingsPath => Path.Combine(AppContext.BaseDirectory, "settings.json");

    public MainForm(string? endpoint, bool autoConnect, bool demo = false)
    {
        LoadSettings();
        if (endpoint != null) _ipBox.Text = endpoint;

        Text = $"螺栓预紧力监视器 v{Application.ProductVersion}";
        Font = new Font("Microsoft YaHei UI", 9f);
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(1270, 680);
        MinimumSize = new Size(980, 560);
        StartPosition = FormStartPosition.CenterScreen;

        BuildUi();

        _btnConnect.Click += async (_, _) => await ToggleAsync();
        _list.SelectedIndexChanged += (_, _) =>
        {
            if (_list.SelectedItems.Count > 0 && _list.SelectedItems[0].Tag is ushort ch)
            {
                _selected = ch;
                UpdateParams();
                RefreshSelected();
            }
        };
        _chkRef.CheckedChanged += (_, _) =>
        {
            _wave.ShowRef = _chkRef.Checked;
            _wave.Invalidate();
        };
        _btnCal.Click += (_, _) =>
        {
            using var dlg = new OpenFileDialog
            {
                Filter = "标定文件 (*.ucf)|*.ucf|所有文件 (*.*)|*.*",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            };
            if (dlg.ShowDialog(this) == DialogResult.OK) TryLoadUcf(dlg.FileName);
        };
        _btnParams.Click += (_, _) => ShowParamsDialog();
        _btnZero.Click += (_, _) => ShowZeroDialog();
        _btnConfig.Click += (_, _) => ShowConfigDialog();
        _btnDash.Click += async (_, _) => await OpenDashboardAsync();
        _btnScan.Click += async (_, _) => await RescanAsync();

        LoadPlan();
        _linkTimer.Tick += (_, _) =>
        {
            if (_connected && _engine != null && !_engine.IsConnected)
            {
                _lblState.Text = "连接已断开：" + (_engine.LastDisconnect ?? "未知原因");
                _lblState.ForeColor = Color.Firebrick;
            }
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
    }

    private void BuildUi()
    {
        var top = new Panel { Dock = DockStyle.Top, Height = 82, Padding = new Padding(10, 4, 10, 2) };
        var row1 = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 38, WrapContents = false };
        row1.Controls.Add(new Label { Text = "设备", AutoSize = true, Margin = new Padding(0, 8, 4, 0) });
        _ipBox.Margin = new Padding(0, 4, 8, 0);
        _btnConnect.Margin = new Padding(0, 1, 12, 0);
        _lblState.Margin = new Padding(0, 8, 0, 0);
        row1.Controls.Add(_ipBox);
        row1.Controls.Add(_btnConnect);
        _btnScan.Margin = new Padding(0, 1, 12, 0);
        _tip.SetToolTip(_btnScan, "重新扫描全部 48 个通道，按回波信噪比找出接了探头的通道，约需 1 分钟。换插探头后点一下");
        row1.Controls.Add(_btnScan);
        row1.Controls.Add(_lblState);
        var row2 = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        _btnCal.Margin = new Padding(0, 1, 8, 0);
        _btnParams.Margin = new Padding(0, 1, 8, 0);
        _lblCal.Margin = new Padding(0, 8, 0, 0);
        _btnZero.Margin = new Padding(0, 1, 8, 0);
        _btnConfig.Margin = new Padding(0, 1, 8, 0);
        _tip.SetToolTip(_btnConfig, "接线（哪路探头在哪个法兰的几号螺栓）、每个法兰的标定文件和额定值、法兰名称/螺栓数/编号。保存写 farm.json，大屏在本机时同时生效");
        row2.Controls.Add(_btnCal);
        row2.Controls.Add(_btnParams);
        row2.Controls.Add(_btnZero);
        row2.Controls.Add(_btnConfig);
        _btnDash.Margin = new Padding(0, 1, 8, 0);
        _tip.SetToolTip(_btnDash, "在浏览器里打开业主大屏。本机的大屏服务没在运行且 BoltPreload.Monitor.exe 就在旁边时，先把服务启动起来");
        row2.Controls.Add(_btnDash);
        row2.Controls.Add(_lblCal);
        top.Controls.Add(row2);
        top.Controls.Add(row1);

        _list.Columns.Add("通道", S(56));
        // 位置 = farm.json 接线表里这一路装在哪个法兰的几号螺栓；没有 farm.json 时这一列是空的
        _list.Columns.Add("位置", S(118));
        _list.Columns.Add("夹紧力kN", S(86));
        _list.Columns.Add("声时差ns", S(84));
        _list.Columns.Add("温度℃", S(72));
        _list.Columns.Add("信噪比dB", S(78));
        _list.Columns.Add("状态", S(70));
        _params.Columns.Add("参数", S(150));
        _params.Columns.Add("值", S(224));
        var paramTitle = new Label
        {
            Text = "通道参数",
            Dock = DockStyle.Top,
            Height = 26,
            TextAlign = ContentAlignment.BottomLeft,
            ForeColor = Color.Gray,
            Font = new Font("Microsoft YaHei UI", 8.5f),
        };
        _leftStack = new SplitContainer
        {
            Size = new Size(400, 600),
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterWidth = 6,
            TabStop = false,
            FixedPanel = FixedPanel.Panel1,
            Panel1MinSize = 80,
            Panel2MinSize = 110,
        };
        _leftStack.Panel1.Controls.Add(_list);
        _leftStack.Panel2.Controls.Add(_params);
        _leftStack.Panel2.Controls.Add(paramTitle);

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 32, Padding = new Padding(12, 6, 10, 4) };
        _chkRef.Location = new Point(12, 6);
        _lblLog.Location = new Point(220, 8);
        bottom.Controls.Add(_chkRef);
        bottom.Controls.Add(_lblLog);

        var cards = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 86,
            ColumnCount = 6,
            Padding = new Padding(4, 2, 10, 2),
        };
        for (var i = 0; i < 6; i++)
        {
            cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100 / 6f));
            var host = new Panel { Dock = DockStyle.Fill, Margin = new Padding(4), BackColor = Color.FromArgb(248, 249, 250) };
            var title = new Label
            {
                Text = CardTitles[i],
                Dock = DockStyle.Top,
                Height = 22,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Color.Gray,
                Font = new Font("Microsoft YaHei UI", 8.5f),
            };
            var value = new Label
            {
                Text = "--",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Microsoft YaHei UI", i == 0 ? 20f : 14f, FontStyle.Bold),
                ForeColor = i == 0 ? Color.FromArgb(0, 110, 200) : Color.FromArgb(50, 50, 50),
            };
            _cardValues[i] = value;
            host.Controls.Add(value);
            host.Controls.Add(title);
            cards.Controls.Add(host, i, 0);
        }

        _split = new SplitContainer
        {
            Size = new Size(1200, 600),
            Dock = DockStyle.Fill,
            SplitterWidth = 6,
            TabStop = false,
            FixedPanel = FixedPanel.Panel1,
            Panel1MinSize = 240,
            Panel2MinSize = 320,
        };
        _split.Panel1.Padding = new Padding(10, 6, 0, 6);
        _split.Panel2.Padding = new Padding(2, 2, 10, 4);
        _split.Panel1.Controls.Add(_leftStack);
        _split.Panel2.Controls.Add(_wave);
        _split.Panel2.Controls.Add(cards);

        _list.ClientSizeChanged += (_, _) => StretchLastColumn(_list);
        _params.ClientSizeChanged += (_, _) => StretchLastColumn(_params);

        Controls.Add(_split);
        Controls.Add(bottom);
        Controls.Add(top);
    }

    private int S(int px) => (int)Math.Round(px * DeviceDpi / 96.0);

    private static void StretchLastColumn(ListView lv)
    {
        if (lv.Columns.Count == 0 || lv.ClientSize.Width <= 0) return;
        var others = 0;
        for (var i = 0; i < lv.Columns.Count - 1; i++) others += lv.Columns[i].Width;
        var rest = lv.ClientSize.Width - others;
        if (rest >= 60) lv.Columns[lv.Columns.Count - 1].Width = rest;
    }

    private async Task ToggleAsync()
    {
        if (_connected)
        {
            await DisconnectAsync();
            return;
        }

        _btnConnect.Enabled = false;
        _lblState.ForeColor = Color.Gray;
        // 连哪台设备就用哪台的接线/标定（farm.json 里一个风场很多台）
        LoadPlan();
        try
        {
            _cts = new CancellationTokenSource();
            _engine = new DeviceEngine();
            _engine.OnLog += s => Post(() => _lblLog.Text = s);
            _engine.OnMeasurement += m => Post(() => Absorb(m));

            _lblState.Text = "正在连接...";
            var opt = new DeviceOptions
            {
                Endpoint = _ipBox.Text.Trim(),
                Address = 1,
                Resume = "multi",
                Gap = 250,
            };
            await _engine.OpenAsync(opt, _cts.Token);
            if (!_engine.IsConnected)
            {
                throw new InvalidOperationException("设备已断开连接，请检查 IP 或设备是否被其他程序占用");
            }

            _connectedEndpoint = opt.Endpoint;

            SaveSettings();
            if (!await ScanAndStartAsync())
            {
                _engine = null;
                return;
            }

            _connected = true;
            _btnConnect.Text = "断开";
            _btnParams.Enabled = true;
            _btnZero.Enabled = true;
            _btnScan.Enabled = true;
            ShowMonitorState();
        }
        catch (Exception ex)
        {
            _lblState.Text = "连接失败：" + ex.Message;
            _lblState.ForeColor = Color.Firebrick;
            if (_engine != null)
            {
                try { await _engine.CloseAsync(); } catch { }
            }

            _engine = null;
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
            _lblState.ForeColor = Color.Gray;
            _lblState.Text = $"正在扫描 {ChannelCount} 个通道...";
            var scan = await _engine!.ScanProbesAsync(ChannelCount,
                text => Post(() => _lblState.Text = text),
                _cts.Token);
            var probes = scan.Where(s => s.HasProbe || s.Suspect).Select(s => s.Channel).ToList();
            _unheard = scan.Where(s => !s.Heard).Select(s => s.Channel).ToList();

            _channels.Clear();
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
                live.Row.SubItems[4].Text = s.SnrAvg.ToString("0");
                if (s.Suspect)
                {
                    live.Row.SubItems[5].Text = "信号弱";
                    live.Row.ForeColor = Color.DarkOrange;
                }

                _channels[kv.Key] = live;
            }

            if (_list.Items.Count > 0)
            {
                _list.Items[0].Selected = true;
            }

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
            _lblState.ForeColor = Color.DarkOrange;
        }
        catch (Exception ex)
        {
            _lblState.Text = "发送重启命令失败：" + ex.Message;
            _lblState.ForeColor = Color.Firebrick;
        }

        return true;
    }

    private void ResetToDisconnected()
    {
        _engine = null;
        _connected = false;
        _btnConnect.Text = "连接";
        _btnParams.Enabled = false;
        _btnZero.Enabled = false;
        _btnScan.Enabled = false;
    }

    private async Task RescanAsync()
    {
        if (_engine == null || !_engine.IsConnected || _scanning) return;
        _btnScan.Enabled = false;
        _btnConnect.Enabled = false;
        _btnParams.Enabled = false;
        _btnZero.Enabled = false;
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
            _lblState.ForeColor = Color.Firebrick;
        }
        finally
        {
            if (keep)
            {
                _btnScan.Enabled = true;
                _btnConnect.Enabled = true;
                _btnParams.Enabled = true;
                _btnZero.Enabled = true;
            }
            else
            {
                ResetToDisconnected();
                _btnConnect.Enabled = true;
            }
        }
    }

    private void ShowMonitorState()
    {
        if (!_connected) return;
        if (_channels.Count == 0)
        {
            _lblState.Text = _unheard.Count >= ChannelCount
                ? "设备没有上报任何通道数据，请检查设备状态（必要时重启设备）后点“扫描通道”重试"
                : "未发现探头，请检查探头连接后点“扫描通道”";
            _lblState.ForeColor = Color.DarkOrange;
            return;
        }

        var names = string.Join("、", _channels.Keys.OrderBy(c => c).Select(c => "CH" + c));
        var unread = _channels.Values.Where(c => c.Cfg.Count == 0).Select(c => "CH" + c.Ch).ToList();
        var text = $"监测中，{_channels.Count} 个通道（{names}）";
        if (unread.Count > 0) text += $"，{string.Join("、", unread)} 参数未读到";
        if (_unheard.Count > 0) text += $"，扫描时 CH{FormatChannels(_unheard)} 未响应";
        if (unread.Count > 0 || _unheard.Count > 0) text += "，可点“扫描通道”重试";
        _lblState.Text = text;
        _lblState.ForeColor = unread.Count > 0 || _unheard.Count > 0 ? Color.DarkOrange : Color.ForestGreen;
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
        return row;
    }

    /// <summary>"CH3（塔筒顶段法兰 #24）"——对话框标题和操作记录里用，让现场知道拧的是哪颗。</summary>
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
            _engine = null;
            _connected = false;
            _btnConnect.Text = "连接";
            _btnConnect.Enabled = true;
            _btnParams.Enabled = false;
            _btnZero.Enabled = false;
            _btnScan.Enabled = false;
            _lblState.Text = "已断开";
            _lblState.ForeColor = Color.Gray;
        }
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

        HashSet<ushort> zeroed;
        using (var dlg = new ZeroDialog(_engine, monitored, _selected == 0 ? monitored[0] : _selected, ChannelLabel, _plan.Mode))
        {
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
        }

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
            dlg = new SiteConfigDialog(_ipBox.Text.Trim(), connected, detected, AppContext.BaseDirectory, _dashboardDir);
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

    /// <summary>
    /// 打开大屏网页。地址：settings.json 里填了就用填的（大屏在别的机器），没填按本机 5080。
    /// 本机地址不通时，看旁边（或现场配置里选的大屏目录）有没有 BoltPreload.Monitor.exe，有就问一句、拉起来再开网页——
    /// 两个程序发布在同一个文件夹里，业主机上双击桌面端也能把大屏带起来。
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
                var exe = FindDashboardExe();
                if (exe is null)
                {
                    MessageBox.Show(this, $"本机 {url} 没有大屏服务在运行，旁边也没找到 BoltPreload.Monitor.exe。\n\n大屏在别的机器上的话，在 settings.json 里填 DashboardUrl（如 http://192.168.1.20:5080/）。",
                        "打开大屏", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                var answer = MessageBox.Show(this, $"本机的大屏服务没在运行。现在启动它？\n\n{exe}", "打开大屏", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (answer != DialogResult.Yes) return;
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe)
                {
                    WorkingDirectory = Path.GetDirectoryName(exe)!,
                    UseShellExecute = true,
                    WindowStyle = System.Diagnostics.ProcessWindowStyle.Minimized,
                });
                OpsLog.Append($"启动大屏服务 {exe}");
                _lblLog.Text = "大屏服务启动中……";
                for (var i = 0; i < 20 && !await DashboardAliveAsync(url); i++) await Task.Delay(500);
                _lblLog.Text = await DashboardAliveAsync(url) ? "大屏服务已启动" : "大屏服务还没响应，浏览器里稍后刷新";
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

    private static async Task<bool> DashboardAliveAsync(string url)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(1200) };
            using var r = await http.GetAsync(new Uri(new Uri(url), "/api/state"));
            return r.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>大屏 exe：优先自己旁边（同一个文件夹发布），其次现场配置里选过的大屏目录。</summary>
    private string? FindDashboardExe()
    {
        foreach (var dir in new[] { AppContext.BaseDirectory, _dashboardDir })
        {
            if (dir is null) continue;
            var exe = Path.Combine(dir, "BoltPreload.Monitor.exe");
            if (File.Exists(exe)) return exe;
        }

        return null;
    }

    private void ShowParamsDialog()
    {
        if (_engine == null || !_engine.IsConnected)
        {
            MessageBox.Show(this, "请先连接设备", "参数下发", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var monitored = _channels.Keys.OrderBy(c => c).ToArray();
        using (var dlg = new ParamsDialog(_engine, monitored, _selected))
        {
            dlg.ShowDialog(this);

            foreach (var kv in dlg.DeviceValues)
            {
                if (!_channels.TryGetValue(kv.Key, out var c)) continue;
                foreach (var item in kv.Value)
                {
                    c.Cfg[item.Key] = item.Value;
                }
            }
        }

        UpdateParams();
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

        var wantLeft = Math.Min(S(470), _split.Width - _split.Panel2MinSize);
        if (wantLeft >= _split.Panel1MinSize) _split.SplitterDistance = wantLeft;
        var wantList = Math.Min(S(190), _leftStack.Height - _leftStack.Panel2MinSize);
        if (wantList >= _leftStack.Panel1MinSize) _leftStack.SplitterDistance = wantList;
    }

    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        if (_connected && !_closing)
        {
            e.Cancel = true;
            _closing = true;
            Hide();
            await DisconnectAsync();
            Close();
            return;
        }

        base.OnFormClosing(e);
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
            c = new ChLive { Ch = m.Channel, Row = NewRow(m.Channel) };
            _channels[m.Channel] = c;
        }

        c.Last = m;
        if (m.EchoWave != null && m.EchoWave.WaveData != null && m.EchoWave.WaveData.Count > 0)
        {
            c.LastWave = m.EchoWave;
            c.Snr = DeviceEngine.Snr(m.EchoWave) ?? c.Snr;
        }

        if (m.DeltaTof.HasValue)
        {
            c.Dt.Enqueue(m.DeltaTof.Value);
            while (c.Dt.Count > 20) c.Dt.Dequeue();
        }

        UpdateRow(c);
        if (_selected == 0) _selected = m.Channel;
        if (m.Channel == _selected) RefreshSelected();
    }

    /// <summary>
    /// 这一路的夹紧力显示值。有 farm.json：按它的口径——自由置零是 k·Δt+b，在役锚定是"额定 + k·Δt"，
    /// 锚定但没配额定值时只能给较锚定的变化量（IsDelta = true，界面前面标 Δ）。和大屏同一套 CalPlan，两边一个数。
    /// 没有 farm.json：不知道口径，按历来的自由置零口径显示 k·Δt+b。
    /// </summary>
    private (decimal? Value, bool IsDelta) Force(ushort ch, decimal deltaTof)
    {
        if (!_plan.HasFarm)
        {
            return (_plan.GlobalCal is { } c ? deltaTof * c.K + c.B : null, false);
        }

        if (_plan.ForceKn(ch, deltaTof) is { } f) return (f, false);
        var d = _plan.DeltaKn(ch, deltaTof);
        return (d, d is not null && _plan.Mode == BaselineMode.Anchor);
    }

    private string ForceText(ushort ch, decimal? deltaTof)
    {
        if (deltaTof is not { } dt) return "--";
        var (v, isDelta) = Force(ch, dt);
        if (v is not { } f) return "--";
        return isDelta ? "Δ" + (f > 0 ? "+" : string.Empty) + f.ToString("F2") : f.ToString("F2");
    }

    private void UpdateParams()
    {
        _params.BeginUpdate();
        _params.Items.Clear();
        if (_channels.TryGetValue(_selected, out var c) && c.Cfg.Count > 0)
        {
            foreach (var p in ParamOrder)
            {
                var raw = DeviceEngine.Raw(c.Cfg, p.Key);
                string value;
                if (p.Key == "MeasMode")
                {
                    value = raw == "0" ? "0 自动" : raw == "1" ? "1 位置" : raw == "2" ? "2 阈值" : raw;
                }
                else if (p.Key == "LEchoWave")
                {
                    value = c.Cfg.TryGetValue("WavePts", out var pts) && pts != "0"
                        ? $"{pts} 点，起点 {c.Cfg.GetValueOrDefault("WaveStart", "?")} ns"
                        : "无";
                }
                else
                {
                    value = raw;
                }

                var item = new ListViewItem(p.Cn);
                item.SubItems.Add(value);
                _params.Items.Add(item);
            }
        }
        else
        {
            var item = new ListViewItem("无参数");
            item.SubItems.Add("-");
            _params.Items.Add(item);
        }

        _params.EndUpdate();
    }

    private void UpdateRow(ChLive c)
    {
        if (c.Row == null || c.Last == null) return;
        var dt = c.Last.DeltaTof;
        c.Row.SubItems[1].Text = _plan.Describe(c.Ch);
        c.Row.SubItems[2].Text = ForceText(c.Ch, dt);
        c.Row.SubItems[3].Text = dt?.ToString("F2") ?? "--";
        var temp = c.Last.SpTemp ?? c.Last.RawTemp;
        c.Row.SubItems[4].Text = temp?.ToString("F2") ?? "--";
        c.Row.SubItems[5].Text = c.Snr.HasValue ? c.Snr.Value.ToString("0") : "--";
        var normal = c.Last.StatusCode == MeasStatusCode.Normal;
        var weak = c.Snr.HasValue && c.Snr.Value < DeviceEngine.ProbeSnrDb;
        c.Row.SubItems[6].Text = normal ? (weak ? "信号弱" : "正常") : DeviceEngine.DescribeStatus(c.Last.StatusCode);
        c.Row.ForeColor = !normal ? Color.Firebrick : weak ? Color.DarkOrange : Color.Black;
    }

    private void RefreshSelected()
    {
        if (!_channels.TryGetValue(_selected, out var c) || c.Last == null) return;
        var m = c.Last;

        _cardValues[0].Text = ForceText(c.Ch, m.DeltaTof);
        _cardValues[1].Text = m.DeltaTof?.ToString("F2") ?? "--";
        _cardValues[2].Text = m.EchoTime?.ToString("F1") ?? "--";
        _cardValues[3].Text = m.SpTemp?.ToString("F2")
                              ?? m.RawTemp?.ToString("F2") ?? "--";
        _cardValues[4].Text = m.DeltaTof.HasValue
            ? (m.DeltaTof.Value * Ucf.UmPerNs).ToString("F2")
            : "--";
        _cardValues[5].Text = StdDev(c.Dt)?.ToString("F2") ?? "--";

        var lw = c.LastWave;
        if (lw != null && lw.WaveData != null && lw.WaveData.Count > 0)
        {
            _wave.Live = lw.WaveData.ToArray();
            _wave.LiveStart = lw.EchoStart;
            _wave.LiveClock = lw.ClockInt;
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

        _wave.Marker = m.EchoTime;
        _wave.Invalidate();
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
        }
        catch (Exception)
        {
        }
    }

    private void SaveSettings()
    {
        try
        {
            var s = new Settings { Endpoint = _ipBox.Text.Trim(), DashboardDir = _dashboardDir, DashboardUrl = _dashboardUrl };
            File.WriteAllText(SettingsPath, System.Text.Json.JsonSerializer.Serialize(s, JsonOptions));
        }
        catch (Exception)
        {
        }
    }

    private void TryLoadUcf(string path)
    {
        try
        {
            LoadUcf(path);
            RefreshSelected();
            foreach (var c in _channels.Values) UpdateRow(c);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"标定文件读取失败：{ex.Message}\n{path}", "标定文件",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
        var dev = farm.ForEndpoint(_ipBox.Text);
        if (dev is not null)
        {
            _plan = dev.Plan;
            _planLabel = $"F{dev.Turbine:00} 机组";
            if (farm.Devices.Count > 1) notes.Add($"farm.json 有 {farm.Devices.Count} 台设备，按地址 {_ipBox.Text.Trim()} 选中 {dev.Label}");
        }
        else
        {
            _plan = CalPlan.Load(null, AppContext.BaseDirectory, notes.Add);
            _planLabel = string.Empty;
            if (farm.HasFarm && farm.Devices.Count > 0)
            {
                notes.Add($"farm.json 里没有地址为 {_ipBox.Text.Trim()} 的设备（有：{string.Join("、", farm.Devices.Select(d => d.Label))}），按无 farm.json 处理");
            }
        }

        foreach (var n in notes) OpsLog.Append(n);
        ShowPlan();
        foreach (var c in _channels.Values) UpdateRow(c);
        RefreshSelected();
    }

    /// <summary>"F01 机组"：当前方案属于哪台机组，标签上带着，免得连错设备还拿别台的接线表看。</summary>
    private string _planLabel = string.Empty;

    private void ShowPlan()
    {
        var live = _plan.Flanges.Where(f => f.OnDevice).ToList();
        string text;
        Color color;
        if (_plan.HasFarm && live.Count > 0)
        {
            var parts = live.Select(f =>
                $"{f.Name}：{(f.Cal is { } c ? $"{c.FileName} {c.K:F4} {c.Unit}/ns" : "未标定")}"
                + (_plan.Mode == BaselineMode.Anchor ? (f.Rated > 0 ? $"，额定 {f.Rated} kN" : "，未配额定值（只显示 Δ 变化量）") : string.Empty));
            text = $"{(_planLabel.Length > 0 ? _planLabel + " · " : string.Empty)}{_plan.ModeText} · " + string.Join("；", parts);
            color = live.All(f => f.Cal is { }) ? Color.FromArgb(0, 110, 200) : Color.DarkOrange;
        }
        else if (_plan.GlobalCal is { } g)
        {
            text = $"标定：{g.FileName}（{g.Points} 点） {g.K:F4} {g.Unit}/ns"
                   + (g.B != 0 ? $" {(g.B > 0 ? "+" : "-")} {Math.Abs(g.B):F2}" : string.Empty)
                   + " · 未找到 farm.json，按自由置零口径显示 k·Δt+b（在役锚定的螺栓这是较锚定的变化量）";
            color = Color.FromArgb(0, 110, 200);
        }
        else
        {
            text = "标定：未加载标定文件（不显示夹紧力，请加载 .ucf）";
            color = Color.DarkOrange;
        }

        _lblCal.Text = text;
        _lblCal.ForeColor = color;
        _tip.SetToolTip(_lblCal, text);
    }

    private void LoadUcf(string path)
    {
        // 拟合只在 FxDevice.Ucf 里做一份，桌面版和大屏算出来的力必须是同一套系数。
        // 手工指定的标定文件对所有法兰生效——这是工程师的显式动作，记操作记录
        var cal = Ucf.Load(path);
        _plan = _plan.WithCal(cal);
        ShowPlan();
        OpsLog.Append($"手工加载标定文件 {cal.FileName}（{cal.Points} 点）k={cal.K:F4} {cal.Unit}/ns b={cal.B:F2}，对全部通道生效");
    }
}
