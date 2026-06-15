namespace BoltPreload.Algorithms.Waveform;

/// <summary>
/// 一条回波波形（用于诊断对比）：原始幅值序列 + 绝对时间轴参数。
/// </summary>
/// <param name="Data">幅值采样点</param>
/// <param name="EchoStartNs">首点对应的绝对时间（ns）</param>
/// <param name="ClockIntNs">采样间隔（ns/点）</param>
public sealed record WaveSeries(IReadOnlyList<short> Data, double EchoStartNs, double ClockIntNs);

/// <summary>
/// 波形对比诊断结果（方案 §8.6 信号质量评估）。
/// </summary>
/// <param name="Similarity">归一化互相关峰值 ∈ [-1,1]，1=形态完全一致（§8.6：余弦相似度等价于归一化互相关，对平移鲁棒）</param>
/// <param name="BestLagNs">取得最大相关时当前波形相对参考的平移量（ns，≈ ΔTOF）</param>
/// <param name="ZeroLagCorrelation">不做平移对齐时的"原位"相关（lag=0）</param>
/// <param name="AttenuationRatio">当前峰峰值 / 参考峰峰值。&lt;1 表示信号衰减（耦合劣化 / 探头老化的征兆）</param>
/// <param name="SnrDb">当前波形 SNR（外部传入，纳入健康度）。null 表示不可用</param>
/// <param name="HealthScore">0~100 综合健康度（§8.6.4：SNR 30% + 相似度 40% + 衰减 20% + 主频 10%）</param>
/// <param name="ScoreSnr">健康度分项：SNR 得分 0~1</param>
/// <param name="ScoreSimilarity">健康度分项：相似度得分 0~1</param>
/// <param name="ScoreAttenuation">健康度分项：衰减得分 0~1</param>
/// <param name="CurrentMainFreqMhz">当前波形主频（FFT 谱峰，MHz，§8.6.3）</param>
/// <param name="ReferenceMainFreqMhz">参考波形主频（MHz）</param>
/// <param name="FreqShiftRatio">主频偏移比 |f_cur − f_ref| / f_ref（探头老化/耦合增厚使主频下降，§8.6.3 经验 &gt;5% 关注）</param>
/// <param name="ScoreFreq">健康度分项：主频稳定性得分 0~1</param>
public sealed record WaveComparison(
    double Similarity,
    double BestLagNs,
    double ZeroLagCorrelation,
    double AttenuationRatio,
    double? SnrDb,
    int HealthScore,
    double ScoreSnr,
    double ScoreSimilarity,
    double ScoreAttenuation,
    double CurrentMainFreqMhz,
    double ReferenceMainFreqMhz,
    double FreqShiftRatio,
    double ScoreFreq);

/// <summary>
/// 波形信号质量诊断（方案 §8.6）。
/// <para>
/// 把"当前回波"与通道的"参考波形（健康基线）"做<b>形态对比</b>，输出归一化互相关相似度、
/// 波形平移、幅度衰减比与综合健康度评分。目标是捕捉"预紧力(TOF)尚未明显异常、但回波波形已先畸变"
/// 的早期失效（探头老化 / 耦合劣化 / 端面状态变化），对应 plan FMEA 中 §8.6 的缓解措施。
/// </para>
/// <para>
/// <b>本类只做诊断计算，不触发任何告警动作。</b> §8.6 的 0.85 相似度、5% 主频偏移等阈值是"经验值"，
/// 是否驱动告警须由可配置、可追溯的告警规则单独决定（遵守本系统"阈值/规则可追溯"红线）。
/// </para>
/// </summary>
public static class WaveDiagnostics
{
    // §8.6.4 健康度权重（SNR 30% / 相似度 40% / 衰减 20% / 主频 10%）。
    private const double WeightSnr = 0.30;
    private const double WeightSimilarity = 0.40;
    private const double WeightAttenuation = 0.20;
    private const double WeightFreq = 0.10;

    // 分项线性映射区间（§8.6 经验值，现场可按信号特性再调）。
    private const double SnrLowDb = 20.0, SnrHighDb = 40.0;
    private const double SimLow = 0.70, SimHigh = 0.98;
    private const double AttenLow = 0.60, AttenHigh = 1.00;
    private const double FreqShiftFull = 0.05;   // 主频偏移 ≥5% → 该项 0 分（§8.6.3 经验阈值）
    // SNR 不可用时给一个中性得分，避免单缺一项把健康度拉到极端。
    private const double NeutralSnrScore = 0.6;

