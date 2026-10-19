using FxDevice;
using FxSDK.Models;

namespace FxMonitor;

/// <summary>
/// 找回波窗（厂家"扫波"的用法）：让设备把这一路从 0 到扫描范围的整段波形发上来，人在波形上点回波起始位置，
/// 程序算出回波窗（起点前留 2 us、按 1 us 取整）写进设备。写完要不要置零由操作员定（和厂家软件一致）。
/// 扫波期间测量暂停；关闭对话框后主窗体恢复监测。
/// </summary>
internal sealed class EchoWindowDialog : Form
{
    private const int MarginNs = 2000;          // 回波起点前留的余量
    private const int DeadZoneNs = 5000;        // 发射脉冲拖尾，自动找回波时跳过
    private static readonly TimeSpan ScanTimeout = TimeSpan.FromSeconds(20);

    private readonly DeviceEngine _engine;
    private readonly ushort _ch;
    private readonly string _label;
    private decimal? _currentWin;
    private readonly decimal _widthUs;

    private readonly NumericUpDown _range = new() { Minimum = 20, Maximum = 500, Increment = 10, Width = 70 };
    private readonly Button _btnScan = new() { Text = "开始扫波", Width = 100, Height = 32 };
    private readonly Button _btnWrite = new() { Text = "写入回波窗", Width = 120, Height = 34, Enabled = false, Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold) };
    private readonly Button _btnClose = new() { Text = "关闭", Width = 84, Height = 34 };
    private readonly Label _status = new() { AutoSize = true, Text = "点「开始扫波」，把整段波形读上来", MaximumSize = new Size(760, 0) };
    private readonly Label _pick = new() { AutoSize = true, Font = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Bold) };
    private readonly WavePanel _wave = new() { Dock = DockStyle.Fill, ShowRef = false };

    private EchoWave? _full;
    private decimal? _echoStart;
    private decimal? _proposed;
    private bool _busy;

    /// <summary>扫过波或写过参数：设备已被切到待机，主窗体关掉对话框后要恢复监测。</summary>
    public bool Touched { get; private set; }

    /// <summary>写入成功的新回波窗（ns）；空 = 没写。</summary>
    public decimal? WrittenEchoWin { get; private set; }

    public EchoWindowDialog(DeviceEngine engine, ushort ch, string label, Dictionary<string, string> cfg)
    {
        _engine = engine;
        _ch = ch;
        _label = label;
        _currentWin = DeviceEngine.Num(cfg, "EchoWin");
        _widthUs = DeviceEngine.Num(cfg, "EchoWidth") ?? 15m;

        Text = $"找回波窗 — {label}";
        Font = new Font("Microsoft YaHei UI", 9.5f);
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(900, 520);
        MinimumSize = new Size(720, 420);
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = true;
        MinimizeBox = false;
        ShowInTaskbar = false;

        // 扫描范围：至少盖住现在的窗口和它的宽度，再往后留 10 us；没读到窗口就按 60 us
        var suggest = _currentWin is { } w ? (int)Math.Ceiling((double)(w / 1000 + _widthUs) + 10) : 60;
        _range.Value = Math.Clamp(Math.Max(60, suggest), (int)_range.Minimum, (int)_range.Maximum);

        BuildUi();

        _btnScan.Click += async (_, _) => await ScanAsync();
        _btnWrite.Click += async (_, _) => await WriteAsync();
        _btnClose.Click += (_, _) => Close();
        _wave.Picked += OnPicked;
        FormClosing += (_, e) =>
        {
            if (_busy)
            {
                e.Cancel = true;
                _status.Text = "正在和设备通信，请稍候";
            }
        };

        ShowPick();
    }

    private void BuildUi()
    {
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(12, 6, 0, 0), WrapContents = false };
        top.Controls.Add(new Label { Text = "扫描范围", AutoSize = true, Margin = new Padding(0, 7, 6, 0) });
        _range.Margin = new Padding(0, 3, 4, 0);
        top.Controls.Add(_range);
        top.Controls.Add(new Label { Text = "us", AutoSize = true, Margin = new Padding(0, 7, 14, 0) });
        _btnScan.Margin = new Padding(0, 0, 14, 0);
        top.Controls.Add(_btnScan);
        _status.Margin = new Padding(0, 7, 0, 0);
        top.Controls.Add(_status);

        var mid = new Panel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(12, 4, 0, 0) };
        _pick.Location = new Point(12, 6);
        mid.Controls.Add(_pick);

        var hint = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 44,
            Padding = new Padding(12, 2, 10, 2),
            ForeColor = Color.Gray,
            Text = "扫波会把回波窗临时放大到扫描范围，拿到整段波形后写回原值。程序先自动标出第一个明显回波，"
                   + "不对就在波形上点回波起始位置；回波窗取起点前 2 us、按 1 us 取整。",
        };

        var foot = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, Padding = new Padding(12, 6, 0, 0), WrapContents = false, FlowDirection = FlowDirection.RightToLeft };
        _btnClose.Margin = new Padding(0, 0, 12, 0);
        _btnWrite.Margin = new Padding(0, 0, 8, 0);
        foot.Controls.Add(_btnClose);
        foot.Controls.Add(_btnWrite);

        var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 0, 8, 0) };
        host.Controls.Add(_wave);

        Controls.Add(host);
        Controls.Add(mid);
        Controls.Add(top);
        Controls.Add(hint);
        Controls.Add(foot);
        CancelButton = _btnClose;
    }

    private async Task ScanAsync()
    {
        if (_busy) return;
        SetBusy(true);
        Touched = true;
        try
        {
            var rangeNs = (uint)_range.Value * 1000u;
            _status.Text = $"正在扫波：0～{_range.Value} us，测量已暂停...";
            _status.ForeColor = Color.DimGray;
            var wave = await _engine.FullWaveAsync(_ch, rangeNs, ScanTimeout, CancellationToken.None);
            if (wave?.WaveData is not { Count: > 1 })
            {
                _status.Text = "没有收到完整波形：设备可能不支持全波扫描，或这一路没有信号。可以直接在「参数下发」里改回波窗";
                _status.ForeColor = Color.Firebrick;
                return;
            }

            _full = wave;
            _wave.Live = wave.WaveData.ToArray();
            _wave.LiveStart = wave.EchoStart;
            _wave.LiveClock = wave.ClockInt;
            var covered = (long)wave.WaveData.Count * wave.ClockInt;
            _status.Text = $"已读到 0～{covered / 1000} us 的波形，共 {wave.WaveData.Count} 点";
            _status.ForeColor = Color.ForestGreen;

            var auto = AutoEchoStart(wave);
            if (auto is { } a) OnPicked(a);
            else
            {
                _status.Text += "；没找到明显回波，请在波形上点回波起始位置";
                _wave.Invalidate();
            }

            if (_engine.EchoWinRestoreNote is { Length: > 0 } note)
            {
                _status.Text += "；" + note;
                _status.ForeColor = Color.Firebrick;
            }
        }
        catch (Exception ex)
        {
            _status.Text = "扫波失败：" + ex.Message;
            _status.ForeColor = Color.Firebrick;
        }
        finally
        {
            // 扫波会临时放大回波窗再写回。用设备上现在的值来判断要不要写，不要用打开对话框时的旧值。
            try
            {
                var now = DeviceEngine.Num(_engine.Snapshot(_ch), "EchoWin");
                if (now is { }) _currentWin = now;
                ShowPick();
            }
            catch
            {
                // 读不到就保持打开时的值
            }

            SetBusy(false);
        }
    }

    /// <summary>自动找第一个明显回波：跳过发射拖尾，找第一处到峰值 40% 的位置，再回退到它前面幅度掉到 10% 的地方。</summary>
    private static decimal? AutoEchoStart(EchoWave wave)
    {
        var d = wave.WaveData!;
        var clock = wave.ClockInt;
        if (clock == 0 || d.Count < 2) return null;
        var dead = Math.Min(d.Count - 1, (int)(DeadZoneNs / clock));
        var peak = 0;
        for (var i = dead; i < d.Count; i++) peak = Math.Max(peak, Math.Abs((int)d[i]));
        if (peak < 60) return null;   // 整段都是噪声

        var thr = peak * 0.4;
        var low = peak * 0.1;
        var hit = -1;
        for (var i = dead; i < d.Count; i++)
        {
            if (Math.Abs((int)d[i]) >= thr)
            {
                hit = i;
                break;
            }
        }

        if (hit < 0) return null;
        var onset = hit;
        while (onset > dead && Math.Abs((int)d[onset]) > low) onset--;
        return wave.EchoStart + (decimal)onset * clock;
    }

    private void OnPicked(decimal ns)
    {
        if (_full is null) return;
        _echoStart = Math.Max(0, ns);
        var win = Math.Floor(Math.Max(0, _echoStart.Value - MarginNs) / 1000) * 1000;
        _proposed = win;
        _wave.Marker = _echoStart;
        _wave.BandStart = win;
        _wave.BandEnd = win + _widthUs * 1000;
        _wave.Invalidate();
        ShowPick();
        _btnWrite.Enabled = !_busy && _proposed is { };
    }

    private void ShowPick()
    {
        var current = _currentWin is { } w ? $"{w:0} ns" : "未读到";
        _pick.Text = _proposed is { } p
            ? $"回波起点 {_echoStart:0} ns → 回波窗 {p:0} ns（当前 {current}，回波宽度 {_widthUs:0} us）"
            : $"回波窗当前 {current}，回波宽度 {_widthUs:0} us";
    }

    private async Task WriteAsync()
    {
        if (_busy || _proposed is not { } value) return;
        if (_currentWin is { } cur && cur == value)
        {
            _status.Text = "和当前回波窗一样，不用写";
            return;
        }

        var msg = $"将把 {_label} 的回波窗写为 {value:0} ns（当前 {(_currentWin is { } c ? $"{c:0} ns" : "未读到")}）。\n\n是否继续？";
        if (MessageBox.Show(this, msg, "写入回波窗", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;

        SetBusy(true);
        Touched = true;
        try
        {
            _status.Text = "正在写入回波窗...";
            _status.ForeColor = Color.DimGray;
            await _engine.StopStreamAsync();
            var rows = await _engine.WriteRegistersAsync(_ch, new[] { ("EchoWin", value) }, CancellationToken.None);
            var row = rows.FirstOrDefault(r => r.Name == "EchoWin" && !r.Implicit);
            if (row is { Verdict: Verdict.StoredOk })
            {
                WrittenEchoWin = decimal.TryParse(row.ReadBack, out var rb) ? rb : value;
                _status.Text = $"回波窗已写入 {row.ReadBack} ns";
                _status.ForeColor = Color.ForestGreen;
                OpsLog.Append($"{_label} 找回波窗：回波窗 {row.BeforeValue} -> {row.ReadBack}（回波起点 {_echoStart:0} ns）");
                _btnWrite.Enabled = false;
            }
            else
            {
                var why = row is null ? "没有结果" : row.Verdict switch
                {
                    Verdict.Ignored => "设备未接受（值没变）",
                    Verdict.Rewritten => $"读回 {row.ReadBack} 与写入不一致",
                    Verdict.NoReadback => "读回无响应",
                    Verdict.NotSent => row.Detail,
                    _ => row.Detail,
                };
                _status.Text = "写入失败：" + why;
                _status.ForeColor = Color.Firebrick;
                OpsLog.Append($"{_label} 找回波窗写入失败：{why}");
            }
        }
        catch (Exception ex)
        {
            _status.Text = "写入出错：" + ex.Message;
            _status.ForeColor = Color.Firebrick;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _btnScan.Enabled = !busy;
        _range.Enabled = !busy;
        _btnClose.Enabled = !busy;
        _btnWrite.Enabled = !busy && _proposed is { } && WrittenEchoWin is null;
    }
}
