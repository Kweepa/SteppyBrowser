using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace SteppyBrowser
{
    /// <summary>
    /// Loads a Koala Painter picture (.koa, .kla, .koala) into a 320×200 bitmap.
    /// Multicolor pixels are doubled horizontally so the image has the usual C64 pixel aspect.
    /// Colors are the Pepto NTSC values VICE 3.10 actually draws for pepto-ntsc.vpl.
    /// </summary>
    internal static class KoalaImage
    {
        private const int BitmapSize = 8000;
        private const int ScreenSize = 1000;
        private const int ColorSize = 1000;
        private const int PayloadSize = BitmapSize + ScreenSize + ColorSize + 1;

        // VICE does not display the bytes in pepto-ntsc.vpl directly. These are the sRGB
        // colors x64sc puts on screen with that palette (no CRT filter).
        private static readonly int[] PeptoPal =
        {
            Argb(0x00, 0x00, 0x00), // 0 black
            Argb(0xFF, 0xFF, 0xFF), // 1 white
            Argb(0x8C, 0x41, 0x2E), // 2 red
            Argb(0x81, 0xD0, 0xE6), // 3 cyan
            Argb(0x94, 0x45, 0xB7), // 4 purple
            Argb(0x65, 0xB7, 0x42), // 5 green
            Argb(0x41, 0x2E, 0xAD), // 6 blue
            Argb(0xE5, 0xFD, 0x73), // 7 yellow
            Argb(0x94, 0x60, 0x1F), // 8 orange
            Argb(0x56, 0x47, 0x00), // 9 brown
            Argb(0xCB, 0x7C, 0x67), // 10 light red
            Argb(0x54, 0x54, 0x54), // 11 dark grey
            Argb(0x86, 0x86, 0x86), // 12 grey
            Argb(0xB8, 0xFF, 0x94), // 13 light green
            Argb(0x86, 0x72, 0xF9), // 14 light blue
            Argb(0xBA, 0xBA, 0xBA), // 15 light grey
        };

        public static Bitmap LoadBitmap(string path)
        {
            byte[] file = File.ReadAllBytes(path);
            int origin = BitmapOrigin(file.Length);

            int background = file[origin + PayloadSize - 1] & 0x0F;
            int[] pixels = new int[320 * 200];

            for (int row = 0; row < 25; row++)
            {
                for (int col = 0; col < 40; col++)
                {
                    int cell = row * 40 + col;
                    byte screen = file[origin + BitmapSize + cell];
                    byte colorRam = file[origin + BitmapSize + ScreenSize + cell];
                    int color0 = PeptoPal[background];
                    int color1 = PeptoPal[(screen >> 4) & 0x0F];
                    int color2 = PeptoPal[screen & 0x0F];
                    // Multicolor bitmap mode uses all 4 bits of color RAM (colors 0–15).
                    int color3 = PeptoPal[colorRam & 0x0F];

                    int cellBitmap = origin + cell * 8;
                    int destY = row * 8;
                    int destX = col * 8;
                    for (int y = 0; y < 8; y++)
                    {
                        byte bits = file[cellBitmap + y];
                        int rowIndex = (destY + y) * 320 + destX;
                        for (int pixel = 0; pixel < 4; pixel++)
                        {
                            int pair = (bits >> (6 - pixel * 2)) & 0x03;
                            int color;
                            switch (pair)
                            {
                                case 0: color = color0; break;
                                case 1: color = color1; break;
                                case 2: color = color2; break;
                                default: color = color3; break;
                            }
                            pixels[rowIndex + pixel * 2] = color;
                            pixels[rowIndex + pixel * 2 + 1] = color;
                        }
                    }
                }
            }

            return CreateBitmapFromBgra(pixels, 320, 200);
        }

        private static int BitmapOrigin(int length)
        {
            if (length == PayloadSize)
                return 0;
            if (length == PayloadSize + 2)
                return 2;
            throw new InvalidDataException("Not a Koala Painter file (expected 10001 or 10003 bytes).");
        }

        private static int Argb(int r, int g, int b)
        {
            return (0xFF << 24) | (r << 16) | (g << 8) | b;
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
