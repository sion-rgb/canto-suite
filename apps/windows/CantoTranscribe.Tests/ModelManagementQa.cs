using System.Runtime.InteropServices;
using System.Text.Json;
using CantoTranscribe;

internal static class ModelManagementQa
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetDllDirectory(string path);

    // Source caches are read-only. Only this freshly created QA root is uninstalled.
    public static async Task RunAsync(string nativeDirectory, string sourceRoot, string qwenDirectory)
    {
        if (!SetDllDirectory(Path.GetFullPath(nativeDirectory))) throw new Exception("native DLL directory failed");
        var root = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(nativeDirectory))!, $"native-qa-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var ids = new[] { "sensevoice-yue-int8-2024-07-17", "whisper-base-multilingual-q5-1",
            "whisper-small-multilingual-q5-1", "qwen3-asr-0.6b-int8-2026-03-25" };
        var models = ModelManager.LoadCatalog().Models.Where(model => ids.Contains(model.Id)).ToList();
        var registry = new ModelRegistry(root, models);
        foreach (var model in models)
        {
            var source = model.Id.StartsWith("qwen3-asr") ? qwenDirectory : Path.Combine(sourceRoot, model.Id, model.Revision);
            var staging = Path.Combine(root, ".staging", $"{model.Id}-{model.Revision}");
            foreach (var file in model.Files)
            {
                var destination = Path.Combine(staging, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(Path.Combine(source, file.Path), destination);
            }
            if (!(await registry.Manager(model).InstallAsync(new Progress<ModelProgress>(), CancellationToken.None)).Verified)
                throw new Exception($"real model install failed: {model.Id}");
        }
        await registry.InitializeAsync();
        var wav = File.ReadAllBytes(Path.Combine(qwenDirectory, "test_wavs", "cantonese.wav"));
        var dataOffset = 12;
        while (System.Text.Encoding.ASCII.GetString(wav, dataOffset, 4) != "data")
            dataOffset += 8 + BitConverter.ToInt32(wav, dataOffset + 4);
        var dataLength = BitConverter.ToInt32(wav, dataOffset + 4);
        var count = Math.Min(dataLength / 2, 16000 * 9);
        var samples = new short[count];
        Buffer.BlockCopy(wav, dataOffset + 8, samples, 0, count * 2);
        var evidence = new List<object>();
        foreach (var (role, id) in new[] { ("TXT_ASR", ids[0]), ("TXT_ASR", ids[3]),
            ("TXT_ASR", ids[1]), ("SRT_ASR", ids[2]), ("SRT_ASR", ids[1]) })
        {
            await registry.SetActiveAsync(role, id);
            var manager = registry.ActiveManager(role);
            using var engine = new NativeEngine(manager.InstalledPath, role == "SRT_ASR");
            if (engine.LoadedModelPath != Path.GetFullPath(manager.InstalledPath) || manager.Model.Id != id)
                throw new Exception("native model identity mismatch");
            var backend = engine.GetCapabilities().Backend;
            Console.WriteLine($"NATIVE_LOADED {role} {id} {backend} {engine.LoadedModelPath}");
            NativeEngine.Check(engine.Push(samples, count, true));
            var stop = DateTime.UtcNow.AddMinutes(5);
            var text = new List<string>();
            var final = false;
            while (DateTime.UtcNow < stop)
            {
                var result = engine.Poll();
                if (result is null) { await Task.Delay(100); continue; }
                if (result.Kind == 3) throw new Exception(result.Text);
                if (!string.IsNullOrWhiteSpace(result.Text)) text.Add(result.Text);
                if (result.Kind == 2) { final = true; break; }
            }
            if (!final || text.Count == 0) throw new Exception($"no real ASR output: {id}");
            Console.WriteLine($"NATIVE_RESULT {id}: {string.Join(" ", text)}");
            evidence.Add(new { role, modelId = id, revision = manager.Model.Revision, backend,
                loadedPath = engine.LoadedModelPath, text });
        }
        var nonActive = models.Single(model => model.Id == ids[3]);
        var before = registry.StorageBytes();
        var removed = registry.Manager(nonActive).StorageBytes();
        await registry.UninstallAsync(nonActive);
        var after = registry.StorageBytes();
        if (before - after != removed || Directory.Exists(Path.Combine(root, nonActive.Id)) ||
            (await registry.Manager(nonActive).GetStateAsync()).Installed) throw new Exception("actual model uninstall/bytes mismatch");
        var reloaded = new ModelRegistry(root, models);
        await reloaded.InitializeAsync();
        if (reloaded.ActiveTxtModelId != ids[1] || reloaded.ActiveSrtModelId != ids[1]) throw new Exception("role persistence lost");
        var report = Path.Combine(root, "evidence.json");
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { evidence,
            deletedId = nonActive.Id, beforeBytes = before, afterBytes = after, reclaimedBytes = removed },
            new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"REAL_MODEL_QA_PASS reclaimed={removed} evidence={report}");
    }
}
