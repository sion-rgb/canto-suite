using System.Text.RegularExpressions;
using CantoTranscribe;

var failures = new List<string>();

Run("Traditional conversion", () =>
    Equal("簡體中文", ChineseScriptConverter.Convert("简体中文", "繁體中文")));
Run("Simplified conversion", () =>
    Equal("万里长城", ChineseScriptConverter.Convert("萬里長城", "簡體中文")));
Run("Dictionary preferred display", () =>
    Equal("歡迎 UNION Design HK 同 AI", new DictionaryService().Clean("歡迎 union design 同 a i")));
Run("Cantonese display preservation", () =>
    Equal("嘅 喺 冇 咗 啲 嚟 噉", new DictionaryService().Clean("嘅 喺 冇 咗 啲 嚟 噉")));
Run("Cantonese-English ground truth preservation", () =>
    Equal("我哋下個 sprint 會 update 個 API。",
        new DictionaryService().Clean("我哋下個 sprint 會 update 個 API。")));
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
        var handler = new RangeHandler(bytes, 8);
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        var model = new CatalogModel("test-model", "revision", ["QUALITY_ASR"], true,
            [new CatalogFile("model.bin", bytes.Length, sha, "https://models.invalid/model.bin")]);
        var manager = new ModelManager(root: root, model: model,
            clientFactory: () => new HttpClient(handler, disposeHandler: false));

        var state = await manager.InstallAsync(
            new Progress<ModelProgress>(_ => { }), CancellationToken.None);

        Equal("bytes=8-", handler.ObservedRange ?? "");
        if (!state.Verified) throw new Exception("installed model was not verified");
        var installed = await File.ReadAllBytesAsync(Path.Combine(manager.InstalledPath, "model.bin"));
        if (!installed.SequenceEqual(bytes)) throw new Exception("resumed model bytes differ");
        if (Directory.Exists(staging)) throw new Exception("staging directory was not atomically moved");
    }
    finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
});

if (failures.Count > 0)
{
    Console.Error.WriteLine(string.Join(Environment.NewLine, failures));
    return 1;
}

Console.WriteLine("CantoTranscribe pure logic tests: PASS (8/8)");
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

sealed class RangeHandler(byte[] bytes, int expectedOffset) : HttpMessageHandler
{
    public string? ObservedRange { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ObservedRange = request.Headers.Range?.ToString();
        var response = new HttpResponseMessage(System.Net.HttpStatusCode.PartialContent)
        {
            Content = new ByteArrayContent(bytes[expectedOffset..])
        };
        return Task.FromResult(response);
    }
}
