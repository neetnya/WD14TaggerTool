using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace WD14Tagger.Core;

/// <summary>所有数据都放在软件目录（便携）：models\ 模型、data\ 配置与结果。</summary>
public sealed class AppPaths
{
    public string AppDir { get; }
    public string ModelsDir { get; }
    public string DataDir { get; }
    public string ConfigPath { get; }
    public string CsvPath { get; }
    public string LogPath { get; }

    public AppPaths(string? appDir = null)
    {
        AppDir = Path.GetFullPath(appDir ?? AppContext.BaseDirectory);
        ModelsDir = Path.Combine(AppDir, "models");
        DataDir = Path.Combine(AppDir, "data");
        ConfigPath = Path.Combine(DataDir, "config.json");
        CsvPath = Path.Combine(DataDir, "results.csv");
        LogPath = Path.Combine(DataDir, "app.log");
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(ModelsDir);
        Directory.CreateDirectory(DataDir);
    }
}

public sealed class AppConfig
{
    public string? ModelName { get; set; }
    public string? LastSourceDir { get; set; }

    public double Threshold { get; set; } = 0.35;
    public double CharacterThreshold { get; set; } = 0.85;
    public bool OutputRating { get; set; } = true;

    /// <summary>复制时生效，首次运行默认勾选。</summary>
    public bool ReplaceUnderscore { get; set; } = true;

    /// <summary>复制时生效，首次运行默认不勾选。</summary>
    public bool TrailingComma { get; set; }

    public bool SortByScore { get; set; }

    public int Threads { get; set; } = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
    public bool Recursive { get; set; } = true;
    public string[] Extensions { get; set; } = { "png", "jpg", "jpeg", "webp", "bmp", "gif", "tiff", "tif" };

    public TaggerOptions ToTaggerOptions() => new()
    {
        Threshold = Threshold,
        CharacterThreshold = CharacterThreshold,
        OutputRating = OutputRating,
        ReplaceUnderscore = ReplaceUnderscore,
        TrailingComma = TrailingComma,
        SortByScore = SortByScore
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static AppConfig LoadOrCreate(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                string json = File.ReadAllText(path);
                var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
                if (cfg != null) return cfg;
            }
        }
        catch
        {
            // 配置损坏时重建，不影响使用
        }
        var fresh = new AppConfig();
        fresh.Save(path); // 首次运行就把默认配置落盘，方便用户直接改 json
        return fresh;
    }

    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch
        {
            // 保存失败不阻塞主流程
        }
    }
}

/// <summary>暂停/继续控制器。</summary>
public sealed class PauseController
{
    private readonly object _gate = new();
    private volatile bool _paused;
    private TaskCompletionSource<bool>? _resume;

    public bool IsPaused => _paused;

    public void Pause()
    {
        lock (_gate)
        {
            if (_paused) return;
            _paused = true;
            _resume = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public void Resume()
    {
        TaskCompletionSource<bool>? tcs;
        lock (_gate)
        {
            if (!_paused) return;
            _paused = false;
            tcs = _resume;
            _resume = null;
        }
        tcs?.TrySetResult(true);
    }

    public void Reset() => Resume();

    public async Task WaitIfPausedAsync(CancellationToken ct)
    {
        Task waitTask;
        lock (_gate)
        {
            if (!_paused || _resume == null) return;
            waitTask = _resume.Task;
        }
        await waitTask.WaitAsync(ct).ConfigureAwait(false);
    }
}
