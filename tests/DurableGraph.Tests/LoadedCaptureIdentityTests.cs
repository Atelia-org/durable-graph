using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;

namespace Atelia.DurableGraph.Tests;

public sealed class LoadedCaptureIdentityTests {
    private sealed class World : IDurableObject {
        internal string? Text;
    }

    private readonly record struct State(ObjectId Text);
    private static readonly DurableSchema Schema = new("loaded.identity", 1, new DurableFieldInfo(1, TypeTag.String));
    private static readonly StatePreparationBinding<State> Preparation = new(Schema,
        static (in State state) => new PreparedBaseBody([(byte)state.Text.Value]),
        static (in State prior, in State current) => new PreparedDeltaBody(prior != current, [(byte)current.Text.Value]));

    [Fact]
    public void ImportedIdentityKeepsOriginalBaselineSlotsAndDoesNotFabricateCurrent() {
        World world = new() { Text = string.Empty };
        Dictionary<object, ObjectId> map = new(ReferenceEqualityComparer.Instance) { [world] = new(1), [string.Empty] = new(3) };
        CaptureSession session = new(10, map);
        map.Clear(); // The caller's map cannot alter the imported identity set.
        Dictionary<ObjectId, ObjectStateRecord> baseline = new() {
            [new ObjectId(1)] = new(new ObjectId(1), Schema, new State(new(9)), Preparation),
            [new ObjectId(3)] = new(new ObjectId(3), string.Empty),
            [new ObjectId(9)] = new(new ObjectId(9), string.Empty),
        };
        using CaptureContext context = session.BeginCapture();
        Assert.Equal(new ObjectId(1u), context.AddRoot(world, Schema,
            static (value, capture) => new State(capture.CaptureString(value.Text)), Preparation));
        CapturedGraph candidate = context.Seal();
        IReadOnlyList<PreparedCapturedObject> prepared = session.PrepareAgainst(candidate, baseline);
        Assert.Null(session.Current);
        Assert.Equal<uint>([1, 3], candidate.Objects.Select(row => row.Id).Select(id => id.Value));
        Assert.Equal(new ObjectId(9u), baseline[new(1)].GetState<State>().Text);
        Assert.True(prepared[0].DeltaBody!.HasChanges);
        Assert.Equal(new ObjectId(3u), prepared[0].Current.GetState<State>().Text);
        session.Discard(candidate);
        Assert.Null(session.Current);
    }

    [Fact]
    public void ExhaustedImportedCursorAllowsKnownIdentitiesAndReleasesFailedCapture() {
        World world = new();
        CaptureSession session = new((ulong)uint.MaxValue + 1,
            new Dictionary<object, ObjectId>(ReferenceEqualityComparer.Instance) { [world] = new(uint.MaxValue) });
        using (CaptureContext context = session.BeginCapture()) {
            Assert.Equal(new ObjectId(uint.MaxValue), context.AddRoot(world, Schema,
                static (value, capture) => new State(capture.CaptureString(value.Text)), Preparation));
            CapturedGraph candidate = context.Seal();
            session.Discard(candidate);
        }
        world.Text = "new";
        using (CaptureContext context = session.BeginCapture()) {
            context.AddRoot(world, Schema, static (value, capture) => new State(capture.CaptureString(value.Text)), Preparation);
            Assert.Throws<InvalidOperationException>(() => context.Seal());
        }
        world.Text = null;
        using CaptureContext retry = session.BeginCapture();
        retry.AddRoot(world, Schema, static (value, capture) => new State(capture.CaptureString(value.Text)), Preparation);
        Assert.Single(retry.Seal().Objects);
        Assert.Null(session.Current);
    }
}
