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
    private readonly ModelManager _models = new();
    private readonly ModelManager _srtModels = new("SRT_ASR");
    private readonly JobStore _jobs = new();
    private readonly TranscriptionService _transcription;
    private readonly App.StartupOptions _startup;
    private ModelState? _modelState;
    private ModelState? _srtModelState;
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
            var stateTasks = new[] { _models.GetStateAsync(), _srtModels.GetStateAsync() };
            var states = await Task.WhenAll(stateTasks);
            _modelState = states[0];
            _srtModelState = states[1];
            RenderModelState();
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
            ModelStatusText.Text = $"已完整驗證 · 建議「{_modelState.RecommendedProfile}」 · {_modelState.CpuThreads} 執行緒 · {_modelState.TotalRamMiB / 1024d:0.0} GiB RAM";
            InstallModelButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            ModelStatusText.Text = $"{_modelState.CpuThreads} 執行緒 · {_modelState.TotalRamMiB / 1024d:0.0} GiB RAM · {_modelState.Gpu} · 建議「{_modelState.RecommendedProfile}」 · 下載 {_modelState.TotalBytes / 1024d / 1024d:0} MiB";
            InstallModelButton.Visibility = Visibility.Visible;
        }
        FastProfile.IsChecked = _modelState.RecommendedProfile == "快速";
        BalancedProfile.IsChecked = _modelState.RecommendedProfile == "平衡";
        HighProfile.IsChecked = _modelState.RecommendedProfile == "高準確度";
        if (_srtModelState?.Installed == true)
        {
            SrtModelStatusText.Text = "SRT 時間碼模型已完整驗證 · Whisper Base 多語言 Q5_1";
            InstallSrtModelButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            var size = (_srtModelState?.TotalBytes ?? 0) / 1024d / 1024d;
            SrtModelStatusText.Text = $"SRT 需另裝時間碼模型 · 下載 {size:0} MiB";
            InstallSrtModelButton.Visibility = Visibility.Visible;
        }
        UpdateStartEnabled();
    }

    private async void InstallSrtModel_Click(object sender, RoutedEventArgs e)
    {
        InstallSrtModelButton.IsEnabled = false;
        ModelProgress.Visibility = Visibility.Visible;
        _cancellation = new CancellationTokenSource();
        var progress = new Progress<ModelProgress>(value =>
        {
            ModelProgress.Maximum = value.TotalBytes;
            ModelProgress.Value = value.ReceivedBytes;
            SrtModelStatusText.Text = $"正在下載同驗證 SRT 模型 · {value.ReceivedBytes * 100 / Math.Max(1, value.TotalBytes)}%";
        });
        try
        {
            _srtModelState = await _srtModels.InstallAsync(progress, _cancellation.Token);
            RenderModelState();
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
        InstallModelButton.IsEnabled = false;
        ModelProgress.Visibility = Visibility.Visible;
        _cancellation = new CancellationTokenSource();
        var progress = new Progress<ModelProgress>(value =>
        {
            ModelProgress.Maximum = value.TotalBytes;
            ModelProgress.Value = value.ReceivedBytes;
            ModelStatusText.Text = $"正在下載同驗證 · {value.ReceivedBytes * 100 / Math.Max(1, value.TotalBytes)}%";
        });
        try
        {
            _modelState = await _models.InstallAsync(progress, _cancellation.Token);
            RenderModelState();
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
        var outputScript = ScriptBox.SelectedIndex == 1 ? "簡體中文" : "繁體中文";
        var quality = HighProfile.IsChecked == true ? "高準確度" : FastProfile.IsChecked == true ? "快速" : "平衡";
        var output = System.IO.Path.ChangeExtension(_selectedPath, format.ToLowerInvariant());
        _completedPath = null;
        _cancellation = new CancellationTokenSource();
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
            _completedPath = await _transcription.RunAsync(_selectedPath, output, format, clean, outputScript, quality,
                activeModel, progress, resume, _cancellation.Token);
            _resume = null;
            JobProgress.Value = 100;
            JobStatusText.Text = "✓ 轉錄完成";
            OpenFileButton.Visibility = Visibility.Visible;
            OpenFolderButton.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException)
        {
            _resume = await _jobs.LoadRecoverableAsync();
            JobStatusText.Text = "已安全暫停；再次開始會由已保存位置繼續。";
        }
        catch (Exception error)
        {
            JobStatusText.Text = "轉錄失敗";
            PreviewText.Text = error.Message;
            try
            {
                var log = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CantoSuite", "CantoTranscribe", "last-job-error.log");
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(log)!);
                await File.WriteAllTextAsync(log, error.ToString());
            }
            catch { }
            await ShowErrorAsync(error.Message);
        }
        finally
        {
            _cancellation?.Dispose();
            _cancellation = null;
            CancelButton.Visibility = Visibility.Collapsed;
            UpdateStartEnabled();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cancellation?.Cancel();

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
}
