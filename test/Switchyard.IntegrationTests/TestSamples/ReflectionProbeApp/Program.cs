using System.Reflection;

// ReflectionProbeApp — verifies dual-version coexistence purely through
// runtime reflection, with no compile-time reference to a specific routed
// version.
//
// The app is routed to TargetLib 1.0.0, so its compile-time reference to
// TargetLib is rewritten to TargetLib.Switchyard.1.0.0. It then dynamically
// loads BOTH routed DLLs from its own base directory via Assembly.LoadFrom and
// reflects to invoke each one's VersionReport.GetVersion(). If Switchyard did
// its job, the two assemblies are distinct identities and report 1.0.0.0 and
// 3.5.0.0 respectively — proving the two versions live side by side in one
// process without ALC isolation.

string baseDir = AppContext.BaseDirectory;

string ver1Dll = Path.Combine(baseDir, "TargetLib.Switchyard.1.0.0.dll");
string ver35Dll = Path.Combine(baseDir, "TargetLib.Switchyard.3.5.0.dll");

Console.WriteLine("[PROBE] Compile-time TargetLib version: " + TargetLib.VersionReport.GetVersion());

Probe(ver1Dll, "1.0.0", expected: "1.0.0.0");
Probe(ver35Dll, "3.5.0", expected: "3.5.0.0");

Console.WriteLine("[PROBE] Done.");

static void Probe(string dllPath, string label, string expected)
{
    if (!File.Exists(dllPath))
    {
        Console.WriteLine($"[PROBE] MISSING routed assembly for {label}: {dllPath}");
        Environment.Exit(1);
    }

    var asm = Assembly.LoadFrom(dllPath);
    var type = asm.GetType("TargetLib.VersionReport");
    if (type is null)
    {
        Console.WriteLine($"[PROBE] Type TargetLib.VersionReport not found in {label} assembly");
        Environment.Exit(2);
    }

    var method = type.GetMethod("GetVersion", BindingFlags.Public | BindingFlags.Static);
    if (method is null)
    {
        Console.WriteLine($"[PROBE] GetVersion method not found on {label} VersionReport");
        Environment.Exit(3);
    }

    var version = (string?)method.Invoke(null, null);
    Console.WriteLine($"[PROBE] Reflection {label} assembly={asm.GetName().Name} version={version}");
    if (version != expected)
    {
        Console.WriteLine($"[PROBE] UNEXPECTED version for {label}: got {version}, expected {expected}");
        Environment.Exit(4);
    }
}