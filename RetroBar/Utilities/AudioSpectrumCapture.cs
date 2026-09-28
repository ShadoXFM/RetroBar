using ManagedShell.Common.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using System;

namespace RetroBar.Utilities
{
    /// <summary>
    /// Captures whatever's currently playing through the default output device (WASAPI loopback -
    /// this is system audio, not tied to any particular app's own session) and reduces it to a
    /// per-channel loudness level, for the media player's VU-meter visualizer (see
    /// MediaPlayer.xaml.cs). Runs its own capture callback thread (NAudio's own, not the UI
    /// thread) - callers must marshal LevelUpdated back to the dispatcher themselves before
    /// touching any UI element.
    /// </summary>
    public class AudioSpectrumCapture : IDisposable
    {
        /// <summary>Fires on NAudio's own capture thread, not the UI thread - per-channel (left,
        /// right) loudness (RMS of each channel's own raw samples) for the stereo VU-meter
        /// visualizer (see MediaPlayer.xaml.cs). A mono source reports the same value for both.
        /// Hard-clamped to 0-1 - a VU meter's fill fraction has to be usable directly as a Width
        /// multiplier.</summary>
        public event Action<float, float> LevelUpdated;

        // Re-tuned for LevelCurveExponent below (a square-root curve saturates at a much smaller
        // input than a log curve would at the same scale).
        private const double LevelScale = 13;

        // A log curve compresses dynamic range hard once its input crosses roughly 1 - most of a
        // track's actual loudness swings ended up crammed into the flat, near-1.0 tail, reading
        // as "barely any difference between quiet and loud parts". A square-root curve compresses
        // far more gently, so more of the meter's own range actually reflects the track's real
        // loudness swings instead of pinning to the top.
        private const double LevelCurveExponent = 0.5;

        // Throttles how often LevelUpdated fires, independent of the capture callback's own
        // (format/buffer-size dependent) cadence - the meter only needs to look smooth, not
        // update as fast as the raw audio buffers arrive.
        private static readonly TimeSpan UpdateInterval = TimeSpan.FromMilliseconds(40);
        private DateTime _lastUpdate = DateTime.MinValue;

        private double _levelSumSquaresLeft;
        private double _levelSumSquaresRight;
        private int _levelSampleCount;

        private WasapiLoopbackCapture _capture;
        private readonly object _lifecycleLock = new();

        public void Start()
        {
            lock (_lifecycleLock)
            {
                if (_capture != null)
                {
                    return;
                }

                try
                {
                    _capture = new WasapiLoopbackCapture(GetDefaultRenderDevice());
                    _capture.DataAvailable += OnDataAvailable;
                    _capture.RecordingStopped += OnRecordingStopped;
                    _capture.StartRecording();
                }
                catch (Exception ex)
                {
                    // No default output device, the device is exclusive-mode locked by another
                    // app, or some other WASAPI-level failure - the visualizer just won't animate
                    // rather than crashing the media player over it.
                    ShellLogger.Debug($"AudioSpectrumCapture: Unable to start loopback capture: {ex.Message}");
                    _capture?.Dispose();
                    _capture = null;
                }
            }
        }

        public void Stop()
        {
            lock (_lifecycleLock)
            {
                if (_capture == null)
                {
                    return;
                }

                try
                {
                    _capture.DataAvailable -= OnDataAvailable;
                    _capture.RecordingStopped -= OnRecordingStopped;
                    _capture.StopRecording();
                }
                catch (Exception ex)
                {
                    ShellLogger.Debug($"AudioSpectrumCapture: Error stopping loopback capture: {ex.Message}");
                }
                finally
                {
                    _capture.Dispose();
                    _capture = null;
                }
            }
        }

        // WasapiLoopbackCapture's own parameterless constructor already picks the default render
        // device, but doing it explicitly lets a device-enumeration failure surface here (and be
        // caught by Start's own try/catch) instead of inside NAudio's constructor.
        private static MMDevice GetDefaultRenderDevice()
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }

        private void OnRecordingStopped(object sender, StoppedEventArgs e)
        {
            if (e.Exception != null)
            {
                ShellLogger.Debug($"AudioSpectrumCapture: Loopback capture stopped unexpectedly: {e.Exception.Message}");
            }
        }

        private void OnDataAvailable(object sender, WaveInEventArgs e)
        {
            WaveFormat format = _capture?.WaveFormat;
            if (format == null || format.BitsPerSample != 32 || format.Encoding != WaveFormatEncoding.IeeeFloat)
            {
                // WasapiLoopbackCapture always uses the render endpoint's own mix format, which is
                // IEEE float on every system this has been seen on - bail rather than misread
                // some other encoding's bytes as float samples if that's ever not true.
                return;
            }

            int channels = format.Channels;
            int frameSize = 4 * channels; // 4 bytes/sample (32-bit float) per channel
            int frameCount = e.BytesRecorded / frameSize;

            for (int frame = 0; frame < frameCount; frame++)
            {
                int frameOffset = frame * frameSize;
                float left = BitConverter.ToSingle(e.Buffer, frameOffset);
                float right = channels >= 2 ? BitConverter.ToSingle(e.Buffer, frameOffset + 4) : left;

                _levelSumSquaresLeft += (double)left * left;
                _levelSumSquaresRight += (double)right * right;
                _levelSampleCount++;
            }

            ProcessLevels();
        }

        private void ProcessLevels()
        {
            if (DateTime.Now - _lastUpdate < UpdateInterval || _levelSampleCount == 0)
            {
                return;
            }
            _lastUpdate = DateTime.Now;

            double rmsLeft = Math.Sqrt(_levelSumSquaresLeft / _levelSampleCount);
            double rmsRight = Math.Sqrt(_levelSumSquaresRight / _levelSampleCount);
            _levelSumSquaresLeft = 0;
            _levelSumSquaresRight = 0;
            _levelSampleCount = 0;
            float levelLeft = (float)Math.Min(1, Math.Pow(rmsLeft * LevelScale, LevelCurveExponent));
            float levelRight = (float)Math.Min(1, Math.Pow(rmsRight * LevelScale, LevelCurveExponent));
            LevelUpdated?.Invoke(levelLeft, levelRight);
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
