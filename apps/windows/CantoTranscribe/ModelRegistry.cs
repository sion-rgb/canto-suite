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

    public ModelRegistry(string? root = null)
    {
        _root = root ?? ModelManager.DefaultRoot;
        _settingsPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CantoSuite", "CantoTranscribe", "model-selections.json");
        Models = ModelManager.LoadCatalog().Models
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

        var txt = Find(_selections.ActiveTxtModelId, "TXT_ASR");
        var srt = Find(_selections.ActiveSrtModelId, "SRT_ASR");
        txt ??= await FirstInstalledAsync("TXT_ASR", cancellationToken);
        srt ??= await FirstInstalledAsync("SRT_ASR", cancellationToken);
        var profile = ModelManager.RecommendProfile();
        txt ??= ForProfile("TXT_ASR", profile) ?? ForProfile("TXT_ASR", "Fast");
        srt ??= ForProfile("SRT_ASR", profile) ?? ForProfile("SRT_ASR", "Fast");
        _selections = new ModelSelections(txt?.Id, srt?.Id,
            CommonProfile(txt, srt) ?? _selections.QualityProfile ?? "Custom");
        await SaveAsync(cancellationToken);
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
        _selections = new ModelSelections(txt.Id, srt.Id, profile);
        await SaveAsync(cancellationToken);
    }

    public async Task SetActiveAsync(string role, string modelId,
        CancellationToken cancellationToken = default)
    {
        var model = Find(modelId, role)
            ?? throw new InvalidOperationException("所選模型不支援呢個輸出角色");
        _selections = role == "SRT_ASR"
            ? _selections with { ActiveSrtModelId = model.Id, QualityProfile = "Custom" }
            : _selections with { ActiveTxtModelId = model.Id, QualityProfile = "Custom" };
        await SaveAsync(cancellationToken);
    }

    public long StorageBytes() => Models.Select(Manager).Sum(manager => manager.StorageBytes());

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

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_settingsPath)!);
        var part = _settingsPath + ".part";
        await File.WriteAllTextAsync(part, JsonSerializer.Serialize(_selections), cancellationToken);
        File.Move(part, _settingsPath, true);
    }
}
