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
`.csproj`. During `dotnet build` / `dotnet publish`, Switchyard's MSBuild task
intercepts the file stream that MSBuild is about to copy to the output
directory, fetches any missing routed versions from NuGet, renames the target
DLLs (and their `.pdb`s), rewrites every caller's `AssemblyReferences` to point
at the renamed assemblies, injects the renamed assemblies into `deps.json`'s
TPA list, and strips the originals back out of the publish stream. The result
lands in `bin` / `publish` with the rewritten files in place — incremental
builds and `dotnet publish` keep working unchanged.

## Installation

Add the Switchyard package to the **main application** project that owns the
routing rules:

```xml
<ItemGroup>
  <PackageReference Include="Switchyard" Version="1.0.0" />
</ItemGroup>
```

That single reference auto-imports `Switchyard.props` / `Switchyard.targets`,
which register the weaving pipeline. No changes are needed on the dependency
side.

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

## Known limitations

* **Strong-name stripping.** Renamed assemblies lose their original strong-name
  signature. Environments that require strict strong-name verification or GAC
  deployment must re-sign the output with a custom key after weaving.
* **String-literal reflection.** Hard-coded `Type.GetType("…, AssemblyName")`
  across a route boundary will not resolve — avoid it.
* **NativeAOT / trimming.** The static analyser cannot always trace the
  implicitly renamed `*.Switchyard.*.dll` references. When using AOT, declare
  the routed assemblies as roots in `ILLink.Descriptors.xml`.

## Package layout

```
Switchyard.nupkg
├── build/
│   ├── Switchyard.props     # defines SwitchyardEnabled / SwitchyardSilent
│   └── Switchyard.targets   # registers the three pipeline phases
└── tasks/net8.0/
    ├── Switchyard.Task.dll  # the MSBuild task assembly
    ├── AsmResolver.*.dll    # metadata + PDB read/write
    └── NuGet.*.dll          # silent routed-version download
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