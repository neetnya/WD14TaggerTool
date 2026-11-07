using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WD14Tagger.Core;

/// <summary>无界面批量模式：WD14Tagger.exe --cli &lt;目录或图片...&gt; [参数]</summary>
public static class CliRunner
{
    public static int Run(string[] args)
    {
        var paths = new List<string>();
        string? modelPath = null;
        double threshold = 0.35, charThreshold = 0.85;
        int threads = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
        string? csvOut = null;
        string? dumpPath = null;
        bool underscore = true, trailing = false, sortByScore = false, escapeParens = false, quiet = false;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"参数 {a} 缺少值");
            switch (a)
            {
                case "--cli": break;
                case "--model": modelPath = Next(); break;
                case "--threshold": threshold = double.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
                case "--char-threshold": charThreshold = double.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
                case "--threads": threads = int.Parse(Next()); break;
                case "--csv": csvOut = Next(); break;
                case "--dump": dumpPath = Next(); break;
                case "--underscore": underscore = true; break;
                case "--no-underscore": underscore = false; break;
                case "--trailing": trailing = true; break;
                case "--no-trailing": trailing = false; break;
                case "--sort": sortByScore = true; break;
                case "--escape-parens": escapeParens = true; break;
                case "--no-escape-parens": escapeParens = false; break;
                case "--quiet": quiet = true; break;
                default: paths.Add(a); break;
            }
        }

        if (paths.Count == 0)
        {
            Console.Error.WriteLine("用法: WD14Tagger.exe --cli <目录或图片...> [--model <onnx>] [--threshold 0.35] [--char-threshold 0.85] [--threads 8] [--csv out.csv] [--sort] [--no-underscore] [--no-trailing] [--escape-parens] [--quiet]");
            return 2;
        }

        var appPaths = new AppPaths();
        appPaths.EnsureCreated();

        // 选模型：固定读软件目录下的 models\
        if (modelPath == null)
        {
            var models = ModelCatalog.Scan(appPaths.ModelsDir);
            if (models.Count == 0)
            {
                Console.Error.WriteLine($"未在 {appPaths.ModelsDir} 找到 onnx+csv 模型");
                return 3;
            }
            modelPath = models[0].OnnxPath;
        }

        string csv = Path.ChangeExtension(modelPath, ".csv");
        if (!File.Exists(csv))
        {
            Console.Error.WriteLine($"缺少标签 csv: {csv}");
            return 3;
        }

        var vocab = TagVocabulary.Load(csv);
        var files = CollectImages(paths, new AppConfig().Extensions);
        if (files.Count == 0)
        {
            Console.Error.WriteLine("没有找到图片文件");
            return 4;
        }

        if (dumpPath != null)
        {
            DumpScores(modelPath, csv, files, threads, dumpPath, vocab);
            return 0;
        }

        var options = new TaggerOptions
        {
            Threshold = threshold,
            CharacterThreshold = charThreshold,
            ReplaceUnderscore = underscore,
            TrailingComma = trailing,
            SortByScore = sortByScore,
            EscapeParens = escapeParens,
            OutputRating = true
        };

        Console.WriteLine($"模型: {Path.GetFileName(modelPath)}  线程: {threads}  图片: {files.Count}  阈值: {threshold}/{charThreshold}");

        var writer = new ResultWriter(csvOut ?? appPaths.CsvPath);

        var sw = Stopwatch.StartNew();
        var items = files.Select(f => new Models.ImageItem(f, "")).ToList();
        try
        {
            using var tagger = new OnnxTagger(modelPath, threads);
            tagger.Warmup();

            var engine = new BatchEngine();
            var progress = new InlineProgress<Models.ItemUpdate>(u =>
            {
                var item = u.Item;
                item.State = u.State;
                item.ElapsedMs = u.ElapsedMs;
                if (u.State == Models.ItemState.Done)
                {
                    if (!quiet) Console.WriteLine($"{item.RelativePath}\t{u.TagCount}\t{u.Tags}");
                }
                else if (u.State == Models.ItemState.Failed)
                {
                    Console.WriteLine($"{item.RelativePath}\tERROR\t{u.Error}");
                }
            });

            // 引擎内部是 async；CLI 直接同步等待完成
            Task.Run(() => engine.RunAsync(items, vocab, options, writer, tagger,
                new PauseController(), progress, null, CancellationToken.None)).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"失败: {ex}");
            return 1;
        }

        sw.Stop();
        int ok = items.Count(i => i.State == Models.ItemState.Done);
        int fail = items.Count(i => i.State == Models.ItemState.Failed);
        long inferMs = items.Where(i => i.State == Models.ItemState.Done).Sum(i => i.ElapsedMs);
        Console.WriteLine($"完成: 成功 {ok}，失败 {fail}，总用时 {sw.Elapsed.TotalSeconds:F1}s" +
                          $"（推理 {inferMs / Math.Max(1, ok)} ms/张，含解码约 {sw.Elapsed.TotalMilliseconds / Math.Max(1, ok + fail):F0} ms/张）");
        return fail == 0 ? 0 : 1;
    }

    /// <summary>把每张图所有分数>0.01的标签按名字导出成 JSON，用于和 python 参考实现逐分数比对。</summary>
    private static void DumpScores(string modelPath, string csvPath, List<string> files, int threads, string dumpPath, TagVocabulary vocab)
    {
        using var tagger = new OnnxTagger(modelPath, threads);
        tagger.Warmup();

        var sb = new StringBuilder(1 << 20);
        sb.Append("{\n");
        for (int i = 0; i < files.Count; i++)
        {
            string file = files[i];
            var sw = Stopwatch.StartNew();
            float[] data = ImagePreprocessor.Prepare(file, tagger.InputSize, out int sw0, out int sh0);
            float[] probs = tagger.Run(data);
            sw.Stop();

            sb.Append("  \"").Append(Path.GetFileName(file).Replace("\\", "\\\\")).Append("\": {");
            bool first = true;
            for (int t = 0; t < probs.Length && t < vocab.Entries.Count; t++)
            {
                if (probs[t] <= 0.01f) continue;
                if (!first) sb.Append(',');
                first = false;
                sb.Append('"').Append(vocab.Entries[t].Name.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append("\":")
                  .Append(((double)probs[t]).ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            }
            sb.Append('}');
            if (i < files.Count - 1) sb.Append(',');
            sb.Append('\n');

            Console.Error.WriteLine($"{Path.GetFileName(file)}  {sw0}x{sh0}  {sw.ElapsedMilliseconds}ms");
        }
        sb.Append("}\n");

        File.WriteAllText(dumpPath, sb.ToString(), new UTF8Encoding(false));
        Console.WriteLine($"分数已导出: {dumpPath}");
    }

    internal static List<string> CollectImages(IEnumerable<string> paths, string[] extensions)
    {
        var set = new HashSet<string>(extensions.Select(e => "." + e.TrimStart('.').ToLowerInvariant()), StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddFile(string file)
        {
            string ext = Path.GetExtension(file);
            if (!set.Contains(ext)) return;
            if (seen.Add(file)) result.Add(file);
        }

        foreach (var p in paths)
        {
            if (File.Exists(p)) { AddFile(Path.GetFullPath(p)); continue; }
            if (!Directory.Exists(p)) continue;
            foreach (var f in Directory.EnumerateFiles(p, "*.*", SearchOption.AllDirectories))
                AddFile(f);
        }

        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    /// <summary>同步回调的 IProgress，避免 Progress&lt;T&gt; 把回调投递到线程池导致统计晚于结束。</summary>
    private sealed class InlineProgress<T> : IProgress<T>
    {
        private readonly Action<T> _action;
        public InlineProgress(Action<T> action) => _action = action;
        public void Report(T value) => _action(value);
    }
}
