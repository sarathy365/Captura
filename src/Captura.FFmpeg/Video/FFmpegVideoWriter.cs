using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading;
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
        static string outputFolderName = null;
        static string additionalVideoInputArgsPre = null;
        static string additionalVideoInputArgsPost = null;

        static Queue<byte[]> framesToBeWritten = null;

        private static readonly string captureVideoLogPath = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().GetName().CodeBase).Substring(6) + @"\..\..\..\logs\captura_video.log";

        private void WriteLog(string content)
        {
            try
            {
                File.AppendAllText(@captureVideoLogPath, DateTime.Now.ToString("yyyy-MM-dd-HH-mm-ss") + ": " + content + "\n");
            }
            catch (Exception)
            { }
        }

        /// <summary>
        /// Creates a new instance of <see cref="FFmpegWriter"/>.
        /// </summary>
        public FFmpegWriter(FFmpegVideoWriterArgs Args)
        {
            var settings = ServiceProvider.Get<FFmpegSettings>();
            VideoInputArgs = Args;
            var crf = (51 * (100 - VideoInputArgs.VideoQuality)) / 99;

            outputFolderName = Args.FileName.Substring(0, VideoInputArgs.FileName.Length - 4);
            additionalVideoInputArgsPre = " -r " + Args.FrameRate + " -start_number 1 -i \"";
            additionalVideoInputArgsPost = "_%d.png\" -s " + Args.ImageProvider.Width + "*" + Args.ImageProvider.Height + " -vcodec libx264 -crf " + crf + " -pix_fmt " + settings.X264.PixelFormat + " -preset " + settings.X264.Preset;
            if (settings.Resize)
            {
                additionalVideoInputArgsPost += " -vf scale=" + settings.ResizeWidth + ":" + settings.ResizeHeight;
            }

            if (settings.RawBackup)
            {
                framesToBeWritten = new Queue<byte[]>();
                (new Thread(ThreadForAppendFrames)).Start();
            }

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
            try
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

                if (framesToBeWritten != null)
                {
                    framesToBeWritten.Enqueue(_videoBuffer);
                }
            }
            catch (Exception e)
            {
                WriteLog("WriteFrame() - " + e.Message + " - " + e.StackTrace);
                throw e;
            }
        }

        private void ThreadForAppendFrames()
        {
            while (true)
            {
                if (framesToBeWritten.Count > 0)
                {
                    AppendAllBytes(framesToBeWritten.Dequeue());
                }
                else
                {
                    Thread.Sleep(300);
                }
            }
        }

        private static long BeginTimeStamp = 0;
        private static Dictionary<long, long> TimeStamps = new Dictionary<long, long>();
        private static readonly string CapturaHomePath = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().GetName().CodeBase).Substring(6) + "\\..\\";

        private void WriteBitmapFile(string filename, int width, int height, byte[] imageData)
        {
            byte[] newData = new byte[imageData.Length];
            for (int x = 0; x < imageData.Length; x += 4)
            {
                byte[] pixel = new byte[4];
                Array.Copy(imageData, x, pixel, 0, 4);
                byte r = pixel[0];
                byte g = pixel[1];
                byte b = pixel[2];
                byte a = pixel[3];
                byte[] newPixel = new byte[] { r, g, b, a };
                Array.Copy(newPixel, 0, newData, x, 4);
            }
            imageData = newData;
            using (var stream = new MemoryStream(imageData))
            using (var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb))
            {
                BitmapData bmpData = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.WriteOnly, bmp.PixelFormat);
                IntPtr pNative = bmpData.Scan0;
                Marshal.Copy(imageData, 0, pNative, imageData.Length);
                bmp.UnlockBits(bmpData);
                bmp.Save(filename);
            }
        }

        public void AppendAllBytes(byte[] bytes)
        {
            try
            {
                long currentTimeStamp = DateTimeOffset.Now.ToUnixTimeMilliseconds();
                string toModifyFileName = null;
                if (currentTimeStamp - BeginTimeStamp > 10000)
                {
                    if (BeginTimeStamp != 0)
                    {
                        toModifyFileName = BeginTimeStamp.ToString();
                    }
                    else
                    {
                        Directory.CreateDirectory(outputFolderName);
                        File.WriteAllLines(outputFolderName + "\\" + "record.info", new string[] { additionalVideoInputArgsPre, additionalVideoInputArgsPost });
                    }
                    BeginTimeStamp = currentTimeStamp;
                    TimeStamps[BeginTimeStamp] = 1;
                    File.WriteAllText(outputFolderName + "\\" + BeginTimeStamp.ToString(), BeginTimeStamp.ToString());
                }
                WriteBitmapFile(outputFolderName + "\\" + BeginTimeStamp + "_" + TimeStamps[BeginTimeStamp]++ + ".png", VideoInputArgs.ImageProvider.Width, VideoInputArgs.ImageProvider.Height, bytes);
                if (toModifyFileName != null)
                {
                    ProcessStartInfo startInfo = new ProcessStartInfo();
                    startInfo.FileName = CapturaHomePath + "ffmpeg.exe";
                    startInfo.Arguments = additionalVideoInputArgsPre + outputFolderName + "\\" + toModifyFileName + additionalVideoInputArgsPost + " \"" + outputFolderName + "\\" + toModifyFileName + ".mp4\"";
                    startInfo.CreateNoWindow = true;
                    startInfo.WindowStyle = ProcessWindowStyle.Hidden;
                    Process proc = new Process();
                    proc.StartInfo = startInfo;
                    proc.Start();
                    proc.WaitForExit();
                    File.Delete(outputFolderName + "\\" + toModifyFileName);
                    string[] filePaths = Directory.GetFiles(outputFolderName, toModifyFileName + "_*.png", SearchOption.TopDirectoryOnly);
                    foreach (string filePath in filePaths)
                    {
                        File.Delete(filePath);
                    }
                }
            }
            catch (Exception e)
            {
                WriteLog("AppendAllBytes() - " + e.Message + " - " + e.StackTrace);
                throw e;
            }
        }
    }
}
