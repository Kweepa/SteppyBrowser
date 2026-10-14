using System;
using System.Collections.Generic;
using System.Reflection;
using System.Windows.Forms;
using NAudio.Midi;

namespace SteppyBrowser
{
    /// <summary>
    /// Plays standard MIDI files through the Windows MIDI synthesizer.
    /// </summary>
    public class MidiPlayer : IDisposable
    {
        private enum ScheduleKind
        {
            Note,
            State
        }

        private struct ScheduledEvent
        {
            public double Time;
            public int Order;
            public ScheduleKind Kind;
            public int Message;
            public byte[] Buffer;
        }

        private struct TempoChange
        {
            public long Tick;
            public int MicrosecondsPerQuarter;
        }

        private readonly List<ScheduledEvent> scheduled = new List<ScheduledEvent>();
        private readonly Timer playbackTimer;
        private readonly MidiOut midiOut;
        private int nextEvent;
        private double currentSeconds;
        private DateTime lastTickTime;
        private bool paused;
        private bool disposed;

        public MidiPlayer(string filePath)
        {
            midiOut = OpenSynth();
            try
            {
                var midiFile = new MidiFile(filePath, false);
                Load(midiFile, filePath);
            }
            catch
            {
                midiOut.Dispose();
                throw;
            }

            playbackTimer = new Timer();
            playbackTimer.Interval = 5;
            playbackTimer.Tick += PlaybackTimer_Tick;
        }

        public double CurrentPosition => Duration > 0 && currentSeconds > Duration ? Duration : currentSeconds;

        public double Duration { get; private set; }

        public bool IsPlaying => !disposed && !paused && playbackTimer != null && playbackTimer.Enabled;

        public string StatusText { get; private set; }

        public string Description { get; private set; }

        public void Start()
        {
            if (disposed)
                return;

            playbackTimer.Stop();
            SilenceAllNotes();
            currentSeconds = 0;
            nextEvent = 0;
            paused = false;
            lastTickTime = DateTime.UtcNow;
            DispatchDueEvents();
            if (nextEvent < scheduled.Count)
                playbackTimer.Start();
        }

        public void Pause()
        {
            if (disposed || !playbackTimer.Enabled)
                return;

            playbackTimer.Stop();
            SilenceAllNotes();
            paused = true;
        }

        public void Resume()
        {
            if (disposed)
                return;

            if (nextEvent >= scheduled.Count)
            {
                Start();
                return;
            }

            paused = false;
            lastTickTime = DateTime.UtcNow;
            playbackTimer.Start();
        }

        public void Stop()
        {
            paused = false;
            playbackTimer?.Stop();
            SilenceAllNotes();
        }

        public void Seek(double positionSeconds)
        {
            if (disposed)
                return;

            if (positionSeconds < 0)
                positionSeconds = 0;
            if (Duration > 0 && positionSeconds > Duration)
                positionSeconds = Duration;

            bool shouldPlay = IsPlaying;
            playbackTimer.Stop();
            SilenceAllNotes();
            currentSeconds = positionSeconds;
            nextEvent = 0;
            while (nextEvent < scheduled.Count && scheduled[nextEvent].Time <= currentSeconds)
            {
                if (scheduled[nextEvent].Kind == ScheduleKind.State)
                    Send(scheduled[nextEvent]);
                nextEvent++;
            }
            SilenceAllNotes();

            if (shouldPlay && nextEvent < scheduled.Count)
            {
                paused = false;
                lastTickTime = DateTime.UtcNow;
                playbackTimer.Start();
            }
            else if (nextEvent < scheduled.Count)
            {
                paused = true;
            }
            else
            {
                paused = false;
            }
        }

        public void Dispose()
        {
            if (disposed)
                return;

            disposed = true;
            Stop();
            playbackTimer?.Dispose();
            midiOut?.Dispose();
        }

