using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace SteppyBrowser
{
    /// <summary>
    /// Loads Sun raster images (magic 0x59A66A95). Sub Culture's credits
    /// screens use this format with a .BMP extension: 640x480, 16-bit RGB565.
    /// </summary>
    internal static class SunRasterImage
    {
        public const uint Magic = 0x59A66A95;

        public static bool HasMagic(string path)
        {
            using (var fs = File.OpenRead(path))
            {
                if (fs.Length < 4)
                    return false;

                int b0 = fs.ReadByte();
                int b1 = fs.ReadByte();
                int b2 = fs.ReadByte();
                int b3 = fs.ReadByte();
                if (b0 < 0 || b1 < 0 || b2 < 0 || b3 < 0)
                    return false;

                uint magic = (uint)((b0 << 24) | (b1 << 16) | (b2 << 8) | b3);
                return magic == Magic;
            }
        }

        public static Bitmap LoadBitmap(string path)
        {
            byte[] data = File.ReadAllBytes(path);
            if (data.Length < 32)
                throw new InvalidDataException("Sun raster file is too small.");

            uint magic = ReadUInt32BE(data, 0);
            if (magic != Magic)
                throw new InvalidDataException("Not a Sun raster image.");

            int width = checked((int)ReadUInt32BE(data, 4));
            int height = checked((int)ReadUInt32BE(data, 8));
            int depth = checked((int)ReadUInt32BE(data, 12));
            int type = checked((int)ReadUInt32BE(data, 20));
            int mapLength = checked((int)ReadUInt32BE(data, 28));

            if (width <= 0 || height <= 0 || width > 8192 || height > 8192)
                throw new InvalidDataException($"Invalid Sun raster dimensions ({width}x{height}).");

            // Types 0 and 1 are uncompressed. Type 3 is uncompressed RGB;
            // 16-bit pixels are copied as-is either way.
            if (type != 0 && type != 1 && type != 3)
                throw new NotSupportedException($"Sun raster type {type} is not supported.");
            if (depth != 16)
                throw new NotSupportedException($"Sun raster depth {depth} is not supported.");
            if (mapLength < 0 || mapLength > data.Length)
                throw new InvalidDataException("Invalid Sun raster color map length.");

            int stride = (width * depth + 7) / 8;
            stride = (stride + 1) & ~1;

            int offset = 32 + mapLength;
            long needed = (long)offset + (long)stride * height;
            if (needed > data.Length)
                throw new InvalidDataException("Sun raster pixel data is truncated.");

            int[] bgra = new int[width * height];
            for (int y = 0; y < height; y++)
            {
                int row = offset + y * stride;
                int dst = y * width;
                for (int x = 0; x < width; x++)
                {
                    int pix = data[row + x * 2] | (data[row + x * 2 + 1] << 8);
                    int r = (pix >> 8) & 0xF8;
                    int g = (pix >> 3) & 0xFC;
                    int b = (pix << 3) & 0xF8;
                    bgra[dst + x] = (0xFF << 24) | (r << 16) | (g << 8) | b;
                }
            }

            return CreateBitmapFromBgra(bgra, width, height);
        }

        private static uint ReadUInt32BE(byte[] data, int offset)
        {
            return ((uint)data[offset] << 24)
                | ((uint)data[offset + 1] << 16)
                | ((uint)data[offset + 2] << 8)
                | data[offset + 3];
        }

        private static Bitmap CreateBitmapFromBgra(int[] bgra, int width, int height)
        {
            var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            BitmapData bits = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (int y = 0; y < height; y++)
                {
                    IntPtr row = IntPtr.Add(bits.Scan0, y * bits.Stride);
                    Marshal.Copy(bgra, y * width, row, width);
                }
            }
            finally
            {
                bmp.UnlockBits(bits);
            }
            return bmp;
        }
    }
}
