using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

internal static class BuildPsuIcon
{
    private static void Main(string[] args)
    {
        int[] sizes = { 16, 20, 24, 32, 40, 48, 64 };
        bool combined = args.Length > 1 && args[1] == "gpu";
        using (var output = new BinaryWriter(File.Create(args[0])))
        {
            output.Write((short)0);
            output.Write((short)1);
            output.Write((short)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            foreach (var size in sizes)
            {
                int length = 40 + size * size * 4 + ((size + 31) / 32) * 4 * size;
                output.Write((byte)size);
                output.Write((byte)size);
                output.Write((short)0);
                output.Write((short)1);
                output.Write((short)32);
                output.Write(length);
                output.Write(offset);
                offset += length;
            }
            foreach (var size in sizes)
                WriteImage(output, size, combined);
        }
    }

    private static void WriteImage(BinaryWriter output, int size, bool combined)
    {
        using (var image = new Bitmap(size, size, PixelFormat.Format32bppArgb))
        using (var canvas = Graphics.FromImage(image))
        using (var background = new SolidBrush(Color.FromArgb(16, 27, 40)))
        using (var rim = new Pen(combined ? Color.FromArgb(85, 107, 216) : Color.FromArgb(43, 164, 138), 1.5F))
        using (var bolt = new SolidBrush(combined ? Color.FromArgb(155, 183, 255) : Color.FromArgb(109, 240, 199)))
        {
            canvas.SmoothingMode = SmoothingMode.AntiAlias;
            canvas.ScaleTransform(size / 32F, size / 32F);
            canvas.FillEllipse(background, 2F, 2F, 28F, 28F);
            canvas.DrawEllipse(rim, 2.5F, 2.5F, 27F, 27F);
            canvas.FillPolygon(bolt, new[]
            {
                new PointF(18.5F, 4.5F), new PointF(8.6F, 17.6F), new PointF(14.8F, 17.6F),
                new PointF(12.5F, 27.6F), new PointF(23.6F, 13.7F), new PointF(17F, 13.7F)
            });
            output.Write(40);
            output.Write(size);
            output.Write(size * 2);
            output.Write((short)1);
            output.Write((short)32);
            output.Write(0);
            output.Write(size * size * 4);
            output.Write(0);
            output.Write(0);
            output.Write(0);
            output.Write(0);
            for (int y = size - 1; y >= 0; y--)
            {
                for (int x = 0; x < size; x++)
                {
                    Color pixel = image.GetPixel(x, y);
                    output.Write(pixel.B);
                    output.Write(pixel.G);
                    output.Write(pixel.R);
                    output.Write(pixel.A);
                }
            }
            output.Write(new byte[((size + 31) / 32) * 4 * size]);
        }
    }
}
