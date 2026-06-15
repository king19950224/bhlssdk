namespace FxSDK.Codec;

public class Pt1000Decoder
{
    //PT1000拟合公式 y = 0.259x - 259.4
    /// <summary>
    /// PT1000标准电阻
    /// </summary>
    public decimal RefSt { get; set; } = 1000M;
    /// <summary>
    /// Pt1000测温高温极限
    /// </summary>
    private const decimal Pt1000Max = 850M;
    /// <summary>
    /// Pt1000测温低温极限
    /// </summary>
    private const decimal Pt1000Min = -200M;
    /// <summary>
    /// 使用 pt1000积分和参考电阻积分
    /// 计算温度值
    /// </summary>
    public decimal? GetResult(decimal pt1000Iv, decimal refResIv)
    {
        if (refResIv == 0)
        {
            //throw new ArgumentException("参考电阻积分值不能为零。", nameof(refResIv));
            return null;
        }
        // 计算pt1000电阻
        var pt1000Res = pt1000Iv * RefSt / refResIv;
        return pt1000Res * 0.259M - 259.4M;
    }
    /// <summary>
    /// AD采集温度
    /// </summary>
    private const decimal AdScale = 0.00625M;
    /// <summary>
    /// 依据Ad采集值获取温度
    /// </summary>
    public decimal GetAdTemp(short adValue, decimal scale = AdScale)
    {
        return adValue * scale;
    }
    /// <summary>
    /// 结果是否有效
    /// </summary>
    public static bool IsValidValue(decimal result)
    {
        return result is >= Pt1000Min and <= Pt1000Max;
    }
}