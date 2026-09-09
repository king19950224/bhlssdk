using FxDevice;
using FxSDK.Models;

namespace FxMonitor;

internal sealed class ZeroDialog : Form
{
    private const int MinSamples = 5;
    private const decimal MaxStdNs = 5.0m;
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    private readonly DeviceEngine _engine;
    private readonly IReadOnlyList<ushort> _monitored;

    private readonly ComboBox _channel = new() { Width = 90, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label _lblNow = new() { AutoSize = true, Text = "当前：-", Font = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold) };
    private readonly Label _lblStable = new() { AutoSize = true, Text = "稳定度：-" };
    private readonly Label _lblBaseline = new() { AutoSize = true, Text = "当前基线：-", ForeColor = Color.DimGray };
    private readonly CheckBox _force = new() { Text = "信号不稳定时允许强制置零", AutoSize = true };
    private readonly Button _btnZero = new() { Text = "置零", Width = 130, Height = 36, Enabled = false, Font = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Bold) };
    private readonly Label _status = new() { AutoSize = true, Text = "等待测量数据...", MaximumSize = new Size(560, 0) };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 500 };

    private readonly object _lock = new();
    private readonly Queue<(DateTime At, decimal Echo, decimal? Temp)> _samples = new();
    private EchoWave? _lastWave;
    private decimal? _lastEcho;
    private decimal? _lastTemp;

    private readonly Action<ChannelResult> _onMeasurement;
    private bool _busy;

    /// <summary>
    /// 当前选中的通道号，给 SDK 接收线程上的 OnMeasurement 用。那条线程不能碰 _channel 这种控件：
    /// WinForms 控件只能在 UI 线程访问，跨线程读 SelectedIndex 轻则偶发异常，重则和 UI 线程互等死锁。
    /// </summary>
    private volatile int _currentCh;

    public HashSet<ushort> ZeroedChannels { get; } = new();

    /// <summary>通道 → "CH3（塔筒顶段法兰 #24）"：下拉框和操作记录里都用它，现场知道拧的是哪颗。</summary>
    private readonly Func<ushort, string> _label;

    private readonly BaselineMode _mode;

    public ZeroDialog(DeviceEngine engine, IReadOnlyList<ushort> monitoredChannels, ushort selectedChannel,
        Func<ushort, string>? label = null, BaselineMode mode = BaselineMode.Anchor)
    {
        if (monitoredChannels.Count == 0)
        {
            throw new ArgumentException("没有可置零的通道", nameof(monitoredChannels));
        }

        _engine = engine;
        _monitored = monitoredChannels;
        _label = label ?? (ch => "CH" + ch);
        _mode = mode;

        Text = "通道置零";
        Font = new Font("Microsoft YaHei UI", 9.5f);
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(620, 300);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;

        BuildUi();

        foreach (var ch in monitoredChannels)
        {
            _channel.Items.Add(_label(ch));
        }

        // 带位置的名字长（"CH3（塔筒顶段法兰 #24）"），下拉框按最长的一项放宽
        if (label is not null)
        {
            using var g = CreateGraphics();
            var widest = monitoredChannels.Max(ch => (int)Math.Ceiling(g.MeasureString(_label(ch), Font).Width));
            _channel.Width = Math.Clamp(widest + 32, 90, 300);
        }

        var idx = monitoredChannels.ToList().IndexOf(selectedChannel);
        _channel.SelectedIndex = idx >= 0 ? idx : 0;
        _currentCh = Current;
        _channel.SelectedIndexChanged += (_, _) =>
        {
            _currentCh = Current;
            ResetSamples();
        };

        _onMeasurement = OnMeasurement;
        _engine.OnMeasurement += _onMeasurement;
        _timer.Tick += (_, _) => RefreshLive();
        _timer.Start();

        _btnZero.Click += async (_, _) => await ZeroAsync();
        FormClosing += (_, e) =>
        {
            if (_busy)
            {
                e.Cancel = true;
                _status.Text = "正在写入，请稍候";
                return;
            }

            _timer.Stop();
            _engine.OnMeasurement -= _onMeasurement;
        };

        ShowBaseline();
    }

    private void BuildUi()
    {
        var row1 = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(14, 8, 0, 0), WrapContents = false };
        row1.Controls.Add(new Label { Text = "通道", AutoSize = true, Padding = new Padding(0, 6, 6, 0) });
        row1.Controls.Add(_channel);
        _lblBaseline.Margin = new Padding(20, 6, 0, 0);
        row1.Controls.Add(_lblBaseline);

        var row2 = new Panel { Dock = DockStyle.Top, Height = 46, Padding = new Padding(14, 6, 0, 0) };
        _lblNow.Location = new Point(14, 8);
        row2.Controls.Add(_lblNow);

        var row3 = new Panel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(14, 4, 0, 0) };
        _lblStable.Location = new Point(14, 6);
        row3.Controls.Add(_lblStable);

        var row4 = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 48, Padding = new Padding(14, 6, 0, 0), WrapContents = false };
        _btnZero.Margin = new Padding(0, 0, 16, 0);
        _force.Margin = new Padding(0, 10, 0, 0);
        row4.Controls.Add(_btnZero);
        row4.Controls.Add(_force);

        var row5 = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14, 4, 10, 4) };
        _status.Location = new Point(14, 6);
        row5.Controls.Add(_status);

        // 提示按项目口径写：自由置零要求螺栓处于自由状态；在役锚定则是在已拧紧的螺栓上置零，之后看的是较此刻的变化量。
        // 口径写错了，现场就会在错误的状态下置零，大屏上的每个数都跟着错
        var hint = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 46,
            Padding = new Padding(14, 4, 10, 2),
            ForeColor = Color.Gray,
            Text = "置零会用当前实测的声时、温度和回波覆盖该通道的基线；置零后该通道之前的读数不再可比。"
                   + (_mode == BaselineMode.Free
                       ? "本项目口径为自由状态置零：请在螺栓拧紧前置零。"
                       : "本项目口径为在役锚定：在螺栓已拧紧的状态下置零，之后显示的是较此刻的变化量（按额定值估算预紧力）。"),
        };

        Controls.Add(row5);
        Controls.Add(row4);
        Controls.Add(row3);
        Controls.Add(row2);
        Controls.Add(row1);
        Controls.Add(hint);
    }

    /// <summary>只在 UI 线程上用；SDK 线程看 _currentCh。</summary>
    private ushort Current => _monitored[Math.Max(0, _channel.SelectedIndex)];

    /// <summary>跑在 SDK 接收线程上：只动自己的字段和锁，不碰任何控件。</summary>
    private void OnMeasurement(ChannelResult m)
    {
        if (m.Channel != _currentCh)
        {
            return;
        }

        lock (_lock)
        {
            if (m.EchoWave != null && m.EchoWave.WaveData != null && m.EchoWave.WaveData.Count > 0)
            {
                _lastWave = m.EchoWave;
            }

            if (m.EchoTime.HasValue)
            {
                var echo = m.EchoTime.Value;
                _lastEcho = echo;
                _lastTemp = m.SpTemp ?? m.RawTemp ?? _lastTemp;
                _samples.Enqueue((DateTime.UtcNow, echo, m.SpTemp ?? m.RawTemp));
            }

            var cutoff = DateTime.UtcNow - Window;
            while (_samples.Count > 0 && _samples.Peek().At < cutoff)
            {
                _samples.Dequeue();
            }
        }
    }

    private void ResetSamples()
    {
        lock (_lock)
        {
            _samples.Clear();
            _lastWave = null;
            _lastEcho = null;
            _lastTemp = null;
        }

        ShowBaseline();
        _status.Text = "等待测量数据...";
    }

    private void ShowBaseline()
    {
        var bag = _engine.Snapshot(Current);
        _lblBaseline.Text = $"当前基线：参考声时 {DeviceEngine.Raw(bag, "RefTime")} ns，参考温度 {DeviceEngine.Raw(bag, "RefTemp")} ℃";
    }

    private (int Count, decimal Std) StableStat()
    {
        lock (_lock)
        {
            if (_samples.Count < 2)
            {
                return (_samples.Count, 0m);
            }

            var xs = _samples.Select(s => s.Echo).ToArray();
            var avg = xs.Average();
            var variance = xs.Sum(x => (x - avg) * (x - avg)) / xs.Length;
            return (xs.Length, (decimal)Math.Sqrt((double)variance));
        }
    }

    private void RefreshLive()
    {
        if (_busy)
        {
            return;
        }

        decimal? echo, temp;
        bool hasWave;
        lock (_lock)
        {
            echo = _lastEcho;
            temp = _lastTemp;
            hasWave = _lastWave != null;
        }

        _lblNow.Text = echo.HasValue
            ? $"当前：声时 {echo.Value.ToString("F1")} ns，温度 {temp?.ToString("F2") ?? "-"} ℃" + (hasWave ? "" : "（无波形）")
            : "当前：无有效回波";

        var (count, std) = StableStat();
        var stable = count >= MinSamples && std <= MaxStdNs;
        _lblStable.Text = $"稳定度：{Window.TotalSeconds:0} 秒内 {count} 帧，标准差 {std.ToString("F2")} ns"
                          + (stable ? "，可以置零" : count < MinSamples ? "，样本不足" : "，波动偏大");
        _lblStable.ForeColor = stable ? Color.ForestGreen : Color.DarkOrange;

        _btnZero.Enabled = echo != null && temp != null && hasWave && (stable || _force.Checked);
    }

    private async Task ZeroAsync()
    {
        decimal echo, temp;
        EchoWave wave;
        lock (_lock)
        {
            if (_lastEcho == null || _lastTemp == null || _lastWave == null)
            {
                _status.Text = "尚未收到完整的测量数据";
                return;
            }

            echo = _lastEcho.Value;
            temp = _lastTemp.Value;
            wave = _lastWave;
        }

        var ch = Current;
        var bag = _engine.Snapshot(ch);
        var oldRef = DeviceEngine.Raw(bag, "RefTime");
        var (count, std) = StableStat();

        var msg = $"将对 CH{ch} 置零：\n\n"
                  + $"参考声时：{oldRef} -> {echo.ToString("F1")} ns\n"
                  + $"参考温度：{DeviceEngine.Raw(bag, "RefTemp")} -> {temp.ToString("F2")} ℃\n"
                  + $"参考波形：替换为当前帧（{count} 帧，标准差 {std.ToString("F2")} ns）\n\n"
                  + "置零后该通道之前的读数不再可比，是否继续？";
        if (MessageBox.Show(this, msg, "确认置零", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning)
            != DialogResult.OK)
        {
            return;
        }

        SetBusy(true);
        try
        {
            _status.Text = "正在写入基线...";
            await _engine.StopStreamAsync();
            var rows = await _engine.ZeroChannelAsync(ch, echo, temp, wave, CancellationToken.None);

            var ok = rows.All(r => r.Verdict == Verdict.StoredOk);
            var detail = string.Join("；", rows.Select(r =>
                $"{r.Cn} {r.BeforeValue} -> {r.ReadBack}" + (r.Verdict == Verdict.StoredOk ? "" : $"（{r.Detail}）")));
            _status.Text = (ok ? "置零成功：" : "置零失败：") + detail;
            _status.ForeColor = ok ? Color.ForestGreen : Color.Firebrick;
            if (ok)
            {
                ZeroedChannels.Add(ch);
            }

            OpsLog.Append($"{_label(ch)} 置零{(ok ? "成功" : "失败")} {detail} 标准差={std:F2}ns 帧数={count}{(_force.Checked ? " 强制" : "")}");
            ShowBaseline();
            ResetStatsAfterZero();
        }
        catch (Exception ex)
        {
            _status.Text = "置零出错：" + ex.Message;
            _status.ForeColor = Color.Firebrick;
        }
        finally
        {
            try
            {
                await _engine.ResumeMonitorAsync(_monitored, CancellationToken.None);
                _status.Text += "，已恢复监测";
            }
            catch (Exception ex)
            {
                _status.Text += "，恢复监测失败：" + ex.Message;
            }

            SetBusy(false);
        }
    }

    private void ResetStatsAfterZero()
    {
        lock (_lock)
        {
            _samples.Clear();
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _btnZero.Enabled = !busy;
        _channel.Enabled = !busy;
        _force.Enabled = !busy;
        if (!busy)
        {
            RefreshLive();
        }
    }
}
