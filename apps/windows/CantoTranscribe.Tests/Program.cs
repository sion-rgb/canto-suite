using System.Text.RegularExpressions;
using CantoTranscribe;

var failures = new List<string>();

Run("Traditional conversion", () =>
    Equal("簡體中文", ChineseScriptConverter.Convert("简体中文", "繁體中文")));
Run("Simplified conversion", () =>
    Equal("万里长城", ChineseScriptConverter.Convert("萬里長城", "簡體中文")));
Run("Dictionary preferred display", () =>
    Equal("歡迎 UNION Design HK 同 AI。", new DictionaryService().Clean("歡迎 union design 同 a i")));
Run("Cantonese display preservation", () =>
    Equal("嘅 喺 冇 咗 啲 嚟 噉。", new DictionaryService().Clean("嘅 喺 冇 咗 啲 嚟 噉")));
Run("Cantonese-English ground truth preservation", () =>
    Equal("我哋下個 sprint 會 update 個 API。",
        new DictionaryService().Clean("我哋下個 sprint 會 update 個 API。")));
Run("HK Traditional clean Cantonese regression", () =>
{
    const string raw = "后面个文件柜，呃 呃 呃，我哋，我哋，喺度倾 union design 嘅 follow up，開會講成開飛";
    var normalized = ChineseScriptConverter.Convert(raw, "香港繁體");
    var clean = new DictionaryService().Clean(normalized);
    Equal("後面個文件櫃，呃，我哋，喺度傾 UNION Design HK 嘅 follow up，開會講成開飛。", clean);
    foreach (var unintended in new[] { '后', '个', '柜', '倾' })
        if (clean.Contains(unintended)) throw new Exception($"unintended Simplified character: {unintended}");
    foreach (var cantonese in new[] { "我哋", "喺", "嘅" })
        if (!clean.Contains(cantonese, StringComparison.Ordinal)) throw new Exception($"lost HK Cantonese form: {cantonese}");
    if (!clean.Contains("開飛", StringComparison.Ordinal))
        throw new Exception("deterministic cleanup falsely corrected a semantic ASR error");
});
Run("Final HK Traditional gate covers TXT and SRT", () =>
{
    var dictionary = new DictionaryService();
    var normalized = OutputNormalizer.NormalizeSegments([
        new TimedText(0, 4_000, "后面个文件柜靠住墙，这啲係我哋嘅嘢", "")
    ], "香港繁體", dictionary);
    Equal("後面個文件櫃靠住牆，這啲係我哋嘅嘢", normalized[0].RawText);
    Equal("後面個文件櫃靠住牆，這啲係我哋嘅嘢。", normalized[0].CleanText);
    var srt = SrtFormatter.Render(normalized, clean: true);
    foreach (var unintended in new[] { '后', '个', '柜', '墙', '这' })
    {
        if (normalized[0].CleanText.Contains(unintended) || srt.Contains(unintended))
            throw new Exception($"final output retained Simplified character: {unintended}");
    }
});
Run("Quality profiles select different real model bundles", () =>
{
    var models = ModelManager.LoadCatalog().Models;
    CatalogModel PickModel(string role, string profile) => models.Single(model => model.Enabled &&
        model.Platforms.Contains("windows-x64") && model.Roles.Contains(role) &&
        ModelManager.SupportsProfile(model, role, profile));
    string Pick(string role, string profile) => PickModel(role, profile).Id;
    var profiles = new[] { "Fast", "Balanced", "High Accuracy" };
    var txt = profiles.Select(profile => Pick("TXT_ASR", profile)).ToArray();
    var srt = profiles.Select(profile => Pick("SRT_ASR", profile)).ToArray();
    if (txt.Distinct().Count() != 3 || srt.Distinct().Count() != 3)
        throw new Exception("quality profiles are aliases instead of different models");
    Equal("qwen3-asr-0.6b-int8-2026-03-25", Pick("TXT_ASR", "High Accuracy"));
    Equal("whisper-large-v3-turbo-q5-0", Pick("SRT_ASR", "High Accuracy"));
    if (Pick("SRT_ASR", "High Accuracy").Contains("base", StringComparison.OrdinalIgnoreCase))
        throw new Exception("Whisper Base is incorrectly marketed as highest accuracy");
    foreach (var profile in profiles)
    {
        var txtModel = PickModel("TXT_ASR", profile);
        var srtModel = PickModel("SRT_ASR", profile);
        if (txtModel.Id == srtModel.Id &&
            (txtModel.TimestampModeForRole("TXT_ASR") ||
             !srtModel.TimestampModeForRole("SRT_ASR")))
            throw new Exception($"{profile} shares weights without explicit content/timestamp modes");
        if (txtModel.DisplayNameForRole("TXT_ASR").Contains(
                "Timestamp", StringComparison.OrdinalIgnoreCase))
            throw new Exception($"{profile} TXT is misleadingly labelled as timestamp ASR");
    }
    if (!PickModel("SRT_ASR", "High Accuracy").DisplayNameForRole("SRT_ASR")
            .Contains("Timestamp", StringComparison.OrdinalIgnoreCase))
        throw new Exception("High Accuracy SRT role is not labelled independently");
});
await RunAsync("High Accuracy preset migration maps TXT to Qwen", async () =>
{
    var root = Path.Combine(Path.GetTempPath(), $"canto-high-preset-{Guid.NewGuid():N}");
    try
    {
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "model-selections.json"),
            "{\"ActiveTxtModelId\":\"whisper-large-v3-turbo-q5-0\",\"ActiveSrtModelId\":\"whisper-large-v3-turbo-q5-0\",\"QualityProfile\":\"High Accuracy\"}");
        var registry = new ModelRegistry(root, ModelManager.LoadCatalog().Models);
        await registry.InitializeAsync();
        Equal("qwen3-asr-0.6b-int8-2026-03-25", registry.ActiveTxtModelId);
        Equal("whisper-large-v3-turbo-q5-0", registry.ActiveSrtModelId);
        if (!registry.ActiveRoles(registry.ActiveTxtModelId).SequenceEqual(["TXT"]))
            throw new Exception("TXT active-role explanation is missing");
    }
    finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
});
Run("SRT syntax and segmentation", () =>
{
    var rendered = SrtFormatter.Render([
        new TimedText(0, 2_500, "第一段廣東話。", "第一段廣東話。"),
        new TimedText(2_500, 5_200, "第二段講 union design。", "第二段講 UNION Design HK。")
    ], clean: true);
    if (!Regex.IsMatch(rendered, @"^1\r?\n00:00:00,000 --> 00:00:02,500\r?\n", RegexOptions.CultureInvariant))
        throw new Exception("first cue timestamp is invalid");
    if (!rendered.Contains("UNION Design", StringComparison.Ordinal) || !rendered.Contains("HK", StringComparison.Ordinal))
        throw new Exception("clean text was not rendered");
    if (!Regex.IsMatch(rendered, @"\r?\n2\r?\n00:00:02,500 --> 00:00:05,200\r?\n", RegexOptions.CultureInvariant))
        throw new Exception("second cue timestamp is invalid");
});
await RunAsync("Resume fingerprint detects a tail mutation", async () =>
{
    var path = Path.Combine(Path.GetTempPath(), $"canto-fingerprint-{Guid.NewGuid():N}.bin");
    try
    {
        var bytes = new byte[3 * 1024 * 1024];
        new Random(42).NextBytes(bytes);
        await File.WriteAllBytesAsync(path, bytes);
        var first = await JobStore.FingerprintAsync(path);
        await using (var file = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            file.Position = file.Length - 1;
            file.WriteByte((byte)(bytes[^1] ^ 0xff));
        }
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(1));
        var second = await JobStore.FingerprintAsync(path);
        if (first == second) throw new Exception("fingerprint did not change");
    }
    finally { if (File.Exists(path)) File.Delete(path); }
});
await RunAsync("Model download Range resume, SHA-256, and atomic install", async () =>
{
    var root = Path.Combine(Path.GetTempPath(), $"canto-model-{Guid.NewGuid():N}");
    try
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("verified-windows-model");
        var staging = Path.Combine(root, ".staging", "test-model-revision");
        Directory.CreateDirectory(staging);
        await File.WriteAllBytesAsync(Path.Combine(staging, "model.bin.part"), bytes[..8]);
        var handler = new RangeHandler(bytes);
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        var model = new CatalogModel("test-model", "Test Model", "revision", ["TXT_ASR"],
            ["windows-x64"], "test", "test", true,
            [new CatalogFile("model.bin", bytes.Length, sha,
                [new CatalogSource("primary", "https://offline.invalid/model.bin"),
                 new CatalogSource("fallback", "https://models.invalid/model.bin")])],
            new Dictionary<string, List<string>> { ["TXT_ASR"] = ["Fast"] });
        var manager = new ModelManager(root: root, model: model,
            clientFactory: () => new HttpClient(handler, disposeHandler: false));

        var state = await manager.InstallAsync(
            new Progress<ModelProgress>(_ => { }), CancellationToken.None);

        Equal("bytes=8-", handler.ObservedRange ?? "");
        if (handler.PrimaryAttempts != 2 || handler.FallbackAttempts != 1)
            throw new Exception("bounded retry/fallback counts are incorrect");
        if (!state.Verified) throw new Exception("installed model was not verified");
        var installed = await File.ReadAllBytesAsync(Path.Combine(manager.InstalledPath, "model.bin"));
        if (!installed.SequenceEqual(bytes)) throw new Exception("resumed model bytes differ");
        if (Directory.Exists(staging)) throw new Exception("staging directory was not atomically moved");

        await File.WriteAllTextAsync(Path.Combine(manager.InstalledPath, "model.bin"), "damaged");
        var repaired = await manager.RepairAsync(
            new Progress<ModelProgress>(_ => { }), CancellationToken.None);
        if (!repaired.Verified) throw new Exception("repair did not restore the pinned model");
        await manager.DeleteAsync();
        if ((await manager.GetStateAsync()).Installed) throw new Exception("delete did not remove the model");
        var redownloaded = await manager.InstallAsync(
            new Progress<ModelProgress>(_ => { }), CancellationToken.None);
        if (!redownloaded.Verified || manager.StorageBytes() < bytes.Length)
            throw new Exception("redownload/storage reporting failed");
        var failing = new ModelManager(root: root, model: model,
            clientFactory: () => new HttpClient(new OfflineHandler()));
        await MustFail(() => failing.InstallAsync(new Progress<ModelProgress>(), CancellationToken.None, forceRedownload: true));
        if (!(await manager.GetStateAsync()).Verified) throw new Exception("failed redownload destroyed working model");
    }
    finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
});

