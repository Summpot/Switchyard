# AGENTS.md — Switchyard contributor & coding-agent guide

This file orients repository developers, contributors, and coding agents
(including AI assistants) who need to modify the Switchyard codebase. It is the
authoritative source for architecture, conventions, the test strategy, and the
CI contract. The design rationale lives as inline comments at the relevant
locations in the source; this document is the map.

> **Read this before making changes.** If a change touches the pipeline, the
> MSBuild wiring, or the IL weaver, the corresponding section below applies.

---

## 1. Repository layout

```
Switchyard/                       # The NuGet package project (build/ + tasks/)
  Switchyard.csproj               # Packs build/*.props/targets + task DLLs
  build/Switchyard.props          # Auto-imported: SwitchyardEnabled / Silent
  build/Switchyard.targets        # The three pipeline phases (MSBuild wiring)
Switchyard.Task/                  # The MSBuild task assembly (built into tasks/)
  SwitchyardTask.cs               # Phase 1 task: weave + redirect + swap items
  SwitchyardDepsPatcherTask.cs    # Phase 2 task: deps.json TPA injection
  SwitchyardPublishFilterTask.cs  # Phase 3 task: publish copy-local cleanup
  Configuration/                  # <SwitchyardRoutes>/<SwitchyardRouteGroup> parsing
  NuGet/                          # project.assets.json + on-demand package download
  Pipeline/                       # Orchestration + deps.json patcher
  Weaver/                         # AsmResolver metadata rewriting
test/
  Switchyard.Core.Tests/          # Level 1: in-memory unit tests (ms-range)
  Switchyard.TestFixtures/        # Portable-PDB fixture assembly for Level 1
  Switchyard.IntegrationTests/    # Level 2 + 3: MSBuild + runtime E2E
    BuildUtility.cs               # dotnet CLI wrapper + local-feed setup
    LocalFeedFixture.cs           # xUnit collection fixture (serialised run)
    PipelineInterceptTests.cs     # Level 2: bin/publish interception
    RuntimeRoutingTests.cs        # Level 3: run compiled exes, assert Stdout
    TestSamples/                  # Static sample apps (NOT in the main sln)
      BasicRouteApp/              # dual-version diversion (+ PaymentModule)
      RouteGroupApp/              # cascade sandbox
      InvalidCastApp/             # boundary tearing (+ BoundaryBreaker)
      NativeBindingApp/           # managed+native isolation, stub fixture (+ NativeConsumerModule)
      SkiaSharpIsolationApp/      # real Avalonia+SkiaSharp scenario (+ SkiaSharpConsumerModule)
      ReflectionProbeApp/         # runtime-reflection dual-version probe
  fixtures/                       # TargetLib / CommonUtils / NativeBindingLib
      TargetLib/               # packed at 1.0.0 / 2.0.0 / 3.5.0
      CommonUtils/             # TargetLib cascade dependency, by version
      NativeBindingLib/        # managed DllImport + native.c, by version
Switchyard.slnx                   # Solution (excludes TestSamples & fixtures)
```

`Switchyard.slnx` includes the main package, the task assembly, the two test
projects, and the TestFixtures library. The `TestSamples/**` and `fixtures/**`
projects are intentionally **not** part of the solution — they are packed into
a local NuGet feed at test time and consumed as `PackageReference`s by the
samples. Do not add them to the solution.

---

## 2. Architecture: the three-phase pipeline

Switchyard intercepts MSBuild's internal file-item collections rather than
overwriting files in an `AfterBuild` step, so incremental builds and
`dotnet publish` keep working. Three phases, each a separate MSBuild target in
`Switchyard/build/Switchyard.targets`:

| Phase | Target                       | Task                            | When (MSBuild)                                                                                |
| ----- | ---------------------------- | ------------------------------- | --------------------------------------------------------------------------------------------- |
| 1     | `ExecuteSwitchyardWeaving`   | `SwitchyardTask`                | `AfterTargets="CoreCompile"`, `BeforeTargets="CopyFilesToOutputDirectory;ComputeFilesToPublish"` |
| 2     | `PatchSwitchyardDepsJson`    | `SwitchyardDepsPatcherTask`     | `AfterTargets="GenerateBuildDependencyFile;GeneratePublishDependencyFile"`, `BeforeTargets="ComputeFilesToPublish"` |
| 3     | `SwitchyardPublishCleanup`   | `SwitchyardPublishFilterTask`   | `BeforeTargets="_ComputeResolvedCopyLocalPublishAssets"`                                       |

