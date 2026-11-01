using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using WD14Tagger.Core;
using WD14Tagger.Models;

namespace WD14Tagger.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly AppPaths _paths;
    private readonly AppConfig _config;
    private readonly BatchEngine _engine = new();
    private readonly PauseController _pause = new();
    private readonly SemaphoreSlim _thumbGate;

    private CancellationTokenSource? _cts;
    private TagVocabulary? _vocab;
    private ResultWriter? _writer;
    private bool _isRunning;

    public MainViewModel(AppPaths paths, AppConfig config)
    {
        _paths = paths;
        _config = config;
        _thumbGate = new SemaphoreSlim(Math.Max(2, Environment.ProcessorCount / 4));

        _threshold = Math.Clamp(config.Threshold, 0, 1);
        _characterThreshold = Math.Clamp(config.CharacterThreshold, 0, 1);
        _sortByScore = config.SortByScore;
        _replaceUnderscore = config.ReplaceUnderscore;
        _trailingComma = config.TrailingComma;
        _escapeParens = config.EscapeParens;
        _outputRating = config.OutputRating;
        _threads = Math.Clamp(config.Threads, 1, 32);
        _recursive = config.Recursive;

        OpenFolderCommand = new AsyncRelayCommand(OpenFolderAsync, () => IsIdle);
        AddFilesCommand = new AsyncRelayCommand(AddFilesAsync, () => IsIdle);
        StartCommand = new AsyncRelayCommand(() => StartAsync(false), () => IsIdle && Items.Count > 0);
        StartSelectedCommand = new AsyncRelayCommand(() => StartAsync(true), () => IsIdle && SelectedItem != null);
        PauseCommand = new RelayCommand(TogglePause, () => IsRunning);
        StopCommand = new RelayCommand(Stop, () => IsRunning);
        ClearCommand = new RelayCommand(Clear, () => IsIdle && Items.Count > 0);
        RemoveSelectedCommand = new RelayCommand(RemoveSelected, () => IsIdle && SelectedItem != null);
        ToggleScoresCommand = new RelayCommand(() => ShowScores = !ShowScores);
        CopyTagsCommand = new RelayCommand(CopyTags, () => SelectedItem != null && !string.IsNullOrWhiteSpace(SelectedTagsText));
    }

    public string ModelsPath => _paths.ModelsDir;
    public ObservableCollection<ImageItem> Items { get; } = new();
    public ObservableCollection<ModelInfo> Models { get; } = new();
    public ObservableCollection<TagChip> SelectedChips { get; } = new();

    public AsyncRelayCommand OpenFolderCommand { get; }
    public AsyncRelayCommand AddFilesCommand { get; }
    public AsyncRelayCommand StartCommand { get; }
    public AsyncRelayCommand StartSelectedCommand { get; }
    public RelayCommand PauseCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand ClearCommand { get; }
    public RelayCommand RemoveSelectedCommand { get; }
    public RelayCommand ToggleScoresCommand { get; }
    public RelayCommand CopyTagsCommand { get; }

    /// <summary>无头自检模式（不弹对话框）。</summary>
    public bool Headless { get; set; }

    // ── 模型（固定读软件目录下的 models\） ─────────────────
    private ModelInfo? _selectedModel;

    public ModelInfo? SelectedModel
    {
        get => _selectedModel;
        set
        {
            if (!Set(ref _selectedModel, value)) return;
            OnPropertyChanged(nameof(ModelSummary));
            if (value != null) _config.ModelName = value.Name;
            UpdateStatusReady();
        }
    }

    public bool HasModels => Models.Count > 0;

    public string ModelSummary => SelectedModel is { IsValid: true } m
        ? $"{m.Name} · {new FileInfo(m.OnnxPath).Length / 1024 / 1024} MB"
        : "models\\ 里没有可用的模型";

    public void ReloadModels()
    {
        string? keep = _config.ModelName;
        Models.Clear();
        foreach (var m in ModelCatalog.Scan(_paths.ModelsDir)) Models.Add(m);

        var pick = Models.FirstOrDefault(m => string.Equals(m.Name, keep, StringComparison.OrdinalIgnoreCase))
                   ?? Models.FirstOrDefault();
        SelectedModel = pick;
        OnPropertyChanged(nameof(HasModels));
        OnPropertyChanged(nameof(ModelSummary));
    }

    // ── 队列 ──────────────────────────────────────────────
    private ImageItem? _selectedItem;

    public ImageItem? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (!Set(ref _selectedItem, value)) return;
            UpdateDetail();
            RefreshCommands();
        }
    }

    public bool HasItems => Items.Count > 0;

    public string QueueSummary
    {
        get
        {
            int total = Items.Count;
            int done = Items.Count(i => i.State == ItemState.Done);
            int failed = Items.Count(i => i.State == ItemState.Failed);
            return total == 0 ? "队列为空" : $"共 {total} 张 · 完成 {done} · 失败 {failed}";
        }
    }

    // ── 预览 / 详情 ───────────────────────────────────────
    private ImageSource? _detailImage;
    public ImageSource? DetailImage
    {
        get => _detailImage;
        private set => Set(ref _detailImage, value);
    }

    private string _selectedTagsText = "";
    /// <summary>文本框内容 = 实际复制出去的内容（受复制格式选项影响）。</summary>
    public string SelectedTagsText
    {
        get => _selectedTagsText;
        private set => Set(ref _selectedTagsText, value);
    }

    private string _selectedRating = "";
    public string SelectedRating
    {
        get => _selectedRating;
        private set
        {
            if (!Set(ref _selectedRating, value)) return;
            OnPropertyChanged(nameof(HasSelectedRating));
        }
    }

    public bool HasSelectedRating => !string.IsNullOrWhiteSpace(_selectedRating);

    private bool _showScores;
    /// <summary>标签分值明细默认收起，需要时手动展开。</summary>
    public bool ShowScores
    {
        get => _showScores;
        set
        {
            if (!Set(ref _showScores, value)) return;
            OnPropertyChanged(nameof(ShowScoresText));
        }
    }

    public string ShowScoresText => ShowScores ? "收起分值" : "显示分值";

    // ── 参数 ──────────────────────────────────────────────
    private double _threshold = 0.35;
    public double Threshold
    {
        get => _threshold;
        set
        {
            if (!Set(ref _threshold, value)) return;
            OnPropertyChanged(nameof(ThresholdText));
        }
    }

    public string ThresholdText => _threshold.ToString("0.00");

    private double _characterThreshold = 0.85;
    public double CharacterThreshold
    {
        get => _characterThreshold;
        set
        {
            if (!Set(ref _characterThreshold, value)) return;
            OnPropertyChanged(nameof(CharacterThresholdText));
        }
    }

    public string CharacterThresholdText => _characterThreshold.ToString("0.00");

    private bool _outputRating = true;
    public bool OutputRating
    {
        get => _outputRating;
        set => Set(ref _outputRating, value);
    }

    private bool _sortByScore;
    public bool SortByScore
    {
        get => _sortByScore;
        set
        {
            if (!Set(ref _sortByScore, value)) return;
            RefreshCopyText();
        }
    }

    private bool _replaceUnderscore = true;
    public bool ReplaceUnderscore
    {
        get => _replaceUnderscore;
        set
        {
            if (!Set(ref _replaceUnderscore, value)) return;
            RefreshCopyText();
        }
    }

    private bool _trailingComma;
    public bool TrailingComma
    {
        get => _trailingComma;
        set
        {
            if (!Set(ref _trailingComma, value)) return;
            RefreshCopyText();
        }
    }

    private bool _escapeParens;
    public bool EscapeParens
    {
        get => _escapeParens;
        set
        {
            if (!Set(ref _escapeParens, value)) return;
            RefreshCopyText();
        }
    }

    private int _threads;
    public int Threads
    {
        get => _threads;
        set
        {
            if (!Set(ref _threads, value)) return;
            OnPropertyChanged(nameof(ThreadsText));
        }
    }

    public string ThreadsText => $"{_threads} 线程";

    private bool _recursive = true;
    public bool Recursive
    {
        get => _recursive;
        set => Set(ref _recursive, value);
    }

    private bool _skipDone = true;
    public bool SkipDone
    {
        get => _skipDone;
        set => Set(ref _skipDone, value);
    }

    // ── 运行状态 ──────────────────────────────────────────
    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (!Set(ref _isRunning, value)) return;
            OnPropertyChanged(nameof(IsIdle));
            OnPropertyChanged(nameof(PauseButtonText));
            RefreshCommands();
        }
    }

    public bool IsIdle => !_isRunning;
    public string PauseButtonText => _pause.IsPaused ? "继续" : "暂停";

    private double _progressValue;
    public double ProgressValue
    {
        get => _progressValue;
        private set => Set(ref _progressValue, value);
    }

    private string _statusText = "就绪";
    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    private string _progressText = "";
    public string ProgressText
    {
        get => _progressText;
        private set => Set(ref _progressText, value);
    }

    private string _currentFile = "";
    public string CurrentFile
    {
        get => _currentFile;
        private set => Set(ref _currentFile, value);
    }

    // ── 初始化 ────────────────────────────────────────────
    public Task InitializeAsync()
    {
        _paths.EnsureCreated();
        ReloadModels();
        UpdateStatusReady();
        Log($"启动 · 模型目录={_paths.ModelsDir} · 找到 {Models.Count} 个模型"
            + (SelectedModel is { } m ? $" · 当前={m.Name}" : ""));
        return Task.CompletedTask;
    }

    private void Log(string message)
    {
        try
        {
            File.AppendAllText(_paths.LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}\n", System.Text.Encoding.UTF8);
        }
        catch
        {
            // 日志失败不影响使用
        }
    }

    private void UpdateStatusReady()
    {
        if (Models.Count == 0)
            StatusText = $"models\\ 里还没有模型：把 xxx.onnx 和同名 xxx.csv 放进 {_paths.ModelsDir}，然后重新打开软件";
        else if (Items.Count == 0)
            StatusText = "就绪 · 选择目录，或把文件夹拖进窗口";
        else
            StatusText = "就绪";
    }

    // ── 加图 ──────────────────────────────────────────────
    private async Task OpenFolderAsync()
    {
        var dlg = new OpenFolderDialog { Title = "选择要反推的图片目录" };
        if (Directory.Exists(_config.LastSourceDir)) dlg.InitialDirectory = _config.LastSourceDir!;
        if (dlg.ShowDialog() != true) return;
        await AddPathAsync(dlg.FolderName);
    }

    private async Task AddFilesAsync()
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择图片",
            Multiselect = true,
            Filter = "图片|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif;*.tif;*.tiff|所有文件|*.*"
        };
        if (dlg.ShowDialog() != true) return;
        await AddPathAsync(dlg.FileNames);
    }

    public async Task AddPathAsync(string path)
    {
        await AddPathAsync(new[] { path });
    }

    public async Task AddPathAsync(IEnumerable<string> paths)
    {
        string[] list = paths.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
        if (list.Length == 0) return;

        StatusText = "正在扫描文件…";
        var existing = new HashSet<string>(Items.Select(i => i.FilePath), StringComparer.OrdinalIgnoreCase);
        var found = await Task.Run(() => CollectImages(list, _recursive, existing));
        found = found.Where(f => !existing.Contains(f)).ToList();

        if (found.Count == 0)
        {
            StatusText = "没有找到新的图片";
            return;
        }

        string baseDir = list.Length == 1 && Directory.Exists(list[0]) ? list[0] : "";
        foreach (var file in found)
        {
            var item = new ImageItem(file, baseDir);
            Items.Add(item);
            _ = LoadThumbAsync(item);
        }

        foreach (var p in list)
            if (Directory.Exists(p)) _config.LastSourceDir = p;

        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(QueueSummary));
        RefreshCommands();
        StatusText = $"已加入 {found.Count} 张图片，共 {Items.Count} 张";
    }

    private static List<string> CollectImages(IEnumerable<string> paths, bool recursive, HashSet<string> _)
    {
        var exts = new HashSet<string>(new AppConfig().Extensions.Select(e => "." + e), StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string file)
        {
            if (!exts.Contains(System.IO.Path.GetExtension(file))) return;
            if (seen.Add(file)) result.Add(file);
        }

        foreach (var p in paths)
        {
            if (File.Exists(p)) { Add(p); continue; }
            if (!Directory.Exists(p)) continue;
            var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            foreach (var f in Directory.EnumerateFiles(p, "*.*", option)) Add(f);
        }

        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    private async Task LoadThumbAsync(ImageItem item)
    {
        await _thumbGate.WaitAsync();
        try
        {
            var img = await Task.Run(() => ImagePreprocessor.Decode(item.FilePath, 96));
            item.Thumb = img;
            item.ThumbLoaded = true;
        }
        catch
        {
            item.ThumbLoaded = true;
        }
        finally
        {
            _thumbGate.Release();
        }
    }

    // ── 详情 ──────────────────────────────────────────────
    private void UpdateDetail()
    {
        var item = SelectedItem;
        SelectedChips.Clear();
        DetailImage = null;
        SelectedRating = item?.Rating ?? "";
        SelectedTagsText = item?.Tags ?? "";

        RefreshCommands();
        if (item == null) return;

        BuildChips(item);

        var path = item.FilePath;
        Task.Run(() => ImagePreprocessor.Decode(path, 1400))
            .ContinueWith(t =>
            {
                if (t.Status != TaskStatus.RanToCompletion) return;
                if (!ReferenceEquals(SelectedItem, item)) return;
                DetailImage = t.Result;
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void BuildChips(ImageItem item)
    {
        SelectedChips.Clear();
        if (item.Hits == null) return;

        var opts = CurrentOptions();
        IEnumerable<TagHit> hits = opts.SortByScore ? item.Hits.OrderByDescending(h => h.Score) : item.Hits;
        foreach (var hit in hits)
        {
            SelectedChips.Add(new TagChip
            {
                Name = hit.Name,
                Score = hit.Score,
                IsCharacter = hit.Category == TagCategory.Character
            });
        }
    }

    /// <summary>复制格式选项变化时，重算文本框与队列里的标签文本。</summary>
    private void RefreshCopyText()
    {
        var opts = CurrentOptions();
        foreach (var item in Items)
        {
            if (item.Hits == null) continue;
            item.Tags = TagVocabulary.FormatCopy(item.Hits, opts);
        }

        if (SelectedItem?.Hits != null)
        {
            SelectedTagsText = TagVocabulary.FormatCopy(SelectedItem.Hits, opts);
            BuildChips(SelectedItem);
        }

        RefreshCommands();
    }

    private TaggerOptions CurrentOptions() => new()
    {
        Threshold = _threshold,
        CharacterThreshold = _characterThreshold,
        OutputRating = _outputRating,
        ReplaceUnderscore = _replaceUnderscore,
        TrailingComma = _trailingComma,
        SortByScore = _sortByScore,
        EscapeParens = _escapeParens
    };

    // ── 开始 / 暂停 / 停止 ────────────────────────────────
    private async Task StartAsync(bool onlySelected)
    {
        if (IsRunning) return;

        var model = SelectedModel;
        if (model == null || !model.IsValid)
        {
            StatusText = $"models\\ 里还没有模型：放好 xxx.onnx + 同名 xxx.csv 后重启软件（{_paths.ModelsDir}）";
            return;
        }

        List<ImageItem> targets;
        if (onlySelected)
        {
            targets = SelectedItem == null ? new List<ImageItem>() : new List<ImageItem> { SelectedItem };
        }
        else
        {
            targets = Items.Where(i => !(_skipDone && i.State == ItemState.Done)).ToList();
            if (targets.Count == 0)
            {
                if (!_skipDone)
                {
                    StatusText = "队列里没有图片";
                    return;
                }
                foreach (var item in Items) item.ResetResult();
                targets = Items.ToList();
            }
        }

        foreach (var item in targets) item.ResetResult();
        OnPropertyChanged(nameof(QueueSummary));

        _cts = new CancellationTokenSource();
        IsRunning = true;
        ProgressValue = 0;
        CurrentFile = "";
        ProgressText = "";
        StatusText = "正在准备模型…";

        try
        {
            var opts = CurrentOptions();
            var onnx = model.OnnxPath;
            int threads = _threads;

            StatusText = "正在加载模型…";
            var tagger = await Task.Run(() => _engine.GetTagger(onnx, threads));
            _vocab ??= await Task.Run(() => TagVocabulary.Load(model.CsvPath));
            _writer = new ResultWriter(_paths.CsvPath);

            await Task.Run(() => tagger.Warmup());

            StatusText = "正在反推…";
            var itemProgress = new Progress<ItemUpdate>(ApplyUpdate);
            var batchProgress = new Progress<BatchProgress>(ApplyProgress);

            await _engine.RunAsync(targets, _vocab, opts, _writer, tagger, _pause,
                itemProgress, batchProgress, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            StatusText = "已停止";
        }
        catch (Exception ex)
        {
            StatusText = "出错：" + ex.Message;
        }
        finally
        {
            IsRunning = false;
            _pause.Reset();
            _cts?.Dispose();
            _cts = null;
            OnPropertyChanged(nameof(PauseButtonText));
            OnPropertyChanged(nameof(QueueSummary));
            RefreshCommands();
            if (IsIdle && StatusText.StartsWith("正在", StringComparison.Ordinal))
                StatusText = "完成";
        }
    }

    private void TogglePause()
    {
        if (!IsRunning) return;
        if (_pause.IsPaused)
        {
            _pause.Resume();
            StatusText = "继续反推…";
        }
        else
        {
            _pause.Pause();
            StatusText = "已暂停";
        }
        OnPropertyChanged(nameof(PauseButtonText));
    }

    private void Stop()
    {
        if (!IsRunning) return;
        StatusText = "正在停止…";
        _pause.Resume();
        _cts?.Cancel();
    }

    private void Clear()
    {
        if (IsRunning) return;
        Items.Clear();
        SelectedItem = null;
        DetailImage = null;
        SelectedChips.Clear();
        SelectedTagsText = "";
        SelectedRating = "";
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(QueueSummary));
        RefreshCommands();
        UpdateStatusReady();
    }

    private void RemoveSelected()
    {
        if (IsRunning || SelectedItem == null) return;
        var item = SelectedItem;
        int index = Items.IndexOf(item);
        Items.Remove(item);
        SelectedItem = Items.Count == 0 ? null : Items[Math.Clamp(index, 0, Items.Count - 1)];
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(QueueSummary));
        RefreshCommands();
    }

    private void CopyTags()
    {
        if (string.IsNullOrWhiteSpace(SelectedTagsText)) return;
        try
        {
            Clipboard.SetText(SelectedTagsText);
            StatusText = $"已复制 {SelectedTagsText.Split(',').Length} 段文本";
        }
        catch (Exception ex)
        {
            StatusText = "复制失败：" + ex.Message;
        }
    }

    private void ApplyUpdate(ItemUpdate u)
    {
        var item = u.Item;
        item.State = u.State;
        if (u.Tags != null) item.Tags = u.Tags;
        if (u.Rating != null) item.Rating = u.Rating;
        item.TagCount = u.TagCount;
        item.ElapsedMs = u.ElapsedMs;
        if (u.Error != null) item.Error = u.Error;
        if (u.Hits != null) item.Hits = u.Hits;

        if (ReferenceEquals(item, SelectedItem))
        {
            SelectedTagsText = item.Tags;
            SelectedRating = item.Rating;
            BuildChips(item);
        }
    }

    private void ApplyProgress(BatchProgress p)
    {
        ProgressValue = p.Percent;
        CurrentFile = p.Current ?? "";
        string eta = p.Eta is { } e ? $" · 剩余 {e:mm\\:ss}" : "";
        ProgressText = $"{p.Done} / {p.Total} · {p.ImagesPerSecond:F2} 张/秒{eta}";
    }

    private void RefreshCommands()
    {
        foreach (var cmd in new System.Windows.Input.ICommand[]
                 {
                     OpenFolderCommand, AddFilesCommand, StartCommand, StartSelectedCommand,
                     PauseCommand, StopCommand, ClearCommand, RemoveSelectedCommand, CopyTagsCommand
                 })
        {
            switch (cmd)
            {
                case RelayCommand rc: rc.RaiseCanExecuteChanged(); break;
                case AsyncRelayCommand ac: ac.RaiseCanExecuteChanged(); break;
            }
        }
    }

    // ── 配置 ──────────────────────────────────────────────
    public void SaveConfig()
    {
        _config.Threshold = _threshold;
        _config.CharacterThreshold = _characterThreshold;
        _config.OutputRating = _outputRating;
        _config.ReplaceUnderscore = _replaceUnderscore;
        _config.TrailingComma = _trailingComma;
        _config.SortByScore = _sortByScore;
        _config.EscapeParens = _escapeParens;
        _config.Threads = _threads;
        _config.Recursive = _recursive;
        _config.ModelName = SelectedModel?.Name ?? _config.ModelName;
        _config.Save(_paths.ConfigPath);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _thumbGate.Dispose();
        _engine.Dispose();
    }
}
