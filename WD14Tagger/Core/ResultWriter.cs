using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace WD14Tagger.Core;

/// <summary>结果落盘：只在软件目录的 data\results.csv 里追加一行汇总。</summary>
public sealed class ResultWriter
{
    private readonly object _sync = new();
    private readonly string _csvPath;
    private bool _csvReady;

    public ResultWriter(string csvPath) => _csvPath = csvPath;

    public void Write(string imagePath, TagResult result, string modelName, TaggerOptions options, long elapsedMs)
    {
        lock (_sync)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_csvPath)!);
            using var writer = new StreamWriter(_csvPath, append: true, new UTF8Encoding(true));
            if (!_csvReady)
            {
                bool needHeader = !File.Exists(_csvPath) || new FileInfo(_csvPath).Length == 0;
                if (needHeader)
                    writer.WriteLine("时间,文件名,目录,模型,阈值,角色阈值,标签数,评级,评级分,标签,耗时ms");
                _csvReady = true;
            }

            var sb = new StringBuilder(512);
            sb.Append(Csv(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))).Append(',');
            sb.Append(Csv(Path.GetFileName(imagePath))).Append(',');
            sb.Append(Csv(Path.GetDirectoryName(imagePath) ?? "")).Append(',');
            sb.Append(Csv(modelName)).Append(',');
            sb.Append(options.Threshold.ToString("0.00", CultureInfo.InvariantCulture)).Append(',');
            sb.Append(options.CharacterThreshold.ToString("0.00", CultureInfo.InvariantCulture)).Append(',');
            sb.Append(result.Tags.Count.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append(Csv(options.OutputRating ? result.Rating : "")).Append(',');
            sb.Append(options.OutputRating ? result.RatingScore.ToString("0.0000", CultureInfo.InvariantCulture) : "").Append(',');
            sb.Append(Csv(result.Text)).Append(',');
            sb.Append(elapsedMs.ToString(CultureInfo.InvariantCulture));
            writer.WriteLine(sb.ToString());
        }
    }

    private static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        bool needQuote = value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0;
        if (!needQuote) return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
