using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CantoTranscribe;

internal sealed record TimedText(long StartMs, long EndMs, string RawText, string CleanText);
internal sealed record JobCheckpoint(
    string SourcePath, string SourceFingerprint, string OutputPath, string Format,
    string Quality, string ModelId, string ModelRevision, long ProcessedMs,
    long? TotalMs, string State, List<TimedText> Segments, string OutputScript = "繁體中文");

internal sealed class JobStore
{
    private readonly string _path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CantoSuite", "CantoTranscribe", "job.v1.json");

    public async Task<JobCheckpoint?> LoadRecoverableAsync()
    {
        if (!File.Exists(_path)) return null;
        try
        {
            var job = JsonSerializer.Deserialize<JobCheckpoint>(await File.ReadAllTextAsync(_path));
            if (job is null || job.State is "completed" or "cancelled" || !File.Exists(job.SourcePath)) return null;
            return await FingerprintAsync(job.SourcePath) == job.SourceFingerprint ? job : null;
        }
        catch { return null; }
    }

    public async Task SaveAsync(JobCheckpoint job, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        var part = _path + ".part";
        await File.WriteAllTextAsync(part, JsonSerializer.Serialize(job, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
        File.Move(part, _path, true);
    }

    public static async Task<string> FingerprintAsync(string path)
    {
        await using var input = File.OpenRead(path);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        digest.AppendData(Encoding.UTF8.GetBytes(System.IO.Path.GetFullPath(path).ToUpperInvariant()));
        digest.AppendData(BitConverter.GetBytes(input.Length));
        digest.AppendData(BitConverter.GetBytes(File.GetLastWriteTimeUtc(path).Ticks));
        var buffer = new byte[1024 * 1024];
        foreach (var offset in SampleOffsets(input.Length, buffer.Length))
        {
            input.Position = offset;
            var wanted = (int)Math.Min(buffer.Length, input.Length - offset);
            var read = await input.ReadAsync(buffer.AsMemory(0, wanted));
            digest.AppendData(BitConverter.GetBytes(offset));
            digest.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
    }

    private static IEnumerable<long> SampleOffsets(long length, int sampleSize)
    {
        if (length <= sampleSize) return [0];
        return new[] { 0L, Math.Max(0, length / 2 - sampleSize / 2), Math.Max(0, length - sampleSize) }
            .Distinct();
    }
}
