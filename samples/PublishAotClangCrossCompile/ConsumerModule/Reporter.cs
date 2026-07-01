using Newtonsoft.Json;

namespace ConsumerModule;

/// <summary>
/// Reports the Newtonsoft.Json version this module is bound to. After Switchyard
/// routing, this module's reference is rewritten to
/// <c>Newtonsoft.Json.Switchyard.12.0.3</c>, so the assembly identity reported
/// here is 12.0.0.0 — distinct from the app's 13.0.0.0 in the same NativeAOT
/// binary.
/// </summary>
public static class Reporter
{
    public static void Report()
    {
        Console.WriteLine("[CONSUMER] Newtonsoft.Json version: "
            + typeof(JsonConvert).Assembly.GetName().Version);
    }
}