    /// <summary>
    /// 对比当前波形与参考波形。两条波形若采样间隔不同，会先把参考波形线性重采样到当前波形的间隔再对比。
    /// </summary>
    /// <param name="current">当前回波</param>
    /// <param name="reference">参考（基线）回波</param>
    /// <param name="snrDb">当前波形 SNR（从 wave_features 取）</param>
    /// <param name="maxLagPoints">互相关搜索的最大平移点数；&lt;=0 时取较短序列长度的一半</param>
    public static WaveComparison Compare(
        WaveSeries current, WaveSeries reference, double? snrDb = null, int maxLagPoints = 0)
    {
        if (current.Data.Count == 0 || reference.Data.Count == 0)
            return new WaveComparison(0, 0, 0, 0, snrDb, 0, 0, 0, 0, 0, 0, 0, 0);

        var a = ToDouble(current.Data);
        // 把参考波形重采样到当前波形的采样间隔，保证两序列在同一时间刻度上滑动对齐
        var b = Math.Abs(current.ClockIntNs - reference.ClockIntNs) < 1e-9
            ? ToDouble(reference.Data)
            : Resample(reference.Data, reference.ClockIntNs, current.ClockIntNs);

        var clk = current.ClockIntNs > 0 ? current.ClockIntNs : 1.0;
        var maxLag = maxLagPoints > 0 ? maxLagPoints : Math.Max(1, Math.Min(a.Length, b.Length) / 2);

        Subtract(a, Mean(a));
        Subtract(b, Mean(b));

        var normA = Norm(a);
        var normB = Norm(b);
        double denom = normA * normB;

        double best = 0, zeroLag = 0;
        int bestLag = 0;
        if (denom > 1e-12)
        {
            for (int lag = -maxLag; lag <= maxLag; lag++)
            {
                double dot = 0;
                // a[i] 对齐 b[i - lag]；只累加两序列都有效的重叠区间
                int iStart = Math.Max(0, lag);
                int iEnd = Math.Min(a.Length, b.Length + lag);
                for (int i = iStart; i < iEnd; i++)
                    dot += a[i] * b[i - lag];
                double ncc = dot / denom;
                if (lag == 0) zeroLag = ncc;
                if (ncc > best) { best = ncc; bestLag = lag; }
            }
        }

        // 衰减比用原始（未去均值）峰峰值算，反映真实幅度变化
        double ppCur = PeakToPeak(current.Data);
        double ppRef = PeakToPeak(reference.Data);
        double atten = ppRef > 0 ? ppCur / ppRef : 0;

        // 主频与主频偏移（§8.6.3）：主频下降是探头老化/耦合增厚的早期征兆
        double curFreq = MainFrequencyMhz(current.Data, current.ClockIntNs);
        double refFreq = MainFrequencyMhz(reference.Data, reference.ClockIntNs);
        double freqShift = refFreq > 1e-9 ? Math.Abs(curFreq - refFreq) / refFreq : 0;

        double scoreSnr = snrDb is null ? NeutralSnrScore : Clamp01((snrDb.Value - SnrLowDb) / (SnrHighDb - SnrLowDb));
        double scoreSim = Clamp01((best - SimLow) / (SimHigh - SimLow));
        double scoreAtten = Clamp01((atten - AttenLow) / (AttenHigh - AttenLow));
        double scoreFreq = Clamp01(1.0 - freqShift / FreqShiftFull);
        int health = (int)Math.Round(100.0 * (
            WeightSnr * scoreSnr + WeightSimilarity * scoreSim +
            WeightAttenuation * scoreAtten + WeightFreq * scoreFreq));

        return new WaveComparison(
            Similarity: best,
            BestLagNs: bestLag * clk,
            ZeroLagCorrelation: zeroLag,
            AttenuationRatio: atten,
            SnrDb: snrDb,
            HealthScore: Math.Clamp(health, 0, 100),
            ScoreSnr: scoreSnr,
            ScoreSimilarity: scoreSim,
            ScoreAttenuation: scoreAtten,
            CurrentMainFreqMhz: curFreq,
            ReferenceMainFreqMhz: refFreq,
            FreqShiftRatio: freqShift,
            ScoreFreq: scoreFreq);
    }

