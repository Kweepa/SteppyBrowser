using System;
using System.Collections.Concurrent;

namespace SteppyBrowser
{
    /// <summary>
    /// Preallocated circular buffer for pending note-offs.
    /// Never allocates after initialization - safe for audio thread.
    /// </summary>
    public class XMINoteOffBuffer
    {
        private struct NoteOffEntry
        {
            public double noteOffTime;
            public int channel;
            public int key;
            public bool active; // false if this slot is free
        }
        
        private readonly NoteOffEntry[] buffer;
        private readonly int capacity;
        private int count;
        
        public int Count => count;
        
        public XMINoteOffBuffer(int maxCapacity = 512)
        {
            capacity = maxCapacity;
            buffer = new NoteOffEntry[capacity];
            count = 0;
        }
        
        // Add a note-off to the buffer
        public bool Add(double noteOffTime, int channel, int key)
        {
            // Find first free slot
            for (int i = 0; i < capacity; i++)
            {
                if (!buffer[i].active)
                {
                    buffer[i].noteOffTime = noteOffTime;
                    buffer[i].channel = channel;
                    buffer[i].key = key;
                    buffer[i].active = true;
                    count++;
                    return true;
                }
            }
            
            // Buffer full! This is bad but won't crash
            System.Diagnostics.Debug.WriteLine($"NoteOff buffer full (capacity: {capacity}). Increase buffer size.");
            return false;
        }
        
        // Process and remove note-offs that are due, returns number processed
        public int ProcessDueNoteOffs(double currentTime, ConcurrentQueue<XMIMusicCommand> commandQueue)
        {
            int processed = 0;
            
            for (int i = 0; i < capacity; i++)
            {
                if (buffer[i].active && buffer[i].noteOffTime <= currentTime)
                {
                    XMIMusicCommand cmd;
                    cmd.Type = XMIMusicCommandType.NoteOff;
                    cmd.Channel = buffer[i].channel;
                    cmd.Note = buffer[i].key;
                    cmd.Velocity = 0;
                    cmd.Program = 0;
                    cmd.Controller = 0;
                    cmd.Value = 0;
                    commandQueue.Enqueue(cmd);
                    
                    buffer[i].active = false;
                    count--;
                    processed++;
                }
            }
            
            return processed;
        }
        
        // Adjust all pending note-off times (for tempo changes)
        public void AdjustAllTimes(double currentTime, double tempoRatio)
        {
            for (int i = 0; i < capacity; i++)
            {
                if (buffer[i].active)
                {
                    double remainingTime = buffer[i].noteOffTime - currentTime;
                    double adjustedRemainingTime = remainingTime * tempoRatio;
                    buffer[i].noteOffTime = currentTime + adjustedRemainingTime;
                }
            }
        }
        
        // Clear all pending note-offs
        public void Clear()
        {
            for (int i = 0; i < capacity; i++)
            {
                buffer[i].active = false;
            }
            count = 0;
        }
    }
}
