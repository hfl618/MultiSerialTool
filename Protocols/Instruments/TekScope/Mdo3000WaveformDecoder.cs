using System.Globalization;
using System.IO;

namespace MultiSerialTool.Protocols.Instruments.TekScope;

/// <summary>
/// 泰克 MDO3000 系波形解码：把 WFMOUTPRE? 前导头与 CURVE? 二进制块解码为物理量（时间/电压）。
/// 前导头是分号分隔的 15 个字段，字节数、字节序、缩放、偏移全部由它决定，故对型号差异足够稳健。
/// </summary>
public static class Mdo3000WaveformDecoder
{
    private const int PreambleFieldCount = 15;

    public static Waveform Decode(string source, string preamble, byte[] data)
    {
        string[] f = preamble.Split(';');
        if (f.Length < PreambleFieldCount)
            throw new InvalidDataException($"WFMOUTPRE 字段不足：期望 {PreambleFieldCount}，实际 {f.Length}");

        int bytesPerPoint = ParseInt(f[0]);
        string encoding = f[2].Trim();
        string byteOrder = f[4].Trim();
        double xIncr = ParseDouble(f[8]);
        double xZero = ParseDouble(f[9]);
        double ptOff = ParseDouble(f[10]);
        double yMult = ParseDouble(f[12]);
        double yZero = ParseDouble(f[13]);
        double yOff = ParseDouble(f[14]);

        double[] voltage = DecodeVoltage(data, bytesPerPoint, encoding, byteOrder, yMult, yZero, yOff);
        double[] time = new double[voltage.Length];
        for (int i = 0; i < time.Length; i++)
            time[i] = xZero + (i - ptOff) * xIncr;

        return new Waveform
        {
            Source = source,
            Time = time,
            Voltage = voltage,
            XIncrement = xIncr,
            YMultiplier = yMult,
            YZero = yZero,
            YOffset = yOff
        };
    }

    private static double[] DecodeVoltage(
        byte[] data, int bytesPerPoint, string encoding, string byteOrder,
        double yMult, double yZero, double yOff)
    {
        // RIBinary/SRIbinary 为有符号，RPBinary/SRPbinary 为无符号
        bool signed = encoding.Contains("RI", StringComparison.OrdinalIgnoreCase)
            && !encoding.Contains("RP", StringComparison.OrdinalIgnoreCase);
        bool littleEndian = byteOrder.StartsWith("LSB", StringComparison.OrdinalIgnoreCase);

        int count = data.Length / bytesPerPoint;
        var voltage = new double[count];

        for (int i = 0; i < count; i++)
        {
            int offset = i * bytesPerPoint;
            double raw = bytesPerPoint switch
            {
                1 => signed ? (sbyte)data[offset] : data[offset],
                2 => ReadInt16(data, offset, littleEndian, signed),
                4 => ReadInt32(data, offset, littleEndian, signed),
                _ => throw new NotSupportedException($"不支持的每点字节数：{bytesPerPoint}")
            };

            // 泰克标准换算：电压 = (原始计数 - 数字偏移) × 每点电压 + 零点电压
            voltage[i] = (raw - yOff) * yMult + yZero;
        }

        return voltage;
    }

    private static double ReadInt16(byte[] data, int offset, bool littleEndian, bool signed)
    {
        short v = littleEndian
            ? (short)(data[offset] | (data[offset + 1] << 8))
            : (short)((data[offset] << 8) | data[offset + 1]);
        return signed ? v : (ushort)v;
    }

    private static double ReadInt32(byte[] data, int offset, bool littleEndian, bool signed)
    {
        uint v = littleEndian
            ? (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24))
            : (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]);
        return signed ? (int)v : v;
    }

    private static int ParseInt(string s) => int.Parse(s.Trim(), CultureInfo.InvariantCulture);

    private static double ParseDouble(string s) => double.Parse(s.Trim(), CultureInfo.InvariantCulture);
}
