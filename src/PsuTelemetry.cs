using System;

public sealed class PsuTelemetry
{
    public const byte TelemetryRegister = 0xE0;
    public const byte ConnectorStatusRegister = 0xC1;

    private const int ReportLength = 65;
    private const int PayloadOffset = 3;
    private const int TelemetryWordCount = 22;
    private const int PinsPerConnector = 6;
    private const int Connector1StatusOffset = 3;
    private const int Connector2StatusOffset = 24;
    private const int ConnectorStatusPayloadLength = 42;

    public double OutputWatts { get; private set; }
    public double EfficiencyPercent { get; private set; }
    public double TemperatureCelsius { get; private set; }
    public double FanRpm { get; private set; }
    public double Voltage12V { get; private set; }
    public double Voltage5V { get; private set; }
    public double Voltage3V3 { get; private set; }
    public double Current12V { get; private set; }
    public double Current5V { get; private set; }
    public double Current3V3 { get; private set; }
    public double[] Connector1Currents { get; private set; }
    public double[] Connector2Currents { get; private set; }
    public byte? Connector1Status { get; private set; }
    public byte? Connector2Status { get; private set; }

    public double? InputWatts
    {
        get
        {
            if (EfficiencyPercent <= 0 || EfficiencyPercent > 100)
                return null;
            return OutputWatts * 100 / EfficiencyPercent;
        }
    }

    private PsuTelemetry() { }

    // The E0 layout below is specific to the MPG Ai1300TS/Ai1600TS.
    // Offsets include the leading zero HID report ID.
    public static PsuTelemetry Decode(byte[] telemetryReply, byte[] statusReply)
    {
        if (!IsResponse(telemetryReply, TelemetryRegister, TelemetryWordCount * 2))
            return null;

        var sample = new PsuTelemetry
        {
            Current12V = ReadWord(telemetryReply, 0),
            Current5V = ReadWord(telemetryReply, 1),
            Current3V3 = ReadWord(telemetryReply, 2),
            Connector1Currents = ReadConnector(telemetryReply, 3),
            Connector2Currents = ReadConnector(telemetryReply, 9),
            OutputWatts = ReadWord(telemetryReply, 15),
            EfficiencyPercent = ReadWord(telemetryReply, 16),
            TemperatureCelsius = ReadWord(telemetryReply, 17),
            FanRpm = ReadWord(telemetryReply, 18),
            Voltage12V = ReadWord(telemetryReply, 19),
            Voltage5V = ReadWord(telemetryReply, 20),
            Voltage3V3 = ReadWord(telemetryReply, 21)
        };

        if (IsResponse(statusReply, ConnectorStatusRegister, ConnectorStatusPayloadLength))
        {
            sample.Connector1Status = statusReply[Connector1StatusOffset];
            sample.Connector2Status = statusReply[Connector2StatusOffset];
        }

        return sample;
    }

    private static bool IsResponse(byte[] reply, byte register, int payloadLength)
    {
        if (reply == null || reply.Length != ReportLength || reply[0] != 0 ||
            reply[1] != 0x51 || reply[2] != register)
            return false;

        // FE with zero padding is a busy reply. FE alone can be a valid low byte.
        if (reply[PayloadOffset] == 0xFE && IsFilled(reply, PayloadOffset + 1, reply.Length, 0))
            return false;

        // Reject a payload filled with FF while preserving valid zero readings.
        return !IsFilled(reply, PayloadOffset, PayloadOffset + payloadLength, 0xFF);
    }

    private static bool IsFilled(byte[] reply, int start, int end, byte value)
    {
        for (int index = start; index < end; index++)
        {
            if (reply[index] != value)
                return false;
        }

        return true;
    }

    private static double[] ReadConnector(byte[] reply, int firstWord)
    {
        var currents = new double[PinsPerConnector];
        for (int pin = 0; pin < currents.Length; pin++)
            currents[pin] = ReadWord(reply, firstWord + pin);
        return currents;
    }

    private static double ReadWord(byte[] reply, int wordIndex)
    {
        int offset = PayloadOffset + wordIndex * 2;
        int encoded = reply[offset] | (reply[offset + 1] << 8);
        int exponent = encoded >> 11;
        if (exponent >= 16)
            exponent -= 32;

        // MSI's words use a signed exponent and an unsigned 11-bit mantissa.
        int mantissa = encoded & 0x07FF;
        return mantissa * Math.Pow(2, exponent);
    }
}
