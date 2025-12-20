using System;
using System.IO;

namespace SteppyBrowser
{
    /// <summary>
    /// Static class for extracting metadata from VOC (Creative Voice File) files
    /// </summary>
    public static class VOCFileInfo
    {
        public class VOCMetadata
        {
            public TimeSpan Duration { get; set; }
            public int SampleRate { get; set; }
            public int Channels { get; set; }
            public bool Found { get; set; }
            public string ErrorMessage { get; set; }
        }

        public static VOCMetadata GetFileInfo(string filePath)
        {
            var metadata = new VOCMetadata
            {
                Found = false,
                Channels = 1 // default mono
            };

            try
            {
                byte[] data = File.ReadAllBytes(filePath);
                
                // Check minimum file size
                if (data.Length < 26)
                {
                    metadata.ErrorMessage = "File too small to parse";
                    return metadata;
                }
                
                string header = "";
                // check header
                for (int i = 0; i < 19 && i < data.Length; i++)
                {
                    header += (char)data[i];
                }
                
                if (header == "Creative Voice File")
                {
                    int index = 0x1a;
                    int totalSamples = 0;
                    int sampleRate = 0;
                    int channels = 1; // default mono
                    
                    while (index + 4 <= data.Length)
                    {
                        int blockType = data[index];
                        
                        // Check if we have enough bytes to read dataSize
                        if (index + 4 > data.Length) break;
                        
                        int dataSize = data[index + 1] + 256 * (data[index + 2] + 256 * data[index + 3]);
                        index += 4;
                        
                        // Check if dataSize is valid
                        if (dataSize < 0 || index + dataSize > data.Length)
                        {
                            break;
                        }
                        
                        if (blockType == 1)
                        {
                            // regular sound data - need at least 2 more bytes
                            if (index + 2 <= data.Length)
                            {
                                int samplingRate = data[index];
                                if (samplingRate != 256) // Avoid division by zero
                                {
                                    int hertz = -1000000 / (samplingRate - 256);
                                    int numSamples = Math.Max(0, dataSize - 2);
                                    totalSamples += numSamples;
                                    if (sampleRate == 0) sampleRate = hertz;
                                }
                            }
                        }
                        else if (blockType == 8)
                        {
                            // digi sound attr extension block - need at least 4 more bytes
                            if (index + 4 <= data.Length)
                            {
                                int voiceMode = data[index + 3];
                                channels = (voiceMode == 1) ? 2 : 1; // stereo = 1, mono = 0
                            }
                        }
                        else if (blockType == 0)
                        {
                            // Terminator block
                            break;
                        }
                        
                        index += dataSize;
                    }
                    
                    if (sampleRate > 0 && totalSamples > 0)
                    {
                        metadata.Found = true;
                        metadata.Duration = TimeSpan.FromSeconds((double)totalSamples / sampleRate);
                        metadata.SampleRate = sampleRate;
                        metadata.Channels = channels;
                    }
                    else
                    {
                        metadata.ErrorMessage = "Could not extract audio information";
                    }
                }
                else
                {
                    metadata.ErrorMessage = "Invalid VOC header";
                }
            }
            catch (Exception ex)
            {
                metadata.ErrorMessage = ex.Message;
            }

            return metadata;
        }
    }
}





