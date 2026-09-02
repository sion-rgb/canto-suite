using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CantoTranscribe;

internal sealed record DictionaryRoot([property: JsonPropertyName("entries")] List<DictionaryEntry> Entries);
internal sealed record DictionaryEntry(
    [property: JsonPropertyName("spoken")] string Spoken,
    [property: JsonPropertyName("display")] string Display,
    [property: JsonPropertyName("caseSensitive")] bool CaseSensitive);

internal sealed class DictionaryService
{
    private static readonly Regex ExcessiveFillers = new(
        @"(?<f>呃|嗯|哦)(?:[\s，,、]*\k<f>){2,}",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex RepeatedRestart = new(
        @"(?<p>即係|其實|我哋|咁樣)(?:[\s，,、]+\k<p>)+",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private readonly List<DictionaryEntry> _entries;

    public DictionaryService()
    {
        var path = System.IO.Path.Combine(AppContext.BaseDirectory,
            "resources", "cantonese-dictionary", "default.v1.json");
        _entries = (JsonSerializer.Deserialize<DictionaryRoot>(File.ReadAllText(path))
            ?? throw new InvalidDataException("術語字典格式錯誤")).Entries
            .OrderByDescending(entry => entry.Spoken.Length).ToList();
    }

    public string Clean(string raw)
    {
        var output = raw.Trim();
        foreach (var entry in _entries)
        {
            output = Regex.Replace(output, Regex.Escape(entry.Spoken),
                _ => entry.Display,
                entry.CaseSensitive ? RegexOptions.CultureInvariant : RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        output = ExcessiveFillers.Replace(output, match => match.Groups["f"].Value);
        output = RepeatedRestart.Replace(output, match => match.Groups["p"].Value);
        output = Regex.Replace(output, @"[，,、]{2,}", "，", RegexOptions.CultureInvariant);
        output = Regex.Replace(output, @"\s+([，。！？；：])", "$1", RegexOptions.CultureInvariant);
        output = Regex.Replace(output, @"([，。！？；：])\s+", "$1", RegexOptions.CultureInvariant);
        if (Regex.IsMatch(output, @"[\u3400-\u9fff]", RegexOptions.CultureInvariant) &&
            !Regex.IsMatch(output, @"[。！？….!?]$", RegexOptions.CultureInvariant))
            output += "。";
        return output;
    }
}
