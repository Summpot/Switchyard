# Switchyard

> Compile-time IL weaving & reference redirection for .NET — let multiple
> versions of the same NuGet package coexist in a single process, **without**
> `AssemblyLoadContext`, `extern alias`, or add-ins like Fody.

`Switchyard` is a build-time tool built on MSBuild tasks and
[AsmResolver](https://github.com/Washi1337/AsmResolver). It breaks .NET's
default "one assembly identity per process" rule so that the main application
and individual dependencies can each bind a *different* version of the same
NuGet package. The work happens entirely in the compile/publish pipeline: target
assemblies are renamed at the IL metadata level (`TargetLib` →
`TargetLib.Switchyard.1.0.0`) and every caller's references are redirected to
the renamed identity. The CLR then sees two unrelated type systems and never
trips its version-loader conflict check.

## How it works (in one paragraph)

You add a `<SwitchyardRoutes>` metadata entry to a `<PackageReference>` in your
`.csproj`. During `dotnet restore`, a Switchyard restore-time target turns the
routed versions into multi-version `<PackageDownload>` items so NuGet itself
fetches them into the global packages folder. During `dotnet build` /
`dotnet publish`, Switchyard's MSBuild task intercepts the file stream that
MSBuild is about to copy to the output directory, locates the already-restored
routed versions (read-only — it never downloads), renames the target DLLs (and
their `.pdb`s), rewrites every caller's `AssemblyReferences` to point at the
renamed assemblies, rewrites `DllImport` entries and ships renamed native
libraries for routed packages that carry a native dependency, injects the
renamed assemblies into `deps.json`'s TPA list, and strips the originals back out
of the publish stream. The result lands in `bin` / `publish` with the rewritten
files in place — incremental builds and `dotnet publish` keep working unchanged.

## Installation

Switchyard ships as a **project SDK**, not a plain `PackageReference`. Reference
it from the **main application** project that owns the routing rules by adding
`Switchyard` to the `Sdk` attribute:

```xml
<Project Sdk="Microsoft.NET.Sdk;Switchyard">
  …
</Project>
```

Pin the SDK version via `global.json` so the build is reproducible:

```json
{
  "msbuild-sdks": { "Switchyard": "1.0.0" }
}
```

That SDK reference auto-imports `Sdk.props` / `Sdk.targets`, which register the
weaving pipeline. No changes are needed on the dependency side. (The SDK form is
not cosmetic: only SDK imports survive `ExcludeRestorePackageImports` during the
restore-traversal evaluation, which is what lets Switchyard inject the routed
versions into restore.)

## Configuration

All routing rules live on `<PackageReference>` nodes in the main app's
`.csproj`. Nothing else is touched.

### Basic routing

Add `<SwitchyardRoutes>` metadata with a `Caller=Version;...` table. `*` is the
catch-all fallback.

```xml
<ItemGroup>
  <PackageReference Include="TargetLib" Version="2.0.0">
    <SwitchyardRoutes>
      MainApp=1.0.0;        PaymentModule=3.5.0;  *=2.0.0
    </SwitchyardRoutes>
  </PackageReference>
</ItemGroup>
```

Effect: `MainApp` binds `TargetLib.Switchyard.1.0.0`, `PaymentModule` binds
`TargetLib.Switchyard.3.5.0`, and any other caller keeps the original 2.0.0.
All three coexist in the same process.

### Dependency cascade isolation groups (`<SwitchyardRouteGroup>`)

When a routed package *itself* depends on another package, use
`<SwitchyardRouteGroup>` to bind several packages into a closed sandbox so
sub-dependency chains don't break:

```xml
<ItemGroup>
  <PackageReference Include="TargetLib" Version="2.0.0">
    <SwitchyardRoutes>AuthModule=1.0.0;*=2.0.0</SwitchyardRoutes>
    <SwitchyardRouteGroup>AuthIsolation</SwitchyardRouteGroup>
  </PackageReference>
  <PackageReference Include="CommonUtils" Version="2.0.0">
    <SwitchyardRoutes>AuthModule=1.0.0;*=2.0.0</SwitchyardRoutes>
    <SwitchyardRouteGroup>AuthIsolation</SwitchyardRouteGroup>
  </PackageReference>
</ItemGroup>
```

In `AuthModule`, both `TargetLib` and `CommonUtils` route to 1.0.0, and the
1.0.0 copy of `TargetLib` has its *internal* `CommonUtils` reference forced to
`CommonUtils.Switchyard.1.0.0` — a closed loop.

### MSBuild switches

| Property           | Default | Effect                                                       |
| ------------------ | ------- | ------------------------------------------------------------ |
| `SwitchyardEnabled` | `true`  | Master on/off switch. Set `false` to disable weaving without removing the package reference. |
| `SwitchyardSilent`  | `false` | Suppresses the high-importance diagnostic messages.          |
| `SwitchyardStrongNameKeyFile` | *(unset)* | Opt-in strong-name re-signing. Path to a `.snk` key-pair file. When set, each routed assembly is re-signed with this key (instead of having its strong name stripped) and every redirected caller reference carries the key's public key token, so the CLR binds the routed assembly by `(Name, Version, PublicKeyToken)`. Signing runs entirely in-process — no `sn.exe` required. Unset = strip strong names (the default). |

## Runtime contract — the one rule you must follow

Because routed versions are renamed into distinct assembly identities, the CLR
treats them as **two unrelated type systems**. They cannot collide physically.

* **Cross-boundary signatures must use BCL primitives** (`string`, `int`,
  `ReadOnlySpan<byte>`, `Stream`, …) or a shared DTO/contract assembly that is
  *not* routed. Do not pass typed objects from a routed package across a route
  boundary.
* **Prefer an interface-isolation pattern.** Define shared interfaces in a
  separate `Contracts.dll`, implement them inside each routed region, and pass
  instances across the boundary via DI.
* **Avoid hard-coded reflection across boundaries.** Something like
  `Type.GetType("TargetLib.MyClass, TargetLib")` will fail at runtime because
  the assembly name no longer exists — the weaver cannot see string literals.

Violating this contract surfaces as `InvalidCastException` at runtime — which
is exactly what the `InvalidCastApp` test sample proves (see
`test/Switchyard.IntegrationTests/TestSamples/InvalidCastApp`).

## Native library isolation

Packages that ship a native dependency (e.g. **SkiaSharp**, the original
motivation for Switchyard) are handled — **including the common case where the
native library lives in a separate "native assets" package**. When you route
the managed package, Switchyard follows its declared dependency chain:

* it parses the routed package's `.nuspec`, selects the dependency group
  matching the project's target framework, and resolves each declared
  dependency's version (which may be a range) to the actual restored version
  (read-only — it never downloads);
* any dependency that actually contains a `runtimes/{rid}/native/` folder
  (e.g. a managed package ships its native lib via a transitive
  `{PackageId}.NativeAssets.{OS}` package) is treated as the native-asset
  provider;
* for each routed version it renames that native file to
  `{native}.Switchyard.{version}.{ext}` and rewrites the routed managed
  assembly's own `DllImport` module name to the renamed native name;
* one renamed native library is shipped per routed version.

So each routed managed version binds its **own** native library, and two
versions of a native-binding package no longer fight over a single native
load.

### When the routed package's `.nuspec` omits a platform's native-assets dependency

Some packages omit a platform's native-assets dependency from their `.nuspec`
(a real-world packaging gap — e.g. SkiaSharp's `.nuspec` declares the Win32 and
macOS native-assets packages for the .NET target frameworks but omits the Linux
one, so `dotnet restore` does not pull it on its own). Switchyard deliberately
does **not** infer the missing companion by naming convention — it has no
knowledge of any package's native-assets naming. In that case the consumer
restores that platform's routed native versions explicitly with a multi-version
`<PackageDownload>` alongside the `<PackageReference>`:

```xml
<ItemGroup>
  <PackageReference Include="SkiaSharp.NativeAssets.Linux" Version="2.88.8" />
  <PackageDownload Include="SkiaSharp.NativeAssets.Linux" Version="[2.88.9];[3.116.1]" />
  <PackageDownload Include="SkiaSharp.NativeAssets.Win32" Version="[2.88.9];[3.116.1]" />
  <PackageDownload Include="SkiaSharp.NativeAssets.macOS" Version="[2.88.9];[3.116.1]" />
</ItemGroup>
```

The `PackageReference` is the original native (the same one a plain app on that
platform would reference); each `PackageDownload` asks NuGet to also fetch the
routed native versions for that platform, which Switchyard then renames per
routed managed version. `<PackageDownload>` is leaf-level: NuGet downloads the
package itself but does **not** trigger transitive restore of its `.nuspec`
dependencies, so the routed versions of any companion native-assets package
must be requested explicitly even when the routed managed package's `.nuspec`
would declare them on the original version. Without these `PackageDownload`
entries on every platform that ships a native-assets companion, the routed
native lib will not be renamed and the bin dir will be missing
`libSkiaSharp.Switchyard.{version}.dll` (the integration tests on a fresh CI
runner would fail on Windows/macOS).

## Known limitations

* **Strong-name stripping (default).** Renamed assemblies lose their original
  strong-name signature. Set `SwitchyardStrongNameKeyFile` to a `.snk`
  key-pair file to instead re-sign every routed assembly with that key (in
  process, no `sn.exe`) and stamp the key's public key token onto every
  redirected caller reference, so the routed assemblies keep a valid
  strong-name identity. Environments that need a *specific* key or SHA-256
  enhanced strong names can still re-sign the output out-of-band after
  weaving.
* **String-literal reflection.** Hard-coded `Type.GetType("…, AssemblyName")`
  across a route boundary will not resolve — avoid it.
* **NativeAOT / trimming.** The static analyser cannot always trace the
  implicitly renamed `*.Switchyard.*.dll` references. When using AOT, declare
  the routed assemblies as roots in `ILLink.Descriptors.xml`.
* **Native build toolchain for the test fixture.** The integration test that
  exercises native-lib isolation builds a tiny native library from
  `test/fixtures/NativeBindingLib/native.c`; on Windows it needs MSVC
  (`cl.exe`) and on Linux `gcc` on PATH. This only affects running the
  integration tests locally, not consuming Switchyard.

## Package layout

```
Switchyard.nupkg
├── Sdk/
│   ├── Sdk.props          # defines SwitchyardEnabled / SwitchyardSilent
│   └── Sdk.targets       # registers the three pipeline phases +
│                          #   SwitchyardInjectRoutedPackageDownloads (pre-Restore)
└── tasks/net10.0/
    ├── Switchyard.Task.dll  # the MSBuild task assembly
    ├── AsmResolver.*.dll     # metadata + PDB read/write
    └── NuGet.*.dll           # read-only package location (NO download)
```

Everything under `tasks/` is loaded **only** by the MSBuild engine at build
time and never becomes a runtime dependency of your project.

## Building from source

```bash
dotnet build Switchyard.slnx
dotnet test  Switchyard.slnx
```

See [`AGENTS.md`](AGENTS.md) for the architecture, the test strategy, and
contributing guidance.

## License

MIT — see [`LICENSE.txt`](LICENSE.txt).