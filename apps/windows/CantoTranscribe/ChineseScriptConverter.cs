using System.Runtime.InteropServices;

namespace CantoTranscribe;

internal static class ChineseScriptConverter
{
    private const uint SimplifiedChinese = 0x02000000;
    private const uint TraditionalChinese = 0x04000000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int LCMapStringEx(string localeName, uint mapFlags,
        string source, int sourceLength, [Out] char[]? destination, int destinationLength,
        IntPtr versionInformation, IntPtr reserved, IntPtr sortHandle);

    public static string Convert(string text, string outputScript)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var simplified = outputScript == "簡體中文";
        var locale = simplified ? "zh-Hans" : "zh-Hant-HK";
        var flags = simplified ? SimplifiedChinese : TraditionalChinese;
        var length = LCMapStringEx(locale, flags, text, text.Length, null, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (length <= 0) throw new InvalidOperationException("中文繁簡轉換失敗");
        var output = new char[length];
        var written = LCMapStringEx(locale, flags, text, text.Length, output, length, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (written <= 0) throw new InvalidOperationException("中文繁簡轉換失敗");
        return new string(output, 0, written);
    }
}
