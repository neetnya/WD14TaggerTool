using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using WD14Tagger.Models;

namespace WD14Tagger.Core;

/// <summary>
/// 批量反推引擎：单 ONNX session（多线程 intra-op）+ 「解码/预处理」与「推理」重叠的双缓冲流水线。
/// 实测：不批处理、不多 session 时吞吐最高（约 0.5s/张 @16 核 448 ViT）。
/// </summary>
public sealed class BatchEngine : IDisposable
{
    private const int Lookahead = 2;

    private OnnxTagger? _tagger;
    private string? _taggerKey;

    public OnnxTagger GetTagger(string onnxPath, int threads)
    {
        string key = $"{onnxPath}|{threads}";
        if (_tagger != null && _taggerKey == key) return _tagger;

        _tagger?.Dispose();
        _tagger = null;
        _taggerKey = null;

        var tagger = new OnnxTagger(onnxPath, threads);
        _tagger = tagger;
        _taggerKey = key;
        return tagger;
    }

    public async Task RunAsync(
        IReadOnlyList<ImageItem> items,
        TagVocabulary vocabulary,
        TaggerOptions options,
        ResultWriter writer,
        OnnxTagger tagger,
        PauseController pause,
        IProgress<ItemUpdate>? itemProgress,
        IProgress<BatchProgress>? progress,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        int done = 0, failed = 0;
        int total = items.Count;
        int size = tagger.InputSize;

        var pending = new Queue<(ImageItem Item, Task<float[]> Prep)>();

        void Report(string? current = null, string? currentTags = null)
        {
            progress?.Report(new BatchProgress
            {
                Done = done,
                Total = total,
                Failed = failed,
                Elapsed = sw.Elapsed,
                Current = current,
                CurrentTags = currentTags
            });
        }

        async Task ProcessAsync((ImageItem Item, Task<float[]> Prep) entry)
        {
            var item = entry.Item;
            if (ct.IsCancellationRequested)
            {
                itemProgress?.Report(new ItemUpdate(item, ItemState.Canceled, null, null, 0, null, 0));
                return;
            }

            var itemSw = Stopwatch.StartNew();
            itemProgress?.Report(new ItemUpdate(item, ItemState.Running, null, null, 0, null, 0));

            try
            {
                float[] data = await entry.Prep.ConfigureAwait(false);
                float[] probs = tagger.Run(data);
                var result = vocabulary.Evaluate(probs, options);

                itemSw.Stop();
                writer.Write(item.FilePath, result, vocabulary.ModelName, options, itemSw.ElapsedMilliseconds);

                done++;
                itemProgress?.Report(new ItemUpdate(item, ItemState.Done, result.CopyText,
                    options.OutputRating ? result.Rating : "", result.Tags.Count, null, itemSw.ElapsedMilliseconds, result.Tags));
                Report(item.FileName, result.CopyText);
            }
            catch (OperationCanceledException)
            {
                itemProgress?.Report(new ItemUpdate(item, ItemState.Canceled, null, null, 0, null, 0));
            }
            catch (Exception ex)
            {
                done++;
                failed++;
                itemProgress?.Report(new ItemUpdate(item, ItemState.Failed, null, null, 0, ex.Message, 0));
                Report(item.FileName, null);
            }
        }

        try
        {
            foreach (var item in items)
            {
                await pause.WaitIfPausedAsync(ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();

                while (pending.Count >= Lookahead)
                {
                    var entry = pending.Dequeue();
                    await ProcessAsync(entry).ConfigureAwait(false);
                    await pause.WaitIfPausedAsync(ct).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                }

                var itemPath = item.FilePath;
                var prep = Task.Run(() =>
                {
                    float[] d = ImagePreprocessor.Prepare(itemPath, size, out _, out _);
                    return d;
                }, ct);
                pending.Enqueue((item, prep));
            }

            while (pending.Count > 0)
            {
                var entry = pending.Dequeue();
                await ProcessAsync(entry).ConfigureAwait(false);
            }
        }
        finally
        {
            foreach (var entry in pending)
            {
                if (entry.Prep.Exception != null) { /* 已捕获 */ }
            }
            Report();
        }
    }

    public void Dispose()
    {
        _tagger?.Dispose();
        _tagger = null;
        _taggerKey = null;
    }
}
