using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using NAudio.Midi;

namespace SteppyBrowser
{
    /// <summary>
    /// XMI Player using Windows' built-in MIDI synthesizer via NAudio.Midi
    /// </summary>
    public class XMIPlayer : IDisposable
    {
        private readonly XMISequencer sequencer;
        private readonly ConcurrentQueue<XMIMusicCommand> commandQueue;
        private MidiOut midiOut;
        private System.Windows.Forms.Timer playbackTimer;
        private readonly int sampleRate = 44100;
        private int samplesPerTick;
        private long totalSamplesProcessed;
        private DateTime lastTickTime;
        private bool disposed = false;

        public bool IsPlaying => sequencer != null && sequencer.IsLoaded && playbackTimer != null && playbackTimer.Enabled;

        public XMIPlayer(string xmiFilePath, bool loop = false)
        {
            commandQueue = new ConcurrentQueue<XMIMusicCommand>();

            // Initialize MIDI output - use Windows' built-in synthesizer
            try
            {
                // Find the Microsoft GS Wavetable Synth device
                int deviceNumber = -1;
                for (int i = 0; i < MidiOut.NumberOfDevices; i++)
                {
                    if (MidiOut.DeviceInfo(i).ProductName.Contains("GS Wavetable") || 
                        MidiOut.DeviceInfo(i).ProductName.Contains("Microsoft"))
                    {
                        deviceNumber = i;
                        break;
                    }
                }

                if (deviceNumber == -1 && MidiOut.NumberOfDevices > 0)
                {
                    // Fallback to first available device
                    deviceNumber = 0;
                }

                if (deviceNumber == -1)
                {
                    throw new Exception("No MIDI output device found");
                }

                midiOut = new MidiOut(deviceNumber);
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to initialize MIDI output: {ex.Message}", ex);
            }

            // Initialize sequencer
            try
            {
                sequencer = new XMISequencer(xmiFilePath, commandQueue, sampleRate, loop);
                if (!sequencer.IsLoaded)
                {
                    throw new Exception("Failed to load XMI file");
                }
            }
            catch (Exception ex)
            {
                midiOut?.Dispose();
                throw new Exception($"Failed to initialize XMI sequencer: {ex.Message}", ex);
            }

            // Initialize playback timer
            // Process at ~200Hz (every 5ms) for more responsive and accurate MIDI playback
            samplesPerTick = sampleRate / 200; // ~220 samples per tick at 44.1kHz
            playbackTimer = new System.Windows.Forms.Timer();
            playbackTimer.Interval = 5; // 5ms for better accuracy
            playbackTimer.Tick += PlaybackTimer_Tick;
        }

        public void Start()
        {
            if (disposed || sequencer == null || !sequencer.IsLoaded)
                return;

            // Reset sequencer
            sequencer.ResetToStart();
            totalSamplesProcessed = 0;
            lastTickTime = DateTime.Now;

            // Initialize all MIDI channels
            // NAudio uses channels 1-16, MIDI uses 0-15 internally
            // ChangeControl parameter order: (controller, value, channel)
            for (int i = 0; i < 16; i++)
            {
                // Set volume to maximum (Controller 7 = Volume)
                midiOut.Send(MidiMessage.ChangeControl(7, 127, i + 1).RawData);
            }

            playbackTimer.Start();
        }

        public void Stop()
        {
            playbackTimer?.Stop();

            // Send All Notes Off to all channels
            if (midiOut != null)
            {
                // NAudio uses channels 1-16, MIDI uses 0-15 internally
                // ChangeControl parameter order: (controller, value, channel)
                for (int channel = 0; channel < 16; channel++)
                {
                    // Controller 123 = All Notes Off, Controller 120 = All Sound Off
                    midiOut.Send(MidiMessage.ChangeControl(123, 0, channel + 1).RawData);
                    midiOut.Send(MidiMessage.ChangeControl(120, 0, channel + 1).RawData);
                }
            }
        }

