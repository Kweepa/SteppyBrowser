using System;
using System.IO;
using System.Collections.Concurrent;

namespace SteppyBrowser
{
    /// <summary>
    /// This class reads an XMI file, finds the EVNT chunk, and
    /// plays it back in real-time by sending commands to a thread-safe queue.
    /// </summary>
    public class XMISequencer
    {
        // --- State ---
        public bool IsLoaded { get; private set; }
        private readonly BinaryReader reader;
        private readonly ConcurrentQueue<XMIMusicCommand> commandQueue;

        // --- Playback ---
        private double currentTimeInSong;
        private double nextEventTimeInSong;
        private long evntChunkEnd;
        private long evntChunkStart;
        
        // Track last status byte for Running Status support
        private byte lastStatus;
        
        // Sample-accurate timing
        private readonly int sampleRate;
        private long totalSamplesProcessed;

        // Constant for "FORM" read as a Big-Endian int
        private const int FORM_AS_INT = 1179603533;
        
        // Preallocated buffer to manage note-offs for XMI's "Note On + Duration" format
        private readonly XMINoteOffBuffer pendingNoteOffs;

        // Ticks Per Quarter Note (Timebase) - calculated from meta-events
        private int tpqn = 30; // Default until we extract from file

        // We'll calculate the secondsPerTick based on the BPM and TPQN.
        private double secondsPerTick;
        
        // Track meta-event data for TPQN calculation
        private double fileBPM = 120.0; // Default
        private int timeSignatureNumerator = 4;
        private int timeSignatureDenominator = 4;
        
        // Track end detection
        private bool hasReachedEVNTEnd = false;
        
        // Loop flag
        public bool Loop { get; set; }

