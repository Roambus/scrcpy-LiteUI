using SkiaSharp;

namespace Kuaitou.Core.Apps;

/// <summary>
/// 图标入库时的图片处理：解码 → 居中裁方 → 缩到 256×256 → 合成白底 → 校验 → 编码 PNG。
/// 对应 legacy/kuaitou/apps.py 的 <c>_icon_bytes_to_webp</c>（落盘格式从 WebP 改成 PNG）。
///
/// 为什么非要用 Skia 而不是 WIC：WIC 的 WebP 解码不可靠（WebP 是 Microsoft Store 扩展
/// 而非系统内置，用户没装就解不出来），且 WIC 解 WebP 会掉 alpha。所以入库这一步固定用
/// Skia，运行期 WPF 读到的全是原生 PNG，WebP 只在入库时出现一次。
///
/// 纯函数、不碰文件与网络，直接单测盖住。
/// </summary>
public static class IconImage
{
    /// <summary>入库图标的边长。</summary>
    public const int Size = 256;

    /// <summary>素材库维护脚本出小图时用的边长（旧版 128）。</summary>
    public const int SmallSize = 128;

    /// <summary>解码后短边小于这个值就认为图太糊，直接判定不合格。</summary>
    private const int MinSourceSide = 96;

    /// <summary>单色校验用的缩略图边长与标准差下限（对应旧版 ImageStat 那一步）。</summary>
    private const int ProbeSize = 24;
    private const double MinStdDev = 6.0;

    private static readonly SKSamplingOptions Sampling = new(SKCubicResampler.Mitchell);

    /// <summary>
    /// 把任意格式的图标字节统一成 <paramref name="size"/>×<paramref name="size"/> 的 PNG。
    /// 不合格（解不出来 / 太小 / 近单色）返回 null，调用方据此跳过这一个图标。
    /// </summary>
    public static byte[]? ToPng(byte[]? raw, int size = Size)
    {
        if (raw is null || raw.Length == 0 || size <= 0)
        {
            return null;
        }

        using SKBitmap? decoded = SKBitmap.Decode(raw);
        if (decoded is null || Math.Min(decoded.Width, decoded.Height) < MinSourceSide)
        {
            return null;
        }

        using SKBitmap squared = CropToSquare(decoded);
        using SKBitmap? scaled = squared.Resize(
            new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul), Sampling);
        if (scaled is null)
        {
            return null;
        }

        // 一律合成到白底：Alpha 全满时结果与直接取 RGB 一致，带透明边时正好补成白底
        // （旧版是「alpha 最小值 < 250 才贴白底」，两者的可见结果一样）。
        using SKBitmap flattened = FlattenOnWhite(scaled, size);
        if (IsTooPlain(flattened))
        {
            return null;
        }

        using SKImage image = SKImage.FromBitmap(flattened);
        using SKData? data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data?.ToArray();
    }

    /// <summary>居中裁成正方形。</summary>
    private static SKBitmap CropToSquare(SKBitmap source)
    {
        int side = Math.Min(source.Width, source.Height);
        int left = (source.Width - side) / 2;
        int top = (source.Height - side) / 2;

        var target = new SKBitmap(new SKImageInfo(side, side, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(target);
        using var paint = new SKPaint();
        canvas.DrawBitmap(
            source,
            new SKRect(left, top, left + side, top + side),
            new SKRect(0, 0, side, side),
            paint);
        return target;
    }

    /// <summary>合成到不透明白底，顺带把 alpha 通道去掉。</summary>
    private static SKBitmap FlattenOnWhite(SKBitmap source, int size)
    {
        var target = new SKBitmap(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Opaque));
        using var canvas = new SKCanvas(target);
        canvas.Clear(SKColors.White);
        using var paint = new SKPaint();
        canvas.DrawBitmap(
            source,
            new SKRect(0, 0, source.Width, source.Height),
            new SKRect(0, 0, size, size),
            paint);
        return target;
    }

    /// <summary>
    /// 近单色判定：缩到 24×24 后算各通道标准差，平均低于 6 就当成纯色底图（在线源偶尔
    /// 会返回「敬请期待」那类占位图），不予采用。
    /// </summary>
    private static bool IsTooPlain(SKBitmap bitmap)
    {
        using SKBitmap? probe = bitmap.Resize(
            new SKImageInfo(ProbeSize, ProbeSize, SKColorType.Rgba8888, SKAlphaType.Opaque), Sampling);
        if (probe is null)
        {
            return false;
        }

        int count = ProbeSize * ProbeSize;
        double sumR = 0, sumG = 0, sumB = 0, sumR2 = 0, sumG2 = 0, sumB2 = 0;
        for (int y = 0; y < ProbeSize; y++)
        {
            for (int x = 0; x < ProbeSize; x++)
            {
                SKColor c = probe.GetPixel(x, y);
                sumR += c.Red;
                sumG += c.Green;
                sumB += c.Blue;
                sumR2 += (double)c.Red * c.Red;
                sumG2 += (double)c.Green * c.Green;
                sumB2 += (double)c.Blue * c.Blue;
            }
        }

        double average = (StdDev(sumR, sumR2, count) + StdDev(sumG, sumG2, count) + StdDev(sumB, sumB2, count)) / 3.0;
        return average < MinStdDev;
    }

    private static double StdDev(double sum, double sumSquares, int count)
    {
        double mean = sum / count;
        double variance = sumSquares / count - mean * mean;
        return variance <= 0 ? 0 : Math.Sqrt(variance);
    }
}
