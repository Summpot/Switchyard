using Xunit;

namespace Switchyard.IntegrationTests;

/// <summary>
/// Verifies the packed Switchyard NuGet package is self-contained and usable
/// from a clean external project. Most integration samples exercise the local
/// feed, but this test keeps the package contract explicit: restore the nupkg,
/// import its build assets, load the task DLL, and run the weaving pipeline.
/// </summary>
[Collection("Integration")]
public class PackageSmokeTests
{
    private readonly LocalFeedFixture _fixture;

    public PackageSmokeTests(LocalFeedFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void PackedNugetPackage_RestoresAndRunsTargets_FromCleanProject()
    {
        string root = Path.Combine(Path.GetTempPath(), "switchyard-package-smoke", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            string feed = Path.Combine(root, "feed");
            Directory.CreateDirectory(feed);
            PackMinimalFeed(feed);

            File.WriteAllText(Path.Combine(root, "nuget.config"),
                $"""
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <packageSources>
                    <clear />
                    <add key="switchyard-smoke-feed" value="{feed}" />
                    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
                  </packageSources>
                </configuration>
                """);

            File.WriteAllText(Path.Combine(root, "PackageSmokeApp.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                    <SwitchyardEnabled>true</SwitchyardEnabled>
                    <RestorePackagesPath>packages</RestorePackagesPath>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="Switchyard" Version="1.0.0" />
                    <PackageReference Include="TargetLib" Version="2.0.0">
                      <SwitchyardRoutes>PackageSmokeApp=1.0.0</SwitchyardRoutes>
                    </PackageReference>
                  </ItemGroup>
                </Project>
                """);

            File.WriteAllText(Path.Combine(root, "Program.cs"),
                """
                using TargetLib;

                Console.WriteLine(VersionReport.GetVersion());
                """);

            var result = BuildUtility.RunCommand(
                "dotnet",
                "build PackageSmokeApp.csproj -c Release --nologo",
                root,
                timeoutMs: 180_000);

            Assert.True(result.Success,
                $"Build failed:\nSTDOUT:\n{result.StandardOutput}\nSTDERR:\n{result.StandardError}");
            Assert.Contains("Switchyard: processing", result.StandardOutput);

            string binDir = Path.Combine(root, "bin", "Release", "net10.0");
            Assert.True(File.Exists(Path.Combine(binDir, "TargetLib.Switchyard.1.0.0.dll")));
            Assert.False(File.Exists(Path.Combine(binDir, "TargetLib.dll")));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static void PackMinimalFeed(string feed)
    {
        string repoRoot = BuildUtility.FindRepoRoot();

        RunRequired("dotnet",
            $"pack \"{Path.Combine(repoRoot, "Switchyard", "Switchyard.csproj")}\" -c Release -p:PackageVersion=1.0.0 -o \"{feed}\" --nologo",
            repoRoot);
        RunRequired("dotnet",
            $"pack \"{Path.Combine(repoRoot, "test", "fixtures", "CommonUtils", "CommonUtils.csproj")}\" -c Release -p:CommonUtilsPackageVersion=1.0.0 -p:CommonUtilsAssemblyVersion=1.0.0 -o \"{feed}\" --nologo",
            repoRoot);
        RunRequired("dotnet",
            $"pack \"{Path.Combine(repoRoot, "test", "fixtures", "TargetLib", "TargetLib.csproj")}\" -c Release -p:TargetLibPackageVersion=1.0.0 -p:TargetLibAssemblyVersion=1.0.0 -p:CommonUtilsPackageVersion=1.0.0 -o \"{feed}\" --nologo",
            repoRoot);
        RunRequired("dotnet",
            $"pack \"{Path.Combine(repoRoot, "test", "fixtures", "TargetLib", "TargetLib.csproj")}\" -c Release -p:TargetLibPackageVersion=2.0.0 -p:TargetLibAssemblyVersion=2.0.0 -p:CommonUtilsPackageVersion=1.0.0 -o \"{feed}\" --nologo",
            repoRoot);
    }

    private static void RunRequired(string fileName, string arguments, string workingDirectory)
    {
        var result = BuildUtility.RunCommand(fileName, arguments, workingDirectory, timeoutMs: 180_000);
        if (!result.Success)
        {
            throw new InvalidOperationException(
                $"Command failed: {fileName} {arguments}\nSTDOUT:\n{result.StandardOutput}\nSTDERR:\n{result.StandardError}");
        }
    }
}
