namespace OneBox.Plugins.Screenshot.Core;

/// <summary>
/// 从 BGRA 像素里裁出一块区域并做双线性放大。纯像素运算，不依赖 WPF。
///
/// 为什么不用 WPF 的 CroppedBitmap / TransformedBitmap：它们是带线程亲和性的
/// imaging 对象。识别过程里 await 之后会切到线程池，此时再对"仍依赖解码器的位图"
/// 做裁剪或冻结，会抛"调用线程无法访问此对象，因为另一个线程拥有该对象"（实测）。
/// 直接在字节数组上算，既没有线程问题，也能脱离 WPF 单元测试。
///
/// 插值必须用双线性：实测最近邻会把"订单编号"识别成"订解号"，
/// 双线性/双三次则明显更好（两者效果相当，双线性实现更简单）。
/// </summary>
public static class OcrImageScaler
{
    /// <summary>
    /// 裁出 <paramref name="region"/> 并按整数倍放大。
    /// 输入是紧凑的 BGRA 缓冲（stride = width × 4）。
    /// </summary>
    /// <param name="factor">放大倍率，小于 1 时按 1 处理。</param>
    public static (byte[] Pixels, int Width, int Height) CropAndScale(
        byte[] bgra, int width, int height, OcrRect region, int factor)
    {
        if (bgra is null || width <= 0 || height <= 0) return ([], 0, 0);
        if (factor < 1) factor = 1;

        var x0 = (int)Math.Round(region.X);
        var y0 = (int)Math.Round(region.Y);
        x0 = Math.Clamp(x0, 0, width - 1);
        y0 = Math.Clamp(y0, 0, height - 1);

        var cropWidth = (int)Math.Round(region.Width);
        var cropHeight = (int)Math.Round(region.Height);
        cropWidth = Math.Clamp(cropWidth, 1, width - x0);
        cropHeight = Math.Clamp(cropHeight, 1, height - y0);

        var outWidth = cropWidth * factor;
        var outHeight = cropHeight * factor;
        var output = new byte[(long)outWidth * outHeight * 4];

        // 逆映射：输出像素中心对应回裁剪区域内的浮点坐标
        var inverse = 1.0 / factor;
        for (int y = 0; y < outHeight; y++)
        {
            var sy = (y + 0.5) * inverse - 0.5;
            if (sy < 0) sy = 0;
            var y1 = (int)sy;
            if (y1 > cropHeight - 1) y1 = cropHeight - 1;
            var y2 = Math.Min(y1 + 1, cropHeight - 1);
            var fy = sy - y1;

            var rowOffset = y * outWidth * 4;
            for (int x = 0; x < outWidth; x++)
            {
                var sx = (x + 0.5) * inverse - 0.5;
                if (sx < 0) sx = 0;
                var x1 = (int)sx;
                if (x1 > cropWidth - 1) x1 = cropWidth - 1;
                var x2 = Math.Min(x1 + 1, cropWidth - 1);
                var fx = sx - x1;

                var o = rowOffset + x * 4;
                var p11 = ((y1 + y0) * width + (x1 + x0)) * 4;
                var p12 = ((y1 + y0) * width + (x2 + x0)) * 4;
                var p21 = ((y2 + y0) * width + (x1 + x0)) * 4;
                var p22 = ((y2 + y0) * width + (x2 + x0)) * 4;

                for (int c = 0; c < 4; c++)
                {
                    var top = bgra[p11 + c] + (bgra[p12 + c] - bgra[p11 + c]) * fx;
                    var bottom = bgra[p21 + c] + (bgra[p22 + c] - bgra[p21 + c]) * fx;
                    var value = top + (bottom - top) * fy;
                    output[o + c] = (byte)Math.Clamp((int)Math.Round(value), 0, 255);
                }
            }
        }

        return (output, outWidth, outHeight);
    }
}
