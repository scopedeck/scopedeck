namespace Siglent.Scope;

/// <summary>Conversion of SDS1000X-E waveform data (WF? DAT2) to physical units.</summary>
public static class WaveformDecoder
{
    /// <summary>ADC codes per vertical division.</summary>
    public const double CodesPerDivision = 25;

    /// <summary>Horizontal divisions on screen.</summary>
    public const int HorizontalDivisions = 14;

    /// <summary>
    /// volts = code * vdiv / 25 - offset, where each byte is a signed 8-bit code. The programming
    /// guide says "subtract 255", which would map both 0x00 and 0xFF to zero; two's complement is correct.
    /// </summary>
    public static float[] ToVolts(ReadOnlySpan<byte> codes, double voltsPerDiv, double offset)
    {
        var volts = new float[codes.Length];
        double scale = voltsPerDiv / CodesPerDivision;
        for (int i = 0; i < codes.Length; i++)
            volts[i] = (float)((sbyte)codes[i] * scale - offset);
        return volts;
    }

    /// <summary>
    /// Time of the first sample relative to the trigger: the screen's left edge.
    /// <paramref name="delay"/> is the screen-center time as the scope displays it ("Delay: -1.00ms"),
    /// which is the negated TRDL? value (verified on hardware; the older guide has the sign reversed).
    /// </summary>
    public static double FirstPointTime(double timePerDiv, double delay) =>
        delay - timePerDiv * HorizontalDivisions / 2;
}
