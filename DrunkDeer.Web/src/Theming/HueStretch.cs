namespace DrunkDeer.Web.Theming;

/// <summary>Knobs for <see cref="HueStretch.Stretch"/>. Defaults match the reference implementation.</summary>
/// <remarks>
/// Written out with initialisers and a parameterless constructor rather than as a positional record
/// so that <c>new HueStretchOptions()</c> means what it looks like it means. A positional record's
/// defaults only apply when its primary constructor is actually called, so <c>new()</c> would
/// zero-fill and silently ask for a palette of no entries. <c>default(HueStretchOptions)</c> still
/// zero-fills — structs always do — which <see cref="HueStretch.Stretch"/> rejects rather than
/// quietly honours.
/// </remarks>
public readonly record struct HueStretchOptions
{
    public HueStretchOptions() { }

    /// <summary>Number of entries in the output palette.</summary>
    public int N { get; init; } = 104;

    /// <summary>Colours less saturated than this are ignored.</summary>
    public float SaturationThreshold { get; init; } = 0.2f;

    /// <summary>Colours darker than this are ignored.</summary>
    public float ValueThreshold { get; init; } = 0.2f;

    /// <summary>Saturation every output colour is rebuilt at.</summary>
    public float Saturation { get; init; } = 0.9f;

    /// <summary>Value every output colour is rebuilt at.</summary>
    public float Value { get; init; } = 1.0f;

    /// <summary>Whether to smooth the palette so neighbouring hues bleed into each other.</summary>
    public bool Blur { get; init; } = true;

    /// <summary>Blur kernel width, as a fraction of <see cref="N"/>.</summary>
    public float BlurRadius { get; init; } = 0.8f;
}

/// <summary>
/// Builds a palette from a set of colours by redistributing their hues across it in proportion to
/// how often each hue occurs. A port of Tsoding's hue_stretch
/// (https://github.com/tsoding/hue_stretch), which does this for the pixels of an image; here the
/// input is the colours currently on the keyboard.
/// </summary>
/// <remarks>
/// <para>
/// The output is best understood as the hue distribution's cumulative curve: entry <c>i</c> holds
/// the hue at quantile <c>i/N</c>, so the palette gives a hue proportionally more room the more
/// keys wear it. Sampling the middle therefore yields the median hue by key count.
/// </para>
/// <para>
/// Two consequences fall out of that and are worth knowing rather than rediscovering:
/// dark and washed-out colours are dropped before binning, so an unlit or black key contributes
/// nothing; and a board wearing a single hue fills every entry with that one hue, so the palette
/// comes back uniform. Neither is a defect — the stretch only has work to do when a profile
/// carries several hues.
/// </para>
/// </remarks>
public static class HueStretch
{
    /// <summary>
    /// Stretches the hues of <paramref name="source"/> across a palette of
    /// <see cref="HueStretchOptions.N"/> colours.
    /// </summary>
    /// <returns>
    /// The palette. All white if nothing in <paramref name="source"/> clears the thresholds, which
    /// is the reference implementation's behaviour for an image with no colour in it.
    /// </returns>
    /// <param name="source">The colours to read hues from.</param>
    /// <param name="options">
    /// Defaults to the reference implementation's settings. Note this is deliberately nullable
    /// rather than <c>default</c>: <c>default(HueStretchOptions)</c> is a zero-filled struct, not
    /// the record's declared defaults, and would ask for a palette of no entries.
    /// </param>
    public static Srgb[] Stretch(IReadOnlyList<Srgb> source, HueStretchOptions? options = null)
    {
        var o = options ?? new HueStretchOptions();
        if (o.N <= 0) throw new ArgumentOutOfRangeException(nameof(options), "Palette size must be positive.");

        int n = o.N;
        float hueStep = 360f / n;

        var final = new Srgb[n];
        Array.Fill(final, new Srgb(255, 255, 255));

        // Bin by hue, ignoring anything too dark or too grey to have a hue worth trusting.
        var frequency = new int[n];
        int counted = 0;
        foreach (var pixel in source)
        {
            var (h, s, v) = ToHsv(pixel);
            if (s < o.SaturationThreshold) continue;
            if (v < o.ValueThreshold) continue;

            // The reference indexes straight off the hue and would run off the end of the array for
            // a hue of exactly 360. Clamping is a deliberate difference: same result everywhere the
            // C is well defined, minus the out-of-bounds write.
            int index = Math.Clamp((int)MathF.Floor(h / hueStep), 0, n - 1);
            frequency[index]++;
            counted++;
        }

        if (counted == 0) return final;

        var share = new float[n];
        for (int i = 0; i < n; i++) share[i] = (float)frequency[i] / counted;

        // Walk the hues in order, giving each a run of palette entries proportional to its share.
        // A hue whose run doesn't land on entry boundaries is split across two, weighted by overlap.
        var palette = new Vec4[n];
        float progress = 0f;
        for (int hueIndex = 0; hueIndex < n; hueIndex++)
        {
            if (progress >= n) break;

            float remaining = share[hueIndex] * n;
            while (remaining > 0f && progress < n)
            {
                int bucket = (int)MathF.Floor(progress);
                float step = MathF.Min(1 - Frac(progress), remaining);
                var colour = Normalise(FromHsv(hueIndex * hueStep, o.Saturation, o.Value));

                palette[bucket] += colour * step;
                progress += step;
                remaining -= step;
            }
        }

        if (o.Blur) palette = Blur(palette, o.BlurRadius);

        for (int i = 0; i < n; i++) final[i] = Denormalise(palette[i]);
        return final;
    }

