namespace WD14Tagger.Core;

/// <summary>
/// 反推与「复制格式」参数。
/// 阈值/角色阈值/评级影响反推与 results.csv；
/// 按分数排序 / 下划线转空格 / 末尾加逗号 只在「复制到剪贴板」时生效。
/// </summary>
public sealed class TaggerOptions
{
    public double Threshold { get; set; } = 0.35;
    public double CharacterThreshold { get; set; } = 0.85;
    public bool OutputRating { get; set; } = true;

    public bool ReplaceUnderscore { get; set; } = true;
    public bool TrailingComma { get; set; } = true;
    public bool SortByScore { get; set; }

    public TaggerOptions Clone() => new()
    {
        Threshold = Threshold,
        CharacterThreshold = CharacterThreshold,
        OutputRating = OutputRating,
        ReplaceUnderscore = ReplaceUnderscore,
        TrailingComma = TrailingComma,
        SortByScore = SortByScore
    };
}
