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
/// Collection definition so all integration tests share a single
/// <see cref="LocalFeedFixture"/> instance.
/// </summary>
[CollectionDefinition("Integration")]
public sealed class IntegrationCollection : ICollectionFixture<LocalFeedFixture>
{
}
