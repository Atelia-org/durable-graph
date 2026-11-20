using Atelia.Binary;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;
using Atelia.DurableGraph.Storage;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class DeclaredNodeGraphTests : IDisposable {
    private const string Prefix = "dg-declared-node-graph-";
    private const int Width = 4096;
    private readonly string _path = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
    private static readonly TypeExpr NodeType = TypeExpr.Named(GraphMember.SchemaId);
    private static readonly TypeExpr StringType = TypeExpr.Builtin(TypeTag.String);

    [Fact]
    public void ColdGraphsKeepListAliasesAndCyclesWhileAWideEmbeddedVectorWritesOnlyItsChangedSlot() {
        GraphRef initial, changed;
        using (var writer = Repository.CreateNew(_path, Models())) {
            var a = new GraphMember { Number = 11 };
            var b = new GraphMember { Number = 22 };
            var c = new GraphMember { Number = 33 };
            GraphMember?[] sharedVector = new GraphMember?[Width];
            sharedVector[0] = sharedVector[1] = b;
            sharedVector[^1] = a;
            a.Links = b.Links = sharedVector;
            List<GraphMember> sharedList = [a, b, a, c];
            a.Bucket = b.Bucket = sharedList;
            using var graph = writer.CreateGraph(new TextWitnessBox<GraphMember> { Value = a, Items = sharedList });
            initial = writer.SaveGraph(graph);
        }
        using (var editor = Repository.OpenExisting(_path, Models())) {
            using var left = editor.OpenGraph(initial);
            using var right = editor.OpenGraph(initial);
            var modified = Check(left, changed: false);
            var untouched = Check(right, changed: false);
            Assert.NotSame(modified.Items, untouched.Items);
            Assert.NotSame(modified.Value, untouched.Value);
            Assert.NotSame(modified.Value.Links, untouched.Value.Links);
            modified.Value.Links[Width / 2] = modified.Items[3];
            changed = editor.SaveGraph(left);
            Check(left, changed: true);
            Check(right, changed: false);

            var prior = Revision(editor, initial);
            var current = Revision(editor, changed);
            var delta = Assert.Single(editor.GraphStore.ReadBatch(current.OwnedBatch!.Value).Records);
            Assert.Equal(ObjectVersionKind.Delta, delta.Kind);
            Assert.Equal(prior.ObjectHeads[delta.ObjectId], delta.PriorAddress);
            BareValueReader patch = new(delta.Body);
            Assert.Equal(1u, patch.ReadVarUInt32()); // Links is the second declared member.
            Assert.Equal(1, patch.ReadByte()); // Same-length, sparse vector patch.
            Assert.Equal((uint)(Width / 2 + 1), patch.ReadVarUInt32());
            uint targetId = patch.ReadVarUInt32();
            Assert.Equal(0u, patch.ReadVarUInt32());
            patch.EnsureFullyConsumed();
            Assert.Equal(11, PriorNodeNumber(editor, prior, delta.ObjectId));
            Assert.Equal(33, PriorNodeNumber(editor, prior, targetId));
            Assert.Equal(prior.ObjectHeads.Count, current.ObjectHeads.Count);
            foreach (var (id, originalHead) in prior.ObjectHeads) {
                if (id != delta.ObjectId) { Assert.Equal(originalHead, current.ObjectHeads[id]); }
            }
            var unchanged = Revision(editor, editor.SaveGraph(left));
            Assert.Null(unchanged.OwnedBatch);
        }
        using var cold = Repository.OpenReadOnlyExisting(_path, Models());
        using var before = cold.OpenGraph(initial);
        using var after = cold.OpenGraph(changed);
        Check(before, changed: false);
        Check(after, changed: true);
    }

    private static TextWitnessBox<GraphMember> Check(GraphBuilder graph, bool changed) {
        var root = Assert.IsType<TextWitnessBox<GraphMember>>(graph.Root);
        var a = root.Value;
        Assert.Equal(4, root.Items.Count);
        var b = root.Items[1];
        var c = root.Items[3];
        Assert.Equal(new[] { 11, 22, 11, 33 }, root.Items.Select(node => node.Number));
        Assert.Same(a, root.Items[0]);
        Assert.Same(a, root.Items[2]);
        Assert.Same(root.Items, a.Bucket);
        Assert.Same(root.Items, b.Bucket);
        Assert.NotSame(a.Links, b.Links); // Both held the same input array. Only the targets have graph identity.
        Assert.Equal(Width, a.Links.Length);
        Assert.Equal(Width, b.Links.Length);
        for (int i = 0; i < Width; i++) {
            var original = i < 2 ? b : i == Width - 1 ? a : null;
            Assert.Same(original, b.Links[i]);
            Assert.Same(changed && i == Width / 2 ? c : original, a.Links[i]);
        }
        Assert.Empty(c.Links);
        Assert.Null(c.Bucket);
        return root;
    }

    private static int PriorNodeNumber(Repository repository, StoredGraphRevision prior, uint id) {
        var record = Assert.Single(repository.GraphStore.ReadObjectChain(prior, id).Entries).Record;
        Assert.Equal(ObjectVersionKind.Base, record.Kind);
        var catalog = SchemaCatalogView.Load(repository.GraphStore, prior.SchemaRoot!.Value);
        var typed = TypedBaseBodyCodec.Decode(record.Body, catalog);
        Assert.Equal(NodeType, typed.Layout.Schema!.Type);
        BareValueReader reader = new(typed.Body);
        return reader.ReadVarInt32();
    }

    private static StoredGraphRevision Revision(Repository repo, GraphRef saved) =>
        repo.GraphStore.ReadBusinessGraph(DB096TestAccess.Resources(repo).ReadHeapDescriptor(saved.CommitId.Value).Address);

    [Fact]
    public void BroaderClrArrayAccessorsRestoreTheExactPersistentVectorAndItsAliases() {
        var target = TypeExpr.VectorArray(StringType);
        var models = new StateModelRegistry();
        models.Register(global::Atelia.DurableGraph.Generated.Family_546578745769746E657373426F78.Definition);
        new DeclaredNode<GraphArraySlots>(static () => new(), [
            DeclaredMember<GraphArraySlots>.Reference<object[]>(target, n => n.Single, (n, a) => n.Single = a),
            DeclaredMember<GraphArraySlots>.References<object[]>(target, n => n.Many, (n, a) => n.Many = a),
        ]).Register(models);
        string[] array = ["first", "second"];
        GraphRef saved;
        using (var repo = Repository.CreateNew(_path, models)) {
            var node = new GraphArraySlots { Single = array, Many = [array, null, array] };
            using var graph = repo.CreateGraph(new TextWitnessBox<GraphArraySlots> { Value = node });
            saved = repo.SaveGraph(graph);
        }
        using var cold = Repository.OpenReadOnlyExisting(_path, models);
        using var restored = cold.OpenGraph(saved);
        var member = Assert.IsType<TextWitnessBox<GraphArraySlots>>(restored.Root).Value;
        Assert.Equal(array, Assert.IsType<string[]>(member.Single));
        Assert.Collection(member.Many, a => Assert.Same(member.Single, a), Assert.Null, a => Assert.Same(member.Single, a));
        var id = new ObjectId(1);
        var exactTable = new ObjectReadTable(new Dictionary<ObjectId, object> { [id] = array });
        Assert.Throws<InvalidDataException>(() => exactTable.ResolveObject<object[]>(id));
    }

    [Fact]
    public void NonNullVectorElementMustSatisfyItsDeclaredPersistentTargetBeforePublication() {
        var models = new StateModelRegistry();
        models.Register(global::Atelia.DurableGraph.Generated.Family_546578745769746E657373426F78.Definition);
        new DeclaredNode<GraphAccessorSlots>(static () => new(), [
            DeclaredMember<GraphAccessorSlots>.References<object>(StringType, n => n.Links, (n, links) => n.Links = links),
        ]).Register(models);
        using var repo = Repository.CreateNew(_path, models);
        var node = new GraphAccessorSlots();
        using var graph = repo.CreateGraph(new TextWitnessBox<GraphAccessorSlots> { Value = node });
        var first = repo.SaveGraph(graph);
        var tip = DB096TestAccess.Resources(repo).ReadHeapTip();
        long frames = repo.GraphStore.AppendedFrames;
        node.Links = [null, node];
        Assert.Throws<InvalidOperationException>(() => repo.SaveGraph(graph));
        Assert.Equal(frames, repo.GraphStore.AppendedFrames);
        Assert.Equal(tip, DB096TestAccess.Resources(repo).ReadHeapTip());
        node.Links = [];
        var retry = Revision(repo, repo.SaveGraph(graph));
        Assert.Equal(Revision(repo, first).DirectoryRoot, retry.DirectoryRoot);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ImpossibleStringAccessorFailsBeforeAnEmptySlotCanPublishItsSchema(bool repeated, bool missingElementType) {
        TypeExpr list = TypeExpr.List(missingElementType ? TypeExpr.Named("NoCurrentClrType") : StringType);
        var bad = repeated
            ? DeclaredMember<GraphAccessorSlots>.References<string>(list, _ => [], (_, _) => { })
            : DeclaredMember<GraphAccessorSlots>.Reference<string>(list, _ => null, (_, _) => { });
        var models = new StateModelRegistry();
        models.Register(global::Atelia.DurableGraph.Generated.Family_546578745769746E657373426F78.Definition);
        new DeclaredNode<GraphAccessorSlots>(static () => new(), [
            DeclaredMember<GraphAccessorSlots>.Int32(_ => 0, (_, _) => { }), bad,
        ]).Register(models);
        using var repo = Repository.CreateNew(_path, models);
        using var graph = repo.CreateGraph(new TextWitnessBox<GraphAccessorSlots> { Value = new() });
        long frames = repo.GraphStore.AppendedFrames;
        for (int attempt = 0; attempt < 2; attempt++) {
            var error = Assert.Throws<InvalidOperationException>(() => repo.SaveGraph(graph));
            Assert.Contains("Test.DeclaredAccessorSlots slot 1", error.Message);
            Assert.Contains(list.ToString(), error.Message);
            Assert.Contains("System.String", error.Message);
            Assert.Equal(frames, repo.GraphStore.AppendedFrames);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InterfaceAccessorAcceptsAListOfNodesRegardlessOfRegistrationOrder(bool ownerFirst) {
        var owner = new DeclaredNode<GraphAccessorSlots>(static () => new(), [
            DeclaredMember<GraphAccessorSlots>.Reference<IReadOnlyList<GraphMember>>(TypeExpr.List(NodeType),
                n => n.Members, (n, members) => n.Members = members),
        ]);
        var member = new DeclaredNode<GraphMember>(static () => new(), []);
        var models = new StateModelRegistry();
        models.Register(global::Atelia.DurableGraph.Generated.Family_546578745769746E657373426F78.Definition);
        if (ownerFirst) { owner.Register(models); member.Register(models); }
        else { member.Register(models); owner.Register(models); }
        GraphRef saved;
        using (var repo = Repository.CreateNew(_path, models)) {
            var shared = new GraphMember();
            var node = new GraphAccessorSlots { Members = new List<GraphMember> { shared, shared } };
            using var graph = repo.CreateGraph(new TextWitnessBox<GraphAccessorSlots> { Value = node });
            saved = repo.SaveGraph(graph);
        }
        using var cold = Repository.OpenReadOnlyExisting(_path, models);
        using var restored = cold.OpenGraph(saved);
        var list = Assert.IsType<List<GraphMember>>(((TextWitnessBox<GraphAccessorSlots>)restored.Root).Value.Members);
        Assert.Equal(2, list.Count);
        Assert.Same(list[0], list[1]);
    }

    [Fact]
    public void NarrowAccessorCanReceiveADerivedSchemaWhoseNominalBaseHasNoCurrentClrRegistration() {
        DurableSchema parent = new("Test.DeclaredAccessorBase", 1);
        DurableSchema child = new("Test.DeclaredAccessorChild", 1, [], parent);
        var models = new StateModelRegistry();
        models.Register(global::Atelia.DurableGraph.Generated.Family_546578745769746E657373426F78.Definition);
        new DeclaredNode<GraphAccessorSlots>(static () => new(), [
            DeclaredMember<GraphAccessorSlots>.Reference<ManualChild>(parent.Type,
                n => (ManualChild?)n.Link, (n, value) => n.Link = value),
            DeclaredMember<GraphAccessorSlots>.Reference<IReadOnlyList<ManualChild>>(TypeExpr.List(parent.Type),
                _ => null, (_, _) => { }),
        ]).Register(models);
        static void Visit(in byte state, IStateReferenceVisitor visitor) { }
        var prepare = new StatePreparationBinding<byte>(child, (in byte state) => new([state]),
            (in byte old, in byte current) => new(old != current, [current]));
        var reader = new StateReaderBinding<byte>(child, (ref BareValueReader input) => input.ReadByte(),
            (ref BareValueReader input, in byte _) => input.ReadByte(), Visit);
        models.Register(new StateModelBinding<ManualChild, byte>(prepare, [reader], row => row.GetState<byte>(),
            static () => new(), (ManualChild value, in byte state, ObjectReadTable _) => value.Value = state,
            (value, _) => value.Value, Visit));

        GraphRef saved;
        using (var repo = Repository.CreateNew(_path, models)) {
            var node = new GraphAccessorSlots { Link = new ManualChild { Value = 42 } };
            using var graph = repo.CreateGraph(new TextWitnessBox<GraphAccessorSlots> { Value = node });
            saved = repo.SaveGraph(graph);
        }
        using var cold = Repository.OpenReadOnlyExisting(_path, models);
        using var restored = cold.OpenGraph(saved);
        Assert.Equal(42, Assert.IsType<ManualChild>(((TextWitnessBox<GraphAccessorSlots>)restored.Root).Value.Link).Value);
    }

    private sealed class ManualChild : IDurableObject {
        internal byte Value;
    }

    private static StateModelRegistry Models() {
        var models = new StateModelRegistry();
        models.Register(global::Atelia.DurableGraph.Generated.Family_546578745769746E657373426F78.Definition);
        new DeclaredNode<GraphMember>(static () => new(), [
            DeclaredMember<GraphMember>.Int32(n => n.Number, (n, number) => n.Number = number),
            DeclaredMember<GraphMember>.References<GraphMember>(NodeType, n => n.Links, (n, links) => n.Links = links),
            DeclaredMember<GraphMember>.Reference<List<GraphMember>>(TypeExpr.List(NodeType), n => n.Bucket, (n, bucket) => n.Bucket = bucket),
        ]).Register(models);
        return models;
    }

    public void Dispose() => SharedReadModel.DeleteFixture(_path, Prefix);
}

[DurableNode(GraphMember.SchemaId)]
public sealed class GraphMember {
    internal const string SchemaId = "Test.DeclaredGraphMember";
    public int Number;
    public GraphMember?[] Links = [];
    public List<GraphMember>? Bucket;
}

[DurableNode("Test.DeclaredArraySlots")]
public sealed class GraphArraySlots {
    public object[]? Single;
    public object[]?[] Many = [];
}

[DurableNode("Test.DeclaredAccessorSlots")]
public sealed class GraphAccessorSlots {
    public object? Link;
    public object?[] Links = [];
    public IReadOnlyList<GraphMember>? Members;
}
