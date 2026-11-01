using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;

namespace WD14Tagger.Models;

public sealed class TagChip
{
    public string Name { get; init; } = "";
    public float Score { get; init; }
    public bool IsCharacter { get; init; }

    public string Display => Name.Replace('_', ' ');
    public string ScoreText => Score.ToString("0.###");
    public Brush ChipBackground => IsCharacter
        ? new SolidColorBrush(Color.FromRgb(0xFF, 0xF0, 0xE2))
        : new SolidColorBrush(Color.FromRgb(0xEC, 0xF3, 0xFF));
    public Brush ChipBorder => IsCharacter
        ? new SolidColorBrush(Color.FromRgb(0xF6, 0xD3, 0xAE))
        : new SolidColorBrush(Color.FromRgb(0xD3, 0xE2, 0xFB));
    public Brush ScoreBrush => IsCharacter
        ? new SolidColorBrush(Color.FromRgb(0xB0, 0x74, 0x2A))
        : new SolidColorBrush(Color.FromRgb(0x5B, 0x7A, 0xA8));
}