    /// <summary>把序列从 srcClock 间隔线性重采样到 dstClock 间隔（起点对齐到第 0 点）。</summary>
    private static double[] Resample(IReadOnlyList<short> data, double srcClock, double dstClock)
    {
        int n = data.Count;
        if (n < 2 || srcClock <= 0 || dstClock <= 0) return ToDouble(data);
        double span = (n - 1) * srcClock;
        int outN = Math.Max(2, (int)Math.Floor(span / dstClock) + 1);
        var outArr = new double[outN];
        for (int k = 0; k < outN; k++)
        {
            double t = k * dstClock;            // 目标时间（相对起点）
            double pos = t / srcClock;          // 源序列的小数索引
            int i0 = (int)Math.Floor(pos);
            if (i0 >= n - 1) { outArr[k] = data[n - 1]; continue; }
            double frac = pos - i0;
            outArr[k] = data[i0] * (1 - frac) + data[i0 + 1] * frac;
        }
        return outArr;
    }

    private static double[] ToDouble(IReadOnlyList<short> data)
    {
        var arr = new double[data.Count];
        for (int i = 0; i < data.Count; i++) arr[i] = data[i];
        return arr;
    }

    private static double Mean(double[] x)
    {
        double s = 0;
        for (int i = 0; i < x.Length; i++) s += x[i];
        return x.Length > 0 ? s / x.Length : 0;
    }

    private static void Subtract(double[] x, double v)
    {
        for (int i = 0; i < x.Length; i++) x[i] -= v;
    }

    private static double Norm(double[] x)
    {
        double s = 0;
        for (int i = 0; i < x.Length; i++) s += x[i] * x[i];
        return Math.Sqrt(s);
    }

    private static double PeakToPeak(IReadOnlyList<short> data)
    {
        int max = short.MinValue, min = short.MaxValue;
        for (int i = 0; i < data.Count; i++)
        {
            int v = data[i];
            if (v > max) max = v;
            if (v < min) min = v;
        }
        return data.Count == 0 ? 0 : max - min;
    }

    private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

    /// <summary>
    /// 估计波形主频（MHz）：去均值 + Hanning 窗 + radix-2 FFT，取功率谱峰（抛物线插值精化峰位）。
    /// 主频随探头老化（晶片退化）下降、随耦合层增厚（高频被吸收）下降，是 §8.6.3 的早期诊断指标。
    /// </summary>
    public static double MainFrequencyMhz(IReadOnlyList<short> data, double clockIntNs)
    {
        int n0 = data.Count;
        if (n0 < 8 || clockIntNs <= 0) return 0;
        // 取不超过 n0 的最大 2 的幂（radix-2 FFT 要求）
        int n = 1;
        while (n * 2 <= n0) n *= 2;

        double mean = 0;
        for (int i = 0; i < n0; i++) mean += data[i];
        mean /= n0;

        var re = new double[n];
        var im = new double[n];
        for (int i = 0; i < n; i++)
        {
            double w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (n - 1));  // Hanning 窗，抑制谱泄漏
            re[i] = (data[i] - mean) * w;
            im[i] = 0;
        }
        Fft(re, im);

        int peak = 1;
        double peakMag = -1;
        for (int k = 1; k < n / 2; k++)
        {
            double mag = re[k] * re[k] + im[k] * im[k];
            if (mag > peakMag) { peakMag = mag; peak = k; }
        }
        // 抛物线插值精化谱峰位置（亚 bin 精度）
        double kf = peak;
        if (peak > 1 && peak < n / 2 - 1)
        {
            double m0 = re[peak - 1] * re[peak - 1] + im[peak - 1] * im[peak - 1];
            double m1 = peakMag;
            double m2 = re[peak + 1] * re[peak + 1] + im[peak + 1] * im[peak + 1];
            double denom = m0 - 2 * m1 + m2;
            if (Math.Abs(denom) > 1e-12) kf = peak + 0.5 * (m0 - m2) / denom;
        }
        double fsHz = 1e9 / clockIntNs;     // 采样频率（clockInt 是 ns/点）
        double fHz = kf * fsHz / n;
        return fHz / 1e6;                   // → MHz
    }

    /// <summary>迭代 radix-2 Cooley-Tukey FFT（in-place，要求长度为 2 的幂）。</summary>
    private static void Fft(double[] re, double[] im)
    {
        int n = re.Length;
        // 位反转置换
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len;
            double wRe = Math.Cos(ang), wIm = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double curRe = 1, curIm = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = i + k + len / 2;
                    double tRe = re[b] * curRe - im[b] * curIm;
                    double tIm = re[b] * curIm + im[b] * curRe;
                    re[b] = re[a] - tRe;
                    im[b] = im[a] - tIm;
                    re[a] += tRe;
                    im[a] += tIm;
                    double nRe = curRe * wRe - curIm * wIm;
                    curIm = curRe * wIm + curIm * wRe;
                    curRe = nRe;
                }
            }
        }
    }
}
