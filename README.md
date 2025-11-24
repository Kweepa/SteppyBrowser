# SteppyBrowser

A Windows Forms application for browsing and playing audio files, images, and text files from your file system.

## Features

- **Audio Playback**: Supports multiple audio formats including WAV, MP3, OGG, WMA, VOC, and XMI files
- **XMI Playback**: Plays XMI (Extended MIDI) files using Windows' built-in MIDI synthesizer
- **Image Viewer**: Displays common image formats (JPG, PNG, BMP, GIF)
- **Text Viewer**: View text files, logs, and URL files
- **File Browser**: Tree view navigation with search functionality
- **Recent Folders**: Remembers recently opened folders
- **Restart Playback**: Click the same file again to restart playback

## Supported Formats

### Audio
- WAV
- MP3
- OGG (Vorbis)
- WMA
- VOC (Creative Voice File)
- XMI (Extended MIDI) - Uses Windows MIDI synthesizer

### Images
- JPG/JPEG
- PNG
- BMP
- GIF

### Text
- TXT
- LOG
- URL

## Requirements

- .NET Framework 4.8.1
- Windows OS (for MIDI synthesizer support)
- Visual Studio 2017 or later (for building)

## Dependencies

The project uses NuGet packages that will be automatically restored:
- NAudio 2.2.1
- NAudio.Midi 2.2.1
- NAudio.Vorbis 1.5.0
- TagLibSharp 2.3.0
- Various System.* packages

## Building

1. Clone the repository
2. Open `SteppyBrowser.sln` in Visual Studio
3. Restore NuGet packages (should happen automatically)
4. Build the solution (F6 or Build → Build Solution)

## Usage

1. Run `SteppyBrowser.exe`
2. Use File → Open Folder to browse your file system
3. Click on files in the tree view to play/view them
4. Click the same file again to restart playback
5. Use the search box to filter files

## XMI File Playback

XMI files are played using Windows' built-in MIDI synthesizer (Microsoft GS Wavetable Synth). No additional SoundFont files are required. The implementation includes:

- XMI file parsing and sequencing
- Sample-accurate MIDI event timing
- Support for tempo changes and time signatures
- Proper note-off handling with duration-based notes

## License

This project uses NAudio, which is licensed under the Microsoft Public License (Ms-PL).
