namespace FxSDK.Utility;

public class AgcContext
{
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
    public AgcContext(decimal initGain, double targetPeak, double[] gainRange, decimal gainStep = 1, int maxIterations = 20)
    {
        if (gainRange == null || gainRange.Length != 2 || gainRange[0] > gainRange[1])
            throw new ArgumentException("GainRange must be a two-element array [min, max] with min <= max.", nameof(gainRange));
        if (maxIterations <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxIterations), "Must be positive.");
        if (targetPeak <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetPeak), "Target peak must be positive.");

        Gain = initGain;
        _targetPeak = targetPeak;
        GainRange = gainRange;
        GainStep = gainStep > 0 ? gainStep : 1;
        _maxIterations = maxIterations;
        Status = AgcStatus.Running;
    }
    /// <summary>
    /// 只保留最新的回波数据
    /// </summary>
    private readonly FixedConcurrentQueue<List<short>> _waveQueue = new(1);
    /// <summary>
    /// 用于取消 AGC 任务
    /// </summary>
    public CancellationTokenSource Cts { get; private set; } = new();
    private readonly int _maxIterations;
    private readonly double _targetPeak;
    public decimal Gain { get; private set; }
    private double[] GainRange { get; }
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
        if (waveData.Count == 0 || Cts.IsCancellationRequested || Status != AgcStatus.Running)
            return;

        _waveQueue.Enqueue(waveData);
    }

    /// <summary>
    /// 等待并处理下一轮回波数据，计算新增益
    /// </summary>
    /// <param name="timeout">总超时时间</param>
    /// <param name="stableTime">等待数据稳定的时间</param>
    public async Task WaitNextGainAsync(TimeSpan timeout, TimeSpan stableTime)
    {
        if (Status != AgcStatus.Running || Cts.IsCancellationRequested)
        {
            // 如果已结束或被取消，直接返回 false
            return;
        }

        var startTime = DateTime.UtcNow;
        var stableDeadline = startTime + stableTime;
        var overallDeadline = startTime + timeout;

        while (DateTime.UtcNow < overallDeadline && Status == AgcStatus.Running && !Cts.IsCancellationRequested)
        {
            try
            {
                if (DateTime.UtcNow >= stableDeadline)
                {
                    if (_waveQueue.TryDequeue(out var waveData))
                    {
                        CalcNextGain(waveData);
                        return;
                    }
                }
                await Task.Delay(10, Cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                Console.WriteLine($"Unexpected error in WaitNextGainAsync: {e}");
                Status = AgcStatus.Cancelled; //异常取消
                return;
            }
        }
    }

    /// <summary>
    /// 计算下一个增益值
    /// </summary>
    private void CalcNextGain(List<short> waveData)
    {
        if (Status != AgcStatus.Running) 
            return;

        FinalIterations++;

        // 检查是否全零或无效
        double currentPeak = waveData.Max(Math.Abs);
        if (currentPeak < 1e-6)
        {
            // 数据无效，尝试增大增益，但不终止
            Gain = Math.Min(Gain * 2, (decimal)GainRange[1]);
            return;
        }

        var tolerance = _targetPeak * 0.1;
        if (Math.Abs(currentPeak - _targetPeak) <= tolerance)
        {
            Status = AgcStatus.Success;//收敛完成
            return;
        }
        // 新增：如果已经是最小增益的情况下，峰值已经超出目标幅度 则直接退出
        if (Gain == (decimal)GainRange[0] && currentPeak > _targetPeak)
        {
            Status = AgcStatus.Success; 
            return;
        }
        // 计算新增益
        var ratio = _targetPeak / currentPeak;
        const double adjustFactor = 0.6;
        var nextGainValue = (double)Gain * Math.Pow(ratio, adjustFactor);

        // 限制在增益范围内
        nextGainValue = Math.Max(GainRange[0], Math.Min(GainRange[1], nextGainValue));

        var nextGain = RoundByStep((decimal)nextGainValue);

        // 如果增益不再变化且已进行足够多迭代，视为收敛失败
        if (nextGain == Gain && FinalIterations >= _maxIterations / 2)
        {
            Status = AgcStatus.MaxIterationsReached;//收敛失败
            return;
        }

        Gain = nextGain;

        // 检查是否达到最大迭代次数
        if (FinalIterations >= _maxIterations)
        {
            Status = AgcStatus.MaxIterationsReached;//收敛失败
        }
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
        if (Status == AgcStatus.Running)
        {
            Cts.Cancel();
            Status = AgcStatus.Cancelled;
        }
    }
}