    /// <summary>
    /// Smooths the palette with a gaussian, so a hue's colour bleeds into its neighbours' entries
    /// instead of changing abruptly at the boundary.
    /// </summary>
    private static Vec4[] Blur(Vec4[] palette, float radius)
    {
        int n = palette.Length;
        int count = (int)MathF.Floor(n * radius);

        // The reference divides by zero building a kernel of 0 or 1 samples. Nothing to smooth with
        // at that size anyway, so leave the palette alone.
        if (count < 2) return palette;

        float[] kernel = GaussianKernel(count);
        int half = count / 2;

        var blurred = new Vec4[n];
        for (int i = 0; i < n; i++)
        {
            var sum = default(Vec4);
            for (int j = 0; j < count; j++)
                sum += palette[Math.Clamp(j - half + i, 0, n - 1)] * kernel[j];

            blurred[i] = Vec4.Min(sum, new Vec4(1, 1, 1, 1));
        }
        return blurred;
    }

    /// <summary>A gaussian sampled over ±5σ and normalised so its weights sum to 1.</summary>
    private static float[] GaussianKernel(int count)
    {
        const float Sigma = 1.0f, Range = 5.0f;

        var kernel = new float[count];
        float sum = 0;
        for (int i = 0; i < count; i++)
        {
            float x = -Range + (2 * Range * i / (count - 1));
            float g = MathF.Exp(-x * x / (2 * Sigma * Sigma)) / (Sigma * MathF.Sqrt(2 * MathF.PI));
            kernel[i] = g;
            sum += g;
        }
        for (int i = 0; i < count; i++) kernel[i] /= sum;
        return kernel;
    }

    private static float Frac(float f) => f - MathF.Floor(f);

    private static Vec4 Normalise(Srgb c) => new(c.R / 255f, c.G / 255f, c.B / 255f, 1f);

    private static Srgb Denormalise(Vec4 v) => new(
        (byte)Math.Clamp(MathF.Round(v.X * 255), 0, 255),
        (byte)Math.Clamp(MathF.Round(v.Y * 255), 0, 255),
        (byte)Math.Clamp(MathF.Round(v.Z * 255), 0, 255));

    /// <summary>Hue in degrees (0-360), saturation and value in 0-1.</summary>
    public static (float H, float S, float V) ToHsv(Srgb c)
    {
        float r = c.R / 255f, g = c.G / 255f, b = c.B / 255f;
        float max = MathF.Max(r, MathF.Max(g, b));
        float min = MathF.Min(r, MathF.Min(g, b));
        float delta = max - min;

        if (delta < 1e-6f || max <= 0f) return (0f, 0f, max);

        float hue;
        if (max == r) hue = 60f * (((g - b) / delta) % 6f);
        else if (max == g) hue = 60f * (((b - r) / delta) + 2f);
        else hue = 60f * (((r - g) / delta) + 4f);

        if (hue < 0f) hue += 360f;
        return (hue, delta / max, max);
    }

    /// <summary>Builds a colour from a hue in degrees and a saturation and value in 0-1.</summary>
    public static Srgb FromHsv(float hue, float saturation, float value)
    {
        hue = ((hue % 360f) + 360f) % 360f;

        float c = value * saturation;
        float x = c * (1 - MathF.Abs((hue / 60f % 2f) - 1));
        float m = value - c;

        var (r, g, b) = (hue / 60f) switch
        {
            < 1f => (c, x, 0f),
            < 2f => (x, c, 0f),
            < 3f => (0f, c, x),
            < 4f => (0f, x, c),
            < 5f => (x, 0f, c),
            _ => (c, 0f, x),
        };

        return new Srgb(
            (byte)Math.Clamp(MathF.Round((r + m) * 255), 0, 255),
            (byte)Math.Clamp(MathF.Round((g + m) * 255), 0, 255),
            (byte)Math.Clamp(MathF.Round((b + m) * 255), 0, 255));
    }

    /// <summary>
    /// A 4-component accumulator. The palette is summed in floating point before being quantised,
    /// so partial contributions from a split hue run don't get rounded away one at a time.
    /// </summary>
    private readonly record struct Vec4(float X, float Y, float Z, float W)
    {
        public static Vec4 operator +(Vec4 a, Vec4 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z, a.W + b.W);
        public static Vec4 operator *(Vec4 a, float s) => new(a.X * s, a.Y * s, a.Z * s, a.W * s);

        public static Vec4 Min(Vec4 a, Vec4 b) => new(
            MathF.Min(a.X, b.X), MathF.Min(a.Y, b.Y), MathF.Min(a.Z, b.Z), MathF.Min(a.W, b.W));
    }
}
