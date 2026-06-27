# WebP Animation Demo — Avalonia + higher SkiaSharp via Switchyard

A runnable, **single-project** sample of the scenario Switchyard was built for:
use Avalonia (which pins an older SkiaSharp for its own rendering) **while
simultaneously** calling SkiaSharp 4.148.0's `SKWebpEncoder.EncodeAnimated` to
produce an animated WebP — an API that does not exist in the 2.88.9 SkiaSharp
Avalonia ships with. One project, one process, two SkiaSharp versions.

## How it is wired (single project, multi-version)

The trick is to declare the **new** version and route the framework **down** to
the old one:

```xml
<PackageReference Include="SkiaSharp" Version="4.148.0">
  <SwitchyardRoutes>WebpAnimationDemo=4.148.0;*=2.88.9</SwitchyardRoutes>
</PackageReference>
```

- The declared version is **4.148.0**, so the project compiles against it and
  `SKWebpEncoder` is visible — the app's own WebP code calls it directly.
- `*=2.88.9` routes **every other caller** (Avalonia.Skia etc.) DOWN to 2.88.9
  at build time. Switchyard downloads 2.88.9 on demand, renames it to
  `SkiaSharp.Switchyard.2.88.9`, rewrites Avalonia.Skia's SkiaSharp reference,
  isolates its native `libSkiaSharp`, and strips 4.148.0's runtime entry for
  Avalonia's view.
- `WebpAnimationDemo=4.148.0` keeps the app's own assembly on the declared
  original, so the app binds `SkiaSharp.dll` (4.148.0) and its native
  `libSkiaSharp.dll` directly.

Result in `bin`:

```
SkiaSharp.dll                            # 4.148.0 (the app's own code)
SkiaSharp.Switchyard.2.88.9.dll          # 2.88.9 (Avalonia)
libSkiaSharp.Switchyard.2.88.9.dll       # 2.88.9 native (Avalonia, isolated)
runtimes/win-x64/native/libSkiaSharp.dll # 4.148.0 native (the app)
```

Avalonia renders with 2.88.9; the app encodes WebP with 4.148.0 — in one process
with no `AssemblyLoadContext`, no `extern alias`, no extra project.

## Why this direction (and not the other way around)

You must declare the version whose API you want to *call at compile time*. If
you declared 2.88.9 (to satisfy Avalonia) and routed the app UP to 4.148.0, the
app would compile against 2.88.9 and could not see `SKWebpEncoder`. Declaring
4.148.0 makes the new API available to compile against, and Switchyard keeps
Avalonia on the version it was validated against.

> NuGet unifies SkiaSharp to 4.148.0 across the graph (Avalonia.Skia's
> `>= 2.88.9` constraint is satisfied by 4.148.0). The MSB3277 "different
> versions of SkiaSharp" build warning is expected and harmless — it is the
> conflict Switchyard resolves at runtime by routing Avalonia down to 2.88.9.

## Build & run

Pack Switchyard into the local feed once (from the repo root):

```bash
dotnet pack Switchyard/Switchyard.csproj -c Release -p:PackageVersion=1.0.0 -o test/local-feed
```

Then build and run the single project:

```bash
dotnet run --project samples/WebpAnimationDemo/WebpAnimationDemo.csproj -c Release
```

A window opens; the console reports `App's SkiaSharp ... 4.148.0.0` and writes
`bouncing-ball.webp` (an animated WebP the app could only have produced via the
4.148.0-only `SKWebpEncoder`). Click **Encode WebP** in the window to regenerate
it and preview the first frame (rendered through Avalonia's 2.88.9 SkiaSharp).