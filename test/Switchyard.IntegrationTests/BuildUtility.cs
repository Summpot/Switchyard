using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Switchyard.IntegrationTests;

/// <summary>
/// Encapsulates dotnet CLI invocations and local NuGet feed management for
/// the integration test suite. All methods are synchronous and capture
/// stdout/stderr for assertion.
/// </summary>
public static class BuildUtility
{
    /// <summary>
    /// Result of a command execution.
    /// </summary>
    public sealed class CommandResult
    {
        public int ExitCode { get; init; }
        public string StandardOutput { get; init; } = string.Empty;
        public string StandardError { get; init; } = string.Empty;

        public bool Success => ExitCode == 0;
    }

    /// <summary>
    /// Runs the specified executable with arguments and returns the captured
    /// output. Throws on timeout.
    /// </summary>
    public static CommandResult RunCommand(string fileName, string arguments, string? workingDirectory = null, int timeoutMs = 120_000)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (workingDirectory is not null)
            psi.WorkingDirectory = workingDirectory;

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(timeoutMs))
        {
            try { process.Kill(true); } catch { }
            throw new TimeoutException($"Command '{fileName} {arguments}' timed out after {timeoutMs}ms");
        }

        return new CommandResult
        {
            ExitCode = process.ExitCode,
            StandardOutput = stdoutTask.Result,
            StandardError = stderrTask.Result
        };
    }

    /// <summary>
    /// Locates the repository root by walking up from the test assembly's
    /// directory until a <c>Switchyard.slnx</c> file is found.
    /// </summary>
    public static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "Switchyard.slnx")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException("Could not locate repository root (Switchyard.slnx not found).");
    }

    /// <summary>
    /// The absolute path to the local NuGet feed used by all test sample apps.
    /// </summary>
    public static string LocalFeedPath => Path.Combine(FindRepoRoot(), "test", "local-feed");

    /// <summary>
    /// The absolute path to the TestSamples directory (copied into the test
    /// output by the csproj).
    /// </summary>
    public static string TestSamplesPath => Path.Combine(AppContext.BaseDirectory, "TestSamples");

    private static readonly object FeedLock = new();
    private static bool _feedReady;

    /// <summary>
    /// Packs Switchyard, CommonUtils, and TargetLib into the local feed.
    /// Thread-safe and idempotent — subsequent calls are no-ops.
    /// </summary>
    public static void EnsureLocalFeedReady()
    {
        lock (FeedLock)
        {
            if (_feedReady)
                return;

            var repoRoot = FindRepoRoot();
            Directory.CreateDirectory(LocalFeedPath);

            // Purge any previously restored copies of the Switchyard package
            // from the global NuGet cache. The Switchyard package is rebuilt
            // from the current source on every test run (its .targets/.dll may
            // have changed), but NuGet will keep serving the stale 1.0.0 from
            // the global packages folder unless we force re-extraction.
            PurgeGlobalPackageCache("switchyard");
            PurgeGlobalPackageCache("nativebindinglib");

            // 1. Pack Switchyard
            PackSwitchyard(repoRoot);

            // 2. Pack CommonUtils at versions 1.0.0, 2.0.0, 3.5.0
            PackCommonUtils(repoRoot, "1.0.0");
            PackCommonUtils(repoRoot, "2.0.0");
            PackCommonUtils(repoRoot, "3.5.0");

            // 3. Pack TargetLib at versions 1.0.0, 2.0.0, 3.5.0
            //    (each depending on the matching CommonUtils version)
            PackTargetLib(repoRoot, "1.0.0", "1.0.0");
            PackTargetLib(repoRoot, "2.0.0", "2.0.0");
            PackTargetLib(repoRoot, "3.5.0", "3.5.0");

            // 4. Build the native library fixture for the host RID and pack
            //    NativeBindingLib at 1.0.0, 2.0.0 and 3.5.0. This exercises the
            //    managed + native-dependency scenario (the Avalonia + higher
            //    SkiaSharp pattern). 2.0.0 is the unused "original" version.
            BuildAndPackNativeBindingLib(repoRoot, "1.0.0");
            BuildAndPackNativeBindingLib(repoRoot, "2.0.0");
            BuildAndPackNativeBindingLib(repoRoot, "3.5.0");

            _feedReady = true;
        }
    }

    private static void PackSwitchyard(string repoRoot)
    {
        string csproj = Path.Combine(repoRoot, "Switchyard", "Switchyard.csproj");
        var result = RunCommand("dotnet", $"pack \"{csproj}\" -c Release -p:PackageVersion=1.0.0 -o \"{LocalFeedPath}\" --nologo", repoRoot);
        if (!result.Success)
            throw new InvalidOperationException($"Failed to pack Switchyard:\n{result.StandardError}");
    }

    /// <summary>
    /// The runtime identifier of the host that runs the tests. The native
    /// library fixture is built only for this RID so the sample's P/Invoke
    /// resolves at runtime.
    /// </summary>
    public static string HostRuntimeIdentifier
    {
        get
        {
            string arch = RuntimeInformation.OSArchitecture switch
            {
                Architecture.X64 => "x64",
                Architecture.X86 => "x86",
                Architecture.Arm64 => "arm64",
                _ => "x64",
            };
            if (OperatingSystem.IsWindows()) return "win-" + arch;
            if (OperatingSystem.IsLinux()) return "linux-" + arch;
            if (OperatingSystem.IsMacOS()) return "osx-" + arch;
            return "win-" + arch;
        }
    }

    /// <summary>
    /// Compiles the native library fixture (native.c) for the host RID with
    /// the version constant baked in, then packs NativeBindingLib at that
    /// version. The native file lands under runtimes/{rid}/native/ in the
    /// package so the consuming app's P/Invoke resolves to it.
    /// </summary>
    private static void BuildAndPackNativeBindingLib(string repoRoot, string version)
    {
        string fixtureDir = Path.Combine(repoRoot, "test", "fixtures", "NativeBindingLib");
        string rid = HostRuntimeIdentifier;
        string nativeOutDir = Path.Combine(fixtureDir, "runtimes", rid, "native");
        Directory.CreateDirectory(nativeOutDir);

        // Parse the major version as the integer constant baked into the
        // native lib (1.0.0 -> 1, 3.5.0 -> 3). Keeps the assertion readable.
        int verConst = int.Parse(version.Split('.')[0]);

        if (OperatingSystem.IsWindows())
        {
            // cl.exe is not normally on PATH. Locate the MSVC dev environment
            // via vswhere and run cl inside an initialised vcvars64 shell so
            // the compiler + linker paths resolve. We write a temporary batch
            // file to avoid fragile nested-quoting through cmd /c.
            string vcvars = FindMsvcDevCmd()
                ?? throw new InvalidOperationException(
                    "Could not locate the MSVC dev environment (vcvars64.bat) via vswhere. " +
                    "Visual Studio with the C++ workload is required to build the native fixture on Windows.");
            string outDll = Path.Combine(nativeOutDir, "libnativebinding.dll");
            string objFile = Path.Combine(nativeOutDir, "native.obj");
            string batPath = Path.Combine(nativeOutDir, "_build_native.bat");
            File.WriteAllText(batPath,
                $"@echo off\r\n" +
                $"call \"{vcvars}\"\r\n" +
                $"cl /LD /O2 /D NATIVE_VER={verConst} \"{Path.Combine(fixtureDir, "native.c")}\" " +
                $"/Fo:\"{objFile}\" /Fe:\"{outDll}\" /link /NOLOGO\r\n");
            var result = RunCommand("cmd", $"/c \"{batPath}\"", fixtureDir);
            if (!result.Success || !File.Exists(outDll))
                throw new InvalidOperationException($"cl.exe failed for NativeBindingLib {version}:\n{result.StandardError}\n{result.StandardOutput}");
            try { File.Delete(batPath); File.Delete(objFile); } catch { }
        }
        else
        {
            string outSo = Path.Combine(nativeOutDir, "libnativebinding.so");
            var result = RunCommand("gcc",
                $"-shared -fPIC -O2 -DNATIVE_VER={verConst} -o \"{outSo}\" native.c",
                fixtureDir);
            if (!result.Success)
                throw new InvalidOperationException($"gcc failed for NativeBindingLib {version}:\n{result.StandardError}\n{result.StandardOutput}");
        }

        // Pack the managed project with the host RID so the correct native
        // file is included in the package.
        string csproj = Path.Combine(fixtureDir, "NativeBindingLib.csproj");
        var packResult = RunCommand("dotnet",
            $"pack \"{csproj}\" -c Release" +
            $" -p:NativeBindingLibPackageVersion={version}" +
            $" -p:NativeBindingLibAssemblyVersion={version}" +
            $" -p:NativeRuntimeRID={rid}" +
            $" -o \"{LocalFeedPath}\" --nologo", repoRoot);
        if (!packResult.Success)
            throw new InvalidOperationException($"Failed to pack NativeBindingLib {version}:\n{packResult.StandardError}\n{packResult.StandardOutput}");
    }

    /// <summary>
    /// Locates the MSVC <c>vcvars64.bat</c> dev-environment script via
    /// <c>vswhere</c> so the native build can invoke <c>cl.exe</c> even though
    /// it is not on PATH. Returns <c>null</c> on non-Windows hosts.
    /// </summary>
    private static string? FindMsvcDevCmd()
    {
        if (!OperatingSystem.IsWindows()) return null;
        string vswhere = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (!File.Exists(vswhere)) return null;

        var vsResult = RunCommand(vswhere, "-latest -products * -property installationPath");
        if (!vsResult.Success) return null;
        string install = vsResult.StandardOutput.Trim();
        if (string.IsNullOrEmpty(install)) return null;

        string vcvars = Path.Combine(install, "VC", "Auxiliary", "Build", "vcvars64.bat");
        return File.Exists(vcvars) ? vcvars : null;
    }

    /// <summary>
    /// Deletes the supplied package id (all versions) from the user's global
    /// NuGet packages folder so that the next restore re-extracts it from the
    /// local feed. Required for the Switchyard package whose contents may
    /// change between test runs while its version stays pinned at 1.0.0.
    /// </summary>
    private static void PurgeGlobalPackageCache(string packageId)
    {
        string globalPackages = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".nuget", "packages", packageId.ToLowerInvariant());
        if (Directory.Exists(globalPackages))
        {
            try { Directory.Delete(globalPackages, recursive: true); }
            catch { }
        }
    }

    private static void PackCommonUtils(string repoRoot, string version)
    {
        string csproj = Path.Combine(repoRoot, "test", "fixtures", "CommonUtils", "CommonUtils.csproj");
        var result = RunCommand("dotnet",
            $"pack \"{csproj}\" -c Release" +
            $" -p:CommonUtilsPackageVersion={version}" +
            $" -p:CommonUtilsAssemblyVersion={version}" +
            $" -o \"{LocalFeedPath}\" --nologo", repoRoot);
        if (!result.Success)
            throw new InvalidOperationException($"Failed to pack CommonUtils {version}:\n{result.StandardError}");
    }

    private static void PackTargetLib(string repoRoot, string packageVersion, string commonUtilsVersion)
    {
        string csproj = Path.Combine(repoRoot, "test", "fixtures", "TargetLib", "TargetLib.csproj");
        var result = RunCommand("dotnet",
            $"pack \"{csproj}\" -c Release" +
            $" -p:TargetLibPackageVersion={packageVersion}" +
            $" -p:TargetLibAssemblyVersion={packageVersion}" +
            $" -p:CommonUtilsPackageVersion={commonUtilsVersion}" +
            $" -o \"{LocalFeedPath}\" --nologo", repoRoot);
        if (!result.Success)
            throw new InvalidOperationException($"Failed to pack TargetLib {packageVersion}:\nSTDOUT:\n{result.StandardOutput}\nSTDERR:\n{result.StandardError}");
    }

    /// <summary>
    /// Cleans the build output of a test sample project, forcing a full
    /// rebuild on the next <c>dotnet build</c>.
    /// </summary>
    public static void CleanProject(string projectPath)
    {
        RunCommand("dotnet", $"clean \"{projectPath}\" --nologo", Path.GetDirectoryName(projectPath));
    }

    /// <summary>
    /// Builds a test sample project.
    /// </summary>
    public static CommandResult BuildProject(string projectPath, string configuration = "Debug")
    {
        return RunCommand("dotnet", $"build \"{projectPath}\" -c {configuration} --nologo", Path.GetDirectoryName(projectPath));
    }

    /// <summary>
    /// Publishes a test sample project.
    /// </summary>
    public static CommandResult PublishProject(string projectPath, string configuration = "Release")
    {
        return RunCommand("dotnet", $"publish \"{projectPath}\" -c {configuration} --nologo", Path.GetDirectoryName(projectPath));
    }

    /// <summary>
    /// Runs a built executable and returns the captured output.
    /// </summary>
    public static CommandResult RunExecutable(string exePath, string workingDirectory)
    {
        return RunCommand(exePath, "", workingDirectory);
    }

    /// <summary>
    /// Locates the built executable for a test sample project.
    /// </summary>
    public static string FindBuiltExecutable(string projectName, string configuration = "Debug", string framework = "net8.0")
    {
        string projectDir = Path.Combine(TestSamplesPath, projectName);
        string exeName = OperatingSystem.IsWindows() ? projectName + ".exe" : projectName;
        string exePath = Path.Combine(projectDir, "bin", configuration, framework, exeName);
        return exePath;
    }

    /// <summary>
    /// Returns the bin output directory for a test sample project.
    /// </summary>
    public static string GetBinDirectory(string projectName, string configuration = "Debug", string framework = "net8.0")
    {
        string projectDir = Path.Combine(TestSamplesPath, projectName);
        return Path.Combine(projectDir, "bin", configuration, framework);
    }

    /// <summary>
    /// Returns the publish output directory for a test sample project.
    /// </summary>
    public static string GetPublishDirectory(string projectName, string configuration = "Release", string framework = "net8.0")
    {
        string projectDir = Path.Combine(TestSamplesPath, projectName);
        return Path.Combine(projectDir, "bin", configuration, framework, "publish");
    }
}
