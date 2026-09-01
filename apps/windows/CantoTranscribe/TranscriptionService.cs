using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace CantoTranscribe;

internal sealed record TranscriptionProgress(long ProcessedMs, long? TotalMs, string State, string Preview);

internal sealed class TranscriptionService
{
    private readonly string _ffmpeg = System.IO.Path.Combine(AppContext.BaseDirectory, "resources", "bin", "ffmpeg.exe");
    private readonly string _ffprobe = System.IO.Path.Combine(AppContext.BaseDirectory, "resources", "bin", "ffprobe.exe");
    private readonly DictionaryService _dictionary = new();
    private readonly JobStore _store;

    public TranscriptionService(JobStore store) => _store = store;

    public async Task<string> RunAsync(string source, string output, string format, bool clean,
        string outputScript, string quality, ModelState model, IProgress<TranscriptionProgress> progress,
        JobCheckpoint? resume, CancellationToken cancellationToken)
    {
        if (!File.Exists(_ffmpeg) || !File.Exists(_ffprobe)) throw new FileNotFoundException("找不到內置 FFmpeg");
        if (model.Path is null) throw new InvalidOperationException("本機模型未安裝");
        var fingerprint = await JobStore.FingerprintAsync(source);
        var totalMs = await ProbeDurationMsAsync(source, cancellationToken);
        var baseMs = resume is not null && resume.SourceFingerprint == fingerprint && resume.Format == format
            ? resume.ProcessedMs : 0;
        var segments = resume is not null && baseMs > 0 ? new List<TimedText>(resume.Segments) : [];
        var job = new JobCheckpoint(source, fingerprint, output, format, quality, model.ModelId,
            model.Revision, baseMs, totalMs, "processing", segments, outputScript);
        await _store.SaveAsync(job, cancellationToken);

        using var engine = new NativeEngine(model.Path);
        var capabilities = engine.GetCapabilities();
        if (format == "SRT" && capabilities.SupportsTimestamps == 0)
            throw new InvalidOperationException("目前安裝嘅模型未提供可靠時間碼；請安裝 SRT 時間碼模型");
        using var decoder = StartDecoder(source, baseMs);
        var errorTask = decoder.StandardError.ReadToEndAsync(cancellationToken);
        var bytes = new byte[32_000];
        var pendingByte = -1;
        long fedSamples = 0;
        long lastPersistMs = baseMs;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var offset = 0;
            if (pendingByte >= 0) { bytes[0] = (byte)pendingByte; offset = 1; pendingByte = -1; }
            var read = await decoder.StandardOutput.BaseStream.ReadAsync(bytes.AsMemory(offset), cancellationToken) + offset;
            if (read == 0) break;
            if (read % 2 != 0) { pendingByte = bytes[read - 1]; read--; }
            var samples = new short[read / 2];
            Buffer.BlockCopy(bytes, 0, samples, 0, read);
            while (true)
            {
                var status = engine.Push(samples, false);
                if (status == 0) break;
                if (status != 4) NativeEngine.Check(status);
                Drain(engine, segments, baseMs, progress, totalMs);
                await Task.Delay(20, cancellationToken);
            }
            fedSamples += samples.Length;
            Drain(engine, segments, baseMs, progress, totalMs);
            var processedMs = baseMs + fedSamples * 1000 / 16_000;
            if (processedMs - lastPersistMs >= 5_000)
            {
                job = job with { ProcessedMs = processedMs, Segments = new List<TimedText>(segments) };
                await _store.SaveAsync(job, cancellationToken);
                lastPersistMs = processedMs;
            }
            progress.Report(new TranscriptionProgress(processedMs, totalMs, "正在轉錄", segments.LastOrDefault()?.CleanText ?? ""));
        }
        NativeEngine.Check(engine.Push([], true));
        var deadline = DateTime.UtcNow.AddMinutes(3);
        var receivedFinal = false;
        while (!receivedFinal && DateTime.UtcNow < deadline)
        {
            var result = engine.Poll();
            if (result is null) { await Task.Delay(50, cancellationToken); continue; }
            receivedFinal |= result.Kind == 2;
            AddResult(result, segments, baseMs);
        }
        await decoder.WaitForExitAsync(cancellationToken);
        if (decoder.ExitCode != 0) throw new InvalidDataException("FFmpeg 解碼失敗：" + await errorTask);
        if (!receivedFinal) throw new TimeoutException("等待 ASR 完成逾時");
        var outputSegments = segments.Select(item => new TimedText(item.StartMs, item.EndMs,
            ChineseScriptConverter.Convert(item.RawText, outputScript),
            ChineseScriptConverter.Convert(item.CleanText, outputScript))).ToList();
        var contents = format == "TXT"
            ? string.Join(Environment.NewLine, outputSegments.Select(item => clean ? item.CleanText : item.RawText))
            : SrtFormatter.Render(outputSegments, clean);
        await AtomicWriteAsync(output, contents, cancellationToken);
        job = job with { ProcessedMs = totalMs ?? baseMs + fedSamples * 1000 / 16_000,
            Segments = segments, State = "completed" };
        await _store.SaveAsync(job, cancellationToken);
        return output;
    }

    private void Drain(NativeEngine engine, List<TimedText> segments, long baseMs,
        IProgress<TranscriptionProgress> progress, long? totalMs)
    {
        NativeTranscript? result;
        while ((result = engine.Poll()) is not null)
        {
            AddResult(result, segments, baseMs);
            progress.Report(new TranscriptionProgress(baseMs + result.EndMs, totalMs,
                "正在轉錄", result.Text));
        }
    }

    private void AddResult(NativeTranscript result, List<TimedText> segments, long baseMs)
    {
        if (result.Kind == 3) throw new InvalidOperationException(result.Text);
        if (string.IsNullOrWhiteSpace(result.Text)) return;
        segments.Add(new TimedText(baseMs + result.StartMs, baseMs + result.EndMs,
            result.Text.Trim(), _dictionary.Clean(result.Text)));
    }

    private Process StartDecoder(string source, long seekMs)
    {
        var arguments = new List<string>();
        if (seekMs > 0) { arguments.Add("-ss"); arguments.Add((seekMs / 1000d).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)); }
        arguments.AddRange(["-i", source, "-vn", "-ac", "1", "-ar", "16000", "-f", "s16le", "-c:a", "pcm_s16le", "pipe:1"]);
        var start = new ProcessStartInfo(_ffmpeg) { UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new InvalidOperationException("FFmpeg 未能啟動");
    }

    private async Task<long?> ProbeDurationMsAsync(string source, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(_ffprobe) { UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in new[] { "-v", "error", "-show_entries", "format=duration", "-of", "json", source }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("ffprobe 未能啟動");
        var json = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        using var document = JsonDocument.Parse(json);
        var text = document.RootElement.GetProperty("format").GetProperty("duration").GetString();
        return double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds)
            ? (long)(seconds * 1000) : null;
    }

    private static async Task AtomicWriteAsync(string path, string contents, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var part = path + ".part";
        await File.WriteAllTextAsync(part, contents, new UTF8Encoding(false), cancellationToken);
        File.Move(part, path, true);
    }
}
