using System;

namespace SteppyBrowser
{
    /// <summary>
    /// A simple struct to define a command for our synthesizer.
    /// We use this to safely pass data to the audio thread queue.
    /// </summary>
    public enum XMIMusicCommandType { NoteOn, NoteOff, ProgramChange, ControllerChange, PolyphonicAftertouch, PitchBend, ChannelAftertouch }

    public struct XMIMusicCommand
    {
        public XMIMusicCommandType Type;
        public int Channel;
        
        // NoteOn/Off
        public int Note;
        public int Velocity;

        // ProgramChange
        public int Program;

        // ControllerChange
        public int Controller;
        public int Value;
    }
}
