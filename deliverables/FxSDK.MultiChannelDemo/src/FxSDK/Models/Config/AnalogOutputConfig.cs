namespace FxSDK.Models
{
    public sealed class AnalogOutputConfig
    {
        /// <summary>
        /// 模拟输出状态
        /// 0 输出轴力(依据标定表输出)
        /// 1 输出电平
        /// </summary>
        public ushort OutMode { get; set; } = 0;
        /// <summary>
        /// 输出电平值
        /// </summary>
        public decimal OutputVoltage { get; set; } = 0M;
        #region 输出轴力配置
        /// <summary>
        /// 声时差范围
        /// </summary>
        public ushort DTofRange { get; set; } = 500;
        /// <summary>
        /// 输出量程
        /// </summary>
        public ushort FullRange { get; set; } = 10;
        /// <summary>
        /// 力输出标定表
        /// 声时差vs输出轴力
        /// </summary>
        public List<decimal[]>? CalibrationTable { get; set; }
        /// <summary>
        /// 输出参考声时(依据条件输出归一的或者原始参考声时)
        /// </summary>
        public decimal? RefEchoTime { get; set; }
        #endregion
    }
}