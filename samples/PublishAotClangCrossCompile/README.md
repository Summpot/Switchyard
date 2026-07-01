# PublishAotClang + Switchyard — Windows → Linux NativeAOT cross-compile with routed versions

A minimal sample that proves **PublishAotClang** and **Switchyard** compose
cleanly in one NativeAOT project: a Windows host cross-compiles a single Linux
binary that carries **two versions of Newtonsoft.Json** — the app keeps
`13.0.1`, a routed consumer module binds `12.0.3`.

## Why they are compatible

Switchyard and PublishAotClang target **disjoint MSBuild hook points** and
**disjoint item vectors**, so they cooperate rather than collide.

| Switchyard target | Hook | PublishAotClang target | Hook | Conflict |
| --- | --- | --- | --- | --- |
| `SwitchyardInjectRoutedPackageDownloads` | `BeforeTargets="CollectPackageDownloads"` | — | — | no |
| `ExecuteSwitchyardWeaving` | `AfterTargets="CoreCompile"` / `BeforeTargets="CopyFilesToOutputDirectory;ComputeFilesToPublish"` | — | — | no |
| `SwitchyardNativeAotCompileInputs` | `BeforeTargets="WriteIlcRspFileForCompilation"` | — | — | no |
| `PatchSwitchyardDepsJson` | `BeforeTargets="ComputeFilesToPublish"` | — | — | no |
| `SwitchyardPublishCleanup` | `BeforeTargets="_ComputeResolvedCopyLocalPublishAssets"` | — | — | no |
| — | — | `SetPathToClang` / `SetPathToZig` | `BeforeTargets="SetupOSSpecificProps"` | no |
| — | — | `OverwriteTargetTriple` | `AfterTargets="SetupOSSpecificProps"` / `BeforeTargets="LinkNative"` | no |

- **Switchyard** shapes the *managed* side: it rewrites `ManagedBinary` /
  `IlcCompileInput` / `IlcReference` to the woven main assembly and the routed
  package DLLs, emits `DirectPInvoke` / `NativeLibrary` items for routed native
  libs, and strips original package DLLs / natives from the publish asset list.
- **PublishAotClang** configures the *native linker toolchain*: prepends the
  Zig-wrapped Clang to `PATH`, sets `TargetTriple` (e.g. `x86_64-linux-gnu`),
  and adds `LinkerArg` items.
- The two converge **inside ILCompiler's `LinkNative` step** — Switchyard feeds
  routed native libs as `NativeLibrary` items, and PublishAotClang makes
  `LinkNative` invoke `clang`/`zig` with the right `--target=`. They solve
  different halves of the same cross-compile.

### One composition caveat (not a conflict)

Cross-compiling Windows → `linux-x64` requires every routed package that ships
native assets to provide them for the **target RID** (e.g.
`runtimes/linux-x64/native/*.so`). This sample sidesteps that by routing a
**pure-managed** package (Newtonsoft.Json), so no native-asset concern arises.
For a native-asset cross-compile (e.g. routed SkiaSharp), restore the routed
native-assets versions explicitly with multi-version `<PackageDownload>` entries
— see `test/Switchyard.IntegrationTests/TestSamples/SkiaSharpIsolationApp/` for
the pattern.

## How it is wired

```xml
<PackageReference Include="Newtonsoft.Json" Version="13.0.1">
  <SwitchyardRoutes>PublishAotClangCrossCompile=13.0.1;ConsumerModule=12.0.3</SwitchyardRoutes>
</PackageReference>
```

- The declared version is **13.0.1**, so the app compiles against it and binds
  `Newtonsoft.Json` (13.0.0.0) directly.
- `ConsumerModule=12.0.3` routes the consumer module **down** to 12.0.3 at build
  time. Switchyard injects a `PackageDownload` for 12.0.3, renames it to
  `Newtonsoft.Json.Switchyard.12.0.3`, rewrites `ConsumerModule`'s reference to
  it, and adds it as an `IlcReference` so `ilc` links the routed version into the
  same NativeAOT binary.
- `<PublishAot>true</PublishAot>` + `<RuntimeIdentifier>linux-x64</RuntimeIdentifier>`
  + `PackageReference PublishAotClang` makes `dotnet publish` cross-compile via
  the Zig-wrapped Clang.

Expected runtime output (on a Linux host / WSL / container):

```
[APP] Newtonsoft.Json version: 13.0.0.0
[CONSUMER] Newtonsoft.Json version: 12.0.0.0
[APP] Done.
```

Two distinct Newtonsoft.Json assembly identities in **one** native binary —
no `AssemblyLoadContext`, no `extern alias`, no extra project beyond the tiny
consumer module.

## Build

> Windows host required for the PublishAotClang cross-compile path. On Linux the
> same project builds natively (PublishAotClang's targets no-op outside Windows).

1. Build the Switchyard task assembly once from the repo root:

   ```bash
   dotnet build Switchyard.slnx -c Release
   ```

2. Publish the sample (cross-compile to `linux-x64`):

   ```bash
   dotnet publish samples/PublishAotClangCrossCompile/PublishAotClangCrossCompile.csproj -c Release
   ```

   The Linux binary lands at
   `samples/PublishAotClangCrossCompile/bin/Release/net10.0/linux-x64/publish/PublishAotClangCrossCompile`.

3. Run it on a Linux machine / WSL / container:

   ```bash
   ./PublishAotClangCrossCompile
   ```

### Other RIDs

PublishAotClang also supports `linux-arm64`, `linux-arm` (.NET 9+), and the
`linux-musl-*` variants — pass `-r` to `dotnet publish`:

```bash
dotnet publish ... -r linux-arm64
dotnet publish ... -r linux-musl-x64
```

`<GLibcVersion>2.17</GLibcVersion>` is set so the gnu variants also run on
CentOS 7 / older glibc.
