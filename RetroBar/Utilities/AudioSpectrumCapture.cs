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

        // A WasapiLoopbackCapture (a playback device's own output) or a plain WasapiCapture (a
        // recording device, e.g. a virtual cable/bus), depending on Settings.VuMeterCaptureDevice.
        private IWaveIn _capture;
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
                    _capture = CreateCapture(Settings.Instance.VuMeterCaptureDevice);
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

        private static IWaveIn CreateCapture(string deviceName)
        {
            using var enumerator = new MMDeviceEnumerator();

            if (!string.IsNullOrWhiteSpace(deviceName))
            {
                string wanted = deviceName.Trim();

                // Recording devices first (a virtual bus like "Voicemeeter Out B1" is one), then
                // playback devices via loopback.
                foreach (MMDevice device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                {
                    if (device.FriendlyName.Contains(wanted, StringComparison.OrdinalIgnoreCase))
                    {
                        return new WasapiCapture(device);
                    }
                }

                foreach (MMDevice device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    if (device.FriendlyName.Contains(wanted, StringComparison.OrdinalIgnoreCase))
                    {
                        return new WasapiLoopbackCapture(device);
                    }
                }

                ShellLogger.Debug($"AudioSpectrumCapture: No active audio device matching '{wanted}', using the default playback device instead.");
            }

            // WasapiLoopbackCapture's own parameterless constructor already picks the default
            // render device, but doing it explicitly lets a device-enumeration failure surface
            // here (and be caught by Start's own try/catch) instead of inside NAudio's constructor.
            return new WasapiLoopbackCapture(enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia));
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
            bool isFloat32 = format != null && format.BitsPerSample == 32 && format.Encoding == WaveFormatEncoding.IeeeFloat;
            bool isPcm16 = format != null && format.BitsPerSample == 16 && format.Encoding == WaveFormatEncoding.Pcm;
            if (!isFloat32 && !isPcm16)
            {
                // A loopback capture uses the playback endpoint's own mix format, which is IEEE
                // float on every system this has been seen on; a recording device (see
                // Settings.VuMeterCaptureDevice) can be 16-bit PCM. Bail rather than misread any
                // other encoding's bytes as samples.
                return;
            }

            int channels = format.Channels;
            int bytesPerSample = isFloat32 ? 4 : 2;
            int frameSize = bytesPerSample * channels;
            int frameCount = e.BytesRecorded / frameSize;

            for (int frame = 0; frame < frameCount; frame++)
            {
                int frameOffset = frame * frameSize;
                float left = ReadSample(e.Buffer, frameOffset, isFloat32);
                float right = channels >= 2 ? ReadSample(e.Buffer, frameOffset + bytesPerSample, isFloat32) : left;

                _levelSumSquaresLeft += (double)left * left;
                _levelSumSquaresRight += (double)right * right;
                _levelSampleCount++;
            }

            ProcessLevels();
        }

        private static float ReadSample(byte[] buffer, int offset, bool isFloat32)
        {
            return isFloat32 ? BitConverter.ToSingle(buffer, offset) : BitConverter.ToInt16(buffer, offset) / 32768f;
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
