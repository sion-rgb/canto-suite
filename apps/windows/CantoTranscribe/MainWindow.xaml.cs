using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace CantoTranscribe;

public sealed partial class MainWindow : Window
{
    private static readonly HashSet<string> SupportedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mov", ".mkv", ".mp3", ".m4a", ".aac", ".wav" };
    private readonly ModelRegistry _modelRegistry = new();
    private readonly JobStore _jobs = new();
    private readonly TranscriptionService _transcription;
    private readonly App.StartupOptions _startup;
    private ModelState? _modelState;
    private ModelState? _srtModelState;
    private ModelManager? _models;
    private ModelManager? _srtModels;
    private bool _modelUiReady;
    private bool _syncingModelSelection;
    private JobCheckpoint? _resume;
    private string? _selectedPath;
    private string? _completedPath;
    private CancellationTokenSource? _cancellation;

    public MainWindow(App.StartupOptions? startup = null)
    {
        InitializeComponent();
        _startup = startup ?? new App.StartupOptions(null, false, false, false, false);
        _transcription = new TranscriptionService(_jobs);
        ((FrameworkElement)Content).Loaded += async (_, _) => await InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            await _modelRegistry.InitializeAsync();
            PopulateModelManagement();
            await RefreshActiveModelsAsync();
            _modelUiReady = true;
            if (_startup.MediaPath is not null) SelectSource(_startup.MediaPath);
            _resume = await _jobs.LoadRecoverableAsync();
            if (_resume is not null && _startup.MediaPath is null)
            {
                SelectSource(_resume.SourcePath);
                JobStatusText.Text = $"發現可恢復工作 · 已處理 {FormatDuration(_resume.ProcessedMs)}";
                FormatBox.SelectedIndex = _resume.Format == "SRT" ? 1 : 0;
            }
            if (_startup.MediaPath is not null)
            {
                FormatBox.SelectedIndex = _startup.Srt ? 1 : 0;
                ScriptBox.SelectedIndex = _startup.Simplified ? 1 : 0;
                TextModeBox.SelectedIndex = _startup.Clean ? 1 : 0;
                if (_startup.AutoStart)
                {
                    Start_Click(this, new RoutedEventArgs());
                    if (_startup.CancelAfterMs is { } delay)
                        _ = CancelAfterDelayAsync(delay);
                }
            }
        }
        catch (Exception error) { await ShowErrorAsync(error.Message); }
    }

    private void RenderModelState()
    {
        if (_modelState is null) return;
        if (_modelState.Installed)
        {
            ModelStatusText.Text = $"TXT：{_models?.Model.DisplayNameForRole("TXT_ASR")} · 已完整驗證 · {_modelState.TotalBytes / 1024d / 1024d:0} MiB";
            InstallModelButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            ModelStatusText.Text = $"TXT：{_models?.Model.DisplayNameForRole("TXT_ASR")} · 尚未安裝 · 下載 {_modelState.TotalBytes / 1024d / 1024d:0} MiB";
            InstallModelButton.Visibility = Visibility.Visible;
        }
        if (_srtModelState?.Installed == true)
        {
            SrtModelStatusText.Text = $"SRT：{_srtModels?.Model.DisplayNameForRole("SRT_ASR")} · 已完整驗證 · {_srtModelState.TotalBytes / 1024d / 1024d:0} MiB";
            InstallSrtModelButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            var size = (_srtModelState?.TotalBytes ?? 0) / 1024d / 1024d;
            SrtModelStatusText.Text = $"SRT：{_srtModels?.Model.DisplayNameForRole("SRT_ASR")} · 尚未安裝 · 下載 {size:0} MiB";
            InstallSrtModelButton.Visibility = Visibility.Visible;
        }
        _syncingModelSelection = true;
        FastProfile.IsChecked = _modelRegistry.QualityProfile == "Fast";
        BalancedProfile.IsChecked = _modelRegistry.QualityProfile == "Balanced";
        HighProfile.IsChecked = _modelRegistry.QualityProfile == "High Accuracy";
        ActiveTxtModelBox.SelectedItem = _modelRegistry.Models.FirstOrDefault(model => model.Id == _modelRegistry.ActiveTxtModelId);
        ActiveSrtModelBox.SelectedItem = _modelRegistry.Models.FirstOrDefault(model => model.Id == _modelRegistry.ActiveSrtModelId);
        _syncingModelSelection = false;
        UpdateStartEnabled();
    }

    private async void InstallSrtModel_Click(object sender, RoutedEventArgs e)
    {
        if (_srtModels is null || _cancellation is not null) return;
        InstallSrtModelButton.IsEnabled = false;
        ModelProgress.Visibility = Visibility.Visible;
        _cancellation = new CancellationTokenSource();
        var progress = new Progress<ModelProgress>(value =>
        {
            ModelProgress.Maximum = value.TotalBytes;
            ModelProgress.Value = value.ReceivedBytes;
            SrtModelStatusText.Text = $"正在下載同驗證 SRT 模型 · {value.ReceivedBytes * 100 / Math.Max(1, value.TotalBytes)}% · {value.CurrentSource}";
        });
        try
        {
            var token = _cancellation.Token;
            _srtModelState = await Task.Run(() => _srtModels.InstallAsync(progress, token));
            RenderModelState();
            await RenderModelManagementAsync();
        }
        catch (OperationCanceledException) { SrtModelStatusText.Text = "SRT 模型下載已暫停；再次按下載會續傳。"; }
        catch (Exception error) { await ShowErrorAsync(error.Message); }
        finally
        {
            _cancellation?.Dispose();
            _cancellation = null;
            InstallSrtModelButton.IsEnabled = true;
            ModelProgress.Visibility = Visibility.Collapsed;
            UpdateStartEnabled();
        }
    }

    private async void InstallModel_Click(object sender, RoutedEventArgs e)
    {
        if (_models is null || _cancellation is not null) return;
        InstallModelButton.IsEnabled = false;
        ModelProgress.Visibility = Visibility.Visible;
        _cancellation = new CancellationTokenSource();
        var progress = new Progress<ModelProgress>(value =>
        {
            ModelProgress.Maximum = value.TotalBytes;
            ModelProgress.Value = value.ReceivedBytes;
            ModelStatusText.Text = $"正在下載同驗證 · {value.ReceivedBytes * 100 / Math.Max(1, value.TotalBytes)}% · {value.CurrentSource}";
        });
        try
        {
            var token = _cancellation.Token;
            _modelState = await Task.Run(() => _models.InstallAsync(progress, token));
            RenderModelState();
            await RenderModelManagementAsync();
        }
        catch (OperationCanceledException) { ModelStatusText.Text = "下載已暫停；再次按下載會續傳。"; }
        catch (Exception error) { await ShowErrorAsync(error.Message); }
        finally
        {
            _cancellation?.Dispose();
            _cancellation = null;
            InstallModelButton.IsEnabled = true;
            ModelProgress.Visibility = Visibility.Collapsed;
            UpdateStartEnabled();
        }
    }

    private void PopulateModelManagement()
    {
        ActiveTxtModelBox.ItemsSource = _modelRegistry.Models
            .Where(model => model.Roles.Contains("TXT_ASR")).ToList();
        ActiveSrtModelBox.ItemsSource = _modelRegistry.Models
            .Where(model => model.Roles.Contains("SRT_ASR")).ToList();
        ManagedModelBox.ItemsSource = _modelRegistry.Models.ToList();
        ManagedModelBox.SelectedIndex = 0;
    }

    private async Task RefreshActiveModelsAsync()
    {
        _models = _modelRegistry.ActiveManager("TXT_ASR");
        _srtModels = _modelRegistry.ActiveManager("SRT_ASR");
        var states = await Task.WhenAll(_models.GetStateAsync(), _srtModels.GetStateAsync());
        _modelState = states[0];
        _srtModelState = states[1];
        RenderModelState();
        await RenderModelManagementAsync();
    }

    private async Task RenderModelManagementAsync()
    {
        var lines = new List<string>();
        foreach (var model in _modelRegistry.Models)
        {
            var state = await _modelRegistry.Manager(model).GetStateAsync();
            var roles = string.Join(" / ", model.Roles.Where(role => role is "TXT_ASR" or "SRT_ASR")
                .Select(role => role == "TXT_ASR" ? "TXT" : "SRT"));
            var profiles = model.QualityProfiles is null ? "自訂" : string.Join(" / ",
                model.QualityProfiles.Values.SelectMany(value => value).Distinct());
            var active = string.Join(" / ", new[] {
                _modelRegistry.ActiveTxtModelId == model.Id ? "TXT 已選用" : null,
                _modelRegistry.ActiveSrtModelId == model.Id ? "SRT 已選用" : null }.Where(value => value is not null));
            lines.Add($"{model.DisplayName} · {model.Id}\n{(state.Installed ? "✓ 已安裝及驗證" : "○ 未安裝或需修復")} · {(active.Length > 0 ? active : "未啟用")} · {roles} · 預設組合：{profiles} · v{model.Revision} · {state.TotalBytes / 1024d / 1024d:0.0} MiB");
        }
        InstalledModelsText.Text = string.Join(Environment.NewLine, lines);
        var storageBytes = await Task.Run(_modelRegistry.StorageBytes);
        ModelStorageText.Text = $"模型儲存空間（包括續傳暫存）：{storageBytes / 1024d / 1024d:0.0} MiB";
    }

    private async void QualityProfile_Checked(object sender, RoutedEventArgs e)
    {
        if (!_modelUiReady || _syncingModelSelection) return;
        var profile = ReferenceEquals(sender, FastProfile) ? "Fast"
            : ReferenceEquals(sender, HighProfile) ? "High Accuracy" : "Balanced";
        try
        {
            await _modelRegistry.SelectProfileAsync(profile);
            await RefreshActiveModelsAsync();
        }
        catch (Exception error) { await ShowErrorAsync(error.Message); }
    }

    private void ToggleModelManagement_Click(object sender, RoutedEventArgs e) =>
        ModelManagementPanel.Visibility = ModelManagementPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed : Visibility.Visible;

    private async void ActiveTxtModel_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_modelUiReady || _syncingModelSelection || ActiveTxtModelBox.SelectedItem is not CatalogModel model) return;
        try { await _modelRegistry.SetActiveAsync("TXT_ASR", model.Id); }
        catch (Exception error) { await ShowErrorAsync(error.Message); }
        await RefreshActiveModelsAsync();
    }

    private async void ActiveSrtModel_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_modelUiReady || _syncingModelSelection || ActiveSrtModelBox.SelectedItem is not CatalogModel model) return;
        try { await _modelRegistry.SetActiveAsync("SRT_ASR", model.Id); }
        catch (Exception error) { await ShowErrorAsync(error.Message); }
        await RefreshActiveModelsAsync();
    }

    private CatalogModel? ManagedModel => ManagedModelBox.SelectedItem as CatalogModel;

    private async void ManageDownload_Click(object sender, RoutedEventArgs e) =>
        await InstallManagedModelAsync(redownload: false);

    private async void ManageRepair_Click(object sender, RoutedEventArgs e) =>
        await InstallManagedModelAsync(redownload: false);

    private async void ManageRedownload_Click(object sender, RoutedEventArgs e) =>
        await InstallManagedModelAsync(redownload: true);

    private async Task InstallManagedModelAsync(bool redownload)
    {
        if (ManagedModel is not { } model || _cancellation is not null) return;
        var manager = _modelRegistry.Manager(model);
        _cancellation = new CancellationTokenSource();
        ModelProgress.Visibility = Visibility.Visible;
        var progress = new Progress<ModelProgress>(value =>
        {
            ModelProgress.Maximum = value.TotalBytes;
            ModelProgress.Value = value.ReceivedBytes;
            ModelStorageText.Text = $"正在處理 {model.DisplayName} · {value.ReceivedBytes * 100 / Math.Max(1, value.TotalBytes)}% · {value.CurrentSource}";
        });
        try
        {
            var token = _cancellation.Token;
            await Task.Run(async () =>
            {
                await manager.InstallAsync(progress, token, forceRedownload: redownload);
            });
            await RefreshActiveModelsAsync();
        }
        catch (OperationCanceledException) { ModelStorageText.Text = "下載已暫停；再次下載會安全續傳。"; }
        catch (Exception error) { await ShowErrorAsync(error.Message); }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
            ModelProgress.Visibility = Visibility.Collapsed;
            UpdateStartEnabled();
        }
    }

    private async void ManageSetTxt_Click(object sender, RoutedEventArgs e)
    {
        if (ManagedModel is not { } model) return;
        try
        {
            await _modelRegistry.SetActiveAsync("TXT_ASR", model.Id);
            await RefreshActiveModelsAsync();
        }
        catch (Exception error) { await ShowErrorAsync(error.Message); }
    }

    private async void ManageSetSrt_Click(object sender, RoutedEventArgs e)
    {
        if (ManagedModel is not { } model) return;
        try
        {
            await _modelRegistry.SetActiveAsync("SRT_ASR", model.Id);
            await RefreshActiveModelsAsync();
        }
        catch (Exception error) { await ShowErrorAsync(error.Message); }
    }

    private async void ManageDelete_Click(object sender, RoutedEventArgs e)
    {
        if (ManagedModel is not { } model || _cancellation is not null) return;
        var dialog = new ContentDialog
        {
            Title = "刪除模型？",
            Content = $"會刪除 {model.DisplayName}；之後可以重新下載。",
            PrimaryButtonText = "刪除",
            CloseButtonText = "取消",
            XamlRoot = Content.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            var before = _modelRegistry.Manager(model).StorageBytes();
            await _modelRegistry.UninstallAsync(model);
            await RefreshActiveModelsAsync();
            ModelStorageText.Text += $" · 已卸載 {model.DisplayName}，回收 {before / 1024d / 1024d:0.0} MiB";
        }
        catch (Exception error) { await ShowErrorAsync(error.Message); }
    }

    private async void ChooseFile_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.VideosLibrary };
        foreach (var extension in SupportedExtensions) picker.FileTypeFilter.Add(extension);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        if (file is not null) SelectSource(file.Path);
    }

    private void DropZone_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "加入轉錄";
    }

    private async void DropZone_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        if (items.OfType<StorageFile>().FirstOrDefault() is { } file) SelectSource(file.Path);
    }

    private void SelectSource(string path)
    {
        if (!SupportedExtensions.Contains(System.IO.Path.GetExtension(path)))
        {
            _ = ShowErrorAsync("不支援呢種媒體格式");
            return;
        }
        _selectedPath = path;
        SelectedFileText.Text = System.IO.Path.GetFileName(path);
        UpdateStartEnabled();
    }

    private void FormatBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateStartEnabled();

    private void UpdateStartEnabled()
    {
        if (StartButton is null) return;
        var modelReady = FormatBox?.SelectedIndex == 1
            ? _srtModelState?.Installed == true
            : _modelState?.Installed == true;
        StartButton.IsEnabled = _selectedPath is not null && modelReady && _cancellation is null;
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPath is null) return;
        var format = ((ComboBoxItem)FormatBox.SelectedItem).Content.ToString() ?? "TXT";
        var activeModel = format == "SRT" ? _srtModelState : _modelState;
        if (activeModel?.Installed != true) return;
        var clean = TextModeBox.SelectedIndex == 1;
        var outputScript = ScriptBox.SelectedIndex == 1 ? "簡體中文" : "香港繁體";
        var quality = _modelRegistry.QualityProfile;
        var output = System.IO.Path.ChangeExtension(_selectedPath, format.ToLowerInvariant());
        _completedPath = null;
        _cancellation = new CancellationTokenSource();
        var cancellationToken = _cancellation.Token;
        StartButton.IsEnabled = false;
        CancelButton.Visibility = Visibility.Visible;
        OpenFileButton.Visibility = Visibility.Collapsed;
        OpenFolderButton.Visibility = Visibility.Collapsed;
        JobStatusText.Text = "正在準備本機轉錄…";
        var progress = new Progress<TranscriptionProgress>(value =>
        {
            JobStatusText.Text = value.State;
            PreviewText.Text = value.Preview;
            DurationText.Text = $"{FormatDuration(value.ProcessedMs)} / {FormatDuration(value.TotalMs ?? 0)}";
            JobProgress.Value = value.TotalMs is > 0 ? Math.Min(100, value.ProcessedMs * 100d / value.TotalMs.Value) : 0;
        });
        try
        {
            var resume = _resume is not null && _resume.SourcePath == _selectedPath &&
                _resume.Format == format && _resume.OutputScript == outputScript ? _resume : null;
            // Run the complete model-load/FFmpeg/PInvoke pipeline away from WinUI's
            // synchronization context. Progress<T> was created on the UI thread and
            // safely marshals the small status updates back here.
            _completedPath = await Task.Run(() => _transcription.RunAsync(
                _selectedPath, output, format, clean, outputScript, quality,
                activeModel, progress, resume, cancellationToken));
            _resume = null;
            JobProgress.Value = 100;
            JobStatusText.Text = "✓ 轉錄完成";
            OpenFileButton.Visibility = Visibility.Visible;
            OpenFolderButton.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException)
        {
            _resume = await _jobs.LoadRecoverableAsync();
            JobStatusText.Text = "已安全暫停；正在背景釋放模型…";
            await _transcription.WaitForCleanupAsync();
            JobStatusText.Text = "已安全暫停；再次開始會由已保存位置繼續。";
        }
        catch (Exception error)
        {
            JobStatusText.Text = "轉錄失敗";
            var userMessage = UserFacingTranscriptionError(error);
            PreviewText.Text = userMessage;
            try
            {
                var log = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CantoSuite", "CantoTranscribe", "last-job-error.log");
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(log)!);
                await File.WriteAllTextAsync(log, error.ToString());
            }
            catch { }
            await ShowErrorAsync(userMessage);
        }
        finally
        {
            _cancellation?.Dispose();
            _cancellation = null;
            CancelButton.Visibility = Visibility.Collapsed;
            CancelButton.IsEnabled = true;
            UpdateStartEnabled();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_cancellation is null) return;
        CancelButton.IsEnabled = false;
        JobStatusText.Text = "正在取消並保存進度…";
        _cancellation.Cancel();
    }

    private async Task CancelAfterDelayAsync(int milliseconds)
    {
        await Task.Delay(milliseconds);
        _cancellation?.Cancel();
    }

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        if (_completedPath is not null) OpenShell(_completedPath);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_completedPath is not null) OpenShell(System.IO.Path.GetDirectoryName(_completedPath)!);
    }

    private static void OpenShell(string path) => System.Diagnostics.Process.Start(
        new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });

    private async Task ShowErrorAsync(string message)
    {
        var dialog = new ContentDialog { Title = "CantoTranscribe", Content = message,
            CloseButtonText = "關閉", XamlRoot = Content.XamlRoot };
        await dialog.ShowAsync();
    }

    private static string FormatDuration(long milliseconds) =>
        TimeSpan.FromMilliseconds(Math.Max(0, milliseconds)).ToString(@"hh\:mm\:ss");

    private static string UserFacingTranscriptionError(Exception error)
    {
        if (error is InvalidDataException &&
            (error.Message.StartsWith("ffprobe", StringComparison.OrdinalIgnoreCase) ||
             error.Message.StartsWith("FFmpeg", StringComparison.OrdinalIgnoreCase)))
            return "無法讀取呢個媒體檔案；請確認檔案完整，並使用支援嘅格式。技術資料已保存到本機錯誤記錄。";
        if (error is TimeoutException) return error.Message;
        return "本機轉錄未能完成。技術資料已保存到本機錯誤記錄；你可以保留進度後重試。";
    }
}
