using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace CantoTranscribe;

internal sealed record TranscriptionProgress(long ProcessedMs, long? TotalMs, string State, string Preview);

internal sealed class TranscriptionService
{
    private static readonly TimeSpan NativeQueueStallTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan DecoderExitTimeout = TimeSpan.FromSeconds(30);
    private const int DiagnosticIntervalMs = 30_000;
    private const int MaximumDiagnosticBytes = 1024 * 1024;
    private const int MaximumFfmpegErrorCharacters = 64 * 1024;

    private readonly string _ffmpeg = System.IO.Path.Combine(
        AppContext.BaseDirectory, "resources", "bin", "ffmpeg.exe");
    private readonly string _ffprobe = System.IO.Path.Combine(
        AppContext.BaseDirectory, "resources", "bin", "ffprobe.exe");
    private readonly string _diagnosticPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CantoSuite", "CantoTranscribe", "job-diagnostics.jsonl");
    private readonly DictionaryService _dictionary = new();
    private readonly JobStore _store;
    private readonly object _cleanupGate = new();
    private Task _cleanupTask = Task.CompletedTask;
    private long _modelLoadCount;

    public TranscriptionService(JobStore store) => _store = store;

    public Task WaitForCleanupAsync()
    {
        lock (_cleanupGate) return _cleanupTask;
    }

    public async Task<string> RunAsync(string source, string output, string format, bool clean,
        string outputScript, string quality, ModelState model, IProgress<TranscriptionProgress> progress,
        JobCheckpoint? resume, CancellationToken cancellationToken)
    {
        if (!File.Exists(_ffmpeg) || !File.Exists(_ffprobe))
            throw new FileNotFoundException("找不到內置 FFmpeg");
        if (model.Path is null) throw new InvalidOperationException("本機模型未安裝");

        var jobId = Guid.NewGuid().ToString("N");
        NativeEngine? activeEngine = null;
        CancellationTokenRegistration engineCancellation = default;
        var backgroundCleanup = false;
        try
        {
            await WriteDiagnosticAsync(jobId, "starting", new
            {
                modelId = model.ModelId,
                modelRevision = model.Revision,
                outputRole = format == "SRT" ? "SRT_ASR" : "TXT_ASR",
                timestampMode = format == "SRT",
                cancellationRequested = cancellationToken.IsCancellationRequested
            });

            var fingerprint = await JobStore.FingerprintAsync(source);
            var totalMs = await ProbeDurationMsAsync(source, cancellationToken);
            var baseMs = resume is not null && resume.SourceFingerprint == fingerprint &&
                resume.Format == format && resume.ModelId == model.ModelId &&
                resume.ModelRevision == model.Revision ? resume.ProcessedMs : 0;
            var segments = resume is not null && baseMs > 0
                ? new List<TimedText>(resume.Segments) : [];
            var job = new JobCheckpoint(source, fingerprint, output, format, quality, model.ModelId,
                model.Revision, baseMs, totalMs, "processing", segments, outputScript);
            await _store.SaveAsync(job, cancellationToken);

            var modelTimer = Stopwatch.StartNew();
            var engine = activeEngine = new NativeEngine(
                model.Path, timestampMode: format == "SRT");
            modelTimer.Stop();
            var modelLoadCount = Interlocked.Increment(ref _modelLoadCount);
            engineCancellation = cancellationToken.Register(engine.RequestCancel);
            var capabilities = engine.GetCapabilities();
            if (format == "SRT" && capabilities.SupportsTimestamps == 0)
                throw new InvalidOperationException(
                    "目前安裝嘅模型未提供可靠時間碼；請安裝 SRT 時間碼模型");
            await WriteDiagnosticAsync(jobId, "model-loaded", new
            {
                modelId = model.ModelId,
                backend = capabilities.Backend,
                modelLoadCount,
                modelLoadDurationMs = modelTimer.ElapsedMilliseconds,
                timestampMode = format == "SRT",
                workingSetMiB = WorkingSetMiB()
            });

            using var decoder = StartDecoder(source, baseMs);
            using var decoderCancellation = cancellationToken.Register(
                static state => TryKill((Process)state!), decoder);
            var errorTask = ReadBoundedTextAsync(
                decoder.StandardError, MaximumFfmpegErrorCharacters);

            var bytes = new byte[32_000];
            var samples = new short[16_000];
            var pendingByte = -1;
            long fedSamples = 0;
            long bytesRead = 0;
            long lastPersistMs = baseMs;
            long nextDiagnosticMs = baseMs + DiagnosticIntervalMs;
            long nativeQueueWaitMs = 0;
            var chunkIndex = 0;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var offset = 0;
                if (pendingByte >= 0)
                {
                    bytes[0] = (byte)pendingByte;
                    offset = 1;
                    pendingByte = -1;
                }
                var received = await decoder.StandardOutput.BaseStream.ReadAsync(
                    bytes.AsMemory(offset), cancellationToken);
                if (received == 0)
                {
                    if (offset != 0)
                        throw new InvalidDataException("FFmpeg 傳回不完整嘅 PCM 樣本");
                    break;
                }
                var read = received + offset;
                bytesRead += received;
                if (read % 2 != 0)
                {
                    pendingByte = bytes[read - 1];
                    read--;
                }
                var sampleCount = read / 2;
                Buffer.BlockCopy(bytes, 0, samples, 0, read);
                var queueWait = Stopwatch.StartNew();
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var status = engine.Push(samples, sampleCount, false);
                    if (status == 0) break;
                    if (status != 4)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        NativeEngine.Check(status);
                    }
                    Drain(engine, segments, baseMs, progress, totalMs);
                    if (queueWait.Elapsed > NativeQueueStallTimeout)
                        throw new TimeoutException(
                            "ASR 引擎超過三分鐘沒有接收新音訊；工作已安全停止，可稍後恢復。");
                    await Task.Delay(20, cancellationToken);
                }
                queueWait.Stop();
                nativeQueueWaitMs += queueWait.ElapsedMilliseconds;
                fedSamples += sampleCount;
                chunkIndex++;
                Drain(engine, segments, baseMs, progress, totalMs);
                var processedMs = baseMs + fedSamples * 1000 / 16_000;
                if (processedMs - lastPersistMs >= 5_000)
                {
                    job = job with
                    {
                        ProcessedMs = processedMs,
                        Segments = new List<TimedText>(segments)
                    };
                    await _store.SaveAsync(job, cancellationToken);
                    lastPersistMs = processedMs;
                }
                progress.Report(new TranscriptionProgress(processedMs, totalMs,
                    $"正在轉錄 · 第 {chunkIndex} 段",
                    segments.LastOrDefault()?.CleanText ?? ""));

