using TargetLib;

namespace PaymentModule;

public static class PaymentReporter
{
    public static void Report()
    {
        Console.WriteLine("[PAYMENT_MODULE] TargetLib loaded version: " + VersionReport.GetVersion());
    }
}
