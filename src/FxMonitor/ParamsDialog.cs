using FxDevice;

namespace FxMonitor;

internal sealed class ParamsDialog : Form
{
    private static readonly (string Key, string Header, string Tip, bool Storage)[] Cols =
    {
        ("ProbeFreq", "探头频率\nMHz", "范围 0~65 MHz", false),
        ("EchoWin", "回波窗\nns", "范围 0~65535000 ns，按 1000 ns 取整", false),
        ("EchoWidth", "回波宽度\nus", "范围 0~65535 us", false),
        ("Gain", "增益\n档", "范围 1~47", false),
        ("Duty", "占空比\n%", "范围 0~100", false),
        ("Forward", "正向\n脉冲", "范围 0~15", false),
        ("Reverse", "反向\n脉冲", "范围 0~15", false),
        ("MeasMode", "测量\n模式", "0 自动，1 位置，2 阈值。存储区参数，修改后该通道重新置零", true),
        ("Threshold", "检波\n阈值", "范围 -32768~32767。存储区参数，修改后该通道重新置零", true),
        ("RefTime", "参考声时\nns", "存储区参数，修改后该通道重新置零；未置零的通道不能写入", true),
        ("RefTemp", "参考温度\n℃", "范围 0~327 ℃。存储区参数，修改后该通道重新置零", true),
    };

    private static bool IsStorageCol(string key) => Cols.First(c => c.Key == key).Storage;

    private readonly DeviceEngine _engine;
    private readonly IReadOnlyList<ushort> _monitored;

