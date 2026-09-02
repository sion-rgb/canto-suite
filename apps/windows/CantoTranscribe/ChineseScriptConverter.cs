using System.Runtime.InteropServices;

namespace CantoTranscribe;

internal static class ChineseScriptConverter
{
    private const uint SimplifiedChinese = 0x02000000;
    private static readonly Lazy<OpenCcStage[]> HongKongStages = new(LoadHongKongStages, true);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int LCMapStringEx(string localeName, uint mapFlags,
        string source, int sourceLength, [Out] char[]? destination, int destinationLength,
        IntPtr versionInformation, IntPtr reserved, IntPtr sortHandle);

    public static string Convert(string text, string outputScript)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return outputScript == "簡體中文" ? ToSimplified(text) : ToHongKongTraditional(text);
    }

    private static string ToHongKongTraditional(string text)
    {
        var output = text;
        foreach (var stage in HongKongStages.Value) output = stage.Convert(output);
        return output;
    }

    private static string ToSimplified(string text)
    {
        const string locale = "zh-Hans";
        var length = LCMapStringEx(locale, SimplifiedChinese, text, text.Length,
            null, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (length <= 0) throw new InvalidOperationException("中文繁簡轉換失敗");
        var output = new char[length];
        var written = LCMapStringEx(locale, SimplifiedChinese, text, text.Length,
            output, length, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (written <= 0) throw new InvalidOperationException("中文繁簡轉換失敗");
        return new string(output, 0, written);
    }

    private static OpenCcStage[] LoadHongKongStages()
    {
        var root = System.IO.Path.Combine(AppContext.BaseDirectory, "resources", "opencc");
        return
        [
            OpenCcStage.Load(System.IO.Path.Combine(root, "STPhrases.txt"),
                System.IO.Path.Combine(root, "STCharacters.txt")),
            OpenCcStage.Load(System.IO.Path.Combine(root, "HKVariantsPhrases.txt"),
                System.IO.Path.Combine(root, "HKVariants.txt"))
        ];
    }

    private sealed class OpenCcStage(Dictionary<string, string> mappings, int maxKeyLength)
    {
        public static OpenCcStage Load(params string[] paths)
        {
            var mappings = new Dictionary<string, string>(StringComparer.Ordinal);
            var maximum = 1;
            foreach (var path in paths)
            {
                foreach (var line in File.ReadLines(path))
                {
                    if (string.IsNullOrWhiteSpace(line) || line[0] == '#') continue;
                    var tab = line.IndexOf('\t');
                    if (tab <= 0 || tab == line.Length - 1) continue;
                    var source = line[..tab];
                    var alternatives = line[(tab + 1)..];
                    var separator = alternatives.IndexOf(' ');
                    var target = separator < 0 ? alternatives : alternatives[..separator];
                    mappings.TryAdd(source, target);
                    maximum = Math.Max(maximum, source.Length);
                }
            }
            return new OpenCcStage(mappings, maximum);
        }

        public string Convert(string text)
        {
            var output = new System.Text.StringBuilder(text.Length);
            for (var index = 0; index < text.Length;)
            {
                string? replacement = null;
                var consumed = 1;
                for (var length = Math.Min(maxKeyLength, text.Length - index); length > 0; length--)
                {
                    if (!mappings.TryGetValue(text.Substring(index, length), out replacement)) continue;
                    consumed = length;
                    break;
                }
                output.Append(replacement ?? text[index].ToString());
                index += consumed;
            }
            return output.ToString();
        }
    }
}
