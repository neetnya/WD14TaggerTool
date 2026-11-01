using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace WD14Tagger.Core;

public sealed record TagEntry(string Name, int Category, int Count);

public sealed record TagHit(string Name, float Score, TagCategory Category);

public enum TagCategory
{
    General = 0,
    Character = 4,
    Rating = 9
}

public sealed class TagResult
{
    /// <summary>存档用文本：保留下划线、按词表顺序、无尾部逗号（写进 results.csv）。</summary>
    public string Text { get; init; } = "";

    /// <summary>复制到剪贴板用的文本：受「按分数排序 / 下划线转空格 / 末尾加逗号」影响。</summary>
    public string CopyText { get; init; } = "";

    public IReadOnlyList<TagHit> Tags { get; init; } = Array.Empty<TagHit>();
    public string Rating { get; init; } = "";
    public float RatingScore { get; init; }

    public static readonly TagResult Empty = new();
}

/// <summary>
/// WD14 标签词表（selected_tags.csv），分类索引与原版 python 插件完全一致：
/// 前几行为 rating(csv 中 category=9)，其后为 general(category=0)，再后为 character(category=4)。
/// </summary>
public sealed class TagVocabulary
{
    public string ModelName { get; }
    public IReadOnlyList<TagEntry> Entries { get; }
    public int RatingCount { get; }
    public int GeneralStart { get; }
    public int CharacterStart { get; }
    public int Count => Entries.Count;

    private TagVocabulary(string modelName, List<TagEntry> entries)
    {
        ModelName = modelName;
        Entries = entries;
        RatingCount = 0;
        while (RatingCount < entries.Count && entries[RatingCount].Category == 9) RatingCount++;

        GeneralStart = -1;
        CharacterStart = entries.Count;
        for (int i = 0; i < entries.Count; i++)
        {
            if (GeneralStart < 0 && entries[i].Category == 0) GeneralStart = i;
            if (entries[i].Category == 4) { CharacterStart = i; break; }
        }
        if (GeneralStart < 0) GeneralStart = RatingCount;
        if (CharacterStart < GeneralStart) CharacterStart = entries.Count;
    }

    public static TagVocabulary Load(string csvPath)
    {
        var entries = new List<TagEntry>(12000);
        using var reader = new StreamReader(csvPath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        string? header = reader.ReadLine(); // tag_id,name,category,count
        if (header == null) throw new InvalidDataException("标签 csv 为空");

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.Length == 0) continue;
            // 格式 tag_id,name,category,count；name 理论上不含逗号，但按「首逗号 + 末两个逗号」切分更稳
            int c1 = line.IndexOf(',');
            if (c1 <= 0) continue;
            int cLast = line.LastIndexOf(',');
            if (cLast <= c1) continue;
            int cPrev = line.LastIndexOf(',', cLast - 1);
            if (cPrev <= c1) continue;

            string name = line.Substring(c1 + 1, cPrev - c1 - 1);
            string catText = line.Substring(cPrev + 1, cLast - cPrev - 1);
            string countText = line.Substring(cLast + 1);

            if (!int.TryParse(catText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int cat)) continue;
            int.TryParse(countText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count);
            entries.Add(new TagEntry(name, cat, count));
        }

        if (entries.Count == 0) throw new InvalidDataException("标签 csv 没有有效行");
        string modelName = Path.GetFileNameWithoutExtension(csvPath);
        return new TagVocabulary(modelName, entries);
    }

    /// <summary>按原版插件逻辑取得标签（角色标签优先，再通用标签）。</summary>
    public TagResult Evaluate(ReadOnlySpan<float> probs, TaggerOptions options)
    {
        if (probs.Length < Entries.Count)
            throw new InvalidOperationException($"模型输出维度({probs.Length})与词表({Entries.Count})不匹配");

        var hits = new List<TagHit>(64);

        for (int i = CharacterStart; i < Entries.Count; i++)
        {
            float p = probs[i];
            if (p > options.CharacterThreshold)
                hits.Add(new TagHit(Entries[i].Name, p, TagCategory.Character));
        }

        int generalEnd = Math.Min(CharacterStart, Entries.Count);
        for (int i = GeneralStart; i < generalEnd; i++)
        {
            float p = probs[i];
            if (p > options.Threshold)
                hits.Add(new TagHit(Entries[i].Name, p, TagCategory.General));
        }

        string rating = "";
        float ratingScore = 0;
        for (int i = 0; i < RatingCount && i < probs.Length; i++)
        {
            if (probs[i] > ratingScore)
            {
                ratingScore = probs[i];
                rating = Entries[i].Name;
            }
        }

        return new TagResult
        {
            Text = string.Join(", ", hits.Select(h => h.Name)),
            CopyText = FormatCopy(hits, options),
            Tags = hits,
            Rating = rating,
            RatingScore = ratingScore
        };
    }

    /// <summary>复制用格式：可选按分数排序、下划线转空格、末尾加逗号；括号原样输出，不做转义。</summary>
    public static string FormatCopy(IReadOnlyList<TagHit> hits, TaggerOptions options)
    {
        if (hits.Count == 0) return "";

        IEnumerable<TagHit> seq = options.SortByScore ? hits.OrderByDescending(h => h.Score) : hits;
        var parts = new List<string>(hits.Count);
        foreach (var hit in seq)
        {
            parts.Add(options.ReplaceUnderscore ? hit.Name.Replace('_', ' ') : hit.Name);
        }

        string text = string.Join(", ", parts);
        return options.TrailingComma ? text + ", " : text;
    }
}
