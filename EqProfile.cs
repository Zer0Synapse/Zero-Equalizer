using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;

namespace ZeroEqualizer;

public sealed record EqProfile(double Bass, double Treble, double[] Bands)
{
    private const int ReferenceRate = 48000;
    private const int BassFrequency = 105;
    private const int TrebleFrequency = 5000;
    private const double ShelfQ = 0.7071;
    private const double BandQ = 1.4142;
    private static readonly int[] BandFrequencies = { 31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };
    private static readonly ConditionalWeakTable<EqProfile, CachedResponse> Responses = new();

    public double PreampDb { get; init; }

    public static int[] Frequencies => (int[])BandFrequencies.Clone();
    public static EqProfile Flat => new(0, 0, new double[BandFrequencies.Length]);
    public static IReadOnlyDictionary<string, EqProfile> Presets { get; } = new ReadOnlyDictionary<string, EqProfile>(
        new Dictionary<string, EqProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["Flat"] = Flat,
            ["Bass Boost"] = new(6, 0, new double[] { 1, 1, 0, -1, 0, 0, 0, 0, 0, 0 }),
            ["Music"] = new(2, 2, new double[] { 0, 1, 0, 0, -1, 0, 1, 1, 0, 0 }),
            ["Gaming"] = new(-2, 1, new double[] { -2, -1, 0, -1, 0, 1, 2, 2, 1, 0 }),
            ["Voice"] = new(-4, 0, new double[] { -3, -2, -1, 0, 0, 1, 3, 2, 0, -1 })
        });

    public EqProfile Normalize()
    {
        var normalized = new double[BandFrequencies.Length];
        for (int band = 0; band < Math.Min(Bands?.Length ?? 0, normalized.Length); band++)
            normalized[band] = NormalizeGain(Bands![band]);
        return new EqProfile(NormalizeGain(Bass), NormalizeGain(Treble), normalized)
        {
            PreampDb = NormalizeGain(PreampDb, -24)
        };
    }

    public string ToApoFilters(bool bypass)
    {
        if (bypass)
            return "# Zero Equalizer bypassed" + Environment.NewLine;

        var response = Response;
        var config = new StringBuilder();
        if (response.Preamp != 0)
            config.AppendLine(FormattableString.Invariant($"Preamp: {response.Preamp:0.0} dB"));

        foreach (var filter in response.Filters)
        {
            // Narrowband playback devices can have a Nyquist limit below a treble band.
            bool needsRateCheck = filter.Frequency >= 4000;
            if (needsRateCheck)
                config.AppendLine(FormattableString.Invariant($"If: sampleRate > {filter.Frequency * 2}"));
            config.AppendLine(FormattableString.Invariant(
                $"Filter: ON {filter.ApoType} Fc {filter.Frequency} Hz Gain {filter.Gain:+0.0;-0.0;0.0} dB Q {filter.Q:0.0000}"));
            if (needsRateCheck)
                config.AppendLine("EndIf:");
        }

        return config.Length == 0 ? "# Zero Equalizer flat" + Environment.NewLine : config.ToString();
    }

    public double ResponseDb(double frequency)
    {
        if (!double.IsFinite(frequency)) return 0;
        var response = Response;
        var sample = new FrequencySample(Math.Clamp(frequency, 0, ReferenceRate / 2d), ReferenceRate);
        double db = response.Preamp;
        foreach (var filter in response.ReferenceFilters)
            db += filter.GainDb(sample);
        return db;
    }

    public double PreampForHeadroom()
    {
        var unattenuated = this with { PreampDb = 0 };
        const int steps = 360;
        double peak = unattenuated.ResponseDb(0);
        for (var step = 0; step <= steps; step++)
        {
            double frequency = 5 * Math.Pow(ReferenceRate / 10.0, step / (double)steps);
            peak = Math.Max(peak, unattenuated.ResponseDb(frequency));
        }

        // Leave 1 dB of estimated headroom and round down to the slider step.
        return Math.Clamp(Math.Floor((-peak - 1) * 2) / 2, -24, 0);
    }

    private CachedResponse Response
    {
        get
        {
            var cached = Responses.GetValue(this, static profile => new CachedResponse(profile));
            if (cached.Matches(this)) return cached;
            Responses.Remove(this);
            return Responses.GetValue(this, static profile => new CachedResponse(profile));
        }
    }

    private static double NormalizeGain(double value, double minimum = -12) => double.IsFinite(value)
        ? Math.Round(Math.Clamp(value, minimum, 12), 1, MidpointRounding.AwayFromZero)
        : 0;

    private static Filter[] CreateFilters(EqProfile profile)
    {
        var filters = new List<Filter>(BandFrequencies.Length + 2);
        if (profile.Bass != 0)
            filters.Add(new Filter("LSC", BassFrequency, profile.Bass, ShelfQ));
        for (int band = 0; band < BandFrequencies.Length; band++)
            if (profile.Bands[band] != 0)
                filters.Add(new Filter("PK", BandFrequencies[band], profile.Bands[band], BandQ));
        if (profile.Treble != 0)
            filters.Add(new Filter("HSC", TrebleFrequency, profile.Treble, ShelfQ));
        return filters.ToArray();
    }

    private sealed class CachedResponse
    {
        private readonly double bass;
        private readonly double treble;
        private readonly double[] bands;

        public CachedResponse(EqProfile source)
        {
            bass = source.Bass;
            treble = source.Treble;
            bands = source.Bands?.ToArray() ?? Array.Empty<double>();
            var normalized = source.Normalize();
            Filters = CreateFilters(normalized);
            ReferenceFilters = Filters.Select(filter => filter.AtRate(ReferenceRate)).ToArray();
            Preamp = normalized.PreampDb;
        }

        public Filter[] Filters { get; }
        public Biquad[] ReferenceFilters { get; }
        public double Preamp { get; }

        public bool Matches(EqProfile source) => bass.Equals(source.Bass) && treble.Equals(source.Treble)
            && bands.AsSpan().SequenceEqual(source.Bands.AsSpan());
    }

    private readonly record struct Filter(string ApoType, int Frequency, double Gain, double Q)
    {
        public Biquad AtRate(int rate)
        {
            // Robert Bristow-Johnson's Audio EQ Cookbook formulas.
            // https://www.w3.org/TR/audio-eq-cookbook/
            var (sine, cosine) = Math.SinCos(2 * Math.PI * Frequency / rate);
            double alpha = sine / (2 * Q);
            double a = Math.Pow(10, Gain / 40);
            if (ApoType == "PK")
                return new Biquad(1 + alpha * a, -2 * cosine, 1 - alpha * a,
                    1 + alpha / a, -2 * cosine, 1 - alpha / a);

            double sum = a + 1;
            double difference = a - 1;
            double spread = 2 * Math.Sqrt(a) * alpha;
            return ApoType == "LSC"
                ? new Biquad(
                    a * (sum - difference * cosine + spread), 2 * a * (difference - sum * cosine), a * (sum - difference * cosine - spread),
                    sum + difference * cosine + spread, -2 * (difference + sum * cosine), sum + difference * cosine - spread)
                : new Biquad(
                    a * (sum + difference * cosine + spread), -2 * a * (difference + sum * cosine), a * (sum + difference * cosine - spread),
                    sum - difference * cosine + spread, 2 * (difference - sum * cosine), sum - difference * cosine - spread);
        }
    }

    private readonly struct FrequencySample
    {
        public double Sine { get; }
        public double Cosine { get; }
        public double Sine2 { get; }
        public double Cosine2 { get; }

        public FrequencySample(double frequency, int rate)
        {
            (Sine, Cosine) = Math.SinCos(2 * Math.PI * frequency / rate);
            Sine2 = 2 * Sine * Cosine;
            Cosine2 = Cosine * Cosine - Sine * Sine;
        }
    }

    private readonly record struct Biquad(double B0, double B1, double B2, double A0, double A1, double A2)
    {
        public double GainDb(FrequencySample sample)
        {
            double numerator = MagnitudeSquared(B0, B1, B2, sample);
            double denominator = MagnitudeSquared(A0, A1, A2, sample);
            return 10 * Math.Log10(numerator / denominator);
        }

        private static double MagnitudeSquared(double c0, double c1, double c2, FrequencySample sample)
        {
            double real = c0 + c1 * sample.Cosine + c2 * sample.Cosine2;
            double imaginary = c1 * sample.Sine + c2 * sample.Sine2;
            return real * real + imaginary * imaginary;
        }
    }
}
