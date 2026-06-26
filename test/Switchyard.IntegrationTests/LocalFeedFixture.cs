using Xunit;

namespace Switchyard.IntegrationTests;

/// <summary>
/// xUnit collection fixture that ensures the local NuGet feed is populated
/// with Switchyard, TargetLib, and CommonUtils packages before any
/// integration test runs.
/// </summary>
public sealed class LocalFeedFixture : IDisposable
{
    public LocalFeedFixture()
    {
        BuildUtility.EnsureLocalFeedReady();
    }

    public void Dispose()
    {
    }
}

/// <summary>
/// Collection definition that pins all integration tests to a single
/// <see cref="LocalFeedFixture"/> instance so xUnit serialises them.
/// </summary>
/// <remarks>
/// Serialisation is required because the <c>TestSamples</c> are copied
/// verbatim into the test output directory and build into the same physical
/// <c>bin/obj</c> tree — running them in parallel causes MSBuild file-lock
/// contention on <c>obj/switchyard/*.dll</c>.
/// </remarks>
[CollectionDefinition("Integration")]
public sealed class IntegrationCollection : ICollectionFixture<LocalFeedFixture>
{
}
