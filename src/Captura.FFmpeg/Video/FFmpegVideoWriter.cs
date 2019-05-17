﻿using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading.Tasks;

namespace Captura.Models
{
    /// <summary>
    /// Encode Video using FFmpeg.exe
    /// </summary>
    public class FFmpegWriter : IVideoFileWriter
    {
        readonly NamedPipeServerStream _audioPipe;

        readonly Process _ffmpegProcess;
        readonly NamedPipeServerStream _ffmpegIn;
        readonly byte[] _videoBuffer;

        static string GetPipeName() => $"captura-{Guid.NewGuid()}";

        static FFmpegVideoWriterArgs VideoInputArgs = null;

        /// <summary>
        /// Creates a new instance of <see cref="FFmpegWriter"/>.
        /// </summary>
        public FFmpegWriter(FFmpegVideoWriterArgs Args)
        {
            VideoInputArgs = Args;

            var settings = ServiceProvider.Get<FFmpegSettings>();

            _videoBuffer = new byte[Args.ImageProvider.Width * Args.ImageProvider.Height * 4];

            Console.WriteLine($"Video Buffer Allocated: {_videoBuffer.Length}");

            var videoPipeName = GetPipeName();

            var argsBuilder = new FFmpegArgsBuilder();

            argsBuilder.AddInputPipe(videoPipeName)
                .AddArg("-thread_queue_size 512")
                .AddArg($"-framerate {Args.FrameRate}")
                .SetFormat("rawvideo")
                .AddArg("-pix_fmt rgb32")
                .SetVideoSize(Args.ImageProvider.Width, Args.ImageProvider.Height);

            var output = argsBuilder.AddOutputFile(Args.FileName)
                .AddArg(Args.VideoArgsProvider(Args.VideoQuality))
                .SetFrameRate(Args.FrameRate);
            
            if (settings.Resize)
            {
                var width = settings.ResizeWidth;
                var height = settings.ResizeHeight;

                if (width % 2 == 1)
                    ++width;

                if (height % 2 == 1)
                    ++height;

                output.AddArg($"-vf scale={width}:{height}");
            }

            if (Args.AudioProvider != null)
            {
                var audioPipeName = GetPipeName();

                argsBuilder.AddInputPipe(audioPipeName)
                    .AddArg("-thread_queue_size 512")
                    .SetFormat("s16le")
                    .SetAudioCodec("pcm_s16le")
                    .SetAudioFrequency(Args.Frequency)
                    .SetAudioChannels(Args.Channels);

                output.AddArg(Args.AudioArgsProvider(Args.AudioQuality));

                // UpdatePeriod * Frequency * (Bytes per Second) * Channels * 2
                var audioBufferSize = (int)((1000.0 / Args.FrameRate) * 44.1 * 2 * 2 * 2);

                _audioPipe = new NamedPipeServerStream(audioPipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, audioBufferSize);
            }

            _ffmpegIn = new NamedPipeServerStream(videoPipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, _videoBuffer.Length);

            output.AddArg(Args.OutputArgs);

            _ffmpegProcess = FFmpegService.StartFFmpeg(argsBuilder.GetArgs(), Args.FileName);
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            _ffmpegIn.Dispose();

            _audioPipe?.Dispose();

            _ffmpegProcess.WaitForExit();
        }

        /// <summary>
        /// Gets whether audio is supported.
        /// </summary>
        public bool SupportsAudio { get; } = true;

        bool _firstAudio = true;

        Task _lastAudio;

        /// <summary>
        /// Write audio block to Audio Stream.
        /// </summary>
        /// <param name="Buffer">Buffer containing audio data.</param>
        /// <param name="Length">Length of audio data in bytes.</param>
        public void WriteAudio(byte[] Buffer, int Length)
        {
            if (_ffmpegProcess.HasExited)
            {
                throw new Exception("An Error Occurred with FFmpeg");
            }

            if (_firstAudio)
            {
                if (!_audioPipe.WaitForConnection(5000))
                {
                    throw new Exception("Cannot connect Audio pipe to FFmpeg");
                }

                _firstAudio = false;
            }

            _lastAudio?.Wait();

            _lastAudio = _audioPipe.WriteAsync(Buffer, 0, Length);
        }

        bool _firstFrame = true;

        Task _lastFrameTask;

        /// <summary>
        /// Writes an Image frame.
        /// </summary>
        public void WriteFrame(IBitmapFrame Frame)
        {
            if (_ffmpegProcess.HasExited)
            {
                Frame.Dispose();
                throw new Exception($"An Error Occurred with FFmpeg, Exit Code: {_ffmpegProcess.ExitCode}");
            }
            
            if (_firstFrame)
            {
                if (!_ffmpegIn.WaitForConnection(5000))
                {
                    throw new Exception("Cannot connect Video pipe to FFmpeg");
                }

                _firstFrame = false;
            }

            _lastFrameTask?.Wait();

            if (!(Frame is RepeatFrame))
            {
                using (Frame)
                {
                    Frame.CopyTo(_videoBuffer, _videoBuffer.Length);
                }
            }

            _lastFrameTask = _ffmpegIn.WriteAsync(_videoBuffer, 0, _videoBuffer.Length);

            AppendAllBytes(_videoBuffer);

        }

        private static long BeginTimeStamp = 0;
        private static string CapturaHomePath = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().GetName().CodeBase).Substring(6) + "\\..\\";

        public static void AppendAllBytes(byte[] bytes)
        {
            string outputFolderName = VideoInputArgs.FileName.Substring(0, VideoInputArgs.FileName.Length-4);
            long currentTimeStamp = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            string toModifyFileName = null;

            if (currentTimeStamp - BeginTimeStamp > 5000)
            {
                if (BeginTimeStamp != 0)
                {
                    toModifyFileName = BeginTimeStamp.ToString();
                }
                else
                {
                    Directory.CreateDirectory(outputFolderName);
                }
                BeginTimeStamp = currentTimeStamp;
            }
            using (var stream = new FileStream(outputFolderName + "\\" + BeginTimeStamp.ToString(), FileMode.Append))
            {
                stream.Write(bytes, 0, bytes.Length);
            }
            if (toModifyFileName != null)
            {
                var settings = ServiceProvider.Get<FFmpegSettings>();
                ProcessStartInfo startInfo = new ProcessStartInfo();
                startInfo.FileName = CapturaHomePath + "ffmpeg.exe";
                toModifyFileName = outputFolderName + "\\" + toModifyFileName;
                startInfo.Arguments = "-thread_queue_size 512 -framerate " + VideoInputArgs.FrameRate + " -f rawvideo -pix_fmt rgb32 -video_size " + VideoInputArgs.ImageProvider.Width + "x" + VideoInputArgs.ImageProvider.Height + " -i \"" + toModifyFileName + "\" -vcodec libx264 -crf 36 -pix_fmt yuv420p -preset ultrafast -r 5";
                if (settings.Resize)
                {
                    startInfo.Arguments += "-vf scale=" + settings.ResizeWidth + ":" + settings.ResizeHeight;
                }
                startInfo.Arguments += " \"" + toModifyFileName + ".mp4\"";
                startInfo.CreateNoWindow = true;
                startInfo.WindowStyle = ProcessWindowStyle.Hidden;
                Process proc = new Process();
                proc.StartInfo = startInfo;
                proc.Start();
                proc.WaitForExit();
                File.Delete(toModifyFileName);
            }
        }
    }
}