    private readonly Button _btnRead = new() { Text = "重新读取", Width = 90, Height = 30 };
    private readonly Button _btnWrite = new() { Text = "下发修改", Width = 130, Height = 30, Enabled = false };
    private readonly Button _btnRevert = new() { Text = "放弃修改", Width = 90, Height = 30, Enabled = false };
    private readonly Button _btnStop = new() { Text = "停止", Width = 60, Height = 30, Enabled = false };
    private readonly Label _status = new() { AutoSize = true, Padding = new Padding(8, 8, 0, 0), Text = "" };

    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.CellSelect,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        BackgroundColor = Color.White,
        BorderStyle = BorderStyle.None,
        EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2,
        ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.EnableResizing,
        ColumnHeadersHeight = 44,
    };

    private readonly Dictionary<ushort, Dictionary<string, string>> _original = new();

    public IReadOnlyDictionary<ushort, Dictionary<string, string>> DeviceValues => _original;

    private readonly HashSet<ushort> _dead = new();

    private CancellationTokenSource? _cts;
    private bool _busy;

    private static readonly Color Edited = Color.FromArgb(255, 249, 196);
    private static readonly Color Ok = Color.FromArgb(214, 245, 214);
    private static readonly Color Bad = Color.FromArgb(250, 216, 216);
    private static readonly Color Blind = Color.FromArgb(252, 244, 205);
    private static readonly Color DeadRow = Color.FromArgb(240, 240, 240);
    private static readonly Color FillRow = Color.FromArgb(235, 242, 250);

    public ParamsDialog(DeviceEngine engine, IReadOnlyList<ushort> monitoredChannels, ushort selectedChannel)
    {
        _engine = engine;
        _monitored = monitoredChannels.Count > 0
            ? monitoredChannels
            : new[] { selectedChannel >= 1 ? selectedChannel : (ushort)1 };

        Text = "参数下发";
        Font = new Font("Microsoft YaHei UI", 9.5f);
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(1300, 540);
        MinimumSize = new Size(1000, 400);
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = true;
        MinimizeBox = false;
        ShowInTaskbar = false;

        BuildUi();

        _btnRead.Click += async (_, _) => await ReadAllAsync();
        _btnWrite.Click += async (_, _) => await WriteAsync();
        _btnRevert.Click += (_, _) => RevertEdits();
        _btnStop.Click += (_, _) =>
        {
            _cts?.Cancel();
            _status.Text = "正在停止...";
        };

        _grid.CellBeginEdit += (_, e) =>
        {
            if (_busy || e.ColumnIndex == 0 || _grid.Columns[e.ColumnIndex].Name == "result"
                || _dead.Contains(RowChannel(e.RowIndex) ?? 0))
            {
                e.Cancel = true;
            }
        };
        _grid.CellEndEdit += (_, e) => OnCellEdited(e.RowIndex, e.ColumnIndex);

        Shown += async (_, _) => await ReadAllAsync();
        FormClosing += (_, e) =>
        {
            if (_busy)
            {
                e.Cancel = true;
                _status.Text = "正在读写设备，请先停止";
            }
        };
    }

    private void BuildUi()
    {
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 42, Padding = new Padding(8, 5, 0, 0), WrapContents = false };
        _btnWrite.Margin = new Padding(0, 1, 8, 0);
        _btnRead.Margin = new Padding(0, 1, 8, 0);
        _btnRevert.Margin = new Padding(0, 1, 8, 0);
        bar.Controls.Add(_btnWrite);
        bar.Controls.Add(_btnRevert);
        bar.Controls.Add(_btnRead);
        bar.Controls.Add(_btnStop);
        bar.Controls.Add(_status);

        var hint = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 30,
            Padding = new Padding(10, 4, 8, 2),
            ForeColor = Color.Gray,
            Text = "在“全部通道”行输入可整列修改。橙色表头的四列为存储区参数，只有已置零的通道能改；改完之前的声时差读数不再可比。",
        };

        var gridHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 4, 8, 0) };
        gridHost.Controls.Add(_grid);

        Controls.Add(gridHost);
        Controls.Add(hint);
        Controls.Add(bar);

        _grid.EnableHeadersVisualStyles = false;
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "ch", HeaderText = "通道", FillWeight = 40, ReadOnly = true,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });
        foreach (var c in Cols)
        {
            var col = new DataGridViewTextBoxColumn
            {
                Name = c.Key,
                HeaderText = c.Header.Replace('\n', ' '),
                ToolTipText = c.Tip,
                FillWeight = 62,
                SortMode = DataGridViewColumnSortMode.NotSortable,
            };
            if (c.Storage)
            {
                col.HeaderCell.Style.BackColor = Color.FromArgb(255, 236, 217);
            }

            _grid.Columns.Add(col);
        }

        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "result", HeaderText = "结果", FillWeight = 150, ReadOnly = true,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });
    }

    private async Task ReadAllAsync()
    {
        if (_busy) return;
        SetBusy(true);
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        try
        {
            _status.Text = "正在读取参数...";
            await _engine.StopStreamAsync();

            BuildRows();
            for (var i = 0; i < _monitored.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var ch = _monitored[i];
                _status.Text = $"正在读取 CH{ch} ({i + 1}/{_monitored.Count})...";
                var storage = await _engine.ReadStorageAsync(ch, ct);
                var regs = await _engine.ReadRegisterValuesAsync(ch, ct);
                var row = _grid.Rows[i + 1];

                if (storage.Count == 0 && regs.Count == 0)
                {
                    _dead.Add(ch);
                    row.DefaultCellStyle.BackColor = DeadRow;
                    row.Cells["result"].Value = "无响应";
                    continue;
                }

                var bag = new Dictionary<string, string>();
                foreach (var c in Cols)
                {
                    var v = (c.Storage ? storage : regs).GetValueOrDefault(c.Key, "-");
                    bag[c.Key] = v;
                    row.Cells[c.Key].Value = v;
                    row.Cells[c.Key].Style.BackColor = Color.White;
                }

                _original[ch] = bag;
            }

            _status.Text = "读取完成";
        }
        catch (OperationCanceledException)
        {
            _status.Text = "已停止";
        }
        catch (Exception ex)
        {
            _status.Text = "读取失败：" + ex.Message;
        }
        finally
        {
            await ResumeAsync();
            _cts?.Dispose();
            _cts = null;
            SetBusy(false);
            RefreshEditState();
        }
    }

    private void BuildRows()
    {
        _grid.Rows.Clear();
        _original.Clear();
        _dead.Clear();

        var fillIdx = _grid.Rows.Add();
        var fill = _grid.Rows[fillIdx];
        fill.Cells["ch"].Value = "全部通道";
        fill.Cells["result"].Value = "在此行输入可整列修改";
        fill.DefaultCellStyle.BackColor = FillRow;
        fill.DefaultCellStyle.ForeColor = Color.FromArgb(40, 70, 120);

        foreach (var ch in _monitored)
        {
            var idx = _grid.Rows.Add();
            var row = _grid.Rows[idx];
            row.Cells["ch"].Value = "CH" + ch;
            row.Tag = ch;
            foreach (var c in Cols) row.Cells[c.Key].Value = "-";
            row.Cells["result"].Value = string.Empty;
        }
    }

    private ushort? RowChannel(int rowIndex)
        => rowIndex >= 0 && rowIndex < _grid.Rows.Count && _grid.Rows[rowIndex].Tag is ushort ch ? ch : null;

    private void OnCellEdited(int rowIndex, int colIndex)
    {
        if (rowIndex < 0 || colIndex <= 0) return;
        var col = _grid.Columns[colIndex].Name;
        if (col == "result") return;

        var cell = _grid.Rows[rowIndex].Cells[colIndex];
        var text = Convert.ToString(cell.Value)?.Trim() ?? string.Empty;

        var rowCh = RowChannel(rowIndex);
        if (rowCh == null)
        {
            if (text.Length == 0) return;
            if (!TryValidate(col, text, out var err))
            {
                _status.Text = $"{HeaderOf(col)}：{err}";
                System.Media.SystemSounds.Beep.Play();
            }
            else
            {
                for (var i = 1; i < _grid.Rows.Count; i++)
                {
                    var target = RowChannel(i);
                    if (target != null && !_dead.Contains(target.Value))
                    {
                        _grid.Rows[i].Cells[colIndex].Value = text;
                        MarkCell(i, col, target.Value);
                    }
                }

                _status.Text = IsStorageCol(col)
                    ? $"{HeaderOf(col)} 已整列填为 {text}，存储区参数下发后所有通道将重新置零"
                    : $"{HeaderOf(col)} 已整列填为 {text}";
            }

            cell.Value = string.Empty;
            RefreshEditState();
            return;
        }

        var ch = rowCh.Value;
        if (text.Length == 0)
        {
            cell.Value = _original.GetValueOrDefault(ch)?.GetValueOrDefault(col, "-") ?? "-";
            MarkCell(rowIndex, col, ch);
            RefreshEditState();
            return;
        }

        if (!TryValidate(col, text, out var error))
        {
            _status.Text = $"CH{ch} {HeaderOf(col)}：{error}";
            System.Media.SystemSounds.Beep.Play();
            cell.Value = _original.GetValueOrDefault(ch)?.GetValueOrDefault(col, "-") ?? "-";
        }
        else if (IsStorageCol(col))
        {
            _status.Text = $"CH{ch} {HeaderOf(col)} 为存储区参数，下发后该通道将重新置零";
        }

        MarkCell(rowIndex, col, ch);
        RefreshEditState();
    }

    private static string HeaderOf(string col) => Cols.First(c => c.Key == col).Header.Replace('\n', ' ');

    private static bool TryValidate(string col, string text, out string error)
    {
        if (!decimal.TryParse(text, out var value))
        {
            error = $"{text} 不是有效数字";
            return false;
        }

        var err = DeviceEngine.Validate(col, value);
        if (err != null)
        {
            error = err;
            return false;
        }

        error = string.Empty;
        return true;
    }

    private void MarkCell(int rowIndex, string col, ushort ch)
    {
        var cell = _grid.Rows[rowIndex].Cells[col];
        var now = Convert.ToString(cell.Value)?.Trim() ?? string.Empty;
        var origin = _original.GetValueOrDefault(ch)?.GetValueOrDefault(col, "-") ?? "-";
        cell.Style.BackColor = DeviceEngine.SameNum(now, origin) ? Color.White : Edited;
    }

    private Dictionary<ushort, List<(string Key, decimal Value, string Origin)>> PendingEdits()
    {
        var edits = new Dictionary<ushort, List<(string, decimal, string)>>();
        for (var i = 1; i < _grid.Rows.Count; i++)
        {
            var rowCh = RowChannel(i);
            if (rowCh == null || _dead.Contains(rowCh.Value)) continue;
            var ch = rowCh.Value;
            var origin = _original.GetValueOrDefault(ch);
            if (origin == null) continue;

            foreach (var c in Cols)
            {
                var key = c.Key;
                var text = Convert.ToString(_grid.Rows[i].Cells[key].Value)?.Trim() ?? string.Empty;
                var before = origin.GetValueOrDefault(key, "-");
                if (text.Length == 0 || DeviceEngine.SameNum(text, before)) continue;
                if (!decimal.TryParse(text, out var value)) continue;
                if (!edits.TryGetValue(ch, out var list))
                {
                    edits[ch] = list = new List<(string, decimal, string)>();
                }

                list.Add((key, value, before));
            }
        }

        return edits;
    }

    private void RefreshEditState()
    {
        var edits = PendingEdits();
        var count = edits.Sum(kv => kv.Value.Count);
        _btnWrite.Text = count > 0 ? $"下发修改 ({count})" : "下发修改";
        _btnWrite.Enabled = count > 0 && !_busy;
        _btnRevert.Enabled = count > 0 && !_busy;
    }

    private void RevertEdits()
    {
        for (var i = 1; i < _grid.Rows.Count; i++)
        {
            var ch = RowChannel(i);
            if (ch == null) continue;
            var origin = _original.GetValueOrDefault(ch.Value);
            if (origin == null) continue;
            foreach (var c in Cols)
            {
                var key = c.Key;
                _grid.Rows[i].Cells[key].Value = origin.GetValueOrDefault(key, "-");
                _grid.Rows[i].Cells[key].Style.BackColor = Color.White;
            }

            _grid.Rows[i].Cells["result"].Value = string.Empty;
        }

        _status.Text = "已放弃修改";
        RefreshEditState();
    }

    private async Task WriteAsync()
    {
        var edits = PendingEdits();
        if (edits.Count == 0) return;

        var lines = edits.OrderBy(kv => kv.Key).SelectMany(kv =>
            kv.Value.Select(e => $"  CH{kv.Key}  {ParamCatalog.CnOf(e.Key)}：{e.Origin} -> {DeviceEngine.Fmt(e.Value)}"));
        var confirm = $"将下发 {edits.Sum(kv => kv.Value.Count)} 处修改，共 {edits.Count} 个通道：\n\n"
                      + string.Join("\n", lines)
                      + "\n\n下发期间测量暂停，完成后自动恢复。是否继续？";

        var storageChannels = edits.Where(kv => kv.Value.Any(e => IsStorageCol(e.Key)))
            .Select(kv => kv.Key).OrderBy(c => c).ToList();
        if (storageChannels.Count > 0)
        {
            confirm += $"\n\n注意：本次包含存储区参数，CH{string.Join("、CH", storageChannels)} 将重新置零，之前的声时差读数不再可比。";
        }

        if (MessageBox.Show(this, confirm, "确认下发", MessageBoxButtons.OKCancel,
                storageChannels.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Question)
            != DialogResult.OK)
        {
            return;
        }

        SetBusy(true);
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var ok = 0;
        var failed = new List<ushort>();
        DataGridViewRow? inflight = null;
        try
        {
            _status.Text = "正在暂停测量...";
            await _engine.StopStreamAsync();

            var channels = edits.Keys.OrderBy(c => c).ToList();
            foreach (var ch in channels)
            {
                ct.ThrowIfCancellationRequested();
                var rowIndex = RowIndexOf(ch);
                if (rowIndex < 0) continue;
                var row = _grid.Rows[rowIndex];
                _status.Text = $"正在下发 CH{ch}...";
                try
                {
                    var items = edits[ch].Select(e => (e.Key, e.Value)).ToList();
                    inflight = row;
                    var rows = await _engine.WriteRegistersAsync(ch, items, ct);
                    inflight = null;

                    var wrong = new List<ParamRow>();
                    var blind = new List<ParamRow>();
                    var unverified = 0;
                    foreach (var r in rows)
                    {
                        if (r.Implicit)
                        {
                            // 连带被重写的参数：值没变不打扰用户；读不回来只标一下，格子里的旧值大概率还对；
                            // 真被改了才当失败列出来
                            if (r.Verdict == Verdict.StoredOk) continue;
                            if (r.Verdict != Verdict.Rewritten)
                            {
                                row.Cells[r.Name].Style.BackColor = Blind;
                                unverified++;
                                continue;
                            }
                        }

                        var actual = r.ReadBack != "-" ? r.ReadBack : r.DeviceValue;
                        row.Cells[r.Name].Value = actual;
                        _original[ch][r.Name] = actual;
                        var bad = r.Verdict == Verdict.Ignored || r.Verdict == Verdict.Rewritten;
                        row.Cells[r.Name].Style.BackColor = r.Verdict == Verdict.StoredOk ? Ok : bad ? Bad : Blind;
                        if (bad) wrong.Add(r);
                        else if (r.Verdict == Verdict.NoReadback || r.Verdict == Verdict.NotSent) blind.Add(r);
                    }

                    var notes = wrong.Concat(blind).Select(Describe).ToList();
                    if (unverified > 0) notes.Add($"连带写入的 {unverified} 项未能读回核对");

                    if (wrong.Count == 0 && blind.Count == 0)
                    {
                        row.Cells["result"].Value = $"{rows.Count(r => !r.Implicit)} 项写入成功"
                                                    + (unverified > 0 ? "；" + notes[^1] : string.Empty);
                        ok++;
                    }
                    else if (wrong.Count == 0)
                    {
                        row.Cells["result"].Value = "未验证：" + string.Join("；", notes);
                        failed.Add(ch);
                    }
                    else
                    {
                        row.Cells["result"].Value = "失败：" + string.Join("；", notes);
                        failed.Add(ch);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    row.Cells["result"].Value = "失败：" + ex.Message;
                    failed.Add(ch);
                }
            }

            _status.Text = failed.Count == 0
                ? $"下发完成，{ok} 个通道全部成功"
                : $"下发完成，成功 {ok}，失败 {failed.Count}（CH{string.Join("、CH", failed)}）";
        }
        catch (OperationCanceledException)
        {
            // 命令可能已经发出去但还没读回：这一行的黄色格子不可信，让用户重新读取
            if (inflight != null)
            {
                inflight.Cells["result"].Value = "已中断：写入可能已生效但未核对，请点“重新读取”";
                inflight.Cells["result"].Style.BackColor = Blind;
            }

            _status.Text = $"已停止，完成 {ok}，失败 {failed.Count}";
        }
        catch (Exception ex)
        {
            _status.Text = "下发失败：" + ex.Message;
        }
        finally
        {
            await ResumeAsync();
            _cts?.Dispose();
            _cts = null;
            SetBusy(false);
            RefreshEditState();
        }
    }

    private int RowIndexOf(ushort ch)
    {
        for (var i = 1; i < _grid.Rows.Count; i++)
        {
            if (RowChannel(i) == ch) return i;
        }

        return -1;
    }

    private static string Describe(ParamRow r)
    {
        var what = r.Verdict switch
        {
            Verdict.Ignored => "写入无效",
            Verdict.Rewritten => r.Implicit ? $"原值 {r.BeforeValue} 被改成 {r.ReadBack}" : $"读回值为 {r.ReadBack}",
            Verdict.NoReadback => "读回无响应",
            Verdict.NotSent => r.Detail.Length > 0 ? $"未发送（{r.Detail}）" : "未发送",
            _ => r.Detail,
        };
        return (r.Implicit ? $"{r.Cn}（连带写入）" : r.Cn) + " " + what;
    }

    private async Task ResumeAsync()
    {
        try
        {
            if (_monitored.Count == 0) return;
            await _engine.ResumeMonitorAsync(_monitored, CancellationToken.None);
            _status.Text += "，已恢复监测";
        }
        catch (Exception ex)
        {
            _status.Text = "恢复监测失败：" + ex.Message;
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _btnRead.Enabled = !busy;
        _btnWrite.Enabled = !busy;
        _btnRevert.Enabled = !busy;
        _btnStop.Enabled = busy;
        _grid.ReadOnly = busy;
        if (!busy) RefreshEditState();
    }
}
