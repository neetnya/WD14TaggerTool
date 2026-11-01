using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WD14Tagger.Core;

public sealed class ModelInfo
{
    public string Name { get; init; } = "";
    public string OnnxPath { get; init; } = "";
    public string CsvPath { get; init; } = "";
    public long OnnxSize { get; init; }

    public string DisplayName => $"{Name}  ({OnnxSize / 1024 / 1024} MB)";
    public bool IsValid => File.Exists(OnnxPath) && File.Exists(CsvPath);
    public override string ToString() => DisplayName;
}

public static class ModelCatalog
{
    /// <summary>扫描目录里所有「onnx + 同名 csv」成对存在的模型。</summary>
    public static List<ModelInfo> Scan(string modelsDir)
    {
        var list = new List<ModelInfo>();
        if (string.IsNullOrWhiteSpace(modelsDir) || !Directory.Exists(modelsDir)) return list;

        foreach (var onnx in Directory.EnumerateFiles(modelsDir, "*.onnx", SearchOption.TopDirectoryOnly))
        {
            string csv = Path.ChangeExtension(onnx, ".csv");
            if (!File.Exists(csv)) continue;
            var fi = new FileInfo(onnx);
            list.Add(new ModelInfo
            {
                Name = Path.GetFileNameWithoutExtension(onnx),
                OnnxPath = onnx,
                CsvPath = csv,
                OnnxSize = fi.Length
            });
        }

        return list.OrderByDescending(m => m.OnnxSize).ToList();
    }

    /// <summary>猜测系统里可能已有的模型来源目录（ComfyUI 插件、常见 ComfyUI 安装位置）。</summary>
    public static List<string> GuessModelSources(string appDir)
    {
        var candidates = new List<string>();
        void Add(string? p)
        {
            if (string.IsNullOrWhiteSpace(p)) return;
            try
            {
                string full = Path.GetFullPath(p);
                if (Directory.Exists(full) && !candidates.Contains(full, StringComparer.OrdinalIgnoreCase))
                    candidates.Add(full);
            }
            catch
            {
                // ignore
            }
        }

        void AddUnder(string? root)
        {
            if (string.IsNullOrWhiteSpace(root)) return;
            Add(root);                                          // 目录本身就是模型目录
            Add(Path.Combine(root, "models"));                  // 软件目录\models
            Add(Path.Combine(root, "ComfyUI-WD14-Tagger", "models"));
            Add(Path.Combine(root, "WD14-Tagger", "models"));
            Add(Path.Combine(root, "ComfyUI", "custom_nodes", "ComfyUI-WD14-Tagger", "models"));
            Add(Path.Combine(root, "ComfyUI", "models", "wd14"));
        }

        // 1) 从程序目录往上找 6 层（把 exe 单独拿走、或者放在插件目录里的情况都能覆盖）
        var dir = new DirectoryInfo(Path.GetFullPath(appDir));
        for (int depth = 0; depth <= 6 && dir != null; depth++, dir = dir.Parent)
            AddUnder(dir.FullName);

        // 2) 桌面 / 文档 / 下载 / 用户目录下的常见位置（只试固定组合，不做递归，避免卡）
        foreach (var special in new[]
                 {
                     Environment.SpecialFolder.Desktop,
                     Environment.SpecialFolder.MyDocuments,
                     Environment.SpecialFolder.UserProfile
                 })
        {
            string root = Environment.GetFolderPath(special);
            AddUnder(root);
            Add(Path.Combine(root, "Downloads", "ComfyUI-WD14-Tagger", "models"));
            Add(Path.Combine(root, "Downloads", "WD14-Tagger", "models"));
        }

        return candidates.Where(d => Scan(d).Count > 0).ToList();
    }
}
