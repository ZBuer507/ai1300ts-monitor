using System;

internal static class GpuLevelSenseSmoke
{
    internal static int Main()
    {
        Check(GpuLevelSense.HasExpectedReadLayout(), "NVAPI x64 structure layout");
        var flat = GpuLevelSense.Decode(new byte[] { 0x00, 0x00, 0x00, 0x00, 0xF0, 0xB5 }, "test GPU");
        Check(flat.RawZ == -3915 && flat.Model == "test GPU", "signed big-endian and model");
        Check(flat.PitchDegrees == 0 && flat.RollDegrees == 0, "horizontal orientation");
        var tilted = GpuLevelSense.Decode(new byte[] { 0x00, 0x44, 0xFF, 0xBC, 0xF0, 0xB5 }, "test GPU");
        Check(tilted.RawX == 68 && tilted.RawY == -68, "signed X and Y");
        Check(Math.Abs(tilted.PitchDegrees - 0.995) < 0.003, "one-degree positive pitch");
        Check(Math.Abs(tilted.RollDegrees + 0.995) < 0.003, "one-degree negative roll");
        Check(Math.Abs(tilted.DeviationFrom(flat) - 1.407) < 0.004, "combined tilt");
        var upright = GpuLevelSense.Decode(new byte[] { 0x0F, 0x4B, 0, 0, 0, 0 }, "test GPU");
        Check(upright.PitchDegrees == 90 && upright.RollDegrees == 0, "vertical mount");
        Check(upright.DeviationFrom(upright) == 0 && upright.DeviationFrom(flat) == 90, "vertical baseline");
        var inverted = GpuLevelSense.Decode(new byte[] { 0, 0, 0, 0, 0x0F, 0x4B }, "test GPU");
        Check(inverted.DeviationFrom(flat) == 180, "inversion");
        ExpectInvalid(new byte[6], "blank response");
        ExpectInvalid(new byte[] { 255, 255, 255, 255, 255, 255 }, "absent response");
        Console.WriteLine("GPU angle decoding and NVAPI ABI checks passed without accessing hardware.");
        return 0;
    }

    private static void Check(bool result, string label)
    {
        if (!result)
            throw new InvalidOperationException(label);
    }

    private static void ExpectInvalid(byte[] axes, string label)
    {
        try
        {
            GpuLevelSense.Decode(axes, String.Empty);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        throw new InvalidOperationException(label);
    }
}
