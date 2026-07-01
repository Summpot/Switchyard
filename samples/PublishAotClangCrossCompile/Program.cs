using Newtonsoft.Json;

// This sample combines two orthogonal concerns in one NativeAOT binary:
//   * Switchyard routes ConsumerModule's Newtonsoft.Json reference DOWN to
//     12.0.3 while the app keeps the declared 13.0.1 — two versions of the
//     same assembly in one NativeAOT binary.
//   * PublishAotClang cross-compiles Windows -> linux-x64 via a Zig-wrapped
//     Clang toolchain.
// They target disjoint MSBuild hook points (Switchyard: managed-assembly
// shaping before WriteIlcRspFileForCompilation + publish cleanup;
// PublishAotClang: native-linker toolchain at SetupOSSpecificProps/LinkNative)
// and compose without conflict. See README.md for the full hook-point analysis.

Console.WriteLine("[APP] Newtonsoft.Json version: "
    + typeof(JsonConvert).Assembly.GetName().Version);

ConsumerModule.Reporter.Report();

Console.WriteLine("[APP] Done.");
