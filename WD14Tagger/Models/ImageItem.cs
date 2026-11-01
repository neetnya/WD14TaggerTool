using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace WD14Tagger.Models;

public enum ItemState
{
    Pending,
    Running,
    Done,
    Failed,
    Skipped,
    Canceled
}

public sealed class ImageItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public string FilePath { get; }
    public string FileName { get; }
    public string DirectoryPath { get; }
    public string RelativePath { get; }

    private ItemState _state = ItemState.Pending;
    private string? _error;
    private string _tags = "";
    private string _rating = "";
    private long _elapsedMs;
    private int _tagCount;
    private ImageSource? _thumb;
    private bool _thumbLoaded;

    public ImageItem(string filePath, string baseDir)
    {
        FilePath = filePath;
        FileName = System.IO.Path.GetFileName(filePath);
        DirectoryPath = System.IO.Path.GetDirectoryName(filePath) ?? "";
        RelativePath = baseDir.Length > 0 && FilePath.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase)
            ? FilePath.Substring(baseDir.Length).TrimStart('\\', '/')
            : FileName;
    }

    public ItemState State
    {
        get => _state;
        set
        {
            if (_state == value) return;
            _state = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StateText));
            OnPropertyChanged(nameof(StateBrush));
            OnPropertyChanged(nameof(HasError));
        }
    }

    public string StateText => State switch
    {
        ItemState.Pending => "等待",
        ItemState.Running => "反推中",
        ItemState.Done => "完成",
        ItemState.Failed => "失败",
        ItemState.Skipped => "跳过",
        ItemState.Canceled => "已取消",
        _ => ""
    };

    public Brush StateBrush => State switch
    {
        ItemState.Done => Brushes.SeaGreen,
        ItemState.Running => Brushes.DodgerBlue,
        ItemState.Failed => Brushes.IndianRed,
        ItemState.Skipped => Brushes.Gray,
        ItemState.Canceled => Brushes.Gray,
        _ => Brushes.SlateGray
    };

    public string? Error
    {
        get => _error;
        set
        {
            if (_error == value) return;
            _error = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_error);

    public string Tags
    {
        get => _tags;
        set
        {
            if (_tags == value) return;
            _tags = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TagsPreview));
            OnPropertyChanged(nameof(HasTags));
        }
    }

    public string TagsPreview => Tags.Length > 220 ? Tags.Substring(0, 220) + "…" : Tags;

    public bool HasTags => !string.IsNullOrWhiteSpace(Tags);

    public string Rating
    {
        get => _rating;
        set
        {
            if (_rating == value) return;
            _rating = value;
            OnPropertyChanged();
        }
    }

    public int TagCount
    {
        get => _tagCount;
        set
        {
            if (_tagCount == value) return;
            _tagCount = value;
            OnPropertyChanged();
        }
    }

    public long ElapsedMs
    {
        get => _elapsedMs;
        set
        {
            if (_elapsedMs == value) return;
            _elapsedMs = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ElapsedText));
        }
    }

    public string ElapsedText => ElapsedMs > 0 ? $"{ElapsedMs} ms" : "";

    public ImageSource? Thumb
    {
        get => _thumb;
        set
        {
            if (ReferenceEquals(_thumb, value)) return;
            _thumb = value;
            OnPropertyChanged();
        }
    }

    public bool ThumbLoaded
    {
        get => _thumbLoaded;
        set
        {
            if (_thumbLoaded == value) return;
            _thumbLoaded = value;
            OnPropertyChanged();
        }
    }

    /// <summary>最近一次反推的标签明细（界面 chip 与「复制格式」重算用）。</summary>
    public IReadOnlyList<Core.TagHit>? Hits { get; set; }

    public void ResetResult()
    {
        Tags = "";
        Rating = "";
        TagCount = 0;
        ElapsedMs = 0;
        Error = null;
        Hits = null;
        State = ItemState.Pending;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public readonly record struct ItemUpdate(
    ImageItem Item,
    ItemState State,
    string? Tags,
    string? Rating,
    int TagCount,
    string? Error,
    long ElapsedMs,
    IReadOnlyList<Core.TagHit>? Hits = null);

public sealed class BatchProgress
{
    public int Done { get; init; }
    public int Total { get; init; }
    public int Failed { get; init; }
    public TimeSpan Elapsed { get; init; }
    public string? Current { get; init; }
    public string? CurrentTags { get; init; }

    public double Percent => Total <= 0 ? 0 : Math.Round(Done * 100.0 / Total, 1);

    public double ImagesPerSecond => Elapsed.TotalSeconds <= 0 ? 0 : Done / Elapsed.TotalSeconds;

    public TimeSpan? Eta
    {
        get
        {
            if (Done <= 0 || Total <= 0 || Done >= Total) return null;
            double perItem = Elapsed.TotalSeconds / Done;
            return TimeSpan.FromSeconds(perItem * (Total - Done));
        }
    }
}
