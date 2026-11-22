using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AleappReader.App;

/// <summary>Pictures decoded with the codecs Windows has (HEIC/AVIF/WebP when their extensions are installed), off the UI
/// thread, nothing written to disk: for the ONNX classifier's input and for vision models that take JPEG/PNG only.</summary>
public static class ImageDecode
{
    static BitmapSource? Decode(byte[] data)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.StreamSource = new MemoryStream(data);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception e) when (e is NotSupportedException or FileFormatException or IOException or ArgumentException or InvalidOperationException
                                       or System.Runtime.InteropServices.COMException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>size×size RGB bytes: the shorter side scaled to <paramref name="size"/>, the centre cropped (as CLIP and ViT
    /// are trained); null when the picture cannot be decoded.</summary>
    public static byte[]? Rgb(byte[] data, int size)
    {
        if (Decode(data) is not { PixelWidth: > 0, PixelHeight: > 0 } src) return null;
        try
        {
            var scale = (size + 0.99) / Math.Min(src.PixelWidth, src.PixelHeight); // never a pixel short after rounding
            BitmapSource scaled = new TransformedBitmap(src, new ScaleTransform(scale, scale));
            var (w, h) = (scaled.PixelWidth, scaled.PixelHeight);
            if (w < size || h < size) return null;
            BitmapSource crop = new CroppedBitmap(scaled, new Int32Rect((w - size) / 2, (h - size) / 2, size, size));
            var rgb = new FormatConvertedBitmap(crop, PixelFormats.Rgb24, null, 0);
            var pixels = new byte[size * size * 3];
            rgb.CopyPixels(pixels, size * 3, 0);
            return pixels;
        }
        catch (Exception e) when (e is NotSupportedException or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    /// <summary>The picture as JPEG (at most <paramref name="maxSide"/> pixels on its longer side), for a vision model that
    /// does not read its format (HEIC, TIFF, BMP…); null when it cannot be decoded.</summary>
    public static byte[]? Jpeg(byte[] data, int maxSide = 2048)
    {
        if (Decode(data) is not { PixelWidth: > 0, PixelHeight: > 0 } src) return null;
        try
        {
            var scale = Math.Min(1.0, (double)maxSide / Math.Max(src.PixelWidth, src.PixelHeight));
            BitmapSource img = scale < 1 ? new TransformedBitmap(src, new ScaleTransform(scale, scale)) : src;
            var enc = new JpegBitmapEncoder { QualityLevel = 90 };
            enc.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(img, PixelFormats.Bgr24, null, 0)));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        }
        catch (Exception e) when (e is NotSupportedException or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }
}
