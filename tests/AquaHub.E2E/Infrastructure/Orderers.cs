using Xunit.Sdk;

[assembly: CollectionBehavior(DisableTestParallelization = true, MaxParallelThreads = 1)]
[assembly: TestCollectionOrderer("AquaHub.E2E.Infrastructure.ByNameCollectionOrderer", "AquaHub.E2E")]
[assembly: TestCaseOrderer("AquaHub.E2E.Infrastructure.ByMethodNameOrderer", "AquaHub.E2E")]

namespace AquaHub.E2E.Infrastructure;

/// <summary>Runs test classes in name order (A01…, A02…, Z_Lifecycle last) so the run is deterministic.</summary>
public sealed class ByNameCollectionOrderer : ITestCollectionOrderer
{
    public IEnumerable<ITestCollection> OrderTestCollections(IEnumerable<ITestCollection> testCollections) =>
        testCollections.OrderBy(c => c.DisplayName, StringComparer.Ordinal);
}

/// <summary>Runs tests inside a class in method-name order (T01_…, T02_…).</summary>
public sealed class ByMethodNameOrderer : ITestCaseOrderer
{
    public IEnumerable<TTestCase> OrderTestCases<TTestCase>(IEnumerable<TTestCase> testCases) where TTestCase : ITestCase =>
        testCases.OrderBy(t => t.TestMethod.Method.Name, StringComparer.Ordinal);
}
