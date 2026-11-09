using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Dsp;
using NAudio.Wave;

namespace ZeroEqualizer;

internal sealed class RoutedAudio : IDisposable
{
    private const int PlaybackBufferMilliseconds = 65;
    private static readonly Regex CableName = new(
        @"^CABLE(?:-[A-D])? Input \(VB-Audio (?:Virtual )?Cable(?: [A-D])?\)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private readonly MMDeviceEnumerator enumerator = new();
    private MMDevice? input;
    private MMDevice? cableRecording;
    private MMDevice? output;
    private DeviceNotifications? notifications;
    private WasapiCapture? capture;
    private WasapiOut? playback;
    private BufferedWaveProvider? buffer;
    private FilteredAudio? filteredAudio;
    private volatile bool running;
    private string? error;
    private int disposed;

    public bool IsRunning => running;
    public string? Error => Volatile.Read(ref error);

    public static bool IsCable(AudioDevice device) => CableName.IsMatch(device.Name);

    public static bool IsPlaybackTarget(AudioDevice device) => IsPlaybackTarget(device.Name);

    private static bool IsPlaybackTarget(string name) =>
        !name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase) &&
        !name.Contains("Voicemeeter", StringComparison.OrdinalIgnoreCase) &&
        !name.Contains("Virtual Cable", StringComparison.OrdinalIgnoreCase);

    public RoutedAudio(Guid inputId, Guid outputId, EqProfile profile, bool bypass)
    {
        try
        {
            if (inputId == Guid.Empty || outputId == Guid.Empty || inputId == outputId)
                throw new InvalidOperationException("Choose a VB-CABLE input and a different playback output.");

            input = GetRenderDevice(inputId);
            output = GetRenderDevice(outputId);
            if (!CableName.IsMatch(input.FriendlyName))
                throw new InvalidOperationException("Selected app audio must come from a VB-Audio CABLE Input device.");
            if (!IsPlaybackTarget(output.FriendlyName))
                throw new InvalidOperationException("Send the processed audio to speakers or headphones outside VB-Audio.");

            cableRecording = GetCableRecordingDevice(input);
            var cableDriver = input.DeviceFriendlyName;
            notifications = new DeviceNotifications(this, input.ID, cableRecording.ID, output.ID);
            Marshal.ThrowExceptionForHR(enumerator.RegisterEndpointNotificationCallback(notifications));
            CheckInputIsDedicated(cableDriver);

            // VB-CABLE forwards its playback input to its recording output. This also works
            // when the driver cannot open a loopback stream on its rendering endpoint.
            capture = new WasapiCapture(cableRecording, true, 40);
            var format = capture.WaveFormat;
            if (format.Channels is < 1 or > 2 || format.SampleRate is < 8000 or > 192000)
                throw new InvalidOperationException("Set the CABLE Output recording device to a mono or stereo format between 8 and 192 kHz in Windows sound settings.");

            buffer = new BufferedWaveProvider(format)
            {
                BufferDuration = TimeSpan.FromMilliseconds(300),
                DiscardOnBufferOverflow = true
            };
            filteredAudio = new FilteredAudio(this, buffer, profile, bypass);
            playback = new WasapiOut(output, AudioClientShareMode.Shared, true, PlaybackBufferMilliseconds);
            playback.Init(filteredAudio.ToWaveProvider());
            capture.DataAvailable += OnDataAvailable;
            capture.RecordingStopped += OnRecordingStopped;
            playback.PlaybackStopped += OnPlaybackStopped;

            running = true;
            CheckInputIsDedicated(cableDriver);
            if (Error is { } routeError)
                throw new InvalidOperationException(routeError);
            capture.StartRecording();
            playback.Play();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Update(EqProfile profile, bool bypass) => filteredAudio?.Update(profile, bypass);

    private MMDevice GetRenderDevice(Guid id)
    {
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            bool matched = false;
            try
            {
                var key = PropertyKeys.PKEY_AudioEndpoint_GUID;
                matched = device.Properties.Contains(key) && device.Properties[key].Value is string endpointGuid &&
                    Guid.TryParse(endpointGuid, out var candidate) && candidate == id;
                if (matched)
                    return device;
            }
            finally
            {
                if (!matched)
                    device.Dispose();
            }
        }

        throw new InvalidOperationException("A selected playback device is no longer active.");
    }

    private MMDevice GetCableRecordingDevice(MMDevice renderDevice)
    {
        var name = renderDevice.FriendlyName.Replace(" Input (", " Output (", StringComparison.OrdinalIgnoreCase);
        var driver = renderDevice.DeviceFriendlyName;
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            bool matched = false;
            try
            {
                matched = StringComparer.OrdinalIgnoreCase.Equals(device.FriendlyName, name) &&
                    StringComparer.OrdinalIgnoreCase.Equals(device.DeviceFriendlyName, driver);
                if (matched)
                    return device;
            }
            finally
            {
                if (!matched)
                    device.Dispose();
            }
        }

        throw new InvalidOperationException("Enable the matching VB-Audio CABLE Output recording device in Windows Sound settings.");
    }

