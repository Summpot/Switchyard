using System.Diagnostics;

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
