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
        return output;
    }
}
