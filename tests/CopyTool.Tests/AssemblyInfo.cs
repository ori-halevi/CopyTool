using Xunit;

// These tests run against the real filesystem and several of them lean on
// process-wide state — the volume profile caches above all. The csproj used to
// declare this with <ParallelizeTestCollections>, which only xUnit's own MSBuild
// runner reads; under `dotnet test` the VSTest adapter never saw it, so the
// suite had been running in parallel the whole time it claimed not to be.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