        public XMISequencer(string filePath, ConcurrentQueue<XMIMusicCommand> queue, int audioSampleRate = 48000, bool loop = false)
        {
            commandQueue = queue;
            sampleRate = audioSampleRate;
            Loop = loop;
            pendingNoteOffs = new XMINoteOffBuffer(512); // Preallocate buffer (adjust size if needed)
            
            // Set the default tempo (120 BPM) using the provided TPQN
            secondsPerTick = (60.0 / 120.0) / this.tpqn;

            try
            {
                FileStream fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
                reader = new BinaryReader(fileStream);

                // Parse the file to find the EVNT chunk
                FindEventChunk(reader.BaseStream.Length);

                if (evntChunkEnd > 0)
                {
                    IsLoaded = true;
                    currentTimeInSong = 0.0;
                    
                    // "Prime the pump" by reading the *first* delta-time.
                    int firstDelta = ReadXmiDeltaTime(evntChunkEnd);
                    nextEventTimeInSong = firstDelta * secondsPerTick;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load XMI Sequencer: {ex.Message}");
                IsLoaded = false;
            }
        }
        
        /// <summary>
        /// Reset the sequencer to the beginning of the track.
        /// Clears all pending note-offs and resets playback state.
        /// </summary>
        public void ResetToStart()
        {
            // Reset reader position to start of EVNT chunk
            reader.BaseStream.Position = evntChunkStart;
            
            // Reset timing state
            currentTimeInSong = 0.0;
            totalSamplesProcessed = 0;
            hasReachedEVNTEnd = false;
            lastStatus = 0;
            
            // Clear all pending note-offs
            pendingNoteOffs.Clear();
            
            // Send All Notes Off and All Sound Off commands to all MIDI channels
            for (int channel = 0; channel < 16; channel++)
            {
                // All Notes Off (CC 123)
                XMIMusicCommand cmdNotesOff;
                cmdNotesOff.Type = XMIMusicCommandType.ControllerChange;
                cmdNotesOff.Channel = channel;
                cmdNotesOff.Controller = 123;
                cmdNotesOff.Value = 0;
                cmdNotesOff.Note = 0;
                cmdNotesOff.Program = 0;
                cmdNotesOff.Velocity = 0;
                commandQueue.Enqueue(cmdNotesOff);
                
                // All Sound Off (CC 120)
                XMIMusicCommand cmdSoundOff;
                cmdSoundOff.Type = XMIMusicCommandType.ControllerChange;
                cmdSoundOff.Channel = channel;
                cmdSoundOff.Controller = 120;
                cmdSoundOff.Value = 0;
                cmdSoundOff.Note = 0;
                cmdSoundOff.Program = 0;
                cmdSoundOff.Velocity = 0;
                commandQueue.Enqueue(cmdSoundOff);
            }
            
            // Read the first delta-time again to schedule the first event
            int firstDelta = ReadXmiDeltaTime(evntChunkEnd);
            nextEventTimeInSong = firstDelta * secondsPerTick;
        }
        
        /// <summary>
        /// Processes audio samples and fires events with sample-accurate timing.
        /// Called from the XMIPlayer's timer callback.
        /// </summary>
        public void ProcessSamples(int sampleCount)
        {
            if (!IsLoaded) return;

            // Calculate current time based on samples processed (sample-accurate timing)
            totalSamplesProcessed += sampleCount;
            currentTimeInSong = (double)totalSamplesProcessed / sampleRate;

            // --- 1. Process any pending note-offs ---
            pendingNoteOffs.ProcessDueNoteOffs(currentTimeInSong, commandQueue);
            
            // --- 2. Process all events scheduled for "now" ---
            while (IsLoaded && nextEventTimeInSong <= currentTimeInSong && !hasReachedEVNTEnd)
            {
                // This function will process the event at 'nextEventTimeInSong'
                // and then calculate the *next* 'nextEventTimeInSong'.
                ReadAndProcessNextEvent();
            }
            
            // --- 3. Check if track has truly ended ---
            // Track ends when we've processed all EVNT events AND all notes have finished
            if (hasReachedEVNTEnd && pendingNoteOffs.Count == 0)
            {
                if (Loop)
                {
                    // Loop back to the beginning
                    ResetToStart();
                }
                else
                {
                    // Stop playback
                    IsLoaded = false;
                }
            }
        }
        
        private void CalculateSecondsPerTick()
        {
            if (fileBPM > 0)
            {
                double timeSigRatio = (double)timeSignatureNumerator / timeSignatureDenominator;
                tpqn = (int)Math.Round(8333.0 / (fileBPM * timeSigRatio)); // QUANT_TIME equ 8333 from XMIDI32.ASM in AIL source
                double secondsPerQuarterNote = 60.0 / fileBPM;
                secondsPerTick = secondsPerQuarterNote / tpqn;
            }
        }

        /// <summary>
        /// This function is called *only* when the song clock has reached
        /// the time for the next scheduled event.
        /// It (1) processes that event, and (2) reads the *next* delta-time
        /// to schedule the *next* event.
        /// </summary>
        private void ReadAndProcessNextEvent()
        {
            // This is the timestamp for the event we are processing *now*.
            double thisEventTime = nextEventTimeInSong;

            // --- 1. Read and Process the Current Event ---
            if (reader.BaseStream.Position >= evntChunkEnd)
            {
                hasReachedEVNTEnd = true;
                return;
            }

            byte status = reader.ReadByte();

            // Handle Running Status: if high bit is NOT set, this is a data byte, not status
            // We need to reuse the last status byte
            if ((status & 0x80) == 0)
            {
                // This is actually a data byte - we have Running Status
                // Rewind one byte so the event handler can read it as data
                reader.BaseStream.Position--;
                
                // Reuse the last status byte
                if (lastStatus == 0)
                {
                    System.Diagnostics.Debug.WriteLine($"Running Status encountered but no previous status byte! Position: {reader.BaseStream.Position}");
                    IsLoaded = false;
                    return;
                }
                status = lastStatus;
            }
            else
            {
                // This is a real status byte, remember it for running status
                lastStatus = status;
            }

            byte eventType = (byte)(status & 0xF0);
            int channel = status & 0x0F;

            switch (eventType)
            {
                case 0x90: // Note On
                    byte key = reader.ReadByte();
                    byte velocity = reader.ReadByte();
                    
                    // Duration in XMI format is a Variable Length Quantity (VLQ)
                    int durationTicks = ReadMidiVLQ(evntChunkEnd);

                    if (velocity > 0)
                    {
                        commandQueue.Enqueue(new XMIMusicCommand { Type = XMIMusicCommandType.NoteOn, Channel = channel, Note = key, Velocity = velocity });
                        
                        // Schedule the note-off
                        double noteOffTime = thisEventTime + (durationTicks * secondsPerTick);
                        pendingNoteOffs.Add(noteOffTime, channel, key);
                    }
                    else
                    {
                        // Velocity 0 is a Note Off
                        commandQueue.Enqueue(new XMIMusicCommand { Type = XMIMusicCommandType.NoteOff, Channel = channel, Note = key });
                    }
                    break;
                
                case 0x80: // Note Off
                    byte keyOff = reader.ReadByte();
                    reader.ReadByte(); // Skip velocity
                    commandQueue.Enqueue(new XMIMusicCommand { Type = XMIMusicCommandType.NoteOff, Channel = channel, Note = keyOff });
                    break;
                
                case 0xC0: // Program (Instrument) Change
                    byte program = reader.ReadByte();
                    commandQueue.Enqueue(new XMIMusicCommand { Type = XMIMusicCommandType.ProgramChange, Channel = channel, Program = program });
                    break;

                case 0xB0: // Controller Change
                    byte controller = reader.ReadByte();
                    byte value = reader.ReadByte();
                    
                    commandQueue.Enqueue(new XMIMusicCommand { Type = XMIMusicCommandType.ControllerChange, Channel = channel, Controller = controller, Value = value });
                    break;
                
                case 0xA0: // Polyphonic Key Pressure (Aftertouch)
                    byte polyKey = reader.ReadByte();
                    byte polyPressure = reader.ReadByte();
                    commandQueue.Enqueue(new XMIMusicCommand { Type = XMIMusicCommandType.PolyphonicAftertouch, Channel = channel, Note = polyKey, Value = polyPressure });
                    break;
                    
                case 0xE0: // Pitch Bend
                    byte pitchLSB = reader.ReadByte();
                    byte pitchMSB = reader.ReadByte();
                    int pitchValue = pitchLSB | (pitchMSB << 7);
                    
                    commandQueue.Enqueue(new XMIMusicCommand { Type = XMIMusicCommandType.PitchBend, Channel = channel, Value = pitchValue });
                    break;
                    
                case 0xD0: // Channel Pressure (Aftertouch)
                    byte channelPressure = reader.ReadByte();
                    commandQueue.Enqueue(new XMIMusicCommand { Type = XMIMusicCommandType.ChannelAftertouch, Channel = channel, Value = channelPressure });
                    break;
                case 0xF0: 
                    if (status == 0xFF) 
                    {
                        byte metaType = reader.ReadByte(); 
                        int length = ReadMidiVLQ(evntChunkEnd); // VLQ is correct for meta-events

                        if (metaType == 0x51 && length == 3) // Set Tempo
                        {
                            int a = reader.ReadByte();
                            int b = reader.ReadByte();
                            int c = reader.ReadByte();
                            int microsecondsPerQuarterNote = (a << 16) | (b << 8) | c;
                            fileBPM = 60_000_000.0 / microsecondsPerQuarterNote;
                            
                            CalculateSecondsPerTick();
                        }
                        else if (metaType == 0x58 && length == 4) // Time Signature
                        {
                            byte numerator = reader.ReadByte();
                            byte denominatorPower = reader.ReadByte();
                            reader.ReadByte(); // ClocksPerClick
                            reader.ReadByte(); // 32nds per quarter
                            
                            timeSignatureNumerator = numerator;
                            timeSignatureDenominator = 1 << denominatorPower;

                            CalculateSecondsPerTick();
                        }
                        else
                        {
                            reader.ReadBytes(length);
                        }
                    }
                    else if (status == 0xF0) // SysEx Start
                    {
                        // Skip SysEx data until we hit 0xF7 (SysEx End)
                        while (reader.BaseStream.Position < evntChunkEnd && reader.ReadByte() != 0xF7) { }
                    }
                    break;
                default:
                    // Unknown or unsupported MIDI event
                    // We need to skip the right number of data bytes to stay in sync
                    System.Diagnostics.Debug.WriteLine($"Unknown MIDI Event: 0x{status:X2} at position {reader.BaseStream.Position}");
                    
                    // Try to determine data byte count based on status byte
                    int dataByteCount;
                    byte eventTypeUnknown = (byte)(status & 0xF0);
                    switch (eventTypeUnknown)
                    {
                        case 0x80: // Note Off
                        case 0x90: // Note On
                        case 0xA0: // Aftertouch
                        case 0xB0: // Controller
                        case 0xE0: // Pitch Bend
                            dataByteCount = 2;
                            break;
                        case 0xC0: // Program Change
                        case 0xD0: // Channel Pressure
                            dataByteCount = 1;
                            break;
                        default:
                            // If we really can't determine, we're in trouble
                            System.Diagnostics.Debug.WriteLine($"Cannot determine data byte count for event 0x{status:X2}. Stopping playback.");
                            IsLoaded = false;
                            return;
                    }
                    
                    // Skip the data bytes
                    System.Diagnostics.Debug.WriteLine($"Skipping {dataByteCount} data bytes for unknown event 0x{status:X2}");
                    for (int i = 0; i < dataByteCount; i++)
                    {
                        if (reader.BaseStream.Position < evntChunkEnd)
                            reader.ReadByte();
                    }
                    break;
            }

            // --- 2. Read the *Next* Delta-Time ---
            if (reader.BaseStream.Position < evntChunkEnd)
            {
                int nextDeltaTicks = ReadXmiDeltaTime(evntChunkEnd);
                double deltaTimeSeconds = nextDeltaTicks * secondsPerTick;
                nextEventTimeInSong += deltaTimeSeconds;
            }
            else
            {
                hasReachedEVNTEnd = true;
            }
        }

        // --- Parser "Setup" Logic (from V14) ---
        // This finds the EVNT chunk and positions the reader.

        private void FindEventChunk(long containerEndPosition)
        {
            while (reader.BaseStream.Position < containerEndPosition)
            {
                if (reader.BaseStream.Position + 8 > containerEndPosition) break;

                string chunkID = ReadChunkID();
                int chunkSize = ReadInt32BigEndian();

                if (chunkID == "XMID" && chunkSize == FORM_AS_INT)
                {
                    chunkID = "FORM";
                    chunkSize = ReadInt32BigEndian();
                }
                
                long subChunkEnd = reader.BaseStream.Position + chunkSize;

                switch (chunkID)
                {
                case "EVNT":
                    // This is what we want!
                    evntChunkStart = reader.BaseStream.Position;
                    evntChunkEnd = subChunkEnd;
                    return; // Stop parsing, we're ready to play
                
                case "FORM":
                    ReadChunkID(); // Read and discard Form Type
                    FindEventChunk(subChunkEnd); // Recurse inside
                    break;
                case "CAT ":
                    FindCatalogChunk(subChunkEnd); // Recurse inside
                    break;
                
                default:
                    // Not a container, skip it
                    reader.BaseStream.Seek(subChunkEnd, SeekOrigin.Begin);
                    break;
                }
                
                if (evntChunkEnd > 0) return; // Found it in a sub-chunk

                // Handle IFF Padding
                if (chunkSize % 2 != 0)
                {
                    reader.ReadByte();
                }
            }
        }

        private void FindCatalogChunk(long catalogEndPosition)
        {
            long startPos = reader.BaseStream.Position;
            string sniff = ReadChunkID();
            reader.BaseStream.Seek(startPos, SeekOrigin.Begin); // Rewind

            if (sniff == "FORM" || sniff == "XMID")
            {
                // Instrument Bank (UW file) - dive in
                FindEventChunk(catalogEndPosition);
            }
            else if (sniff == "MROF")
            {
                // Song Bank (AW file) - dive in
                FindEventChunk(catalogEndPosition);
            }
            else
            {
                // It's an offset list. This is the V14 logic.
                int numEntries = reader.ReadInt16(); // Little-Endian
                reader.ReadInt16(); // Skip 2 bytes

                for (int i = 0; i < numEntries; i++)
                {
                    if (reader.BaseStream.Position + 4 > catalogEndPosition) break;
                    int offset = reader.ReadInt32(); // Little-Endian
                    if (offset == 0) continue; 

                    long resumePos = reader.BaseStream.Position; 
                    try
                    {
                        reader.BaseStream.Seek(offset, SeekOrigin.Begin); 
                        // A sub-song is a FORM XMID
                        string formID = ReadChunkID();
                        int formSize = ReadInt32BigEndian();
                        long formEndPosition = reader.BaseStream.Position + formSize;
                        string formType = ReadChunkID();
                        
                        if(formID == "FORM" && formType == "XMID")
                        {
                            FindEventChunk(formEndPosition);
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Failed to parse sub-song at offset {offset}: {ex.Message}");
                    }
                    
                    reader.BaseStream.Seek(resumePos, SeekOrigin.Begin);
                    if (evntChunkEnd > 0) return; // Found it!
                }
            }
        }

        // --- Low-Level Readers ---

        private string ReadChunkID()
        {
            return new string(reader.ReadChars(4));
        }

        private int ReadInt32BigEndian()
        {
            byte[] bytes = reader.ReadBytes(4);
            Array.Reverse(bytes);
            return BitConverter.ToInt32(bytes, 0);
        }
        
        private int ReadMidiVLQ(long maxPosition)
        {
            int value = 0;
            byte b;
            do
            {
                if (reader.BaseStream.Position >= maxPosition)
                    throw new EndOfStreamException("ReadMidiVLQ read beyond chunk end.");
                b = reader.ReadByte();
                value = (value << 7) | (b & 0x7F);
            } while ((b & 0x80) != 0);
            return value;
        }

        private int ReadXmiDeltaTime(long maxPosition)
        {
            int deltaTime = 0;
            byte timeByte;
            
            while (reader.BaseStream.Position < maxPosition)
            {
                // Peek at the next byte
                timeByte = reader.ReadByte();

                // Check if it's an event byte
                if ((timeByte & 0x80) != 0)
                {
                    // It's an event. Rewind the stream and return the
                    // delta-time we've accumulated so far (which is 0
                    // if this was the first byte we read).
                    reader.BaseStream.Position--;
                    return deltaTime;
                }

                // It's a time byte.
                deltaTime += timeByte;

                // The XMI format says delta-time is a *series* of bytes
                // that are summed, *until* one is not 127.
                // (Most delta-times will be < 127, so this loop runs once)
                if (timeByte != 127)
                {
                    return deltaTime;
                }
            }
            
            // End of chunk
            return deltaTime;
        }
    }

    /// <summary>
    /// Static class for extracting metadata from XMI files without full playback initialization
    /// </summary>
    public static class XMIFileInfo
    {
        private const int FORM_AS_INT = 1179603533;

        public class XMIMetadata
        {
            public TimeSpan Duration { get; set; }
            public double BPM { get; set; }
            public int TimeSignatureNumerator { get; set; }
            public int TimeSignatureDenominator { get; set; }
            public int EventCount { get; set; }
            public bool Found { get; set; }
            public string ErrorMessage { get; set; }
        }

        public static XMIMetadata GetFileInfo(string filePath)
        {
            var metadata = new XMIMetadata
            {
                Found = false,
                BPM = 120.0,
                TimeSignatureNumerator = 4,
                TimeSignatureDenominator = 4
            };

            try
            {
                using (FileStream fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read))
                using (BinaryReader reader = new BinaryReader(fileStream))
                {
                    long evntChunkStart = 0;
                    long evntChunkEnd = 0;
                    
                    // Find EVNT chunk using recursive search
                    FindEventChunk(reader, fileStream.Length, ref evntChunkStart, ref evntChunkEnd);
                    
                    if (evntChunkEnd == 0)
                    {
                        metadata.ErrorMessage = "Could not find EVNT chunk";
                        return metadata;
                    }
                    
                    // Parse events to extract tempo, time signature, and calculate duration
                    fileStream.Position = evntChunkStart;
                    double currentTime = 0;
                    double fileBPM = 120.0;
                    int timeSigNum = 4;
                    int timeSigDen = 4;
                    int tpqn = 30;
                    double secondsPerTick = (60.0 / fileBPM) / tpqn;
                    int eventCount = 0;
                    double lastEventTime = 0;
                    
                    byte lastStatus = 0;
                    
                    while (fileStream.Position < evntChunkEnd)
                    {
                        // Read delta time
                        int deltaTime = 0;
                        while (fileStream.Position < evntChunkEnd)
                        {
                            byte timeByte = reader.ReadByte();
                            if ((timeByte & 0x80) != 0)
                            {
                                fileStream.Position--;
                                break;
                            }
                            deltaTime += timeByte;
                            if (timeByte != 127) break;
                        }
                        
                        if (fileStream.Position >= evntChunkEnd) break;
                        
                        currentTime += deltaTime * secondsPerTick;
                        lastEventTime = currentTime;
                        
                        byte status = reader.ReadByte();
                        
                        // Handle Running Status
                        if ((status & 0x80) == 0)
                        {
                            fileStream.Position--;
                            if (lastStatus == 0) break;
                            status = lastStatus;
                        }
                        else
                        {
                            lastStatus = status;
                        }
                        
                        byte eventType = (byte)(status & 0xF0);
                        
                        eventCount++;
                        
                        if (status == 0xFF) // Meta event
                        {
                            byte metaType = reader.ReadByte();
                            
                            // Read VLQ length
                            int length = 0;
                            byte b;
                            do
                            {
                                if (fileStream.Position >= evntChunkEnd) break;
                                b = reader.ReadByte();
                                length = (length << 7) | (b & 0x7F);
                            } while ((b & 0x80) != 0);
                            
                            if (metaType == 0x51 && length == 3) // Set Tempo
                            {
                                int a = reader.ReadByte();
                                int b1 = reader.ReadByte();
                                int c = reader.ReadByte();
                                int microsecondsPerQuarterNote = (a << 16) | (b1 << 8) | c;
                                fileBPM = 60_000_000.0 / microsecondsPerQuarterNote;
                                
                                // Recalculate secondsPerTick
                                double timeSigRatio = (double)timeSigNum / timeSigDen;
                                tpqn = (int)Math.Round(8333.0 / (fileBPM * timeSigRatio));
                                double secondsPerQuarterNote = 60.0 / fileBPM;
                                secondsPerTick = secondsPerQuarterNote / tpqn;
                            }
                            else if (metaType == 0x58 && length == 4) // Time Signature
                            {
                                timeSigNum = reader.ReadByte();
                                byte denominatorPower = reader.ReadByte();
                                reader.ReadByte(); // ClocksPerClick
                                reader.ReadByte(); // 32nds per quarter
                                timeSigDen = 1 << denominatorPower;
                                
                                // Recalculate secondsPerTick
                                double timeSigRatio = (double)timeSigNum / timeSigDen;
                                tpqn = (int)Math.Round(8333.0 / (fileBPM * timeSigRatio));
                                double secondsPerQuarterNote = 60.0 / fileBPM;
                                secondsPerTick = secondsPerQuarterNote / tpqn;
                            }
                            else
                            {
                                // Skip meta event data
                                for (int i = 0; i < length && fileStream.Position < evntChunkEnd; i++)
                                {
                                    reader.ReadByte();
                                }
                            }
                        }
                        else if (eventType == 0x90) // Note On
                        {
                            reader.ReadByte(); // key
                            reader.ReadByte(); // velocity
                            
                            // Read duration VLQ
                            int noteDuration = 0;
                            byte b;
                            do
                            {
                                if (fileStream.Position >= evntChunkEnd) break;
                                b = reader.ReadByte();
                                noteDuration = (noteDuration << 7) | (b & 0x7F);
                            } while ((b & 0x80) != 0);
                        }
                        else if (eventType == 0x80) // Note Off
                        {
                            reader.ReadByte(); // key
                            reader.ReadByte(); // velocity
                        }
                        else if (eventType == 0xC0 || eventType == 0xD0) // Program Change or Channel Pressure
                        {
                            reader.ReadByte(); // 1 data byte
                        }
                        else if (eventType == 0xE0 || eventType == 0xB0 || eventType == 0xA0) // Pitch Bend, Controller, Aftertouch
                        {
                            reader.ReadByte(); // 2 data bytes
                            reader.ReadByte();
                        }
                        else if (status == 0xF0) // SysEx
                        {
                            while (fileStream.Position < evntChunkEnd && reader.ReadByte() != 0xF7) { }
                        }
                    }
                    
                    metadata.Found = true;
                    metadata.Duration = TimeSpan.FromSeconds(lastEventTime);
                    metadata.BPM = fileBPM;
                    metadata.TimeSignatureNumerator = timeSigNum;
                    metadata.TimeSignatureDenominator = timeSigDen;
                    metadata.EventCount = eventCount;
                }
            }
            catch (Exception ex)
            {
                metadata.ErrorMessage = ex.Message;
            }

            return metadata;
        }

        private static void FindEventChunk(BinaryReader reader, long containerEndPosition, ref long evntChunkStart, ref long evntChunkEnd)
        {
            while (reader.BaseStream.Position < containerEndPosition)
            {
                if (reader.BaseStream.Position + 8 > containerEndPosition) break;

                string chunkID = ReadChunkID(reader);
                int chunkSize = ReadInt32BigEndian(reader);

                if (chunkID == "XMID" && chunkSize == FORM_AS_INT)
                {
                    chunkID = "FORM";
                    chunkSize = ReadInt32BigEndian(reader);
                }
                
                long subChunkEnd = reader.BaseStream.Position + chunkSize;

                switch (chunkID)
                {
                case "EVNT":
                    // This is what we want!
                    evntChunkStart = reader.BaseStream.Position;
                    evntChunkEnd = subChunkEnd;
                    return; // Stop parsing, we're ready
                
                case "FORM":
                    ReadChunkID(reader); // Read and discard Form Type
                    FindEventChunk(reader, subChunkEnd, ref evntChunkStart, ref evntChunkEnd);
                    break;
                case "CAT ":
                    FindCatalogChunk(reader, subChunkEnd, ref evntChunkStart, ref evntChunkEnd);
                    break;
                
                default:
                    // Not a container, skip it
                    reader.BaseStream.Seek(subChunkEnd, SeekOrigin.Begin);
                    break;
                }
                
                if (evntChunkEnd > 0) return; // Found it in a sub-chunk

                // Handle IFF Padding
                if (chunkSize % 2 != 0 && reader.BaseStream.Position < containerEndPosition)
                {
                    reader.ReadByte();
                }
            }
        }

        private static void FindCatalogChunk(BinaryReader reader, long catalogEndPosition, ref long evntChunkStart, ref long evntChunkEnd)
        {
            long startPos = reader.BaseStream.Position;
            string sniff = ReadChunkID(reader);
            reader.BaseStream.Seek(startPos, SeekOrigin.Begin); // Rewind

            if (sniff == "FORM" || sniff == "XMID")
            {
                // Instrument Bank (UW file) - dive in
                FindEventChunk(reader, catalogEndPosition, ref evntChunkStart, ref evntChunkEnd);
            }
            else if (sniff == "MROF")
            {
                // Song Bank (AW file) - dive in
                FindEventChunk(reader, catalogEndPosition, ref evntChunkStart, ref evntChunkEnd);
            }
            else
            {
                // It's an offset list. This is the V14 logic.
                int numEntries = reader.ReadInt16(); // Little-Endian
                reader.ReadInt16(); // Skip 2 bytes

                for (int i = 0; i < numEntries; i++)
                {
                    if (reader.BaseStream.Position + 4 > catalogEndPosition) break;
                    int offset = reader.ReadInt32(); // Little-Endian
                    if (offset == 0) continue; 

                    long resumePos = reader.BaseStream.Position; 
                    try
                    {
                        reader.BaseStream.Seek(offset, SeekOrigin.Begin); 
                        // A sub-song is a FORM XMID
                        string formID = ReadChunkID(reader);
                        int formSize = ReadInt32BigEndian(reader);
                        long formEndPosition = reader.BaseStream.Position + formSize;
                        string formType = ReadChunkID(reader);
                        
                        if(formID == "FORM" && formType == "XMID")
                        {
                            FindEventChunk(reader, formEndPosition, ref evntChunkStart, ref evntChunkEnd);
                        }
                    }
                    catch (Exception)
                    {
                        // Skip invalid entries
                    }
                    
                    reader.BaseStream.Seek(resumePos, SeekOrigin.Begin);
                    if (evntChunkEnd > 0) return; // Found it!
                }
            }
        }

        private static string ReadChunkID(BinaryReader reader)
        {
            return new string(reader.ReadChars(4));
        }

        private static int ReadInt32BigEndian(BinaryReader reader)
        {
            byte[] bytes = reader.ReadBytes(4);
            Array.Reverse(bytes);
            return BitConverter.ToInt32(bytes, 0);
        }
    }
}
