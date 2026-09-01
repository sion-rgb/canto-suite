using System.Text;

namespace CantoTranscribe;

internal static class SrtFormatter
{
    public static string Render(IReadOnlyList<TimedText> segments, bool clean)
    {
        var cues = new List<(long Start, long End, string Text)>();
        foreach (var segment in segments)
        {
            var text = (clean ? segment.CleanText : segment.RawText).Trim();
            if (text.Length == 0 || segment.EndMs <= segment.StartMs) continue;
            var pieces = Split(text, 36);
            var duration = Math.Max(900, segment.EndMs - segment.StartMs);
            for (var index = 0; index < pieces.Count; index++)
            {
                var start = segment.StartMs + duration * index / pieces.Count;
                var end = segment.StartMs + duration * (index + 1) / pieces.Count;
                end = Math.Clamp(end, start + 900, start + 7_000);
                cues.Add((start, end, Wrap(pieces[index], 18)));
            }
        }
        for (var index = 1; index < cues.Count; index++)
            if (cues[index - 1].End > cues[index].Start)
                cues[index - 1] = (cues[index - 1].Start, Math.Max(cues[index - 1].Start + 1, cues[index].Start), cues[index - 1].Text);
        var output = new StringBuilder();
        for (var index = 0; index < cues.Count; index++)
        {
            output.AppendLine((index + 1).ToString());
            output.Append(Stamp(cues[index].Start)).Append(" --> ").AppendLine(Stamp(cues[index].End));
            output.AppendLine(cues[index].Text).AppendLine();
        }
        return output.ToString();
    }

    private static List<string> Split(string text, int max)
    {
        var result = new List<string>();
        var remaining = text.Trim();
        while (remaining.Length > max)
        {
            var boundary = remaining[..max].LastIndexOfAny(['，', '。', '！', '？', '；', ',', '.', '!', '?', ';', ' ']);
            if (boundary < max / 2) boundary = max - 1;
            result.Add(remaining[..(boundary + 1)].Trim());
            remaining = remaining[(boundary + 1)..].Trim();
        }
        if (remaining.Length > 0) result.Add(remaining);
        return result;
    }

    private static string Wrap(string text, int width) => text.Length <= width ? text : text[..width] + "\n" + text[width..];
    private static string Stamp(long ms) => TimeSpan.FromMilliseconds(Math.Max(0, ms)).ToString(@"hh\:mm\:ss\,fff");
}
