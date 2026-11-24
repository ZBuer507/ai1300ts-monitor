using System;

public static class PsuTelemetrySmoke
{
    public static int Main()
    {
        byte[] sample = Frame(PsuTelemetry.TelemetryRegister);
        Word(sample, 0, 48, -2);
        Word(sample, 1, 10, -1);
        Word(sample, 2, 13, -3);
        for (int word = 3; word <= 14; word++)
            Word(sample, word, 80 * (word - 2), -5);
        Word(sample, 15, 360, 0);
        Word(sample, 16, 90, 0);
        Word(sample, 17, 43, 0);
        Word(sample, 19, 384, -5);
        Word(sample, 20, 640, -7);
        Word(sample, 21, 105, -5);

        byte[] status = Frame(PsuTelemetry.ConnectorStatusRegister);
        status[24] = 3;
        PsuTelemetry decoded = PsuTelemetry.Decode(sample, status);
        Check(decoded.Current12V == 12 && decoded.Current5V == 5 && decoded.Current3V3 == 1.625, "rail current");
        Check(decoded.Connector1Currents[0] == 2.5 && decoded.Connector1Currents[5] == 15, "connector 1");
        Check(decoded.Connector2Currents[0] == 17.5 && decoded.Connector2Currents[5] == 30, "connector 2");
        Check(decoded.OutputWatts == 360 && decoded.EfficiencyPercent == 90 && decoded.InputWatts == 400, "power");
        Check(decoded.TemperatureCelsius == 43 && decoded.FanRpm == 0, "temperature/fan");
        Check(decoded.Voltage12V == 12 && decoded.Voltage5V == 5 && decoded.Voltage3V3 == 3.28125, "rail voltage");
        Check(decoded.Connector1Status == 0 && decoded.Connector2Status == 3, "status");

        Check(PsuTelemetry.Decode(sample, null).Connector1Status == null, "missing status");
        status[1] = 0xFA;
        Check(PsuTelemetry.Decode(sample, status).Connector2Status == null, "wrong status opcode");
        status[1] = 0x51;
        status[24] = 0;
        status[3] = 0xFE;
        Check(PsuTelemetry.Decode(sample, status).Connector2Status == null, "status busy reply");
        Word(sample, 16, 0, 0);
        Check(PsuTelemetry.Decode(sample, null).InputWatts == null, "zero efficiency");
        Word(sample, 16, 101, 0);
        Check(PsuTelemetry.Decode(sample, null).InputWatts == null, "over-range efficiency");
        Word(sample, 0, 254, -4);
        Check(PsuTelemetry.Decode(sample, null).Current12V == 15.875, "valid FE low byte");
        for (int offset = 3; offset <= 46; offset++)
            sample[offset] = 0xFF;
        Check(PsuTelemetry.Decode(sample, null) == null, "invalid fill");
        sample = Frame(PsuTelemetry.TelemetryRegister);
        sample[3] = 0xFE;
        Check(PsuTelemetry.Decode(sample, null) == null, "busy reply");
        Check(PsuTelemetry.Decode(new byte[64], null) == null, "truncated frame");
        Check(PsuTelemetry.Decode(null, null) == null, "missing frame");
        Check(PsuTelemetry.Decode(Frame(0xC1), null) == null, "wrong register");
        Console.WriteLine("PsuTelemetry fixture checks passed.");
        return 0;
    }

    private static byte[] Frame(byte register)
    {
        byte[] frame = new byte[65];
        frame[1] = 0x51;
        frame[2] = register;
        return frame;
    }

    private static void Word(byte[] frame, int index, int mantissa, int exponent)
    {
        int encoded = mantissa | ((exponent & 31) << 11);
        frame[3 + 2 * index] = (byte)encoded;
        frame[4 + 2 * index] = (byte)(encoded >> 8);
    }

    private static void Check(bool expected, string name)
    {
        if (!expected)
            throw new Exception(name);
    }
}