await RunAsync("Registry role switching, safe uninstall, storage and rollback", async () =>
{
    var root = Path.Combine(Path.GetTempPath(), $"canto-registry-{Guid.NewGuid():N}");
    var bytes = System.Text.Encoding.UTF8.GetBytes("real registry test payload");
    var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
    var models = new[] { "first", "second", "third" }.Select(id => new CatalogModel(id, id, "rev",
        ["TXT_ASR", "SRT_ASR"], ["windows-x64"], "fixture", "fixture", true,
        [new CatalogFile("model.bin", bytes.Length, sha, [])],
        new Dictionary<string, List<string>> { ["TXT_ASR"] = ["Fast"], ["SRT_ASR"] = ["Fast"] })).ToList();
    try
    {
        foreach (var model in models.Take(2))
        {
            var directory = Path.Combine(root, model.Id, model.Revision);
            Directory.CreateDirectory(directory);
            await File.WriteAllBytesAsync(Path.Combine(directory, "model.bin"), bytes);
        }
        var registry = new ModelRegistry(root, models);
        await registry.InitializeAsync();
        await registry.SetActiveAsync("TXT_ASR", "second");
        Equal("second", registry.ActiveTxtModelId);
        Equal("first", registry.ActiveSrtModelId);
        Directory.CreateDirectory(Path.Combine(root, "model-selections.json.part"));
        await MustFail(() => registry.SetActiveAsync("TXT_ASR", "first"));
        Equal("second", registry.ActiveTxtModelId);
        Directory.Delete(Path.Combine(root, "model-selections.json.part"));
        await MustFail(() => registry.SetActiveAsync("TXT_ASR", "third"));
        Equal("second", registry.ActiveTxtModelId);
        await MustFail(() => registry.UninstallAsync(models[0]));
        await registry.SetActiveAsync("SRT_ASR", "second");
        using (ModelUseGate.Acquire(registry.Manager(models[0]).InstalledPath))
            await MustFail(() => registry.UninstallAsync(models[0]));
        await MustFail(() => registry.UninstallAsync(models[0], _ => throw new IOException("injected delete failure")));
        if (!(await registry.Manager(models[0]).GetStateAsync()).Verified) throw new Exception("failed delete lost installed model");
        var before = registry.StorageBytes();
        await registry.UninstallAsync(models[0]);
        if (Directory.Exists(Path.Combine(root, "first")) || before - registry.StorageBytes() != bytes.Length)
            throw new Exception("uninstall did not reclaim exact model bytes");
        var reloaded = new ModelRegistry(root, models);
        await reloaded.InitializeAsync();
        Equal("second", reloaded.ActiveTxtModelId);
        Equal("second", reloaded.ActiveSrtModelId);
    }
    finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
});

