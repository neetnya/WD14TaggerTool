using System;
using System.Threading.Tasks;

namespace WD14Tagger.Core;

/// <summary>
/// 高质量重采样：抗锯齿 Lanczos3（与 PIL 的 Image.LANCZOS 行为一致），
/// 大图先做整数盒式预缩小（area average）以保证速度，再做 Lanczos 精缩。
/// 输出直接写入 BGR 画布（模型输入需要 BGR 顺序）。
/// </summary>
internal static class Lanczos
{
    private const double Support = 3.0; // Lanczos3

    /// <summary>缩小超过这个倍率才做盒式预缩小；设为 0 表示总走单程抗锯齿 Lanczos（与 PIL 更一致）。</summary>
    private const double BoxPreshrinkMinFactor = 0.0;

    public static void ResizeBgraToBgrCanvas(
        byte[] srcBgra, int sw, int sh, int dw, int dh,
        byte[] dstBgr, int dstW, int dstH, int offsetX, int offsetY)
    {
        // 1) 大倍率缩小：先用盒式平均砍到目标尺寸的 2 倍以内，再 Lanczos，速度与质量兼顾
        //    （BoxPreshrinkMinFactor<=0 表示完全不预缩小，单程抗锯齿 Lanczos，最贴近 PIL）
        byte[] workBgra = srcBgra;
        int workW = sw, workH = sh;

        double factor = Math.Min((double)sw / dw, (double)sh / dh);
        if (BoxPreshrinkMinFactor > 0 && factor >= BoxPreshrinkMinFactor)
        {
            int step = (int)Math.Floor(factor / 2.0);
            if (step >= 2)
            {
                int tw = Math.Max(dw, sw / step);
                int th = Math.Max(dh, sh / step);
                if (tw != sw || th != sh)
                {
                    workBgra = BoxAverageBgra(srcBgra, sw, sh, tw, th);
                    workW = tw;
                    workH = th;
                }
            }
        }

        // 2) 水平方向：BGR 三通道 float 中间结果
        var hWeights = BuildWeights(workW, dw);
        var temp = new float[dw * workH * 3];
        void HorizontalRow(int y)
        {
            int srcRow = y * workW * 4;
            for (int x = 0; x < dw; x++)
            {
                ref readonly var w = ref hWeights[x];
                double b = 0, g = 0, r = 0;
                int start = srcRow + w.Start * 4;
                for (int k = 0; k < w.Weights.Length; k++)
                {
                    double ww = w.Weights[k];
                    int p = start + k * 4;
                    b += ww * workBgra[p];
                    g += ww * workBgra[p + 1];
                    r += ww * workBgra[p + 2];
                }
                int o = (y * dw + x) * 3;
                temp[o] = (float)b;
                temp[o + 1] = (float)g;
                temp[o + 2] = (float)r;
            }
        }

        if ((long)dw * workH > 200_000)
            Parallel.For(0, workH, HorizontalRow);
        else
            for (int y = 0; y < workH; y++) HorizontalRow(y);

        // 3) 垂直方向 + 写入画布
        var vWeights = BuildWeights(workH, dh);
        void VerticalRow(int y)
        {
            ref readonly var w = ref vWeights[y];
            int dstRow = (offsetY + y) * dstW + offsetX;
            for (int x = 0; x < dw; x++)
            {
                double b = 0, g = 0, r = 0;
                for (int k = 0; k < w.Weights.Length; k++)
                {
                    double ww = w.Weights[k];
                    int p = ((w.Start + k) * dw + x) * 3;
                    b += ww * temp[p];
                    g += ww * temp[p + 1];
                    r += ww * temp[p + 2];
                }
                int o = (dstRow + x) * 3;
                dstBgr[o] = Clamp(b);
                dstBgr[o + 1] = Clamp(g);
                dstBgr[o + 2] = Clamp(r);
            }
        }

        if ((long)dw * dh > 200_000)
            Parallel.For(0, dh, VerticalRow);
        else
            for (int y = 0; y < dh; y++) VerticalRow(y);
    }

    private static byte Clamp(double v)
    {
        int i = (int)Math.Round(v, MidpointRounding.AwayFromZero);
        if (i < 0) return 0;
        if (i > 255) return 255;
        return (byte)i;
    }

    private static (int Start, double[] Weights)[] BuildWeights(int srcLen, int dstLen)
    {
        var result = new (int, double[])[dstLen];
        double scale = (double)srcLen / dstLen;
        double filterScale = scale > 1.0 ? scale : 1.0;
        double support = Support * filterScale;

        for (int d = 0; d < dstLen; d++)
        {
            double center = (d + 0.5) * scale - 0.5;
            int min = (int)Math.Floor(center - support - 1);
            int max = (int)Math.Ceiling(center + support + 1);
            if (min < 0) min = 0;
            if (max > srcLen - 1) max = srcLen - 1;

            var weights = new double[max - min + 1];
            double sum = 0;
            for (int i = min; i <= max; i++)
            {
                double v = Kernel((i - center) / filterScale);
                weights[i - min] = v;
                sum += v;
            }
            if (sum == 0)
            {
                int nearest = Math.Clamp((int)Math.Round(center), 0, srcLen - 1);
                min = nearest;
                weights = new[] { 1.0 };
            }
            else
            {
                for (int i = 0; i < weights.Length; i++) weights[i] /= sum;
            }
            result[d] = (min, weights);
        }
        return result;
    }

    private static double Kernel(double x) => Sinc(x) * Sinc(x / Support);

    private static double Sinc(double x)
    {
        if (x == 0) return 1.0;
        double px = Math.PI * x;
        return Math.Sin(px) / px;
    }

    private static byte[] BoxAverageBgra(byte[] src, int sw, int sh, int dw, int dh)
    {
        var dst = new byte[dw * dh * 4];
        // 整数块平均（不足一块按实际范围平均），保持颜色精度
        for (int y = 0; y < dh; y++)
        {
            int y0 = (int)((long)y * sh / dh);
            int y1 = Math.Max(y0 + 1, (int)((long)(y + 1) * sh / dh));
            for (int x = 0; x < dw; x++)
            {
                int x0 = (int)((long)x * sw / dw);
                int x1 = Math.Max(x0 + 1, (int)((long)(x + 1) * sw / dw));
                long sb = 0, sg = 0, sr = 0, sa = 0;
                int n = 0;
                for (int yy = y0; yy < y1; yy++)
                {
                    int row = yy * sw * 4;
                    for (int xx = x0; xx < x1; xx++)
                    {
                        int p = row + xx * 4;
                        sb += src[p];
                        sg += src[p + 1];
                        sr += src[p + 2];
                        sa += src[p + 3];
                        n++;
                    }
                }
                int o = (y * dw + x) * 4;
                dst[o] = (byte)(sb / n);
                dst[o + 1] = (byte)(sg / n);
                dst[o + 2] = (byte)(sr / n);
                dst[o + 3] = (byte)(sa / n);
            }
        }
        return dst;
    }
}
