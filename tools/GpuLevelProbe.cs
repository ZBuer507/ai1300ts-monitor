using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;

internal static class GpuLevelProbe
{
    private static int Main()
    {
        var records = new List<string>();
        int exit = 0;
        try
        {
            using (var sensor = new GpuLevelSense())
            {
                GpuLevelReading baseline = sensor.Read();
                for (int sample = 0; sample < 5; sample++)
                {
                    GpuLevelReading reading = sensor.Read();
                    records.Add(String.Format(CultureInfo.InvariantCulture,
                        "{0}: pitch={1:F4} roll={2:F4} raw=({3},{4},{5}) delta={6:F4}", reading.Model,
                        reading.PitchDegrees, reading.RollDegrees, reading.RawX, reading.RawY, reading.RawZ,
                        reading.DeviationFrom(baseline)));
                    Thread.Sleep(600);
                }
            }
        }
        catch (Exception error)
        {
            exit = 1;
            records.Add(error.ToString());
        }
        File.WriteAllLines(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "gpu-level-probe.txt"), records);
        foreach (string record in records)
            Console.WriteLine(record);
        return exit;
    }
}
