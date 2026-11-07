using NAudio.CoreAudioApi;
using NAudio.Wave;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace RetroBar.Utilities
{
    /// <summary>
    /// A loopback capture of an output device that hands the sound over as soon as the audio engine has it. NAudio's own WasapiLoopbackCapture
    /// asks for a 100 ms buffer and looks at it every 50 ms, so what the VU meter was shown was up to 50 ms old (and 25 on average) before it was
    /// even drawn. This one asks for a 20 ms buffer (the engine's period is 10) and looks at it every millisecond or two, on a thread of its own.
    ///
    /// A loopback capture has nothing to read while nothing plays: when no packet has come for a while it hands over silence of the same
    /// length, so that the meter (which falls on the data it is given) goes down to nothing.
    /// </summary>
    internal sealed class LowLatencyLoopback : IWaveIn
    {
        private const int BufferHundredNanoseconds = 200000; // 20 ms
        private static readonly TimeSpan SilenceAfter = TimeSpan.FromMilliseconds(40);
        private const int SilenceMilliseconds = 20;

        [DllImport("winmm.dll")]
        private static extern uint timeBeginPeriod(uint milliseconds);

        [DllImport("winmm.dll")]
        private static extern uint timeEndPeriod(uint milliseconds);

        private readonly MMDevice _device;
        private AudioClient _client;
        private AudioCaptureClient _capture;
        private Thread _thread;
        private volatile bool _stop;
        private bool _periodRaised;

        public LowLatencyLoopback(MMDevice device)
        {
            _device = device;
        }

        public WaveFormat WaveFormat { get; set; }

        public event EventHandler<WaveInEventArgs> DataAvailable;

        public event EventHandler<StoppedEventArgs> RecordingStopped;

        public void StartRecording()
        {
            if (_thread != null)
            {
                throw new InvalidOperationException("Already recording");
            }

            _client = _device.AudioClient;
            WaveFormat = _client.MixFormat;
            try
            {
                _client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.Loopback, BufferHundredNanoseconds, 0, WaveFormat, Guid.Empty);
            }
            catch (Exception)
            {
                // The system did not take the short buffer: a fresh client, with the usual 100 ms (what the data waits for is the polling, which
                // is as often as before, not the buffer's size).
                _client.Dispose();
                _client = new MMDeviceEnumerator().GetDevice(_device.ID).AudioClient;
                WaveFormat = _client.MixFormat;
                _client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.Loopback, 1000000, 0, WaveFormat, Guid.Empty);
            }

            _capture = _client.AudioCaptureClient;
            _client.Start();

            // The system's timer ticks every 15.6 ms unless a program asks for finer: a sleep of a millisecond is then 15.
            _periodRaised = timeBeginPeriod(1) == 0;

            _stop = false;
            _thread = new Thread(Run) { IsBackground = true, Name = "VuMeterLoopback", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
        }

        public void StopRecording()
        {
            _stop = true;
            Thread thread = _thread;
            if (thread != null && thread != Thread.CurrentThread)
            {
                thread.Join(500);
            }

            _thread = null;
        }

        private void Run()
        {
            Exception failure = null;
            try
            {
                int blockAlign = WaveFormat.BlockAlign;
                var buffer = new byte[blockAlign * WaveFormat.SampleRate]; // a second: far more than a packet
                var sinceData = Stopwatch.StartNew();

                while (!_stop)
                {
                    int packet = _capture.GetNextPacketSize();
                    if (packet == 0)
                    {
                        if (sinceData.Elapsed >= SilenceAfter)
                        {
                            int silent = Math.Min(buffer.Length, WaveFormat.SampleRate * SilenceMilliseconds / 1000 * blockAlign);
                            Array.Clear(buffer, 0, silent);
                            DataAvailable?.Invoke(this, new WaveInEventArgs(buffer, silent));
                            sinceData.Restart();
                        }

                        Thread.Sleep(1);
                        continue;
                    }

                    while (packet > 0 && !_stop)
                    {
                        IntPtr data = _capture.GetBuffer(out int frames, out AudioClientBufferFlags flags);
                        int bytes = Math.Min(frames * blockAlign, buffer.Length);
                        if ((flags & AudioClientBufferFlags.Silent) != 0)
                        {
                            Array.Clear(buffer, 0, bytes);
                        }
                        else
                        {
                            Marshal.Copy(data, buffer, 0, bytes);
                        }

                        _capture.ReleaseBuffer(frames);
                        DataAvailable?.Invoke(this, new WaveInEventArgs(buffer, bytes));
                        sinceData.Restart();
                        packet = _capture.GetNextPacketSize();
                    }
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                try
                {
                    _client?.Stop();
                }
                catch (Exception)
                {
                }

                RecordingStopped?.Invoke(this, new StoppedEventArgs(failure));
            }
        }

        public void Dispose()
        {
            StopRecording();
            if (_periodRaised)
            {
                timeEndPeriod(1);
                _periodRaised = false;
            }

            _capture = null;
            _client?.Dispose();
            _client = null;
        }
    }
}
