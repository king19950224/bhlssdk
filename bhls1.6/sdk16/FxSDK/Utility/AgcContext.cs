namespace FxSDK.Utility;

public class AgcContext : IDisposable
{
    private readonly record struct AgcSample(decimal Gain, double Peak, bool IsSaturated);

    private const double TargetToleranceRatio = 0.10;
    private const double MinUsefulPeakRatio = 0.25;
    private const double PlatformPeakGrowthRatio = 0.05;

    /// <summary>
    /// Agc 状态
    /// </summary>
    public enum AgcStatus
    {
        Running,
        Success,
        MaxIterationsReached,
        Cancelled
    }
    public AgcContext(decimal initGain, double targetPeak, double[] gainRange, CancellationToken token, decimal gainStep = 1, int maxIterations = 20)
    {
        if (gainRange == null || gainRange.Length != 2 || gainRange[0] > gainRange[1])
            throw new ArgumentException("GainRange must be a two-element array [min, max] with min <= max.", nameof(gainRange));
        if (maxIterations <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxIterations), "Must be positive.");
        if (targetPeak <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetPeak), "Target peak must be positive.");

        _targetPeak = targetPeak;
        GainStep = gainStep > 0 ? gainStep : 1;
        _minGain = (decimal)gainRange[0];
        _maxGain = (decimal)gainRange[1];
        Gain = ClampGain(initGain);
        _initialSearchStep = BuildInitialSearchStep();
        _maxIterations = maxIterations;
        Status = AgcStatus.Running;
        _cts = token.CanBeCanceled 
            ? CancellationTokenSource.CreateLinkedTokenSource(token) 
            : new CancellationTokenSource();
    }
    /// <summary>
    /// 只保留最新的回波数据
    /// </summary>
    private readonly FixedConcurrentQueue<List<short>> _waveQueue = new(1);

    /// <summary>
    /// 用于取消 AGC 任务
    /// </summary>
    private readonly CancellationTokenSource _cts;
    private int _disposed;
    private readonly int _maxIterations;
    private readonly double _targetPeak;
    private readonly decimal _minGain;
    private readonly decimal _maxGain;
    private readonly decimal _initialSearchStep;
    private readonly List<AgcSample> _samples = new();
    private AgcSample? _bestUsableSample;
    private decimal? _lowGain;
    private decimal? _highGain;
    private DateTime _ignoreWaveBeforeUtc = DateTime.MinValue;
    private int _emptyWaveWaitCount;
    public decimal Gain { get; set; }
    private decimal GainStep { get; }
    public int FinalIterations { get; private set; } = 0;
    /// <summary>
    /// 当前 AGC 状态（线程安全读取）
    /// </summary>
    public AgcStatus Status { get; private set; }

    /// <summary>
    /// 添加最新的回波数据（线程安全）
    /// </summary>
    public void AddWaveData(List<short> waveData)
    {
        AddWaveData(waveData, DateTime.UtcNow);
    }

    /// <summary>
    /// 添加最新的回波数据（线程安全）
    /// </summary>
    public void AddWaveData(List<short> waveData, DateTime receiveTimeUtc)
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            return;
        }
        try
        {
            if (_cts.Token.IsCancellationRequested)
            {
                Status = AgcStatus.Cancelled;
                return;
            }

            if (waveData.Count == 0 || Status != AgcStatus.Running)
                return;

            if (receiveTimeUtc < _ignoreWaveBeforeUtc)
            {
                return;
            }

            _waveQueue.Enqueue(waveData);
        }
        catch (ObjectDisposedException)
        {
            // 上层会话已经结束，迟到的波形回调直接忽略。
        }
    }

    public void BeginGainProbe(TimeSpan settleDelay)
    {
        while (_waveQueue.TryDequeue(out _))
        {
        }

        _ignoreWaveBeforeUtc = DateTime.UtcNow + settleDelay;
    }

    /// <summary>
    /// 等待并处理下一轮回波数据，计算新增益
    /// </summary>
    /// <param name="timeout">总超时时间</param>
    /// <param name="stableTime">等待数据稳定的时间</param>
    public async Task<decimal> WaitNextGainAsync(TimeSpan timeout, TimeSpan stableTime)
    {
        if (_cts.Token.IsCancellationRequested)
        {
            //如果已取消改变状态 然后返回
            Status = AgcStatus.Cancelled;
            return Gain;
        }
        if (Status != AgcStatus.Running)
        {
            // 如果已结束，直接返回 false
            return Gain;
        }
        
        var startTime = DateTime.UtcNow;
        var stableDeadline = startTime + stableTime;
        var overallDeadline = startTime + timeout;

        while (DateTime.UtcNow < overallDeadline && Status == AgcStatus.Running && !_cts.IsCancellationRequested)
        {
            try
            {
                if (DateTime.UtcNow >= stableDeadline)
                {
                    if (_waveQueue.TryDequeue(out var waveData))
                    {
                        return CalcNextGain(waveData);
                    }
                }
                await Task.Delay(10, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                Status = AgcStatus.Cancelled;
                return Gain;
            }
            catch (Exception e)
            {
                Console.WriteLine($"Unexpected error in WaitNextGainAsync: {e}");
                Status = AgcStatus.Cancelled; //异常取消
                return Gain;
            }
        }
        if (++_emptyWaveWaitCount >= 2)
        {
            CompleteWithBest(AgcStatus.MaxIterationsReached);
        }

        return Gain;
    }

    /// <summary>
    /// 计算下一个增益值
    /// </summary>
    private decimal CalcNextGain(List<short> waveData)
    {
        var nextGain = Gain;
        if (_cts.Token.IsCancellationRequested)
        {
            //如果已取消改变状态 然后返回
            Status = AgcStatus.Cancelled;
            return nextGain;
        }
        
        if (Status != AgcStatus.Running) 
            return nextGain;

        FinalIterations++;

        double currentPeak = waveData.Max(Math.Abs);
        _emptyWaveWaitCount = 0;
        var sample = BuildSample(Gain, currentPeak);
        _samples.Add(sample);
        UpdateBestUsableSample(sample);
        UpdateSearchBounds(sample);

        if (IsTargetMatched(sample))
        {
            Status = AgcStatus.Success;
            Gain = sample.Gain;
            return Gain;
        }

        if (currentPeak < 1e-6)
        {
            nextGain = CalcNextProbeGain(sample);
            return nextGain == Gain ? CompleteWithBest(AgcStatus.MaxIterationsReached) : nextGain;
        }

        if (FinalIterations >= _maxIterations)
        {
            return CompleteWithBest(AgcStatus.MaxIterationsReached);
        }

        // 增益和幅度不是线性关系时，用夹逼/二分比比例反推更稳。
        nextGain = CalcNextProbeGain(sample);
        if (nextGain == Gain)
        {
            return CompleteWithBest(_bestUsableSample.HasValue
                ? AgcStatus.Success
                : AgcStatus.MaxIterationsReached);
        }

        return nextGain;
    }

    private AgcSample BuildSample(decimal gain, double peak)
    {
        return new AgcSample(gain, peak, IsPlatformSaturated(gain, peak));
    }

    private bool IsTargetMatched(AgcSample sample)
    {
        if (sample.IsSaturated)
        {
            return false;
        }

        var tolerance = _targetPeak * TargetToleranceRatio;
        return Math.Abs(sample.Peak - _targetPeak) <= tolerance;
    }

    private void UpdateBestUsableSample(AgcSample sample)
    {
        if (sample.IsSaturated || sample.Peak < _targetPeak * MinUsefulPeakRatio)
        {
            return;
        }

        if (!_bestUsableSample.HasValue ||
            GetTargetError(sample) < GetTargetError(_bestUsableSample.Value))
        {
            _bestUsableSample = sample;
        }
    }

    private void UpdateSearchBounds(AgcSample sample)
    {
        var lowerTarget = _targetPeak * (1 - TargetToleranceRatio);
        var upperTarget = _targetPeak * (1 + TargetToleranceRatio);

        if (sample.IsSaturated || sample.Peak > upperTarget)
        {
            if (!_highGain.HasValue || sample.Gain < _highGain.Value)
            {
                _highGain = sample.Gain;
            }
            return;
        }

        if (sample.Peak < lowerTarget)
        {
            if (!_lowGain.HasValue || sample.Gain > _lowGain.Value)
            {
                _lowGain = sample.Gain;
            }
        }
    }

    private decimal CalcNextProbeGain(AgcSample sample)
    {
        if (_lowGain.HasValue && _highGain.HasValue)
        {
            if (_highGain.Value <= _lowGain.Value ||
                Math.Abs(_highGain.Value - _lowGain.Value) <= GainStep)
            {
                return Gain;
            }

            return ClampGain((_lowGain.Value + _highGain.Value) / 2);
        }

        var upperTarget = _targetPeak * (1 + TargetToleranceRatio);
        if (sample.IsSaturated || sample.Peak > upperTarget)
        {
            return ClampGain(sample.Gain - _initialSearchStep);
        }

        return ClampGain(sample.Gain + _initialSearchStep);
    }

    private bool IsPlatformSaturated(decimal gain, double peak)
    {
        AgcSample? previous = null;
        foreach (var sample in _samples)
        {
            if (sample.Gain < gain && (!previous.HasValue || sample.Gain > previous.Value.Gain))
            {
                previous = sample;
            }
        }

        if (!previous.HasValue || Math.Abs(gain - previous.Value.Gain) < GainStep)
        {
            return false;
        }

        if (previous.Value.Peak < _targetPeak * MinUsefulPeakRatio)
        {
            return false;
        }

        // 增益继续增大，但峰值基本不涨，说明模拟前端/AD 已进入有效平台区。
        return peak <= previous.Value.Peak * (1 + PlatformPeakGrowthRatio);
    }

    private decimal CompleteWithBest(AgcStatus fallbackStatus)
    {
        if (_bestUsableSample.HasValue)
        {
            Gain = _bestUsableSample.Value.Gain;
            Status = AgcStatus.Success;
            return Gain;
        }

        Status = fallbackStatus;
        return Gain;
    }

    private double GetTargetError(AgcSample sample)
    {
        return Math.Abs(sample.Peak - _targetPeak);
    }

    private decimal BuildInitialSearchStep()
    {
        var range = _maxGain - _minGain;
        if (range <= 0)
        {
            return GainStep;
        }

        var step = RoundByStep(range / 4);
        return step < GainStep ? GainStep : step;
    }

    private decimal ClampGain(decimal value)
    {
        if (value < _minGain)
        {
            value = _minGain;
        }
        else if (value > _maxGain)
        {
            value = _maxGain;
        }

        var rounded = RoundByStep(value);
        if (rounded < _minGain)
        {
            return _minGain;
        }

        return rounded > _maxGain ? _maxGain : rounded;
    }

    /// <summary>
    /// 按增益步长取整
    /// </summary>
    private decimal RoundByStep(decimal value)
    {
        if (GainStep <= 0) return value;
        return Math.Round(value / GainStep, MidpointRounding.AwayFromZero) * GainStep;
    }

    /// <summary>
    /// 主动取消 AGC 过程
    /// </summary>
    public void Cancel()
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            return;
        }

        if (Status == AgcStatus.Running)
        {
            try
            {
                _cts.Cancel();
                Status = AgcStatus.Cancelled;
            }
            catch (ObjectDisposedException)
            {
                // AGC 已经结束，迟到的取消请求不需要再向外传播。
            }
        }
    }

    public AgcResult ToResult()
    {
        return new AgcResult(Status, Gain, FinalIterations);
    }

    public void CompleteIfStillRunning()
    {
        if (Status == AgcStatus.Running)
        {
            CompleteWithBest(AgcStatus.MaxIterationsReached);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _cts.Dispose();
        }
    }
}

public sealed record AgcResult(AgcContext.AgcStatus Status, decimal Gain, int FinalIterations);
