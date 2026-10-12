using Atelia.DurableGraph.Storage;
using Atelia.Rbf;
using Atelia.Rbf.Internal;
using Atelia.RbfSegmentStore;
using Node = Atelia.DurableGraph.Persistence.Tests.SharedReadModel.Node;

namespace Atelia.DurableGraph.Persistence.Tests;

internal sealed class DB085S0CFixture : IDisposable {
    private const string Prefix = "durable-db085-s0c-";
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
    internal static readonly ReadAmplificationBaseBudgetParameters NoRebase = new(1000000, 1);
    internal CheckpointAddress Head { get; }
    internal DB085S0CFixture(int commits = 0, int liveNodes = 1) {
        Node first = new() { Value = 1 }, cursor = first;
        for (int i = 1; i < liveNodes; i++) { cursor.Next = new() { Value = (byte)i }; cursor = cursor.Next; }
        using var repository = Repository.CreateNew(Root, SharedReadModel.Models(), new() { NewStoreLayout = RbfSegmentStoreLayout.Flat });
        using var branch = repository.CreateBranch("main", first, NoRebase);
        for (int i = 0; i < commits; i++) { first.Value++; branch.CommitState(first, NoRebase); }
        Head = branch.Head;
    }
    internal string FileFor(string role) => role == "schema" ? Path.Combine(Root, "schemas.rbf") :
        Directory.GetFiles(Path.Combine(Root, role == "event" ? "journal/events" : role == "ref" ? "journal/refs/objects" : "state"), "*.rbf", SearchOption.AllDirectories).Single();
    private IEnumerable<string> SourceFiles() => Directory.GetFiles(Root, "*", SearchOption.AllDirectories)
        .Where(p => !string.Equals(Path.GetFileName(p), "repository.lock", StringComparison.OrdinalIgnoreCase));
    internal Dictionary<string, byte[]> Snapshot() => SourceFiles().ToDictionary(p => p, File.ReadAllBytes);
    internal void AssertUnchanged(Dictionary<string, byte[]> before) {
        Assert.Equal(before.Keys.Order(), SourceFiles().Order());
        foreach (var pair in before) { Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key)); }
    }
    internal void AssertLockReleased() {
        using var exclusive = new FileStream(Path.Combine(Root, "repository.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }
    internal void AssertLockHeld() => Assert.ThrowsAny<IOException>(() =>
        new FileStream(Path.Combine(Root, "repository.lock"), FileMode.Open, FileAccess.Read, FileShare.Read).Dispose());
    internal void PartialWrite(string role, Func<int, int>? selectWrittenBytes = null) {
        string path = Path.GetFullPath(FileFor(role));
        long length = new FileInfo(path).Length;
        int observations = 0;
        int writtenBytes = 0;
        RbfWriteInstrumentation.Current = new() {
            BeforeWrite = request => string.Equals(request.Path, path, StringComparison.OrdinalIgnoreCase)
                ? writtenBytes = selectWrittenBytes?.Invoke(request.RequestedBytes) ?? 2 : request.RequestedBytes,
            AfterWrite = observation => { if (string.Equals(observation.Path, path, StringComparison.OrdinalIgnoreCase)) { observations++; Assert.Equal(writtenBytes, observation.WrittenBytes); } },
        };
        try {
            using var writer = RbfFile.OpenExisting(path);
            Assert.Throws<IOException>(() => writer.Append(0xF200, new byte[64]));
        }
        finally { RbfWriteInstrumentation.Current = null; }
        Assert.Equal(1, observations);
        Assert.Equal(length + writtenBytes, new FileInfo(path).Length);
    }
    internal void ContinueAndColdOpen(Repository repository, byte expected = 1) {
        CheckpointAddress next;
        using (var branch = repository.Checkout("main")) {
            Node state = Assert.IsType<Node>(branch.State);
            Assert.Equal(expected, state.Value);
            state.Value = 99;
            next = branch.CommitState(NoRebase);
        }
        repository.Dispose();
        using var fresh = Repository.OpenExisting(Root, SharedReadModel.Models());
        Assert.Equal(next.RevisionAddress, fresh.GetHead("main").RevisionAddress);
        Assert.Equal((byte)99, Assert.IsType<Node>(fresh.ReadState(fresh.GetHead("main"))).Value);
    }
    public void Dispose() => SharedReadModel.DeleteFixture(Root, Prefix);
}
