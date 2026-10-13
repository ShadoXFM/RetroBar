using ManagedShell.Common.Logging;
using NAudio.CoreAudioApi;
using NAudio.Dsp;
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

        /// <summary>Fires on the capture thread like LevelUpdated: the loudness (0-1, in decibels like the VU meter) of each of
        /// SpectrumBands frequency bands, lowest first, for the spectrum in the media popup.</summary>
        public event Action<float[]> SpectrumUpdated;

        public const int SpectrumBands = 14;
        private const int FftSize = 2048;
        private const int FftLog2 = 11;
        private const double LowestBandHz = 50;
        private const double HighestBandHz = 14000;

        private readonly float[] _fftWindow = new float[FftSize];
        private int _fftFilled;
        private int _fftWritten;
        private readonly Complex[] _fftBuffer = new Complex[FftSize];
        private readonly float[] _spectrumSamples = new float[FftSize];

        // The meter reads loudness in decibels, as a VU meter does: the bottom of the bar is this level (silence and anything
        // quieter), the top is full scale (0 dB), and a sound in between fills the bar by how many dB it is from the bottom. A
        // plain multiplier of the signal pinned the bar at the top for any ordinary music: the loudness of music sits within a
        // few dB of full scale on a digital bus, which a gain can't spread over the bar - decibels do.
        private const double FloorDb = -48;

        // The VU meter does not use all of that: music sits within a few dB of one level (about -27 dB on a bus), which on a bar over 48 dB
        // stays in the same stretch. Over the 24 dB from VuFloorDb to VuCeilingDb it moves twice as far for the same change in loudness, and
        // that level is a little further up the bar. Anything louder than the ceiling fills it.
        private const double VuFloorDb = -42;
        private const double VuCeilingDb = -18;

        // The popup's spectrum is louder than the signal by this (2 is 6 dB: every bar an eighth of the bar further up).
        private const double SpectrumGain = 2;

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

        // The output device being listened to (a loopback capture hears the sound before the device's own volume is applied, so
        // a quiet listening volume still read as a loud signal).
        private MMDevice _outputDevice;
        private float _outputVolume = 1;
        private DateTime _outputVolumeRead = DateTime.MinValue;
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
                    _capture = CreateCapture(Settings.Instance.VuMeterCaptureDevice, out _outputDevice);
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
                    _outputDevice?.Dispose();
                    _outputDevice = null;
                }
            }
        }

        private static IWaveIn CreateCapture(string deviceName, out MMDevice outputDevice)
        {
            outputDevice = null;
            var enumerator = new MMDeviceEnumerator();

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
                        outputDevice = device;
                        return new WasapiLoopbackCapture(device);
                    }
                }

                ShellLogger.Debug($"AudioSpectrumCapture: No active audio device matching '{wanted}', using the default playback device instead.");
            }

            // WasapiLoopbackCapture's own parameterless constructor already picks the default
            // render device, but doing it explicitly lets a device-enumeration failure surface
            // here (and be caught by Start's own try/catch) instead of inside NAudio's constructor.
            outputDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return new WasapiLoopbackCapture(outputDevice);
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

                _spectrumSamples[_fftWritten] = (left + right) / 2;
                _fftWritten = (_fftWritten + 1) % FftSize;
                _fftFilled = Math.Min(FftSize, _fftFilled + 1);
            }

            ProcessLevels(format.SampleRate);
        }

        private static float ReadSample(byte[] buffer, int offset, bool isFloat32)
        {
            return isFloat32 ? BitConverter.ToSingle(buffer, offset) : BitConverter.ToInt16(buffer, offset) / 32768f;
        }

        private static float ToVuLevel(double rms)
        {
            double db = 20 * Math.Log10(Math.Max(rms, 1e-6));
            return (float)Math.Clamp((db - VuFloorDb) / (VuCeilingDb - VuFloorDb), 0, 1);
        }

        private static float ToLevel(double rms)
        {
            double db = 20 * Math.Log10(Math.Max(rms, 1e-6));
            return (float)Math.Clamp((db - FloorDb) / -FloorDb, 0, 1);
        }

        private float GetOutputVolume()
        {
            if (_outputDevice == null)
            {
                return 1;
            }

            if (DateTime.Now - _outputVolumeRead > TimeSpan.FromMilliseconds(250))
            {
                _outputVolumeRead = DateTime.Now;

                try
                {
                    AudioEndpointVolume endpoint = _outputDevice.AudioEndpointVolume;
                    _outputVolume = endpoint.Mute ? 0 : endpoint.MasterVolumeLevelScalar;
                }
                catch (Exception)
                {
                    _outputVolume = 1;
                }
            }

            return _outputVolume;
        }

        private void ProcessLevels(int sampleRate)
        {
            if (DateTime.Now - _lastUpdate < UpdateInterval || _levelSampleCount == 0)
            {
                return;
            }
            _lastUpdate = DateTime.Now;

            // As loud as it is heard: scaled by the output device's volume (and silent while it is muted).
            double volume = GetOutputVolume();
            double rmsLeft = Math.Sqrt(_levelSumSquaresLeft / _levelSampleCount) * volume;
            double rmsRight = Math.Sqrt(_levelSumSquaresRight / _levelSampleCount) * volume;
            _levelSumSquaresLeft = 0;
            _levelSumSquaresRight = 0;
            _levelSampleCount = 0;
            float levelLeft = ToVuLevel(rmsLeft);
            float levelRight = ToVuLevel(rmsRight);
            LevelUpdated?.Invoke(levelLeft, levelRight);

            if (SpectrumUpdated != null && _fftFilled == FftSize)
            {
                SpectrumUpdated(ComputeSpectrum(sampleRate, volume));
            }
        }

        // The last FftSize samples through a Hann window and an FFT, the magnitudes of the bins gathered into bands spaced evenly
        // on a log scale (as the ear hears pitch), each band's loudest bin taken and turned into the 0-1 decibel level.
        private float[] ComputeSpectrum(int sampleRate, double volume)
        {
            for (int i = 0; i < FftSize; i++)
            {
                float sample = _spectrumSamples[(_fftWritten + i) % FftSize];
                double window = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (FftSize - 1));
                _fftBuffer[i].X = (float)(sample * window);
                _fftBuffer[i].Y = 0;
            }

            FastFourierTransform.FFT(true, FftLog2, _fftBuffer);

            var bands = new float[SpectrumBands];
            double binHz = (double)sampleRate / FftSize;
            double highest = Math.Min(HighestBandHz, sampleRate / 2.0);

            for (int band = 0; band < SpectrumBands; band++)
            {
                double fromHz = LowestBandHz * Math.Pow(highest / LowestBandHz, (double)band / SpectrumBands);
                double toHz = LowestBandHz * Math.Pow(highest / LowestBandHz, (double)(band + 1) / SpectrumBands);
                int fromBin = Math.Max(1, (int)Math.Floor(fromHz / binHz));
                int toBin = Math.Max(fromBin, (int)Math.Ceiling(toHz / binHz) - 1);

                double peak = 0;
                for (int bin = fromBin; bin <= toBin && bin < FftSize / 2; bin++)
                {
                    double magnitude = Math.Sqrt(_fftBuffer[bin].X * _fftBuffer[bin].X + _fftBuffer[bin].Y * _fftBuffer[bin].Y);
                    peak = Math.Max(peak, magnitude);
                }

                // A full-scale sine through the window reads FftSize / 4 in its bin once the FFT is scaled by 1 / FftSize (NAudio's
                // forward transform does that): scale back to 1 for a full-scale sine, so 0 dB is full scale as in the VU meter.
                bands[band] = ToLevel(peak * 4 * volume * SpectrumGain);
            }

            return bands;
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
