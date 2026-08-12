using System.Collections;
using System.Text;

namespace FxSDK.Utility
{
    /// <summary>
    /// 字节处理工具类
    /// </summary>
    public static class ByteUtility
    {
        /// <summary>
        /// 字节数组转16进制字符串
        /// </summary>
        public static string BytesToHexStr(IEnumerable<byte>? bytes)
        {
            if (bytes == null) return string.Empty;
            
            var sbHex = new StringBuilder();
            foreach (var b in bytes)
            {
                sbHex.AppendFormat("{0:X2} ", b);
            }
            return sbHex.ToString();
        }
        /// <summary>
        /// 在字节序列中查找模式的首次出现位置。
        /// </summary>
        /// <param name="source">源字节序列</param>
        /// <param name="pattern">要查找的模式</param>
        /// <param name="startIndex">开始搜索的索引（默认为 0）</param>
        /// <returns>匹配起始索引，未找到返回 -1</returns>
        public static int IndexOf(IList<byte> source, ReadOnlySpan<byte> pattern, int startIndex = 0)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (pattern == null) throw new ArgumentNullException(nameof(pattern));
            if (startIndex < 0) throw new ArgumentOutOfRangeException(nameof(startIndex));

            var sourceCount = source.Count;
            var patternLength = pattern.Length;

            if (patternLength == 0) return startIndex; // 空模式匹配起始位置
            if (patternLength > sourceCount - startIndex) return -1;

            var maxSearch = sourceCount - patternLength;

            for (var i = startIndex; i <= maxSearch; i++)
            {
                var match = true;
                for (var j = 0; j < patternLength; j++)
                {
                    if (source[i + j] != pattern[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match) return i;
            }

            return -1;
        }
        /// <summary>
        /// 尝试从字节序列的指定位置读取大端无符号 32 位整数。
        /// </summary>
        public static bool TryReadUInt32BigEndian(IReadOnlyList<byte>? source, int offset, out uint value)
        {
            value = 0;
            if (source == null || offset < 0 || offset > source.Count - sizeof(uint))
            {
                return false;
            }

            value = ((uint)source[offset] << 24)
                    | ((uint)source[offset + 1] << 16)
                    | ((uint)source[offset + 2] << 8)
                    | source[offset + 3];
            return true;
        }
        /// <summary>
        /// 计算 Modbus RTU 使用的 CRC-16 校验（多项式 0xA001，低位在前）
        /// </summary>
        /// <param name="data">输入数据</param>
        /// <returns>2 字节数组，[0] 为低字节，[1] 为高字节</returns>
        public static byte[] CRC16_Modbus(byte[] data)
        {
            ushort crc = 0xFFFF; // 初始化 CRC 寄存器
            foreach (var b in data)
            {
                crc ^= b;
                for (var i = 0; i < 8; i++)
                {
                    if ((crc & 1) != 0)
                    {
                        crc >>= 1;
                        crc ^= 0xA001; // 多项式 0xA001
                    }
                    else
                    {
                        crc >>= 1;
                    }
                }
            }
            return new[]
            {
                (byte)(crc & 0xFF),     // 低字节
                (byte)((crc >> 8) & 0xFF) // 高字节
            };
        }
        /// <summary>
        /// CCITT CRC校验
        /// </summary>
        public static byte[] CRC16_CCITT(byte[] data)
        {
            return CRC16CCITT.CalculateBytes(data);
        }
        /// <summary>
        /// 累加和校验
        /// </summary>
        public static byte CheckSum(IEnumerable<byte> data)
        {
            byte sum = 0;
            foreach (var t in data)
            {
                sum += t;
            }
            return sum;
        }
        /// <summary>
        /// BCC异或校验
        /// </summary>
        public static byte BccXor(byte[] data)
        {
            byte bccByte = 0;
            var len = data.Length;
            for (var i = 0; i < len; i++)
            {
                bccByte ^= data[i];
            }
            return bccByte;
        }
        /// <summary>
        /// 从有符号16位转无符号数
        /// </summary>
        public static ushort ToUInt16(short value)
        {
            // 将 Int16 转换为字节数组
            var bytes = BitConverter.GetBytes(value);

            // 如果系统是大端需要反转
            if (!BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }
            // 从字节数组转换为 UInt16
            return BitConverter.ToUInt16(bytes, 0);
        }
        /// <summary>
        /// 1或者2字节转整型
        /// </summary>
        public static ushort ToUInt16(byte[] data)
        {
            ushort iData = 0;
            if (data.Length == 1)
            {
                iData = data[0];
            }
            else if (data.Length == 2)
            {
                iData = BitConverter.ToUInt16(data, 0);
            }
            return iData;
        }
        /// <summary>
        /// 最高4字节 长度字节模式 默认高位在前 
        /// </summary>
        /// <param name="lenBytes">数据长度字节</param>
        /// <param name="bigEndian">大端,高位在前</param>
        public static uint GetDataLen(byte[] lenBytes, bool bigEndian = true)
        {
            uint dataLen = 0;
            if (bigEndian && BitConverter.IsLittleEndian) 
                Array.Reverse(lenBytes);//转为小端模式
            var lenByteList = new List<byte>();
            lenByteList.AddRange(lenBytes);
            if (lenBytes.Length % 2 != 0)
            {
                lenByteList.Add(0);//增加高位 0 
            }
            if (lenByteList.Count == 2)
            {
                dataLen = BitConverter.ToUInt16(lenByteList.ToArray(), 0);
            }
            else if (lenByteList.Count == 4)
            {
                dataLen = BitConverter.ToUInt32(lenByteList.ToArray(), 0);
            }
            return dataLen;
        }
        /// <summary>
        /// 位数组反
        /// </summary>
        public static void Reverse(this BitArray array)
        {
            var length = array.Length;
            var mid = (length / 2);
            for (var i = 0; i < mid; i++)
            {
                (array[i], array[length - i - 1]) = (array[length - i - 1], array[i]);
            }
        }
    }
    public class CRC16CCITT
    {
        private const ushort Polynomial = 0x1021;//多项式
        private const ushort InitialValue = 0x0000; // 初始值
        private const ushort XOrOut = 0x0000;//输出异或值
        private static readonly ushort[] Table = new ushort[256];

        static CRC16CCITT()
        {
            ushort value;
            ushort temp;
            for (ushort i = 0; i < Table.Length; ++i)
            {
                value = 0;
                temp = (ushort)(i << 8);
                for (byte j = 0; j < 8; ++j)
                {
                    if (((value ^ temp) & 0x8000) != 0)
                    {
                        value = (ushort)((value << 1) ^ Polynomial);
                    }
                    else
                    {
                        value <<= 1;
                    }
                    temp <<= 1;
                }
                Table[i] = value;
            }
        }
        public static byte[] CalculateBytes(IEnumerable<byte> data)
        {
            var crc = Calculate(data);
            return BitConverter.GetBytes(crc);
        }
        private static ushort Calculate(IEnumerable<byte> data)
        {
            var crc = InitialValue;
            foreach (var t in data)
            {
                new BitArray(t).Reverse();
                var index = (byte)(Reflect(t, 8) ^ (crc >> 8));
                crc = (ushort)(Table[index] ^ (crc << 8));
            }
            return (ushort) (Reflect(crc, 16) ^ XOrOut);
        }
        private static ushort Reflect(ushort data, int size)
        {
            ushort output = 0;
            for (var i = 0; i < size; i++)
            {
                if ((data & 1) != 0)
                {
                    output |= (ushort)(1 << (size - 1 - i));
                }
                data = (ushort)(data >> 1);
            }
            return output;
        }
    }
}
