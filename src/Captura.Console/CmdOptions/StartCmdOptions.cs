﻿using System.Collections.Generic;
using CommandLine;
using CommandLine.Text;

// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable AutoPropertyCanBeMadeGetOnly.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global

namespace Captura
{
    // ReSharper disable once ClassNeverInstantiated.Global
    [Verb("start", HelpText = "Start Recording")]
    class StartCmdOptions : CommonCmdOptions
    {
        public StartCmdOptions()
        {
            var settings = ServiceProvider.Get<Settings>();

            settings.Keystrokes.Display = settings.Clicks.Display = false;

            FrameRate = settings.Video.FrameRate;
            VideoQuality = settings.Video.Quality;
            AudioQuality = settings.Audio.Quality;
        }

        [Option("delay", HelpText = "Milliseconds to wait before starting recording.")]
        public int Delay { get; set; }

        [Option("elapsetime", HelpText = "Include elapse time (default = false).")]
        public bool ElapseTime { get; set; }

        [Option("ffmpegresize", HelpText = "Resize the video for FFMPEG format (default = false).")]
        public bool FfmpegResize { get; set; }

        [Option("ffmpegresizewidth", HelpText = "Width of the video for FFMPEG format (default = 640).")]
        public int FfmpegResizeWidth { get; set; }

        [Option("ffmpegresizeheight", HelpText = "Height of the video for FFMPEG format (default = 480).")]
        public int FfmpegResizeHeight { get; set; }

        [Option('t', "length", HelpText = "Length of Recording in seconds.")]
        public int Length { get; set; }

        [Option('y', HelpText = "Overwrite existing file")]
        public bool Overwrite { get; set; }
        
        [Option("keys", HelpText = "Include Keystrokes in Recording (default = false).")]
        public bool Keys { get; set; }

        [Option("clicks", HelpText = "Include Mouse Clicks in Recording (default = false).")]
        public bool Clicks { get; set; }

        [Option("mic", Default = -1, HelpText = "Index of Microphone source. Default = -1 (No Microphone).")]
        public int Microphone { get; set; }

        [Option("speaker", Default = -1, HelpText = "Index of Speaker output source. Default = -1 (No Speaker).")]
        public int Speaker { get; set; }

        [Option('r', "framerate", HelpText = "Recording frame rate.")]
        public int FrameRate { get; set; }

        [Option("encoder", HelpText = "Video encoder to use.")]
        public string Encoder { get; set; }

        [Option("vq", HelpText = "Video Quality")]
        public int VideoQuality { get; set; }

        [Option("aq", HelpText = "Audio Quality")]
        public int AudioQuality { get; set; }

        [Option("webcam", Default = -1, HelpText = "Webcam to use. Default = -1 (No Webcam)")]
        public int Webcam { get; set; }

        [Usage]
        public static IEnumerable<Example> Examples
        {
            get
            {
                yield return new Example("Record 10 seconds with cursor and keystrokes and audio from first speaker output.", new StartCmdOptions
                {
                    Cursor = true,
                    Keys = true,
                    Length = 10,
                    Speaker = 0
                });

                yield return new Example("Record specific region", new StartCmdOptions
                {
                    Source = "100,100,300,400"
                });

                yield return new Example("Record as Gif to out.gif", new StartCmdOptions
                {
                    Encoder = "gif",
                    FileName = "out.gif"
                });
            }
        }
    }
}