        private void Load(MidiFile midiFile, string filePath)
        {
            int ppqn = midiFile.DeltaTicksPerQuarterNote > 0 ? midiFile.DeltaTicksPerQuarterNote : 480;
            var globalTempo = CollectTempo(midiFile, -1);
            string title = null;
            string copyright = null;
            string timeSignature = "4/4";
            double bpm = 120;
            bool sawTempo = false;
            bool sawTimeSignature = false;
            int eventCount = 0;
            int order = 0;
            double offset = 0;

            for (int trackIndex = 0; trackIndex < midiFile.Tracks; trackIndex++)
            {
                IList<MidiEvent> track = midiFile.Events[trackIndex];
                var tempo = midiFile.FileFormat == 2 ? CollectTempo(midiFile, trackIndex) : globalTempo;
                double trackEnd = offset;

                foreach (MidiEvent midiEvent in track)
                {
                    if (midiEvent is TempoEvent tempoEvent && !sawTempo)
                    {
                        bpm = tempoEvent.Tempo;
                        sawTempo = true;
                    }

                    if (!sawTimeSignature && midiEvent is TimeSignatureEvent signature &&
                        !string.IsNullOrWhiteSpace(signature.TimeSignature) &&
                        signature.TimeSignature.IndexOf("Unknown", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        timeSignature = signature.TimeSignature;
                        sawTimeSignature = true;
                    }

                    if (midiEvent is TextEvent text && !string.IsNullOrWhiteSpace(text.Text))
                    {
                        if (text.MetaEventType == MetaEventType.SequenceTrackName && title == null)
                            title = text.Text.Trim();
                        else if (text.MetaEventType == MetaEventType.Copyright && copyright == null)
                            copyright = text.Text.Trim();
                        else if (text.MetaEventType == MetaEventType.TextEvent && title == null)
                            title = text.Text.Trim();
                    }

                    if (midiEvent.CommandCode == MidiCommandCode.MetaEvent &&
                        (midiEvent is MetaEvent meta) && meta.MetaEventType == MetaEventType.EndTrack)
                    {
                        continue;
                    }

                    eventCount++;
                    double time = offset + TicksToSeconds(midiEvent.AbsoluteTime, ppqn, tempo);
                    if (time > trackEnd)
                        trackEnd = time;

                    TrySchedule(midiEvent, time, ref order);
                }

                if (trackEnd > Duration)
                    Duration = trackEnd;
                if (midiFile.FileFormat == 2)
                    offset = trackEnd;
            }

            scheduled.Sort(delegate (ScheduledEvent a, ScheduledEvent b)
            {
                int byTime = a.Time.CompareTo(b.Time);
                return byTime != 0 ? byTime : a.Order.CompareTo(b.Order);
            });

            string durationStr = FormatDuration(TimeSpan.FromSeconds(Duration));
            StatusText = "Duration=" + durationStr;

            string description = "MIDI File: " + System.IO.Path.GetFileName(filePath) + Environment.NewLine;
            if (!string.IsNullOrWhiteSpace(title))
                description += "Title: " + title + Environment.NewLine;
            if (!string.IsNullOrWhiteSpace(copyright))
                description += "Copyright: " + copyright + Environment.NewLine;
            description += "Duration: " + durationStr + Environment.NewLine
                  + "BPM: " + bpm.ToString("F1") + Environment.NewLine
                  + "Time Signature: " + timeSignature + Environment.NewLine
                  + "Format: " + midiFile.FileFormat + Environment.NewLine
                  + "Tracks: " + midiFile.Tracks + Environment.NewLine
                  + "Events: " + eventCount + Environment.NewLine
                  + "Using Windows MIDI Synthesizer";
            Description = description;
        }

        private bool TrySchedule(MidiEvent midiEvent, double time, ref int order)
        {
            int channel = midiEvent.Channel;
            if (midiEvent.CommandCode != MidiCommandCode.Sysex && midiEvent.CommandCode != MidiCommandCode.MetaEvent &&
                (channel < 1 || channel > 16))
            {
                return false;
            }

            switch (midiEvent.CommandCode)
            {
                case MidiCommandCode.NoteOn:
                case MidiCommandCode.NoteOff:
                    var note = (NoteEvent)midiEvent;
                    if (note.NoteNumber < 0 || note.NoteNumber > 127)
                        return false;
                    bool noteOn = midiEvent.CommandCode == MidiCommandCode.NoteOn && note.Velocity > 0;
                    int velocity = note.Velocity;
                    if (velocity < 0) velocity = 0;
                    if (velocity > 127) velocity = 127;
                    int message = noteOn
                        ? MidiMessage.StartNote(note.NoteNumber, velocity, channel).RawData
                        : MidiMessage.StopNote(note.NoteNumber, velocity, channel).RawData;
                    scheduled.Add(new ScheduledEvent
                    {
                        Time = time,
                        Order = order++,
                        Kind = ScheduleKind.Note,
                        Message = message
                    });
                    return true;

                case MidiCommandCode.ControlChange:
                    var control = (ControlChangeEvent)midiEvent;
                    int controller = (int)control.Controller;
                    int controlValue = control.ControllerValue;
                    if (controller < 0 || controller > 127 || controlValue < 0 || controlValue > 127)
                        return false;
                    scheduled.Add(new ScheduledEvent
                    {
                        Time = time,
                        Order = order++,
                        Kind = ScheduleKind.State,
                        Message = MidiMessage.ChangeControl(controller, controlValue, channel).RawData
                    });
                    return true;

                case MidiCommandCode.PatchChange:
                    var patch = (PatchChangeEvent)midiEvent;
                    if (patch.Patch < 0 || patch.Patch > 127)
                        return false;
                    scheduled.Add(new ScheduledEvent
                    {
                        Time = time,
                        Order = order++,
                        Kind = ScheduleKind.State,
                        Message = MidiMessage.ChangePatch(patch.Patch, channel).RawData
                    });
                    return true;

                case MidiCommandCode.PitchWheelChange:
                    var bend = (PitchWheelChangeEvent)midiEvent;
                    int pitch = bend.Pitch;
                    if (pitch < 0) pitch = 0;
                    if (pitch > 16383) pitch = 16383;
                    scheduled.Add(new ScheduledEvent
                    {
                        Time = time,
                        Order = order++,
                        Kind = ScheduleKind.State,
                        Buffer = new byte[]
                        {
                            (byte)(0xE0 + channel - 1),
                            (byte)(pitch & 0x7F),
                            (byte)((pitch >> 7) & 0x7F)
                        }
                    });
                    return true;

                case MidiCommandCode.ChannelAfterTouch:
                    var channelAfter = (ChannelAfterTouchEvent)midiEvent;
                    int pressure = channelAfter.AfterTouchPressure;
                    if (pressure < 0) pressure = 0;
                    if (pressure > 127) pressure = 127;
                    scheduled.Add(new ScheduledEvent
                    {
                        Time = time,
                        Order = order++,
                        Kind = ScheduleKind.State,
                        Buffer = new byte[] { (byte)(0xD0 + channel - 1), (byte)pressure }
                    });
                    return true;

                case MidiCommandCode.KeyAfterTouch:
                    if (midiEvent is NoteEvent keyAfter)
                    {
                        scheduled.Add(new ScheduledEvent
                        {
                            Time = time,
                            Order = order++,
                            Kind = ScheduleKind.State,
                            Buffer = new byte[]
                            {
                                (byte)(0xA0 + channel - 1),
                                (byte)keyAfter.NoteNumber,
                                (byte)keyAfter.Velocity
                            }
                        });
                        return true;
                    }
                    return false;

                case MidiCommandCode.Sysex:
                    byte[] sysex = GetSysexData(midiEvent as SysexEvent);
                    if (sysex == null || sysex.Length == 0)
                        return false;
                    scheduled.Add(new ScheduledEvent
                    {
                        Time = time,
                        Order = order++,
                        Kind = ScheduleKind.State,
                        Buffer = sysex
                    });
                    return true;

                default:
                    return false;
            }
        }

        private void PlaybackTimer_Tick(object sender, EventArgs e)
        {
            if (disposed)
            {
                playbackTimer.Stop();
                return;
            }

            DateTime now = DateTime.UtcNow;
            double elapsed = (now - lastTickTime).TotalSeconds;
            lastTickTime = now;
            if (elapsed < 0)
                elapsed = 0;
            currentSeconds += elapsed;
            DispatchDueEvents();
            if (nextEvent >= scheduled.Count)
            {
                paused = false;
                playbackTimer.Stop();
                SilenceAllNotes();
            }
        }

        private void DispatchDueEvents()
        {
            while (nextEvent < scheduled.Count && scheduled[nextEvent].Time <= currentSeconds)
            {
                Send(scheduled[nextEvent]);
                nextEvent++;
            }
        }

        private void Send(ScheduledEvent midiEvent)
        {
            try
            {
                if (midiEvent.Buffer != null)
                    midiOut.SendBuffer(midiEvent.Buffer);
                else
                    midiOut.Send(midiEvent.Message);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Error sending MIDI event: " + ex.Message);
            }
        }

        private void SilenceAllNotes()
        {
            if (midiOut == null)
                return;

            for (int channel = 1; channel <= 16; channel++)
            {
                try
                {
                    midiOut.Send(MidiMessage.ChangeControl(123, 0, channel).RawData);
                    midiOut.Send(MidiMessage.ChangeControl(120, 0, channel).RawData);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("Error silencing MIDI channel: " + ex.Message);
                }
            }
        }

        private static MidiOut OpenSynth()
        {
            int deviceNumber = -1;
            for (int i = 0; i < MidiOut.NumberOfDevices; i++)
            {
                string name = MidiOut.DeviceInfo(i).ProductName;
                if (name.Contains("GS Wavetable") || name.Contains("Microsoft"))
                {
                    deviceNumber = i;
                    break;
                }
            }

            if (deviceNumber == -1 && MidiOut.NumberOfDevices > 0)
                deviceNumber = 0;
            if (deviceNumber == -1)
                throw new InvalidOperationException("No MIDI output device found");

            return new MidiOut(deviceNumber);
        }

        private static List<TempoChange> CollectTempo(MidiFile midiFile, int trackFilter)
        {
            var changes = new List<TempoChange>();
            for (int trackIndex = 0; trackIndex < midiFile.Tracks; trackIndex++)
            {
                if (trackFilter >= 0 && trackIndex != trackFilter)
                    continue;

                foreach (MidiEvent midiEvent in midiFile.Events[trackIndex])
                {
                    if (midiEvent is TempoEvent tempo && tempo.MicrosecondsPerQuarterNote > 0)
                    {
                        changes.Add(new TempoChange
                        {
                            Tick = tempo.AbsoluteTime,
                            MicrosecondsPerQuarter = tempo.MicrosecondsPerQuarterNote
                        });
                    }
                }
            }

            changes.Sort(delegate (TempoChange a, TempoChange b) { return a.Tick.CompareTo(b.Tick); });
            return changes;
        }

        private static double TicksToSeconds(long tick, int ppqn, List<TempoChange> tempoMap)
        {
            double seconds = 0;
            long lastTick = 0;
            int tempo = 500000;
            for (int i = 0; i < tempoMap.Count; i++)
            {
                long changeTick = tempoMap[i].Tick;
                if (changeTick > tick)
                    break;
                if (changeTick > lastTick)
                    seconds += (changeTick - lastTick) * (double)tempo / (ppqn * 1000000.0);
                lastTick = changeTick;
                tempo = tempoMap[i].MicrosecondsPerQuarter;
            }

            if (tick > lastTick)
                seconds += (tick - lastTick) * (double)tempo / (ppqn * 1000000.0);
            return seconds;
        }

        private static byte[] GetSysexData(SysexEvent sysex)
        {
            if (sysex == null)
                return null;

            FieldInfo field = typeof(SysexEvent).GetField("data", BindingFlags.Instance | BindingFlags.NonPublic);
            return field == null ? null : field.GetValue(sysex) as byte[];
        }

        private static string FormatDuration(TimeSpan duration)
        {
            return string.Format("{0:D2}:{1:D2}:{2:D2}.{3:D3}",
                (int)duration.TotalHours, duration.Minutes, duration.Seconds, duration.Milliseconds);
        }
    }
}
