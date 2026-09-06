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
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("revision")] string Revision,
    [property: JsonPropertyName("role")] List<string> Roles,
    [property: JsonPropertyName("platform")] List<string> Platforms,
    [property: JsonPropertyName("backend")] string Backend,
    [property: JsonPropertyName("architecture")] string Architecture,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("files")] List<CatalogFile> Files,
    [property: JsonPropertyName("qualityProfiles")] Dictionary<string, List<string>>? QualityProfiles = null,
    [property: JsonPropertyName("roleDisplayNames")] Dictionary<string, string>? RoleDisplayNames = null,
    [property: JsonPropertyName("roleRuntimeOptions")] Dictionary<string, CatalogRoleRuntime>? RoleRuntimeOptions = null)
{
    [JsonIgnore] public string TxtDisplayName => DisplayNameForRole("TXT_ASR");
    [JsonIgnore] public string SrtDisplayName => DisplayNameForRole("SRT_ASR");

    public string DisplayNameForRole(string role) =>
        $"{DisplayName} · {(role == "SRT_ASR" ? "SRT / Timestamp" : role)} · {Id}";

    public bool TimestampModeForRole(string role) =>
        RoleRuntimeOptions?.TryGetValue(role, out var value) == true
            ? value.TimestampMode : role == "SRT_ASR";
}
internal sealed record CatalogRoleRuntime(
    [property: JsonPropertyName("timestampMode")] bool TimestampMode);
internal sealed record CatalogFile(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("sources")] List<CatalogSource> Sources);
internal sealed record CatalogSource(
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("kind")] string? Kind = null);
internal sealed record ModelProgress(long ReceivedBytes, long TotalBytes, string CurrentFile,
    string CurrentSource = "");
internal sealed record ModelState(bool Installed, bool Verified, string ModelId, string Revision,
    string? Path, long TotalBytes, int CpuThreads, ulong TotalRamMiB, string Gpu,
    string RecommendedProfile, string DisplayName, string Backend, IReadOnlyList<string> Roles);

internal sealed class ModelManager
{
    private static readonly Lazy<string> DetectedGpu = new(DetectGpu, true);
    internal static string DefaultRoot => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CantoSuite", "CantoTranscribe", "models");

    private readonly string _root;
    private readonly CatalogModel _model;
    private readonly Func<HttpClient> _clientFactory;

    public ModelManager(string requiredRole = "TXT_ASR", string? root = null,
        CatalogModel? model = null, Func<HttpClient>? clientFactory = null,
        string? qualityProfile = null)
    {
        _root = root ?? DefaultRoot;
        if (model is not null) _model = model;
        else
        {
            var models = LoadCatalog().Models;
            _model = models.FirstOrDefault(item => item.Enabled &&
                item.Platforms.Contains("windows-x64") &&
                item.Roles.Contains(requiredRole) &&
                (qualityProfile is null || SupportsProfile(item, requiredRole, qualityProfile)))
                ?? throw new InvalidDataException("模型目錄未有可用嘅廣東話模型");
        }
        _clientFactory = clientFactory ?? (() => new HttpClient());
    }

    public CatalogModel Model => _model;
    public string InstalledPath => System.IO.Path.Combine(_root, _model.Id, _model.Revision);

    public static Catalog LoadCatalog()
    {
        var catalogPath = System.IO.Path.Combine(AppContext.BaseDirectory,
            "resources", "model-catalog", "catalog.v1.json");
        return JsonSerializer.Deserialize<Catalog>(File.ReadAllText(catalogPath))
            ?? throw new InvalidDataException("模型目錄格式錯誤");
    }

    public static bool SupportsProfile(CatalogModel model, string role, string profile) =>
        model.QualityProfiles?.TryGetValue(role, out var profiles) == true &&
        profiles.Contains(profile, StringComparer.Ordinal);

    public static string RecommendProfile()
    {
        var memoryMiB = GetMemoryMiB();
        return memoryMiB >= 16 * 1024 && Environment.ProcessorCount >= 8
            ? "High Accuracy"
            : memoryMiB >= 8 * 1024 && Environment.ProcessorCount >= 4
                ? "Balanced" : "Fast";
    }

