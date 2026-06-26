using PaymentModule;
using TargetLib;

Console.WriteLine("[MAIN_APP] TargetLib loaded version: " + VersionReport.GetVersion());

PaymentReporter.Report();

Console.WriteLine("[MAIN_APP] Done.");
