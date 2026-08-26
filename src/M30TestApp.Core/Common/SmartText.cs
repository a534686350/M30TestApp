using System;
using System.IO;
using System.Text;

namespace M30TestApp.Core.Common;

/// <summary>
/// 统一的配置/数据文本文件编码策略。
///
/// 背景：现场存在 GBK/ANSI 编码的 ini/csv（LabVIEW 或中文记事本生成），此前按 UTF-8
/// 读取会静默产生 U+FFFD 乱码（TestPlan 里曾被迫留下 mojibake 别名补丁）。
///
/// 读取策略：BOM 优先（UTF-8/UTF-16LE/UTF-16BE）→ 无 BOM 时先按严格 UTF-8 解码，
/// 含非法字节序列则回退 GBK(936)。写入侧统一 UTF-8 with BOM（记事本/Excel 均可识别）。
/// </summary>
public static class SmartText
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static Encoding? _gbk;

    /// <summary>GBK(936) 编码。首次访问时自动注册 CodePages provider。</summary>
    public static Encoding GbkEncoding
    {
        get
        {
            if (_gbk is not null) return _gbk;
            // 注意：未注册 provider 时 .NET 抛 NotSupportedException（不是 ArgumentException）。
            // RegisterProvider 幂等，直接先注册再取编码。
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            _gbk = Encoding.GetEncoding(936);
            return _gbk;
        }
    }

    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);

    /// <summary>标准写入编码：UTF-8 with BOM。</summary>
    public static Encoding WriteEncoding => Utf8WithBom;

    /// <summary>按 BOM 探测 + 严格 UTF-8 失败回退 GBK 的策略读取整个文件。</summary>
    public static string ReadAllText(string path)
    {
        var bytes = File.ReadAllBytes(path);

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

        try
        {
            return StrictUtf8.GetString(bytes, 0, bytes.Length);
        }
        catch (DecoderFallbackException)
        {
            return GbkEncoding.GetString(bytes, 0, bytes.Length);
        }
    }

    /// <summary>与 <see cref="File.ReadAllText(string)"/> 行语义一致的智能编码版本。</summary>
    public static string[] ReadAllLines(string path)
    {
        var lines = ReadAllText(path).Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        if (lines.Length > 0 && lines[^1].Length == 0)
            Array.Resize(ref lines, lines.Length - 1);
        return lines;
    }
}
