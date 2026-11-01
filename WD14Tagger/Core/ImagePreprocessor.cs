using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WD14Tagger.Core;

/// <summary>
/// 图片解码 + 预处理：完全对齐 SmilingWolf WD 系列模型的输入约定
/// decode(WIC, 带 EXIF 方向) -> 等比 Lanczos3 缩放 -> 白底居中补边 -> BGR float32 (NHWC, 0~255)
/// </summary>
public static class ImagePreprocessor
{
    /// <summary>把图片文件预处理成模型可以直接喂的 float 数组。</summary>
    public static float[] Prepare(string path, int size, out int sourceWidth, out int sourceHeight)
    {
        BitmapSource frame = LoadBitmap(path, out sourceWidth, out sourceHeight);

        byte[] bgra = new byte[sourceWidth * sourceHeight * 4];
        frame.CopyPixels(bgra, sourceWidth * 4, 0);

        double ratio = (double)size / Math.Max(sourceWidth, sourceHeight);
        int newW = Math.Max(1, (int)(sourceWidth * ratio));
        int newH = Math.Max(1, (int)(sourceHeight * ratio));

        int canvasW = size, canvasH = size;
        byte[] canvas = new byte[canvasW * canvasH * 3];
        Array.Fill(canvas, (byte)255); // 白底

        int offsetX = (size - newW) / 2;
        int offsetY = (size - newH) / 2;

        Lanczos.ResizeBgraToBgrCanvas(bgra, sourceWidth, sourceHeight, newW, newH,
                                      canvas, canvasW, canvasH, offsetX, offsetY);

        var data = new float[canvas.Length];
        for (int i = 0; i < canvas.Length; i++) data[i] = canvas[i];
        return data;
    }

    /// <summary>只解码（用于界面预览），可选缩小尺寸。</summary>
    public static BitmapSource Decode(string path, int decodePixelWidth = 0)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        if (decodePixelWidth > 0) bmp.DecodePixelWidth = decodePixelWidth;
        bmp.UriSource = new Uri(path);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    private static BitmapSource LoadBitmap(string path, out int width, out int height)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        var decoder = BitmapDecoder.Create(fs,
            BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
            BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0) throw new InvalidOperationException("图片格式无法识别（无可用帧）");

        BitmapSource frame = decoder.Frames[0];
        frame = ApplyExifOrientation(frame, decoder.Metadata as BitmapMetadata);

        BitmapSource bgra = frame;
        if (bgra.Format != PixelFormats.Bgra32)
        {
            var converted = new FormatConvertedBitmap();
            converted.BeginInit();
            converted.Source = frame;
            converted.DestinationFormat = PixelFormats.Bgra32;
            converted.EndInit();
            converted.Freeze();
            bgra = converted;
        }

        width = bgra.PixelWidth;
        height = bgra.PixelHeight;
        if (width <= 0 || height <= 0) throw new InvalidOperationException("图片尺寸无效");
        return bgra;
    }

    private static BitmapSource ApplyExifOrientation(BitmapSource src, BitmapMetadata? meta)
    {
        int orientation = 1;
        try
        {
            if (meta != null && meta.ContainsQuery("/app1/ifd/{ushort=274}"))
            {
                var value = meta.GetQuery("/app1/ifd/{ushort=274}");
                orientation = value switch
                {
                    ushort u => u,
                    short s => s,
                    int i => i,
                    _ => 1
                };
            }
        }
        catch
        {
            orientation = 1;
        }

        if (orientation is <= 1 or > 8) return src;

        Transform transform = orientation switch
        {
            2 => new ScaleTransform(-1, 1),
            3 => new RotateTransform(180),
            4 => new ScaleTransform(1, -1),
            5 => new TransformGroup { Children = { new ScaleTransform(-1, 1), new RotateTransform(270) } },
            6 => new RotateTransform(90),
            7 => new TransformGroup { Children = { new ScaleTransform(-1, 1), new RotateTransform(90) } },
            8 => new RotateTransform(270),
            _ => Transform.Identity
        };

        var tb = new TransformedBitmap(src, transform);
        tb.Freeze();
        return tb;
    }
}