**Why these exact hook points (critical):**

* Phase 1 must run `AfterTargets="CoreCompile"`, **not**
  `ResolveAssemblyReferences`. Before `CoreCompile`, `@(IntermediateAssembly)`
  does not exist yet, so the main project's own assembly could never be
  redirected.
* Phase 2 must run **after** `GenerateBuildDependencyFile`, because the deps
  file is only written to the output directory at that late stage. Patching it
  earlier has no effect.
* Phase 3 exists because the publish pipeline re-resolves copy-local assets
  from the lock file (`_ResolvedCopyLocalBuildAssets`), which resurrects the
  original `{PackageId}.dll` even after Phase 1 stripped it. Phase 3 strips it
  back out of both `@(ReferenceCopyLocalPaths)` and
  `@(_ResolvedCopyLocalPublishAssets)` right before the publish asset list is
  finalised.

If you change the wiring, re-derive the hook points from this table and
re-verify with the Level 2 tests.

### Phase 1 internals (`SwitchyardPipeline.Execute`)

1. **Resolve** on-disk package directories for every routed version
   (`PackageResolver` — auto-downloads missing versions via NuGet.Protocol).
2. **Rename** each non-original target to `{Pkg}.Switchyard.{ver}` and copy its
   `.pdb` (`AssemblyWeaver.PrepareAndRename`). The original version declared on
   the `<PackageReference>` is already restored by NuGet and is *not* renamed.
3. **Cascade-close** explicit `<SwitchyardRouteGroup>` sandboxes: redirect each
   renamed target's *own* references to the other group members' routed names.
4. **Redirect** every caller's `AssemblyReferences` to the appropriate routed
   name based on the caller's position in the route table
   (`ReferenceRedirector`).
4. **Rename native libraries** for routed packages that carry a native
   dependency, and rewrite the routed managed assembly's own `DllImport`
   module names to the renamed native names so each routed version binds its
   own native lib.
5. **Swap** the MSBuild items: rebuild `ReferenceCopyLocalPaths` /
   `IntermediateAssembly` so MSBuild copies the rewritten files.

### Two subtle correctness rules (do not regress)

* **Reference version sync:** when
  `ReferenceRedirector` rewrites `TargetLib → TargetLib.Switchyard.1.0.0`, it
  must also set `AssemblyReference.Version` to the four-component version
  parsed from the routed-name suffix (`1.0.0.0`). The CLR binds on
  `(Name, Version, PublicKey)`; leaving the old version makes the loader fail
  with `FileNotFoundException`. The version is padded to four components so
  AsmResolver serialises concrete `0`s instead of the `0xFFFF` "unspecified"
  sentinel (which the CLR reads as 65535).
* **deps.json / TPA injection:**
  framework-dependent apps resolve assemblies only from the TPA list built out
  of `deps.json`. A renamed DLL in `bin` but not in `deps.json` throws
  `FileNotFoundException` at runtime. `DepsJsonPatcher` adds each routed
  assembly as a synthetic `"type":"project"` library (mirroring project-ref
  outputs) and strips the original package's `runtime` entry so the SDK's
  publish flow does not copy the un-routed DLL back from the NuGet cache.

### Native library isolation

