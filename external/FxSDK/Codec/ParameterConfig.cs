namespace FxSDK.Codec;

/// <summary>
/// 参数配置模型
/// </summary>
public class ParameterConfig
{
    public ushort StartAddress { get; set; }
    public byte NumberOfParameters { get; set; }
    public byte[] ParameterValues { get; set; }

    public ParameterConfig(ushort startAddress, byte numberOfParameters, byte[] parameterValues)
    {
        StartAddress = startAddress;
        NumberOfParameters = numberOfParameters;
        ParameterValues = parameterValues;
    }
}