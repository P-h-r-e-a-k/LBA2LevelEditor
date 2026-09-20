using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace LbaBodyStudio;

// FlatImage <-> System.Drawing.Bitmap.
public static class FlatBitmap
{
    public static Bitmap ToBitmap(FlatImage image)
    {
        var bmp = new Bitmap(image.Width, image.Height, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, image.Width, image.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try { Marshal.Copy(image.Bgra, 0, data.Scan0, image.Bgra.Length); }
        finally { bmp.UnlockBits(data); }
        return bmp;
    }

    public static FlatImage FromBitmap(Bitmap bmp)
    {
        using var copy = bmp.Clone(new Rectangle(0, 0, bmp.Width, bmp.Height), PixelFormat.Format32bppArgb);
        var data = copy.LockBits(new Rectangle(0, 0, copy.Width, copy.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var bytes = new byte[copy.Width * copy.Height * 4];
        try { Marshal.Copy(data.Scan0, bytes, 0, bytes.Length); }
        finally { copy.UnlockBits(data); }
        return new FlatImage(copy.Width, copy.Height, bytes);
    }

    public static FlatImage Load(string path)
    {
        using var source = new Bitmap(path);
        return FromBitmap(source);
    }

    public static void Save(FlatImage image, string path)
    {
        using var bmp = ToBitmap(image);
        bmp.Save(path, ImageFormat.Png);
    }
}
