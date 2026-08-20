using Xunit;

namespace Contoso.Lending.ParityTests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ParityArtifactCollection : ICollectionFixture<ParityArtifactFixture>
{
    public const string Name = "Parity artifact";
}

public sealed class ParityArtifactFixture : IDisposable
{
    public ParityArtifactFixture()
    {
        ParityArtifact.Reset();
    }

    public void Dispose()
    {
        string root = Directory.GetParent(GoldenCorpus.Dir)!.Parent!.FullName;
        string path = Environment.GetEnvironmentVariable("PARITY_L2_ARTIFACT")
            ?? Path.Combine(root, "parity", "artifacts", "l2-cases.json");
        ParityArtifact.Flush(path);
    }
}
