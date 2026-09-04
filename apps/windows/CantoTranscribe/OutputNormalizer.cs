namespace CantoTranscribe;

internal static class OutputNormalizer
{
    public static List<TimedText> NormalizeSegments(IEnumerable<TimedText> segments,
        string outputScript, DictionaryService dictionary)
    {
        return segments.Select(segment =>
        {
            // Convert once before controlled cleanup, then again as the final gate so
            // dictionary replacements can never reintroduce mixed Simplified fragments.
            var normalizedRaw = ChineseScriptConverter.Convert(segment.RawText, outputScript);
            var cleaned = dictionary.Clean(normalizedRaw);
            var finalCleaned = ChineseScriptConverter.Convert(cleaned, outputScript);
            return new TimedText(segment.StartMs, segment.EndMs, normalizedRaw, finalCleaned);
        }).ToList();
    }
}
