using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CantoTranscribe;

internal sealed record Catalog([property: JsonPropertyName("models")] List<CatalogModel> Models);
internal sealed record CatalogModel(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("revision")] string Revision,
    [property: JsonPropertyName("role")] List<string> Roles,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("files")] List<CatalogFile> Files);
internal sealed record CatalogFile(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("url")] string Url);
internal sealed record ModelProgress(long ReceivedBytes, long TotalBytes, string CurrentFile);
internal sealed record ModelState(bool Installed, bool Verified, string ModelId, string Revision,
    string? Path, long TotalBytes, int CpuThreads, ulong TotalRamMiB, string Gpu, string RecommendedProfile);

internal sealed class ModelManager
{
    private readonly string _root;
    private readonly CatalogModel _model;
    private readonly Func<HttpClient> _clientFactory;

    public ModelManager(string requiredRole = "QUALITY_ASR", string? root = null,
        CatalogModel? model = null, Func<HttpClient>? clientFactory = null)
    {
        _root = root ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CantoSuite", "CantoTranscribe", "models");
        if (model is not null) _model = model;
        else
        {
            var catalogPath = System.IO.Path.Combine(AppContext.BaseDirectory,
                "resources", "model-catalog", "catalog.v1.json");
            var catalog = JsonSerializer.Deserialize<Catalog>(File.ReadAllText(catalogPath))
                ?? throw new InvalidDataException("模型目錄格式錯誤");
            _model = catalog.Models.FirstOrDefault(item =>
                item.Enabled && item.Roles.Contains(requiredRole))
                ?? throw new InvalidDataException("模型目錄未有可用嘅廣東話模型");
        }
        _clientFactory = clientFactory ?? (() => new HttpClient());
    }

    public string InstalledPath => System.IO.Path.Combine(_root, _model.Id, _model.Revision);

    public async Task<ModelState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        var verified = Directory.Exists(InstalledPath);
        if (verified)
        {
            foreach (var file in _model.Files)
            {
                if (!await VerifyAsync(System.IO.Path.Combine(InstalledPath, file.Path), file, cancellationToken))
                {
                    verified = false;
                    break;
                }
            }
        }
        var memoryMiB = GetMemoryMiB();
        var profile = memoryMiB >= 16 * 1024 && Environment.ProcessorCount >= 8
            ? "高準確度" : memoryMiB >= 8 * 1024 && Environment.ProcessorCount >= 4 ? "平衡" : "快速";
        return new ModelState(verified, verified, _model.Id, _model.Revision,
            verified ? InstalledPath : null, _model.Files.Sum(file => file.Size),
            Environment.ProcessorCount, memoryMiB, DetectGpu(), profile);
    }

    public async Task<ModelState> InstallAsync(IProgress<ModelProgress> progress, CancellationToken cancellationToken)
    {
        var staging = System.IO.Path.Combine(_root, ".staging", $"{_model.Id}-{_model.Revision}");
        Directory.CreateDirectory(staging);
        using var client = _clientFactory();
        client.Timeout = Timeout.InfiniteTimeSpan;
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CantoTranscribe-WinUI/0.1");
        var total = _model.Files.Sum(file => file.Size);
        long completed = 0;
        foreach (var modelFile in _model.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = SafeChildPath(staging, modelFile.Path);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);
            if (await VerifyAsync(destination, modelFile, cancellationToken))
            {
                completed += modelFile.Size;
                progress.Report(new ModelProgress(completed, total, modelFile.Path));
                continue;
            }
            if (File.Exists(destination)) File.Delete(destination);
            var part = destination + ".part";
            var existing = File.Exists(part) ? new FileInfo(part).Length : 0;
            if (existing >= modelFile.Size)
            {
                File.Delete(part);
                existing = 0;
            }
            using var request = new HttpRequestMessage(HttpMethod.Get, modelFile.Url);
            if (existing > 0) request.Headers.Range = new RangeHeaderValue(existing, null);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var resumed = existing > 0 && response.StatusCode == HttpStatusCode.PartialContent;
            if (!resumed) existing = 0;
            await using var output = new FileStream(part, resumed ? FileMode.Append : FileMode.Create,
                FileAccess.Write, FileShare.None, 1024 * 1024, true);
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            var buffer = new byte[1024 * 1024];
            var received = existing;
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken);
                if (read == 0) break;
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                received += read;
                progress.Report(new ModelProgress(completed + received, total, modelFile.Path));
            }
            await output.FlushAsync(cancellationToken);
            output.Close();
            if (received != modelFile.Size || !await VerifyAsync(part, modelFile, cancellationToken))
            {
                File.Delete(part);
                throw new InvalidDataException($"模型檔案校驗失敗：{modelFile.Path}");
            }
            File.Move(part, destination, true);
            completed += modelFile.Size;
        }

        var manifest = JsonSerializer.Serialize(new { modelId = _model.Id, revision = _model.Revision, verified = true });
        await AtomicWriteAsync(System.IO.Path.Combine(staging, "install.json"), manifest, cancellationToken);
        var backup = InstalledPath + ".previous";
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(InstalledPath)!);
        if (Directory.Exists(backup)) Directory.Delete(backup, true);
        if (Directory.Exists(InstalledPath)) Directory.Move(InstalledPath, backup);
        try
        {
            Directory.Move(staging, InstalledPath);
            if (Directory.Exists(backup)) Directory.Delete(backup, true);
        }
        catch
        {
            if (!Directory.Exists(InstalledPath) && Directory.Exists(backup)) Directory.Move(backup, InstalledPath);
            throw;
        }
        return await GetStateAsync(cancellationToken);
    }

    private static string SafeChildPath(string root, string relative)
    {
        var fullRoot = System.IO.Path.GetFullPath(root) + System.IO.Path.DirectorySeparatorChar;
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, relative));
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("模型目錄包含不安全路徑");
        return full;
    }

    private static async Task<bool> VerifyAsync(string path, CatalogFile modelFile, CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != modelFile.Size) return false;
        await using var input = File.OpenRead(path);
        var digest = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken));
        return digest.Equals(modelFile.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task AtomicWriteAsync(string path, string content, CancellationToken cancellationToken)
    {
        var part = path + ".part";
        await File.WriteAllTextAsync(part, content, cancellationToken);
        File.Move(part, path, true);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetPhysicallyInstalledSystemMemory(out ulong totalMemoryInKilobytes);

    private static ulong GetMemoryMiB() => GetPhysicallyInstalledSystemMemory(out var kib) ? kib / 1024 : 0;

    private static string DetectGpu()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("nvidia-smi", "--query-gpu=name,memory.total --format=csv,noheader")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true });
            if (process is null) return "未偵測到獨立 GPU";
            var output = process.StandardOutput.ReadLine();
            process.WaitForExit(2_000);
            return string.IsNullOrWhiteSpace(output) ? "未偵測到獨立 GPU" : output.Trim();
        }
        catch { return "未偵測到獨立 GPU"; }
    }
}
