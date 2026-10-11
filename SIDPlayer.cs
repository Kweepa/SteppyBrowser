using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using SharpSid;

namespace SteppyBrowser
{
    /// <summary>
    /// Plays Commodore 64 SID files through SharpSid.
    /// </summary>
    public class SIDPlayer : IDisposable
    {
        private readonly string filePath;
        private readonly Stopwatch elapsed = new Stopwatch();
        private Player player;
        private SidTuneInfo info;
        private bool disposed;

        /// <summary>
        /// SharpSid references unsigned NAudio 1.10.0. The app uses strong-named
        /// NAudio 2.2.1, and a binding redirect cannot cross that public-key change.
        /// </summary>
        static SIDPlayer()
        {
            AppDomain.CurrentDomain.AssemblyResolve += ResolveSharpSidNAudio;
        }

        public SIDPlayer(string filePath)
        {
            this.filePath = filePath;
            player = new Player();
            try
            {
                if (!player.LoadSIDFromFile(filePath))
                    throw new InvalidOperationException("Could not load SID file");

                info = player.TuneInfo;
                SongCount = Math.Max(info.songs, 1);
                Song = info.startSong > 0 && info.startSong <= SongCount ? info.startSong : 1;
                player.Start(0);
                elapsed.Restart();
            }
            catch
            {
                player.Dispose();
                player = null;
                throw;
            }
        }

        public int Song { get; private set; }

        public int SongCount { get; private set; }

        /// <summary>Elapsed playback time in seconds. SID tunes loop, so there is no duration.</summary>
        public double CurrentPosition => elapsed.Elapsed.TotalSeconds;

        public bool IsPlaying => player != null && player.State == State.PLAYING;

        public string StatusText => $"Song {Song} of {SongCount}";

        public string Description
        {
            get
            {
                string text = $"SID File: {Path.GetFileName(filePath)}" + Environment.NewLine;
                if (info != null)
                {
                    if (!string.IsNullOrWhiteSpace(info.InfoString1))
                        text += $"Title: {info.InfoString1}" + Environment.NewLine;
                    if (!string.IsNullOrWhiteSpace(info.InfoString2))
                        text += $"Author: {info.InfoString2}" + Environment.NewLine;
                    if (!string.IsNullOrWhiteSpace(info.InfoString3))
                        text += $"Released: {info.InfoString3}" + Environment.NewLine;

                    text += $"Song: {Song} of {SongCount}" + Environment.NewLine
                          + $"SID Model: {SidModelName(info.sidModel)}" + Environment.NewLine;
                    if (!string.IsNullOrWhiteSpace(info.speedstring))
                        text += $"Speed: {info.speedstring}" + Environment.NewLine;
                    text += $"Load: ${info.loadAddr:X4}  Init: ${info.initAddr:X4}  Play: ${info.playAddr:X4}";
                }
                else
                {
                    text += $"Song: {Song} of {SongCount}";
                }

                if (SongCount > 1)
                    text += Environment.NewLine + "Page Up / Page Down: previous / next song";
                return text;
            }
        }

        public void ChangeSong(int delta)
        {
            if (disposed || player == null || SongCount <= 1)
                return;

            int next = Song + delta;
            if (next < 1)
                next = SongCount;
            if (next > SongCount)
                next = 1;

            player.Stop();
            player.Start(next);
            Song = next;
            info = player.TuneInfo;
            elapsed.Restart();
        }

        public void TogglePause()
        {
            if (disposed || player == null)
                return;

            if (player.State == State.PLAYING)
            {
                player.Pause();
                elapsed.Stop();
            }
            else
            {
                if (player.State == State.PAUSED)
                    player.Resume();
                else
                    player.Start(Song);
                elapsed.Start();
            }
        }

        public void Dispose()
        {
            if (disposed)
                return;

            disposed = true;
            elapsed.Stop();
            elapsed.Reset();
            if (player != null)
            {
                player.Stop();
                player.Dispose();
                player = null;
            }
        }

        private static string SidModelName(int sidModel)
        {
            switch (sidModel)
            {
                case SidTune.SIDTUNE_SIDMODEL_6581:
                    return "6581";
                case SidTune.SIDTUNE_SIDMODEL_8580:
                    return "8580";
                case SidTune.SIDTUNE_SIDMODEL_ANY:
                    return "6581 or 8580";
                default:
                    return "Unknown";
            }
        }

        private static Assembly ResolveSharpSidNAudio(object sender, ResolveEventArgs args)
        {
            var requested = new AssemblyName(args.Name);
            if (!string.Equals(requested.Name, "NAudio", StringComparison.Ordinal))
                return null;
            if (requested.Version == null || requested.Version.Major != 1)
                return null;

            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "naudio110", "NAudio.dll");
            if (!File.Exists(path))
                return null;

            return Assembly.LoadFrom(path);
        }
    }
}
