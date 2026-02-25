using System.Drawing.Imaging;

namespace CbitAgent.Tray;

public static class ScreenshotCapture
{
    /// <summary>
    /// Captures the entire virtual screen (all monitors).
    /// Call this BEFORE showing any form so the form isn't in the capture.
    /// </summary>
    public static Bitmap? CaptureFullScreen()
    {
        try
        {
            var bounds = SystemInformation.VirtualScreen;
            var bmp = new Bitmap(bounds.Width, bounds.Height);
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size);
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Creates a scaled-down thumbnail for the form preview.
    /// </summary>
    public static Image CreateThumbnail(Bitmap source, int maxWidth, int maxHeight)
    {
        double ratioX = (double)maxWidth / source.Width;
        double ratioY = (double)maxHeight / source.Height;
        double ratio = Math.Min(ratioX, ratioY);

        int newWidth = (int)(source.Width * ratio);
        int newHeight = (int)(source.Height * ratio);

        var thumb = new Bitmap(newWidth, newHeight);
        using var g = Graphics.FromImage(thumb);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.DrawImage(source, 0, 0, newWidth, newHeight);
        return thumb;
    }

    /// <summary>
    /// Converts a bitmap to PNG bytes for upload.
    /// </summary>
    public static byte[] ToBytes(Bitmap bitmap)
    {
        using var ms = new MemoryStream();
        bitmap.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }
}
