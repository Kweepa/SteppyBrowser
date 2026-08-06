using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace SteppyBrowser
{
    /// <summary>
    /// Loads Truevision TGA into a 32-bit bitmap (GDI+ does not decode TGA).
    /// </summary>
    internal static class TgaImage
    {
        public static Bitmap LoadBitmap(string path)
        {
            using (var fs = File.OpenRead(path))
            using (var br = new BinaryReader(fs))
            {
                byte idLength = br.ReadByte();
                byte colorMapType = br.ReadByte();
                byte imageType = br.ReadByte();
                ushort colorMapFirstIndex = br.ReadUInt16();
                ushort colorMapLength = br.ReadUInt16();
                byte colorMapEntrySize = br.ReadByte();
                br.ReadUInt16(); // xOrigin
                br.ReadUInt16(); // yOrigin
                ushort width = br.ReadUInt16();
                ushort height = br.ReadUInt16();
                byte bitsPerPixel = br.ReadByte();
                byte imageDescriptor = br.ReadByte();

                if (width == 0 || height == 0)
                    throw new InvalidDataException("Invalid TGA dimensions.");

                if (idLength > 0)
                    br.ReadBytes(idLength);

                int[] palette = null;
                if (colorMapType == 1)
                {
                    if (colorMapLength == 0)
                        throw new InvalidDataException("TGA color map has zero length.");
                    palette = ReadPalette(br, colorMapLength, colorMapEntrySize);
                }

                bool topOrigin = (imageDescriptor & 0x20) != 0;
                int pixelCount = width * height;
                int[] bgra = new int[pixelCount];

                switch (imageType)
                {
                    case 1:
                        if (palette == null)
                            throw new InvalidDataException("TGA type 1 requires a color map.");
                        ReadIndexed8(br, bgra, palette, colorMapFirstIndex);
                        break;
                    case 2:
                        ReadTrueColorUncompressed(br, bgra, width, height, bitsPerPixel);
                        break;
                    case 9:
                        if (palette == null)
                            throw new InvalidDataException("TGA type 9 requires a color map.");
                        ReadIndexed8Rle(br, bgra, palette, colorMapFirstIndex, bitsPerPixel);
                        break;
                    case 10:
                        ReadTrueColorRle(br, bgra, pixelCount, bitsPerPixel);
                        break;
                    case 3:
                        ReadGrayscaleUncompressed(br, bgra, bitsPerPixel);
                        break;
                    case 11:
                        ReadGrayscaleRle(br, bgra, pixelCount, bitsPerPixel);
                        break;
                    default:
                        throw new NotSupportedException($"TGA image type {imageType} is not supported.");
                }

                if (!topOrigin)
                    FlipVertical(bgra, width, height);

                return CreateBitmapFromBgra(bgra, width, height);
            }
        }

        private static int[] ReadPalette(BinaryReader br, int entryCount, int bitsPerEntry)
        {
            var palette = new int[entryCount];
            for (int i = 0; i < entryCount; i++)
            {
                if (bitsPerEntry == 24)
                {
                    byte b = br.ReadByte(), g = br.ReadByte(), r = br.ReadByte();
                    palette[i] = (0xFF << 24) | (r << 16) | (g << 8) | b;
                }
                else if (bitsPerEntry == 32)
                {
                    byte b = br.ReadByte(), g = br.ReadByte(), r = br.ReadByte(), a = br.ReadByte();
                    palette[i] = (a << 24) | (r << 16) | (g << 8) | b;
                }
                else if (bitsPerEntry == 16)
                {
                    palette[i] = Rgb555ToArgb(br.ReadUInt16());
                }
                else
                    throw new NotSupportedException($"TGA palette entry size {bitsPerEntry} bits is not supported.");
            }
            return palette;
        }

        private static int Rgb555ToArgb(ushort v)
        {
            int b = v & 0x1F;
            int g = (v >> 5) & 0x1F;
            int r = (v >> 10) & 0x1F;
            b = (b << 3) | (b >> 2);
            g = (g << 3) | (g >> 2);
            r = (r << 3) | (r >> 2);
            return (0xFF << 24) | (r << 16) | (g << 8) | b;
        }

        private static void ReadIndexed8(BinaryReader br, int[] bgra, int[] palette, ushort firstIndex)
        {
            for (int i = 0; i < bgra.Length; i++)
            {
                byte idx = br.ReadByte();
                int pi = idx - firstIndex;
                if (pi < 0 || pi >= palette.Length)
                    bgra[i] = unchecked((int)0xFF000000);
                else
                    bgra[i] = palette[pi];
            }
        }

        private static void ReadTrueColorUncompressed(BinaryReader br, int[] bgra, int width, int height, int bpp)
        {
            int n = width * height;
            if (bpp == 32)
            {
                for (int i = 0; i < n; i++)
                {
                    byte b = br.ReadByte(), g = br.ReadByte(), r = br.ReadByte(), a = br.ReadByte();
                    bgra[i] = (a << 24) | (r << 16) | (g << 8) | b;
                }
            }
            else if (bpp == 24)
            {
                for (int i = 0; i < n; i++)
                {
                    byte b = br.ReadByte(), g = br.ReadByte(), r = br.ReadByte();
                    bgra[i] = (0xFF << 24) | (r << 16) | (g << 8) | b;
                }
            }
            else if (bpp == 16)
            {
                for (int i = 0; i < n; i++)
                    bgra[i] = Rgb555ToArgb(br.ReadUInt16());
            }
            else
                throw new NotSupportedException($"TGA {bpp} bpp true-color is not supported.");
        }

        private static void ReadGrayscaleUncompressed(BinaryReader br, int[] bgra, int bpp)
        {
            if (bpp == 8)
            {
                for (int i = 0; i < bgra.Length; i++)
                {
                    byte y = br.ReadByte();
                    bgra[i] = (0xFF << 24) | (y << 16) | (y << 8) | y;
                }
            }
            else
                throw new NotSupportedException($"TGA grayscale {bpp} bpp is not supported.");
        }

        private static void ReadIndexed8Rle(BinaryReader br, int[] bgra, int[] palette, ushort firstIndex, int bpp)
        {
            if (bpp != 8)
                throw new NotSupportedException("TGA RLE color-mapped expects 8 bpp.");
            int written = 0;
            int total = bgra.Length;
            while (written < total)
            {
                byte packet = br.ReadByte();
                bool isRle = (packet & 0x80) != 0;
                int count = (packet & 0x7F) + 1;
                if (written + count > total)
                    count = total - written;
                if (isRle)
                {
                    byte idx = br.ReadByte();
                    int pi = idx - firstIndex;
                    int color = (pi >= 0 && pi < palette.Length) ? palette[pi] : unchecked((int)0xFF000000);
                    for (int k = 0; k < count; k++)
                        bgra[written++] = color;
                }
                else
                {
                    for (int k = 0; k < count; k++)
                    {
                        byte idx = br.ReadByte();
                        int pi = idx - firstIndex;
                        bgra[written++] = (pi >= 0 && pi < palette.Length) ? palette[pi] : unchecked((int)0xFF000000);
                    }
                }
            }
        }

        private static void ReadTrueColorRle(BinaryReader br, int[] bgra, int pixelCount, int bpp)
        {
            if (bpp != 24 && bpp != 32)
                throw new NotSupportedException($"TGA RLE true-color {bpp} bpp is not supported.");

            int written = 0;
            while (written < pixelCount)
            {
                byte packet = br.ReadByte();
                bool isRle = (packet & 0x80) != 0;
                int count = (packet & 0x7F) + 1;
                if (written + count > pixelCount)
                    count = pixelCount - written;

                if (isRle)
                {
                    int color = ReadTrueColorPixel(br, bpp);
                    for (int k = 0; k < count; k++)
                        bgra[written++] = color;
                }
                else
                {
                    for (int k = 0; k < count; k++)
                        bgra[written++] = ReadTrueColorPixel(br, bpp);
                }
            }
        }

        private static int ReadTrueColorPixel(BinaryReader br, int bpp)
        {
            if (bpp == 32)
            {
                byte b = br.ReadByte(), g = br.ReadByte(), r = br.ReadByte(), a = br.ReadByte();
                return (a << 24) | (r << 16) | (g << 8) | b;
            }
            byte b24 = br.ReadByte(), g24 = br.ReadByte(), r24 = br.ReadByte();
            return (0xFF << 24) | (r24 << 16) | (g24 << 8) | b24;
        }

        private static void ReadGrayscaleRle(BinaryReader br, int[] bgra, int pixelCount, int bpp)
        {
            if (bpp != 8)
                throw new NotSupportedException($"TGA RLE grayscale {bpp} bpp is not supported.");
            int written = 0;
            while (written < pixelCount)
            {
                byte packet = br.ReadByte();
                bool isRle = (packet & 0x80) != 0;
                int count = (packet & 0x7F) + 1;
                if (written + count > pixelCount)
                    count = pixelCount - written;
                if (isRle)
                {
                    byte y = br.ReadByte();
                    int color = (0xFF << 24) | (y << 16) | (y << 8) | y;
                    for (int k = 0; k < count; k++)
                        bgra[written++] = color;
                }
                else
                {
                    for (int k = 0; k < count; k++)
                    {
                        byte y = br.ReadByte();
                        bgra[written++] = (0xFF << 24) | (y << 16) | (y << 8) | y;
                    }
                }
            }
        }

        private static void FlipVertical(int[] bgra, int width, int height)
        {
            for (int y = 0; y < height / 2; y++)
            {
                int rowA = y * width;
                int rowB = (height - 1 - y) * width;
                for (int x = 0; x < width; x++)
                {
                    int t = bgra[rowA + x];
                    bgra[rowA + x] = bgra[rowB + x];
                    bgra[rowB + x] = t;
                }
            }
        }

        private static Bitmap CreateBitmapFromBgra(int[] bgra, int width, int height)
        {
            var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            BitmapData data = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (int y = 0; y < height; y++)
                {
                    IntPtr row = IntPtr.Add(data.Scan0, y * data.Stride);
                    Marshal.Copy(bgra, y * width, row, width);
                }
            }
            finally
            {
                bmp.UnlockBits(data);
            }
            return bmp;
        }
    }
}
