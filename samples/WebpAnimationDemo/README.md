# WebP Animation Demo — Avalonia + higher SkiaSharp via Switchyard

A runnable sample of the exact scenario Switchyard was built for: use Avalonia
(which pins an older SkiaSharp for its own rendering) **while simultaneously**
calling SkiaSharp 4.147.0-preview's `SKWebpEncoder.EncodeAnimated` to produce an
animated WebP — an API that does not exist in the 2.88.9 SkiaSharp Avalonia
ships with.

## What it shows

- Avalonia 11.3 renders its window with SkiaSharp **2.88.9** (its transitive
  dependency).
- `WebpEncoder.dll` (a tiny library compiled against SkiaSharp
  **4.147.0-preview**) encodes a bouncing-ball animated WebP via
  `SKWebpEncoder.EncodeAnimated`.
- Both managed SkiaSharp versions **and both native `libSkiaSharp` copies**
  coexist in one process, isolated by name — Avalonia never sees the 4.147
  native, the WebP encoder never sees the 2.88.9 native.

## How it is wired

```
samples/WebpAnimationDemo/
  WebpAnimationDemo.csproj   # Avalonia app; pins SkiaSharp 2.88.9, routes WebpEncoder to 4.147.0-preview.3.1
  Program.cs / MainWindow    # Avalonia UI + WebP-first-frame preview
  WebpEncoder/               # class library compiled against SkiaSharp 4.147.0-preview (sees SKWebpEncoder)
  nuget.config               # local Switchyard feed + nuget.org
```

The two-project split is required by how NuGet works: a project compiles
against the **single** SkiaSharp version it restores, so the code that uses
`SKWebpEncoder` (only in 4.147.0-preview) must live in a library that restores
4.147.0-preview. That library is referenced as a **raw `<Reference>`** so its
4.147 dependency does NOT flow into the Avalonia app's restore (which must stay
on 2.88.9 for Avalonia). Switchyard then routes the `WebpEncoder` assembly to
4.147.0-preview at build time (downloaded on demand) while Avalonia keeps 2.88.9.

## Build & run

First pack Switchyard into the local feed (once, from the repo root):

```bash
dotnet pack Switchyard/Switchyard.csproj -c Release -p:PackageVersion=1.0.0 -o test/local-feed
```

Then build the library and the app, and run:

```bash
dotnet build samples/WebpAnimationDemo/WebpEncoder/WebpEncoder.csproj -c Release
dotnet run --project samples/WebpAnimationDemo/WebpAnimationDemo.csproj -c Release
```

A window opens; click **Encode WebP** to regenerate `bouncing-ball.webp` and
preview its first frame (rendered through Avalonia's 2.88.9 SkiaSharp). The
console prints which SkiaSharp each side bound to.

> Rebuild `WebpEncoder` first whenever you change it; the app references the
> built DLL. The MSB3277 "different versions of SkiaSharp" warning during the
> app build is expected and harmless — it is the conflict Switchyard resolves
> at runtime.