    private void CheckInputIsDedicated(string cableDriver)
    {
        foreach (var role in Enum.GetValues<Role>())
        {
            if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Render, role))
                continue;

            using var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, role);
            if (StringComparer.OrdinalIgnoreCase.Equals(defaultDevice.DeviceFriendlyName, cableDriver))
                throw new InvalidOperationException(DeviceNotifications.DefaultDeviceError);
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs args)
    {
        if (running && args.BytesRecorded > 0)
            buffer?.AddSamples(args.Buffer, 0, args.BytesRecorded);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs args) =>
        Fail("The VB-CABLE input stopped. Check the device and restart selected-app EQ.");

    private void OnPlaybackStopped(object? sender, StoppedEventArgs args) =>
        Fail("The playback output stopped. Check the device and restart selected-app EQ.");

    private void Fail(string message)
    {
        if (Volatile.Read(ref disposed) != 0)
            return;

        Interlocked.CompareExchange(ref error, message, null);
        running = false;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;

        running = false;
        if (notifications != null)
            enumerator.UnregisterEndpointNotificationCallback(notifications);
        if (capture != null)
        {
            capture.DataAvailable -= OnDataAvailable;
            capture.RecordingStopped -= OnRecordingStopped;
        }
        if (playback != null)
            playback.PlaybackStopped -= OnPlaybackStopped;

        playback?.Dispose();
        capture?.Dispose();
        output?.Dispose();
        cableRecording?.Dispose();
        input?.Dispose();
        enumerator.Dispose();
    }

    private sealed class DeviceNotifications(RoutedAudio owner, string inputId, string recordingId, string outputId) : IMMNotificationClient
    {
        public const string DefaultDeviceError = "Set speakers or headphones as the Windows default playback device. Leave VB-CABLE assigned only to the selected app.";

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string deviceId)
        {
            // A new default may be another input on the same cable. Recheck on explicit restart.
            if (flow == DataFlow.Render)
                owner.Fail("The Windows default playback device changed. Keep VB-CABLE assigned only to the selected app and restart app EQ.");
        }

        public void OnDeviceStateChanged(string deviceId, DeviceState state)
        {
            if (state != DeviceState.Active)
                OnDeviceRemoved(deviceId);
        }

        public void OnDeviceRemoved(string deviceId)
        {
            if (StringComparer.OrdinalIgnoreCase.Equals(inputId, deviceId) ||
                StringComparer.OrdinalIgnoreCase.Equals(recordingId, deviceId) ||
                StringComparer.OrdinalIgnoreCase.Equals(outputId, deviceId))
                owner.Fail("A selected audio device became unavailable. Reconnect it and restart selected-app EQ.");
        }

        public void OnDeviceAdded(string deviceId) { }
        public void OnPropertyValueChanged(string deviceId, PropertyKey key) { }
    }

    private sealed class FilteredAudio : ISampleProvider
    {
        private readonly RoutedAudio owner;
        private readonly BufferedWaveProvider buffer;
        private readonly ISampleProvider source;
        private readonly int fadeLength;
        private readonly int primeLength;
        private bool priming = true;
        private FilterBank current;
        private FilterBank? pending;
        private FilterBank? incoming;
        private int fadeFrame;

        public FilteredAudio(RoutedAudio owner, BufferedWaveProvider buffer, EqProfile profile, bool bypass)
        {
            this.owner = owner;
            this.buffer = buffer;
            source = buffer.ToSampleProvider();
            fadeLength = WaveFormat.SampleRate / 50;
            // Reserve a full render buffer and one additional engine packet before consuming input.
            primeLength = buffer.WaveFormat.AverageBytesPerSecond * (PlaybackBufferMilliseconds + 15) / 1000;
            current = new FilterBank(WaveFormat, profile, bypass);
        }

        public WaveFormat WaveFormat => source.WaveFormat;

        public void Update(EqProfile profile, bool bypass) =>
            Interlocked.Exchange(ref pending, new FilterBank(WaveFormat, profile, bypass));

        public int Read(float[] samples, int offset, int count)
        {
            if (!owner.IsRunning)
                return 0;

            if (priming && buffer.BufferedBytes < primeLength)
            {
                Array.Clear(samples, offset, count);
                return count;
            }

            int read = source.Read(samples, offset, count);
            priming = buffer.BufferedBytes == 0;
            int channels = WaveFormat.Channels;
            for (int frame = offset; frame < offset + read; frame += channels)
            {
                if (incoming == null && Volatile.Read(ref pending) != null)
                    incoming = Interlocked.Exchange(ref pending, null);
                if (incoming == null)
                {
                    for (int channel = 0; channel < channels; channel++)
                        samples[frame + channel] = current.Transform(channel, samples[frame + channel]);
                    continue;
                }

                float mix = (float)++fadeFrame / fadeLength;
                for (int channel = 0; channel < channels; channel++)
                {
                    float sample = samples[frame + channel];
                    float before = current.Transform(channel, sample);
                    float after = incoming.Transform(channel, sample);
                    samples[frame + channel] = before + (after - before) * mix;
                }

                if (fadeFrame >= fadeLength)
                {
                    current = incoming;
                    incoming = null;
                    fadeFrame = 0;
                }
            }

            return read;
        }
    }

    private sealed class FilterBank
    {
        private readonly BiQuadFilter[][] filters;
        private readonly float gain;

        public FilterBank(WaveFormat format, EqProfile profile, bool bypass)
        {
            var normalized = bypass ? EqProfile.Flat : profile.Normalize();
            gain = (float)Math.Pow(10, normalized.PreampDb / 20);
            var frequencies = EqProfile.Frequencies;
            filters = new BiQuadFilter[format.Channels][];
            for (int channel = 0; channel < filters.Length; channel++)
            {
                var chain = new List<BiQuadFilter>();
                int rate = format.SampleRate;
                if (normalized.Bass != 0)
                    chain.Add(BiQuadFilter.LowShelf(rate, 105, 1, (float)normalized.Bass));
                if (normalized.Treble != 0 && rate > 10000)
                    chain.Add(BiQuadFilter.HighShelf(rate, 5000, 1, (float)normalized.Treble));
                for (int band = 0; band < frequencies.Length; band++)
                {
                    if (normalized.Bands[band] != 0 && rate > frequencies[band] * 2)
                        chain.Add(BiQuadFilter.PeakingEQ(rate, frequencies[band], 1.4142f, (float)normalized.Bands[band]));
                }

                filters[channel] = chain.ToArray();
            }
        }

        public float Transform(int channel, float sample)
        {
            sample *= gain;
            foreach (var filter in filters[channel])
                sample = filter.Transform(sample);
            return sample;
        }
    }
}
