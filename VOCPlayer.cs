using System;
using System.IO;
using NAudio.Wave;

namespace SteppyBrowser
{
    /// <summary>
    /// Handles playback of VOC (Creative Voice File) files
    /// </summary>
    public static class VOCPlayer
    {
        /// <summary>
        /// Creates an IWaveProvider from a VOC file for playback
        /// </summary>
        public static IWaveProvider CreateWaveProvider(string fileName)
        {
            IWaveProvider waveProvider = null;

            byte[] data = File.ReadAllBytes(fileName);
            string header = "";
            // check header
            for (int i = 0; i < 19; i++)
            {
                header += (char)data[i];
            }
            if (header == "Creative Voice File")
            {
                int index = 0x1a;
                int timeConstant = 0;
                int packMethod = 0;
                int voiceMode = 0; // mono
                while (index < data.Length)
                {
                    int blockType = data[index];
                    int dataSize = data[index + 1] + 256 * (data[index + 2] + 256 * data[index + 3]);
                    index += 4;
                    if (blockType == 1)
                    {
                        // regular sound data
                        int samplingRate = data[index];
                        packMethod = data[index + 1];
                        int hertz = -1000000 / (samplingRate - 256);
                        int numSamples = dataSize - 2;
                        float[] samples = new float[numSamples];
                        index += 2;
                        for (int i = 0; i < numSamples; i++)
                        {
                            samples[i] = data[i + index] / 256.0f - 0.5f;
                        }

                        // Create NAudio WaveFormat (Important: Use IeeeFloat)
                        WaveFormat waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(hertz, 1);

                        // Create NAudio IWaveProvider from float array
                        waveProvider = new RawSourceFloatWaveProvider(samples, waveFormat);
                    }
                    else if (blockType == 8)
                    {
                        // digi sound attr extension block
                        timeConstant = data[index] + 256 * data[index + 1];
                        packMethod = data[index + 2];
                        voiceMode = data[index + 3];
                    }
                    index += dataSize;
                }
            }

            return waveProvider;
        }

        /// <summary>
        /// Internal class for providing audio data from a float array
        /// </summary>
        internal class RawSourceFloatWaveProvider : IWaveProvider
        {
            private readonly float[] source;
            private readonly WaveFormat waveFormat;
            private int position;

            public RawSourceFloatWaveProvider(float[] source, WaveFormat waveFormat)
            {
                this.source = source;
                this.waveFormat = waveFormat;
            }

            public int Read(byte[] buffer, int offset, int count)
            {
                int sourceIndex = position * waveFormat.Channels;
                int sourceSamplesRemaining = source.Length - sourceIndex;
                int destBytesRemaining = buffer.Length - offset;
                int bytesToCopy = Math.Min(Math.Min(destBytesRemaining, sourceSamplesRemaining * 4), count);

                if (bytesToCopy > 0)
                {
                    Buffer.BlockCopy(source, sourceIndex * 4, buffer, offset, bytesToCopy);
                    position += bytesToCopy / (waveFormat.Channels * 4);
                }
                else
                {
                    return 0;
                }

                return bytesToCopy;
            }

            public WaveFormat WaveFormat
            {
                get { return waveFormat; }
            }
        }
    }
}





