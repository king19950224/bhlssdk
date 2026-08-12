using System.IO.Ports;

namespace FxSDK.Utility;

public static class CommExtensions
{
    public static SerialPort ToSerialPort(this string paramString)
    {
        if (string.IsNullOrEmpty(paramString)) 
            throw new ArgumentException("Invalid communication parameter for serial port.", nameof(paramString));
        try
        {
            var strArray = paramString.Split(new[] { ';', '；', ',', '，', ':', '：' }, StringSplitOptions.RemoveEmptyEntries);
            var port = new SerialPort();
            port.PortName = strArray[0];
            port.BaudRate = strArray.Length > 1 ? int.Parse(strArray[1]) : 115200;
            if (strArray.Length > 2)
                port.Parity = (Parity)int.Parse(strArray[2]);
            if (strArray.Length > 3)
                port.DataBits = int.Parse(strArray[3]);
            if (strArray.Length > 4)
            {
                var decBits = decimal.Parse(strArray[4]);
                port.StopBits = ParseStopBits(decBits);
            } 
            return port;
        }
        catch (Exception)
        {
            throw new ArgumentException("Invalid communication parameter for serial port.", nameof(paramString));
        }
    }
    private static StopBits ParseStopBits(decimal decBits)
    {
        var enumBits = StopBits.One;
        if (decBits == 1.5M)
            enumBits = StopBits.OnePointFive;
        else if (decBits == 2M)
            enumBits = StopBits.Two;
        return enumBits;
    }

    public static Tuple<string,int> ToIpEndPoint(this string paramString)
    {
        try
        {
            var paramArray = paramString.Split(new[] {':', ','}, StringSplitOptions.RemoveEmptyEntries);
            if (paramArray.Length < 2)
            {
                throw new ArgumentException("Invalid communication parameter for IPEndPoint.", nameof(paramString));
            }
            return Tuple.Create(paramArray[0], int.Parse(paramArray[1]));
        }
        catch (Exception)
        {
            throw new ArgumentException("Invalid communication parameter for IPEndPoint.", nameof(paramString));
        }
    }
}