                if (processedMs >= nextDiagnosticMs)
                {
                    await WriteDiagnosticAsync(jobId, "processing", new
                    {
                        chunkIndex,
                        bytesRead,
                        samplesPushed = fedSamples,
                        ffmpegProcessedMs = processedMs,
                        nativeQueueWaitMs,
                        lastNativeAsrMs = engine.LastInferenceMs,
                        resultCount = segments.Count,
                        workingSetMiB = WorkingSetMiB(),
                        cancellationRequested = cancellationToken.IsCancellationRequested
                    });
                    nextDiagnosticMs = processedMs + DiagnosticIntervalMs;
                }
            }

            NativeEngine.Check(engine.Push(Array.Empty<short>(), 0, true));
            var finalTimer = Stopwatch.StartNew();
            var deadline = DateTime.UtcNow.AddMinutes(3);
            var receivedFinal = false;
            while (!receivedFinal && DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = engine.Poll();
                if (result is null)
                {
                    await Task.Delay(50, cancellationToken);
                    continue;
                }
                receivedFinal |= result.Kind == 2;
                AddResult(result, segments, baseMs);
            }
            finalTimer.Stop();

            try
            {
                await decoder.WaitForExitAsync(cancellationToken)
                    .WaitAsync(DecoderExitTimeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                TryKill(decoder);
                throw new TimeoutException("FFmpeg 完成解碼後未能正常結束");
            }
            var ffmpegError = await errorTask;
            if (decoder.ExitCode != 0)
                throw new InvalidDataException("FFmpeg 解碼失敗：" + ffmpegError);
            if (!receivedFinal) throw new TimeoutException("等待 ASR 完成逾時");

            var outputSegments = OutputNormalizer.NormalizeSegments(
                segments, outputScript, _dictionary);
            var contents = format == "TXT"
                ? string.Join(Environment.NewLine,
                    outputSegments.Select(item => clean ? item.CleanText : item.RawText))
                : SrtFormatter.Render(outputSegments, clean);
            await AtomicWriteAsync(output, contents, cancellationToken);
            job = job with
            {
                ProcessedMs = totalMs ?? baseMs + fedSamples * 1000 / 16_000,
                Segments = segments,
                State = "completed"
            };
            await _store.SaveAsync(job, cancellationToken);
            await WriteDiagnosticAsync(jobId, "completed", new
            {
                chunkCount = chunkIndex,
                bytesRead,
                samplesPushed = fedSamples,
                resultCount = segments.Count,
                finalNativeWaitMs = finalTimer.ElapsedMilliseconds,
                nativeQueueWaitMs,
                lastNativeAsrMs = engine.LastInferenceMs,
                workingSetMiB = WorkingSetMiB()
            });
            return output;
        }
        catch (OperationCanceledException)
        {
            backgroundCleanup = true;
            await WriteDiagnosticAsync(jobId, "cancelled", new
            {
                cancellationRequested = true,
                workingSetMiB = WorkingSetMiB()
            });
            throw;
        }
        catch (Exception error)
        {
            await WriteDiagnosticAsync(jobId, "failed", new
            {
                errorType = error.GetType().Name,
                cancellationRequested = cancellationToken.IsCancellationRequested,
                workingSetMiB = WorkingSetMiB()
            });
            throw;
        }
        finally
        {
            engineCancellation.Dispose();
            if (activeEngine is not null)
            {
                if (backgroundCleanup)
                {
                    activeEngine.RequestCancel();
                    TrackCleanup(activeEngine.DisposeAsync(), jobId);
                }
                else
                {
                    activeEngine.Dispose();
                }
            }
        }
    }

    private void TrackCleanup(Task cleanup, string jobId)
    {
        lock (_cleanupGate) _cleanupTask = CompleteCleanupAsync(cleanup, jobId);
    }

    private async Task CompleteCleanupAsync(Task cleanup, string jobId)
    {
        var timer = Stopwatch.StartNew();
        await cleanup;
        timer.Stop();
        await WriteDiagnosticAsync(jobId, "native-cleanup-completed", new
        {
            cleanupDurationMs = timer.ElapsedMilliseconds,
            workingSetMiB = WorkingSetMiB()
        });
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
        var arguments = new List<string> { "-nostdin", "-hide_banner", "-loglevel", "error" };
        if (seekMs > 0)
        {
            arguments.Add("-ss");
            arguments.Add((seekMs / 1000d).ToString(
                "0.000", System.Globalization.CultureInfo.InvariantCulture));
        }
        arguments.AddRange(
            ["-i", source, "-vn", "-ac", "1", "-ar", "16000",
             "-f", "s16le", "-c:a", "pcm_s16le", "pipe:1"]);
        var start = new ProcessStartInfo(_ffmpeg)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new InvalidOperationException("FFmpeg 未能啟動");
    }

    private async Task<long?> ProbeDurationMsAsync(
        string source, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(_ffprobe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
        {
            "-v", "error", "-show_entries", "format=duration",
            "-of", "json", source
        }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("ffprobe 未能啟動");
        using var registration = cancellationToken.Register(
            static state => TryKill((Process)state!), process);
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = ReadBoundedTextAsync(
            process.StandardError, MaximumFfmpegErrorCharacters);
        try
        {
            await process.WaitForExitAsync(cancellationToken)
                .WaitAsync(DecoderExitTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            TryKill(process);
            throw new TimeoutException("ffprobe 讀取媒體資料逾時");
        }
        var json = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
            throw new InvalidDataException("ffprobe 未能讀取媒體：" + error);
        using var document = JsonDocument.Parse(json);
        var text = document.RootElement.GetProperty("format")
            .GetProperty("duration").GetString();
        return double.TryParse(text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var seconds)
            ? (long)(seconds * 1000) : null;
    }

    private static async Task<string> ReadBoundedTextAsync(
        StreamReader reader, int maximumCharacters)
    {
        var output = new StringBuilder(Math.Min(maximumCharacters, 4096));
        var buffer = new char[4096];
        while (true)
        {
            var read = await reader.ReadAsync(buffer);
            if (read == 0) break;
            var remaining = maximumCharacters - output.Length;
            if (remaining > 0) output.Append(buffer, 0, Math.Min(remaining, read));
        }
        return output.ToString();
    }

    private async Task WriteDiagnosticAsync(string jobId, string state, object metrics)
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(_diagnosticPath)!;
            Directory.CreateDirectory(directory);
            if (File.Exists(_diagnosticPath) &&
                new FileInfo(_diagnosticPath).Length > MaximumDiagnosticBytes)
            {
                File.Move(_diagnosticPath, _diagnosticPath + ".previous", true);
            }
            var line = JsonSerializer.Serialize(new
            {
                timestamp = DateTimeOffset.UtcNow,
                jobId,
                state,
                workerThreadId = Environment.CurrentManagedThreadId,
                metrics
            });
            await File.AppendAllTextAsync(
                _diagnosticPath, line + Environment.NewLine, new UTF8Encoding(false));
        }
        catch
        {
            // Diagnostics must never break local transcription.
        }
    }

    private static long WorkingSetMiB()
    {
        using var process = Process.GetCurrentProcess();
        return process.WorkingSet64 / 1024 / 1024;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // The decoder may have exited between HasExited and Kill.
        }
    }

    private static async Task AtomicWriteAsync(
        string path, string contents, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var part = path + ".part";
        await File.WriteAllTextAsync(
            part, contents, new UTF8Encoding(false), cancellationToken);
        File.Move(part, path, true);
    }
}