if (args.Length > 0 && args[0] == "--native-model-qa")
    await RunAsync("Real installed model ID/native load/uninstall", () => ModelManagementQa.RunAsync(args[1], args[2], args[3]));

if (failures.Count > 0)
{
    Console.Error.WriteLine(string.Join(Environment.NewLine, failures));
    return 1;
}

Console.WriteLine("CantoTranscribe tests: PASS");
return 0;

void Run(string name, Action test)
{
    try { test(); Console.WriteLine($"PASS {name}"); }
    catch (Exception error) { failures.Add($"FAIL {name}: {error.Message}"); }
}

async Task RunAsync(string name, Func<Task> test)
{
    try { await test(); Console.WriteLine($"PASS {name}"); }
    catch (Exception error) { failures.Add($"FAIL {name}: {error.Message}"); }
}

void Equal(string expected, string actual)
{
    if (!string.Equals(expected, actual, StringComparison.Ordinal))
        throw new Exception($"expected '{expected}', got '{actual}'");
}

async Task MustFail(Func<Task> action)
{
    try { await action(); }
    catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException) { return; }
    throw new Exception("unsafe operation unexpectedly succeeded");
}

sealed class RangeHandler(byte[] bytes) : HttpMessageHandler
{
    public string? ObservedRange { get; private set; }
    public int PrimaryAttempts { get; private set; }
    public int FallbackAttempts { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri?.Host == "offline.invalid")
        {
            PrimaryAttempts++;
            throw new HttpRequestException("DNS lookup failed");
        }
        FallbackAttempts++;
        ObservedRange = request.Headers.Range?.ToString();
        var offset = (int)(request.Headers.Range?.Ranges.FirstOrDefault()?.From ?? 0);
        var response = new HttpResponseMessage(System.Net.HttpStatusCode.PartialContent)
        {
            Content = new ByteArrayContent(bytes[offset..])
        };
        response.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(
            offset, bytes.Length - 1, bytes.Length);
        return Task.FromResult(response);
    }
}

sealed class OfflineHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
        throw new HttpRequestException("injected offline source");
}
