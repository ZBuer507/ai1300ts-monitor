using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

internal static class PsuCancellationAudit
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetEvent(SafeWaitHandle handle);

    private static void Main()
    {
        const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic;
        var transport = typeof(PsuHidTransport);
        var slot = transport.GetField("pendingCancellation", statics);
        var reap = transport.GetMethod("TryFinishPendingCancellation", statics);
        var nativeType = transport.GetNestedType("HidOperation", BindingFlags.NonPublic);
        var operation = Activator.CreateInstance(nativeType, new object[] { new byte[65] });
        var signal = (SafeWaitHandle)nativeType.GetProperty("Event").GetValue(operation, null);
        slot.SetValue(null, operation);
        for (int iteration = 0; iteration < 100; iteration++)
        {
            if ((bool)reap.Invoke(null, null) || signal.IsClosed || !ReferenceEquals(slot.GetValue(null), operation))
                throw new InvalidOperationException("Pending operation was replaced or freed before completion.");
        }
        if (!SetEvent(signal) || !(bool)reap.Invoke(null, null) || !signal.IsClosed || slot.GetValue(null) != null)
            throw new InvalidOperationException("Signal did not clear the pending operation and close its event.");
        Console.WriteLine("PASS: 100 pending checks, then a simulated completion releases the sole pending request.");
    }
}
