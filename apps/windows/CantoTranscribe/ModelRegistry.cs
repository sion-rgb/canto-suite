using System.Text.Json;

namespace CantoTranscribe;

internal sealed record ModelSelections(
    string? ActiveTxtModelId,
    string? ActiveSrtModelId,
    string? QualityProfile);

internal sealed class ModelRegistry
{
    private readonly string _root;
    private readonly string _settingsPath;
    private ModelSelections _selections = new(null, null, null);
    private readonly SemaphoreSlim _changes = new(1, 1);

    public ModelRegistry(string? root = null, IReadOnlyList<CatalogModel>? catalog = null)
    {
        _root = root ?? ModelManager.DefaultRoot;
        _settingsPath = root is null ? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CantoSuite", "CantoTranscribe", "model-selections.json")
            : Path.Combine(root, "model-selections.json");
        Models = (catalog ?? ModelManager.LoadCatalog().Models)
            .Where(model => model.Enabled && model.Platforms.Contains("windows-x64") &&
                (model.Roles.Contains("TXT_ASR") || model.Roles.Contains("SRT_ASR")))
            .ToList();
    }

    public IReadOnlyList<CatalogModel> Models { get; }
    public string QualityProfile => _selections.QualityProfile ?? "Custom";
    public string ActiveTxtModelId => _selections.ActiveTxtModelId
        ?? throw new InvalidOperationException("未設定 TXT 模型");
    public string ActiveSrtModelId => _selections.ActiveSrtModelId
        ?? throw new InvalidOperationException("未設定 SRT 模型");

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (File.Exists(_settingsPath))
        {
            try
            {
                _selections = JsonSerializer.Deserialize<ModelSelections>(
                    await File.ReadAllTextAsync(_settingsPath, cancellationToken))
                    ?? _selections;
            }
            catch (JsonException) { }
        }

        CatalogModel? txt;
        CatalogModel? srt;
        if (IsPreset(_selections.QualityProfile))
        {
            // A named preset is an exact role-pair, not a historical label.
            // This migrates the old High pair (Large TXT/SRT) to Qwen TXT + Large SRT.
            txt = ForProfile("TXT_ASR", _selections.QualityProfile!);
            srt = ForProfile("SRT_ASR", _selections.QualityProfile!);
        }
        else
        {
            txt = Find(_selections.ActiveTxtModelId, "TXT_ASR");
            srt = Find(_selections.ActiveSrtModelId, "SRT_ASR");
        }
        txt ??= await FirstInstalledAsync("TXT_ASR", cancellationToken);
        srt ??= await FirstInstalledAsync("SRT_ASR", cancellationToken);
        var recommended = ModelManager.RecommendProfile();
        txt ??= ForProfile("TXT_ASR", recommended) ?? ForProfile("TXT_ASR", "Fast");
        srt ??= ForProfile("SRT_ASR", recommended) ?? ForProfile("SRT_ASR", "Fast");
        var selections = new ModelSelections(txt?.Id, srt?.Id,
            CommonProfile(txt, srt) ?? _selections.QualityProfile ?? "Custom");
        await SaveAsync(selections, cancellationToken);
    }

    public ModelManager ActiveManager(string role)
    {
        var model = role == "SRT_ASR"
            ? Find(ActiveSrtModelId, role) : Find(ActiveTxtModelId, role);
        return new ModelManager(root: _root,
            model: model ?? throw new InvalidOperationException("目前模型選擇無效"));
    }

    public ModelManager Manager(CatalogModel model) => new(root: _root, model: model);

    public async Task SelectProfileAsync(string profile,
        CancellationToken cancellationToken = default)
    {
        var txt = ForProfile("TXT_ASR", profile)
            ?? throw new InvalidOperationException($"{profile} 未有 TXT 模型");
        var srt = ForProfile("SRT_ASR", profile)
            ?? throw new InvalidOperationException($"{profile} 未有 SRT 模型");
        await _changes.WaitAsync(cancellationToken);
        try { await SaveAsync(new ModelSelections(txt.Id, srt.Id, profile), cancellationToken); }
        finally { _changes.Release(); }
    }

    public async Task SetActiveAsync(string role, string modelId,
        CancellationToken cancellationToken = default)
    {
        var model = Find(modelId, role)
            ?? throw new InvalidOperationException("所選模型不支援呢個輸出角色");
        await _changes.WaitAsync(cancellationToken);
        try
        {
            using var lease = ModelUseGate.Acquire(Manager(model).InstalledPath);
            if (!(await Manager(model).GetStateAsync(cancellationToken)).Verified)
                throw new InvalidOperationException("請先下載並完整驗證所選模型，再設為使用中模型。");
            var selections = role == "SRT_ASR"
                ? _selections with { ActiveSrtModelId = model.Id, QualityProfile = "Custom" }
                : _selections with { ActiveTxtModelId = model.Id, QualityProfile = "Custom" };
            await SaveAsync(selections, cancellationToken);
        }
        finally { _changes.Release(); }
    }

    public IReadOnlyList<string> ActiveRoles(string id) => new[]
    {
        _selections.ActiveTxtModelId == id ? "TXT" : null,
        _selections.ActiveSrtModelId == id ? "SRT" : null,
    }.Where(role => role is not null).Select(role => role!).ToList();

    public bool IsActive(string id) => ActiveRoles(id).Count != 0;

    public async Task UninstallAsync(CatalogModel model, Action<string>? deleteDirectory = null)
    {
        await _changes.WaitAsync();
        try
        {
            var activeRoles = ActiveRoles(model.Id);
            if (activeRoles.Count != 0)
                throw new InvalidOperationException($"呢個模型正在供 {string.Join("／", activeRoles)} 使用。請先把該角色切換至另一個已安裝模型，再卸載。");
            await Manager(model).DeleteAsync(deleteDirectory);
        }
        finally { _changes.Release(); }
    }

    public long StorageBytes() => Directory.Exists(_root)
        ? new DirectoryInfo(_root).EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length)
        : 0;

    private CatalogModel? Find(string? id, string role) => Models.FirstOrDefault(model =>
        model.Id == id && model.Roles.Contains(role));

    private CatalogModel? ForProfile(string role, string profile) => Models.FirstOrDefault(model =>
        model.Roles.Contains(role) && ModelManager.SupportsProfile(model, role, profile));

    private async Task<CatalogModel?> FirstInstalledAsync(string role,
        CancellationToken cancellationToken)
    {
        foreach (var model in Models.Where(model => model.Roles.Contains(role)))
        {
            if ((await Manager(model).GetStateAsync(cancellationToken)).Installed) return model;
        }
        return null;
    }

    private static string? CommonProfile(CatalogModel? txt, CatalogModel? srt)
    {
        if (txt?.QualityProfiles?.TryGetValue("TXT_ASR", out var txtProfiles) != true ||
            srt?.QualityProfiles?.TryGetValue("SRT_ASR", out var srtProfiles) != true ||
            txtProfiles is null || srtProfiles is null)
            return null;
        return txtProfiles.Intersect(srtProfiles, StringComparer.Ordinal).FirstOrDefault();
    }

    private static bool IsPreset(string? profile) =>
        profile is "Fast" or "Balanced" or "High Accuracy";

    private async Task SaveAsync(ModelSelections selections, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_settingsPath)!);
        var part = _settingsPath + ".part";
        await File.WriteAllTextAsync(part, JsonSerializer.Serialize(selections), cancellationToken);
        File.Move(part, _settingsPath, true);
        _selections = selections;
    }
}