        private void PlaybackTimer_Tick(object sender, EventArgs e)
        {
            if (disposed || sequencer == null || !sequencer.IsLoaded)
            {
                playbackTimer?.Stop();
                return;
            }

            // Calculate actual elapsed time for more accurate timing
            DateTime currentTime = DateTime.Now;
            double elapsedSeconds = (currentTime - lastTickTime).TotalSeconds;
            lastTickTime = currentTime;
            
            // Calculate samples to process based on actual elapsed time
            int samplesToProcess = (int)(elapsedSeconds * sampleRate);
            if (samplesToProcess <= 0) samplesToProcess = samplesPerTick; // Fallback to default
            
            // Process sequencer events
            sequencer.ProcessSamples(samplesToProcess);
            totalSamplesProcessed += samplesToProcess;

            // Process all pending MIDI commands
            while (commandQueue.TryDequeue(out XMIMusicCommand cmd))
            {
                try
                {
                    // NAudio uses channels 1-16, but MIDI internally uses 0-15
                    // Convert from 0-15 to 1-16 for NAudio methods
                    // Validate channel is in valid range (0-15)
                    if (cmd.Channel < 0 || cmd.Channel > 15)
                    {
                        System.Diagnostics.Debug.WriteLine($"Invalid channel value: {cmd.Channel}, skipping command");
                        continue;
                    }
                    
                    int naudioChannel = cmd.Channel + 1;
                    
                    switch (cmd.Type)
                    {
                        case XMIMusicCommandType.NoteOn:
                            midiOut.Send(MidiMessage.StartNote(cmd.Note, cmd.Velocity, naudioChannel).RawData);
                            break;
                        case XMIMusicCommandType.NoteOff:
                            midiOut.Send(MidiMessage.StopNote(cmd.Note, 0, naudioChannel).RawData);
                            break;
                        case XMIMusicCommandType.ProgramChange:
                            // Validate program is in valid range (0-127)
                            if (cmd.Program < 0 || cmd.Program > 127)
                            {
                                System.Diagnostics.Debug.WriteLine($"Invalid program value: {cmd.Program}, skipping");
                                continue;
                            }
                            midiOut.Send(MidiMessage.ChangePatch(cmd.Program, naudioChannel).RawData);
                            break;
                        case XMIMusicCommandType.ControllerChange:
                            // Validate controller and value are in valid ranges
                            if (cmd.Controller < 0 || cmd.Controller > 127 || cmd.Value < 0 || cmd.Value > 127)
                            {
                                System.Diagnostics.Debug.WriteLine($"Invalid controller change: controller={cmd.Controller}, value={cmd.Value}, skipping");
                                continue;
                            }
                            // ChangeControl parameter order: (controller, value, channel)
                            midiOut.Send(MidiMessage.ChangeControl(cmd.Controller, cmd.Value, naudioChannel).RawData);
                            break;
                        case XMIMusicCommandType.PolyphonicAftertouch:
                            // NAudio doesn't have a direct method for polyphonic aftertouch
                            // Send as raw MIDI message: 0xA0 + channel (0-15), note, pressure
                            byte[] polyAftertouch = new byte[] { (byte)(0xA0 + cmd.Channel), (byte)cmd.Note, (byte)cmd.Value };
                            midiOut.SendBuffer(polyAftertouch);
                            break;
                        case XMIMusicCommandType.PitchBend:
                            int pitchLSB = cmd.Value & 0x7F;
                            int pitchMSB = (cmd.Value >> 7) & 0x7F;
                            // Pitch Bend is MIDI message 0xE0, not a controller
                            // Use raw channel (0-15) for raw MIDI messages
                            byte[] pitchBend = new byte[] { (byte)(0xE0 + cmd.Channel), (byte)pitchLSB, (byte)pitchMSB };
                            midiOut.SendBuffer(pitchBend);
                            break;
                        case XMIMusicCommandType.ChannelAftertouch:
                            // Send as raw MIDI message: 0xD0 + channel (0-15), pressure
                            byte[] channelAftertouch = new byte[] { (byte)(0xD0 + cmd.Channel), (byte)cmd.Value };
                            midiOut.SendBuffer(channelAftertouch);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error sending MIDI command: {ex.Message}");
                }
            }

            // Check if playback should stop
            if (!sequencer.IsLoaded)
            {
                playbackTimer.Stop();
            }
        }

        public void Dispose()
        {
            if (!disposed)
            {
                Stop();
                playbackTimer?.Dispose();
                midiOut?.Dispose();
                disposed = true;
            }
        }
    }
}