The `DllImport` target lives inside the routed package's own managed assembly
(not in its callers), so the native-name rewrite must be applied to the
**prepared target assembly** after `PrepareAndRename`, not only to callers.
`SwitchyardPipeline.PrepareNativeLibraries` discovers native libs under
`runtimes/{rid}/native/` for the host RID, derives the `DllImport` module name
(the file basename without extension — .NET does NOT add a `lib` prefix on
resolve, so the import name already matches the file basename, e.g.
SkiaSharp's `libSkiaSharp`), renames the file to
`{name}.Switchyard.{version}.{ext}` preserving on-disk casing, and rewrites
the routed assembly's `ModuleRef` rows via
`ReferenceRedirector.RewritePInvokeModules`.

Real packages split their native dependency across a separate "native assets"
package (SkiaSharp ships `libskiasharp` via the transitive
`SkiaSharp.NativeAssets.Win32` / `.Linux` / `.macOS` packages, not inside the
`SkiaSharp` package). Switchyard follows the full dependency chain
automatically: it parses the routed package's `.nuspec`, resolves each declared
dependency at its declared version, and treats any dependency that actually
contains a `runtimes/{rid}/native/` folder as a native-asset provider. The user
routes only the managed package (e.g. `SkiaSharp`); the native-asset packages
are discovered and isolated without extra configuration.

The original native libs are stripped from the copy-local stream (and from
publish) via `SwitchyardPublishFilterTask.BlockedNativeFileNames`.

### AssemblyVersion vs package version

The routed assembly's actual `AssemblyVersion` may differ from the NuGet
package version the routed name encodes (SkiaSharp 2.88.9 has `AssemblyVersion`
2.88.0.0). Two places must use the **actual** `AssemblyVersion`, read from the
DLL metadata via `SwitchyardPipeline.ReadAssemblyVersion`:

* `ReferenceRedirector.RedirectReferences` syncs the caller's
  `AssemblyReference.Version` to it (via `assemblyVersionOverrides`), so the CLR
  binds the routed assembly by `(Name, Version, PublicKey)`.
* `DepsJsonPatcher.AddRoutedAssemblies` writes it as `assemblyVersion` /
  `fileVersion` in the synthetic `deps.json` runtime entry, so `hostpolicy`
  records a TPA entry the binder can match.

Leaving the package version in either place makes the loader fail with
`FileNotFoundException` for a version that does not exist on disk.

---

## 3. IL weaving mechanics

All metadata rewriting goes through
[AsmResolver](https://github.com/Washi1337/AsmResolver). The key insight is
that renaming `AssemblyDefinition.Name` and clearing
the strong name only touches the metadata definition tables — AsmResolver
transparently fixes up every downstream `TypeRef` / `MemberRef` token, so we
never scan method bodies.

* `AssemblyWeaver.PrepareAndRename` — renames the target, strips the strong
  name (`PublicKey = null`, `HashAlgorithm = None`), repoints the CodeView
  (RSDS) debug data path at the new `.pdb` filename, and copies the `.pdb`
  alongside. The MVID is preserved so debug binding stays intact.
* `AssemblyWeaver.StripStrongNameInPlace` — strips the strong name without
  renaming (used when an original-version package participates in a group but
  keeps its identity).
* `ReferenceRedirector.RedirectReferences` — rewrites a caller's
  `AssemblyReferences` table, syncs the version, clears the public key token,
  repoints the CodeView path, copies the `.pdb`.
* `ReferenceRedirector.ReferencesAnyPackage` — short-circuit predicate so the
  pipeline does not rewrite assemblies that do not reference any routed
  package.

### Limitations baked into the design

* Strong names are stripped → re-sign the output if your environment requires
  strong-name verification or GAC deployment.
* String-literal reflection across a route boundary fails — the weaver cannot
  see `Type.GetType("…, AssemblyName")` literals.
* NativeAOT/trimming may need the routed assemblies declared as roots in
  `ILLink.Descriptors.xml`.

When you add a new weaving feature, ask whether it interacts with any of
these.

---

## 4. Runtime type-boundary contract

Post-weaving, V1.0.0 and V3.5.0 of a package are **two unrelated type systems**
to the CLR. Crossing a route boundary with a typed object throws
`InvalidCastException`. This is an *architecture contract* Switchyard does not
(and cannot) enforce at weave time. The `InvalidCastApp` sample exists
precisely to prove the boundary is torn cleanly.

Guidance to surface to users:
* Cross-boundary signatures must use BCL primitives or a shared, non-routed
  DTO/contract assembly.
* Prefer an interface-isolation pattern: define interfaces in a separate
  `Contracts.dll`, implement them inside each routed region, pass instances
  across the boundary via DI.
* Avoid hard-coded reflection across boundaries.

---

## 5. Test strategy — an inverted pyramid

Switchyard is a CLR-runtime + MSBuild-pipeline tool. Most defects only surface
in a real physical build, so the test pyramid is inverted: E2E is the backbone,
unit tests are the fast guard.

```
Level 3: E2E runtime verification  — run compiled exe, assert Stdout
Level 2: MSBuild integration       — inspect bin/publish + PDB state
Level 1: Core algorithm unit tests — in-memory AsmResolver symbol rewrite
```

### Level 1 — `Switchyard.Core.Tests` (milliseconds, no physical build)

Uses `TestAssemblyFactory` to generate assemblies in-memory with AsmResolver.
Covers the three core algorithms:

* `AssemblyRenameTests` — use case 1 "Def-Reshaping": name rewritten, strong
  name fully stripped, output re-readable, netmodule rejected.
* `ReferenceRedirectTests` — use case 2 "Ref-Redirect": reference table
  rewritten, public key token cleared, original reference gone, short-circuits.
  Also covers P/Invoke (`DllImport`) module-name rewrite and the
  `GetPInvokeModuleNames` probe used by native-lib isolation.
* `PdbAlignmentTests` — use case 3 "PDB-Align": `.pdb` copied, CodeView path
  repointed, MVID preserved, no-`.pdb` input handled. Uses the compiled
  `Switchyard.TestFixtures` assembly as the input (portable PDB).
* `ConfigurationParserTests` — route table / RouteGroup parsing.

### Level 2 + 3 — `Switchyard.IntegrationTests` (real `dotnet build`/`publish`/run)

* `BuildUtility.EnsureLocalFeedReady` packs `Switchyard`, `TargetLib`,
  `CommonUtils` (at 1.0.0/2.0.0/3.5.0) and `NativeBindingLib` (with its native
  library built from `native.c` via `cl`/`gcc`) into `test/local-feed` once per
  run, after purging the `switchyard` / `nativebindinglib` entries from the
  global NuGet cache so the freshly built `.targets` / Task DLL is re-extracted.
* `LocalFeedFixture` is an xUnit **collection fixture**; all integration tests
  share the `[Collection("Integration")]` tag so xUnit **serialises** them.
  Reason: the `TestSamples` are built into a shared physical `bin/obj` tree —
  parallel runs would cause MSBuild file-lock contention on
  `obj/switchyard/*.dll`. **Do not remove the collection attribute.**
* `PipelineInterceptTests` (Level 2): bin/publish interception, incremental
  build, RouteGroup cascade deep-inspection.
* `NativeBindingTests` (Level 2 + 3): the managed+native scenario at a small,
  deterministic, offline scale using the `NativeBindingLib` stub fixture
  (managed DllImport + a tiny native lib built from `native.c`). Asserts the
  renamed managed + native assemblies land in `bin`, the original native lib
  is blocked, the routed managed DLL's `DllImport` module name was rewritten,
  and the compiled exe reports distinct native version constants per routed
  version.
* `SkiaSharpIsolationTests` (Level 2 + 3): the **real** Avalonia +
  higher-SkiaSharp scenario, using the actual SkiaSharp NuGet packages (managed
  assembly + native `libskiasharp` shipped via the transitive
  `SkiaSharp.NativeAssets.*` packages). Asserts the renamed managed + native
  assemblies land in `bin`, the routed managed DLL's DllImport was rewritten,
  and the compiled exe successfully loads BOTH native libraries in one process
  (each routed version reports its distinct SkiaSharp version). This is the
  concrete situation Switchyard was built for.
* `ReflectionProbeTests` (Level 3): `ReflectionProbeApp` loads both routed
  DLLs via `Assembly.LoadFrom` + reflection (no compile-time reference to a
  specific routed version) and reports the two distinct versions.
* `RuntimeRoutingTests` (Level 3): run the compiled exes and assert Stdout
  proves dual-version diversion, type-boundary tearing, and cascade versions.

### Edge/matrix tests always in the suite

* shadow hot-download — a route referencing a version absent from the
  global cache; `PackageResolver` fetches it from NuGet upstream.
* RouteGroup cascade — deep-inspect the renamed DLL and assert its
  internal reference has been cascaded.
* boundary tearing — `InvalidCastApp` expects `InvalidCastException`,
  exits 0.
* native-lib isolation — `NativeBindingApp` asserts each routed managed
  version binds its own renamed native library (distinct native version
  constants), proving native isolation rather than a single shared native load.
* real SkiaSharp coexistence — `SkiaSharpIsolationApp` proves two real
  SkiaSharp versions (managed + native `libskiasharp`) coexist in one process.

### Local prerequisites for integration tests

* **Windows:** Visual Studio with the C++ workload (for `cl.exe`), located via
  `vswhere`; `BuildUtility` initialises `vcvars64` itself.
* **Linux:** `gcc` on PATH (preinstalled on `ubuntu-latest`).
The native fixture is only built for the host RID, so each CI matrix runner
builds its own native library.

When you add a feature, add a corresponding Level 1 test (fast) and a
Level 2/3 sample if the feature changes the file stream or runtime behaviour.

---

## 6. Coding conventions

* **Language/framework:** C# latest, .NET 8.0, `Nullable` enabled, `ImplicitUsings`
  enabled. The task assembly targets `net8.0`.
* **Comments:** XML doc comments (`///`) on every public type and member.
  Design context lives as inline comments at the relevant location in the
  source (not in separate design documents).
* **No inline comments that duplicate the code.** Comments explain *why*,
  especially MSBuild hook ordering, version-sync rules, and TPA injection.
* **Do not add comments to code unless asked** — but the design-context
  `///` docs and inline `why` comments are the established convention here
  and must be maintained when you change the corresponding logic.
* **Path handling:** always normalise relative paths MSBuild hands you with
  `Path.GetFullPath` before traversing (see `SwitchyardPipeline.Create`). The
  task runs with the working directory set to the project directory.
* **MSBuild item mutation:** never overwrite `@(ReferenceCopyLocalPaths)`
  in-place without going through the task's output parameters — the targets
  file rebuilds the item group from the task output to preserve metadata.
* **No new heavy dependencies.** AsmResolver + the NuGet Protocol stack are
  the deliberate toolset. Do not introduce Fody, Mono.Cecil, or similar.

---

## 7. Build & test commands

```bash
# Restore + build everything in the solution
dotnet build Switchyard.slnx -c Release

# Run Level 1 unit tests (fast, no physical build)
dotnet test test/Switchyard.Core.Tests/Switchyard.Core.Tests.csproj

# Run Level 2 + 3 integration tests (packs the local feed, builds samples,
# runs the compiled exes). Slow.
dotnet test test/Switchyard.IntegrationTests/Switchyard.IntegrationTests.csproj

# Run everything
dotnet test Switchyard.slnx
```

The integration tests auto-pack `Switchyard` / `TargetLib` / `CommonUtils` into
`test/local-feed` on first run via `LocalFeedFixture`. If you change the task
assembly or the `.targets`, the fixture purges the stale `switchyard` package
from the global NuGet cache automatically — no manual cleanup needed locally.

> **CI note:** CI must additionally clean `obj/` and the local NuGet cache for
> the test stub packages before running, to force `NuGet.Protocol` to
> re-resolve. See the GitHub Actions workflow (`.github/workflows/ci.yml`).

---

## 8. CI contract

1. **OS matrix:** run on **both** `windows-latest` and `ubuntu-latest`.
   AsmResolver and MSBuild macros differ in trailing-slash / path-separator
   behaviour between Windows and Linux — all integration tests must pass on
   both.
2. **Clean-cache contract:** before the test step, clean `obj/` and purge the
   test stub packages from the global NuGet cache so `NuGet.Protocol`'s fetch
   and resolve paths are exercised on every run.
3. The CI workflow lives at `.github/workflows/ci.yml`; the publish workflow
   lives at `.github/workflows/publish.yml`.

---

## 9. Checklist for changes

Before submitting a change, verify:

- [ ] `dotnet build Switchyard.slnx` succeeds on Windows (and Linux if you can).
- [ ] `dotnet test Switchyard.slnx` passes — including the slow integration
      tests. If you skip them locally, CI will catch regressions.
- [ ] If you changed the MSBuild wiring, the Level 2 tests still pass and you
      have re-derived the hook points from the architecture section above.
- [ ] If you changed the weaver, the Level 1 tests still pass and you added a
      new one for the new behaviour.
      new one for the new behaviour.
- [ ] If you changed the deps.json patcher or publish filter, the Level 3
      runtime tests still pass (they exercise the TPA list end-to-end).
- [ ] XML doc comments and inline design context are updated to reflect
      the change.
- [ ] No new heavy dependencies were introduced.
- [ ] Commit messages are concise and match the repo style.

---

## 10. Canonical references

* `AGENTS.md` (this file) — architecture, conventions, test strategy, CI
  contract.
* `README.md` — user-facing introduction, configuration, and runtime contract.
* Inline comments at the relevant source location — the *why* for MSBuild hook
  ordering, version-sync rules, TPA injection, and the type-boundary contract.
  When in doubt, start there.