    public Task<ModelState> GetStateAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => GetStateCoreAsync(cancellationToken), cancellationToken);

    private async Task<ModelState> GetStateCoreAsync(CancellationToken cancellationToken)
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
        return new ModelState(verified, verified, _model.Id, _model.Revision,
            verified ? InstalledPath : null, _model.Files.Sum(file => file.Size),
            Environment.ProcessorCount, memoryMiB, DetectedGpu.Value, RecommendProfile(),
            _model.DisplayName, _model.Backend, _model.Roles);
    }

    public async Task<ModelState> InstallAsync(IProgress<ModelProgress> progress,
        CancellationToken cancellationToken, bool forceRedownload = false)
    {
        using var mutation = ModelUseGate.Acquire(InstalledPath, mutation: true);
        var current = await GetStateAsync(cancellationToken);
        if (current.Verified && !forceRedownload) return current;

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
            await DownloadWithFallbackAsync(client, modelFile, part, completed, total,
                progress, cancellationToken);
            File.Move(part, destination, true);
            completed += modelFile.Size;
        }

        var manifest = JsonSerializer.Serialize(new
        {
            modelId = _model.Id,
            revision = _model.Revision,
            verified = true,
            installedAt = DateTimeOffset.UtcNow
        });
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
            if (!Directory.Exists(InstalledPath) && Directory.Exists(backup))
                Directory.Move(backup, InstalledPath);
            throw;
        }
        return await GetStateAsync(cancellationToken);
    }

    public Task<ModelState> RepairAsync(IProgress<ModelProgress> progress,
        CancellationToken cancellationToken) => InstallAsync(progress, cancellationToken);

    internal Task DeleteAsync(Action<string>? deleteDirectory = null) => Task.Run(() =>
    {
        using var mutation = ModelUseGate.Acquire(InstalledPath, mutation: true);
        var modelRoot = SafeChildPath(_root, _model.Id);
        if (!Directory.Exists(modelRoot)) return;
        // Moving the entire ID removes every revision and install manifest in one step.
        var trash = SafeChildPath(_root, $".trash/{_model.Id}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.GetDirectoryName(trash)!);
        Directory.Move(modelRoot, trash);
        try { (deleteDirectory ?? (path => Directory.Delete(path, true)))(trash); }
        catch
        {
            // Restore visibility after failure. GetState always re-verifies all bytes;
            // it never trusts a stale install.json after a partial filesystem failure.
            if (Directory.Exists(trash) && !Directory.Exists(modelRoot)) Directory.Move(trash, modelRoot);
            throw;
        }
    });

    public long StorageBytes()
    {
        var modelRoot = SafeChildPath(_root, _model.Id);
        if (!Directory.Exists(modelRoot)) return 0;
        return new DirectoryInfo(modelRoot).EnumerateFiles("*", SearchOption.AllDirectories)
            .Sum(file => file.Length);
    }

    private static async Task DownloadWithFallbackAsync(HttpClient client, CatalogFile modelFile,
        string part, long completed, long total, IProgress<ModelProgress> progress,
        CancellationToken cancellationToken)
    {
        var failures = new List<string>();
        foreach (var source in modelFile.Sources)
        {
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    var existing = File.Exists(part) ? new FileInfo(part).Length : 0;
                    if (existing >= modelFile.Size)
                    {
                        if (existing == modelFile.Size &&
                            await VerifyAsync(part, modelFile, cancellationToken)) return;
                        File.Delete(part);
                        existing = 0;
                    }
                    using var request = new HttpRequestMessage(HttpMethod.Get, source.Url);
                    if (existing > 0) request.Headers.Range = new RangeHeaderValue(existing, null);
                    using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    headerTimeout.CancelAfter(TimeSpan.FromSeconds(25));
                    using var response = await client.SendAsync(request,
                        HttpCompletionOption.ResponseHeadersRead, headerTimeout.Token);
                    response.EnsureSuccessStatusCode();
                    var resumed = existing > 0 && response.StatusCode == HttpStatusCode.PartialContent &&
                        response.Content.Headers.ContentRange?.From == existing;
                    if (existing > 0 && response.StatusCode == HttpStatusCode.PartialContent && !resumed)
                    {
                        File.Delete(part);
                        throw new InvalidDataException("伺服器續傳範圍不正確");
                    }
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
                        if (received > modelFile.Size)
                            throw new InvalidDataException("下載檔案超出已釘選大小");
                        progress.Report(new ModelProgress(completed + received, total,
                            modelFile.Path, source.Label));
                    }
                    await output.FlushAsync(cancellationToken);
                    output.Close();
                    if (received != modelFile.Size)
                        throw new EndOfStreamException($"只收到 {received}/{modelFile.Size} bytes");
                    if (!await VerifyAsync(part, modelFile, cancellationToken))
                    {
                        File.Delete(part);
                        throw new InvalidDataException("SHA-256 不符");
                    }
                    return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception error)
                {
                    failures.Add($"{source.Label} #{attempt}: {error.Message}");
                    if (attempt < 2) await Task.Delay(400 * attempt, cancellationToken);
                }
            }
        }
        throw new IOException("模型下載失敗；已嘗試所有來源。\n" + string.Join("\n", failures));
    }

    private static string SafeChildPath(string root, string relative)
    {
        var fullRoot = System.IO.Path.GetFullPath(root) + System.IO.Path.DirectorySeparatorChar;
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, relative));
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("模型目錄包含不安全路徑");
        return full;
    }

    private static async Task<bool> VerifyAsync(string path, CatalogFile modelFile,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != modelFile.Size) return false;
        await using var input = File.OpenRead(path);
        var digest = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken));
        return digest.Equals(modelFile.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task AtomicWriteAsync(string path, string content,
        CancellationToken cancellationToken)
    {
        var part = path + ".part";
        await File.WriteAllTextAsync(part, content, cancellationToken);
        File.Move(part, path, true);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetPhysicallyInstalledSystemMemory(out ulong totalMemoryInKilobytes);

    private static ulong GetMemoryMiB() =>
        GetPhysicallyInstalledSystemMemory(out var kib) ? kib / 1024 : 0;

    private static string DetectGpu()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("nvidia-smi",
                "--query-gpu=name,memory.total --format=csv,noheader")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true });
            if (process is null) return "未偵測到獨立 GPU";
            var output = process.StandardOutput.ReadLine();
            process.WaitForExit(2_000);
            return string.IsNullOrWhiteSpace(output) ? "未偵測到獨立 GPU" : output.Trim();
        }
        catch { return "未偵測到獨立 GPU"; }
    }
}
