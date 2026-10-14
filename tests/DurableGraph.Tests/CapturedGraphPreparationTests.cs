using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using System.Collections;
using Atelia.DurableGraph.Serialization;

namespace Atelia.DurableGraph.Tests;

public sealed class CapturedGraphPreparationTests {
    private static readonly DurableSchema Schema = new("preparation.test", 1,
        new DurableFieldInfo(1, TypeTag.Int32), new DurableFieldInfo(2, TypeTag.String));
    private static readonly StatePreparationBinding<State> Binding = new(Schema, Base, Delta);

    private sealed class Domain : IDurableObject {
        public int Value;
        public string? Text;
    }

    private readonly record struct State(int Value, ObjectId Text);
    private static State Capture(Domain value, CaptureContext context) => new(value.Value, context.CaptureString(value.Text));
    private static PreparedBaseBody Base(in State state) => new([(byte)state.Value, (byte)state.Text.Value]);
    private static PreparedDeltaBody Delta(in State prior, in State current) => prior == current
        ? new(false, [0]) : new(true, [1, (byte)current.Value, (byte)current.Text.Value]);

    private static CapturedGraph CaptureGraph(CaptureSession session, params Domain?[] roots) {
        CaptureContext context = session.BeginCapture();
        foreach (Domain? root in roots) {
            context.AddRoot(root, Schema, Capture, Binding);
        }
        return context.Seal();
    }

    [Fact]
    public void CompleteRowsPreserveSourcesIdentityClassificationAndFrozenContent() {
        string shared = new(['x']);
        Domain first = new() { Value = 7, Text = shared };
        Domain removed = new() { Value = 9, Text = string.Empty };
        CaptureSession session = new();
        CapturedGraph previous = CaptureGraph(session, first, null, removed, first);
        PreparedCapturedGraph initial = session.Prepare(previous);
        Assert.Null(initial.Previous);
        Assert.Same(previous, initial.Candidate);
        Assert.All(initial.Objects, row => {
            Assert.Null(row.Previous);
            Assert.Null(row.DeltaBody);
        });
        session.Accept(previous);

        first.Value = 8;
        Domain added = new() { Value = 11, Text = shared };
        CapturedGraph candidate = CaptureGraph(session, first, added);
        first.Value = 99;
        first.Text = "changed after seal";
        PreparedCapturedGraph prepared = session.Prepare(candidate);
        PreparedCapturedGraph repeated = session.Prepare(candidate);
        Assert.Same(previous, session.Current);
        Assert.Same(previous, prepared.Previous);
        Assert.Equal(candidate.Objects.Select(item => item.Id), prepared.Objects.Select(row => row.Current.Id));
        Assert.Equal(3, prepared.Objects.Count);
        PreparedCapturedObject changed = prepared.Objects[0];
        Assert.Same(candidate.Objects[0], changed.Current);
        Assert.Same(previous.Objects[0], changed.Previous);
        Assert.True(changed.DeltaBody!.HasChanges);
        Assert.Equal(new byte[] { 8, 3 }, changed.BaseBody.Body.ToArray());
        PreparedCapturedObject text = prepared.Objects[1];
        Assert.Same(shared, text.Current.StringContent);
        Assert.NotNull(text.Previous);
        Assert.Null(text.DeltaBody);
        Assert.Null(prepared.Objects[2].Previous);
        for (int index = 0; index < prepared.Objects.Count; index++) {
            Assert.Equal(prepared.Objects[index].BaseBody.Body.ToArray(), repeated.Objects[index].BaseBody.Body.ToArray());
        }
        session.Discard(candidate);
        Assert.Equal(new byte[] { 8, 3 }, changed.BaseBody.Body.ToArray());
        Assert.False(prepared.Objects is ICollection);
        Assert.False(prepared.Objects is IList<PreparedCapturedObject>);
        Assert.Empty(typeof(PreparedCapturedGraph).GetConstructors());
        Assert.Empty(typeof(PreparedCapturedObject).GetConstructors());
        Assert.All(typeof(PreparedCapturedObject).GetProperties(), property => Assert.Null(property.SetMethod));
    }

    [Fact]
    public void UnchangedDurableKeepsNonemptyZeroBitmapAndEveryBase() {
        Domain domain = new() { Value = 4, Text = string.Empty };
        CaptureSession session = new();
        session.Accept(CaptureGraph(session, domain));
        CapturedGraph candidate = CaptureGraph(session, domain);
        PreparedCapturedGraph prepared = session.Prepare(candidate);
        Assert.False(prepared.Objects[0].DeltaBody!.HasChanges);
        Assert.Equal(new byte[] { 0 }, prepared.Objects[0].DeltaBody!.Body.ToArray());
        Assert.Equal(new byte[] { 4, 2 }, prepared.Objects[0].BaseBody.Body.ToArray());
        Assert.Same(string.Empty, prepared.Objects[1].Current.StringContent);
        Assert.Equal(StringBodyPreparer.PrepareBase(string.Empty).Body.ToArray(), prepared.Objects[1].BaseBody.Body.ToArray());
        session.Accept(candidate);
    }

    [Fact]
    public void EqualDistinctStringsAreNewWhileSharedSurvivorsRemain() {
        Domain first = new() { Text = new string(['x']) };
        Domain second = new() { Text = first.Text };
        CaptureSession session = new();
        session.Accept(CaptureGraph(session, first, second));
        first.Text = new string(['x']);
        PreparedCapturedGraph prepared = session.Prepare(CaptureGraph(session, first, second));
        Assert.True(prepared.Objects[0].DeltaBody!.HasChanges);
        Assert.False(prepared.Objects[1].DeltaBody!.HasChanges);
        PreparedCapturedObject[] strings = prepared.Objects.Where(row => row.Current.Kind == ObjectStateKind.String).ToArray();
        Assert.Equal(2, strings.Length);
        Assert.NotNull(strings[0].Previous);
        Assert.Null(strings[1].Previous);
        Assert.Equal(strings[0].BaseBody.Body.ToArray(), strings[1].BaseBody.Body.ToArray());
    }

    [Fact]
    public void EmptyAndNullRootsPrepareWithoutRowsIncludingAfterRemovingAllObjects() {
        CaptureSession session = new();
        PreparedCapturedGraph initial = session.Prepare(CaptureGraph(session, null, null));
        Assert.Empty(initial.Objects);
        Assert.Equal<uint>([0, 0], initial.Candidate.RootIds.Select(id => id.Value));
        session.Accept(initial.Candidate);
        session.Accept(CaptureGraph(session, new Domain()));
        PreparedCapturedGraph empty = session.Prepare(CaptureGraph(session));
        Assert.Empty(empty.Objects);
        Assert.Single(empty.Previous!.Objects);
    }

    [Fact]
    public void MissingBindingFailsBeforeAnyEncodingButCaptureOnlyStillAccepts() {
        int calls = 0;
        var binding = new StatePreparationBinding<State>(Schema,
            (in State state) => { calls++; return Base(in state); }, Delta);
        CaptureSession session = new();
        CaptureContext context = session.BeginCapture();
        context.AddRoot(new Domain(), Schema, Capture, binding);
        context.AddRoot(new Domain(), Schema, Capture);
        CapturedGraph candidate = context.Seal();
        Assert.Throws<InvalidOperationException>(() => session.Prepare(candidate));
        Assert.Equal(0, calls);
        Assert.Null(session.Current);
        session.Accept(candidate);
        Assert.Same(candidate, session.Current);
    }

    [Fact]
    public void RemovedCaptureOnlyPriorDoesNotNeedPreparation() {
        CaptureSession session = new();
        CaptureContext context = session.BeginCapture();
        context.AddRoot(new Domain(), Schema, Capture);
        session.Accept(context.Seal());
        Assert.Empty(session.Prepare(CaptureGraph(session)).Objects);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepeatedRootRejectsDifferentOrMissingBinding(bool missing) {
        CaptureSession session = new();
        CaptureContext context = session.BeginCapture();
        Domain domain = new();
        context.AddRoot(domain, Schema, Capture, Binding);
        Assert.Throws<ArgumentException>(() => {
            if (missing) {
                context.AddRoot(domain, Schema, Capture);
            }
            else {
                context.AddRoot(domain, Schema, Capture, new StatePreparationBinding<State>(Schema, Base, Delta));
            }
        });
        using CaptureContext next = session.BeginCapture();
    }

    [Fact]
    public void ConstructorAndRegistrationRejectMissingOperationsAndWrongDescriptor() {
        Assert.Throws<ArgumentNullException>(() => new StatePreparationBinding<State>(null!, Base, Delta));
        Assert.Throws<ArgumentNullException>(() => new StatePreparationBinding<State>(Schema, null!, Delta));
        Assert.Throws<ArgumentNullException>(() => new StatePreparationBinding<State>(Schema, Base, null!));
        DurableSchema different = new(Schema.SchemaId, Schema.Version, new DurableFieldInfo(1, TypeTag.UInt32));
        CaptureSession session = new();
        CaptureContext context = session.BeginCapture();
        Assert.Throws<ArgumentException>(() => context.AddRoot(new Domain(), different, Capture, Binding));
        using CaptureContext next = session.BeginCapture();
    }

    [Fact]
    public void NullPreparationAbortsRegistrationButKeepsAllocatedIdsConsumed() {
        CaptureSession session = new();
        CaptureContext context = session.BeginCapture();
        Assert.Equal(new ObjectId(1u), context.AddRoot(new Domain(), Schema, Capture, Binding));
        Assert.Throws<ArgumentNullException>(() => context.AddRoot(new Domain(), Schema, Capture, null!));
        Assert.Throws<InvalidOperationException>(() => context.Seal());
        using CaptureContext next = session.BeginCapture();
        Assert.Equal(new ObjectId(2u), next.AddRoot(new Domain(), Schema, Capture, Binding));
    }

    [Fact]
    public void ExistingObjectRejectsChangedBindingEvenWithIdenticalOperationsAndSchema() {
        CaptureSession session = new();
        Domain domain = new();
        CapturedGraph previous = CaptureGraph(session, domain);
        session.Accept(previous);
        using CaptureContext context = session.BeginCapture();
        context.AddRoot(domain, Schema, Capture, new StatePreparationBinding<State>(Schema, Base, Delta));
        CapturedGraph candidate = context.Seal();
        Assert.Throws<InvalidOperationException>(() => session.Prepare(candidate));
        Assert.Same(previous, session.Current);
    }

    [Fact]
    public void DeltaFailureAfterBaseSuccessPreservesBaselineAndCanRetry() {
        bool fail = true;
        int baseCalls = 0;
        var binding = new StatePreparationBinding<State>(Schema,
            (in State state) => { baseCalls++; return Base(in state); },
            (in State prior, in State current) => fail ? throw new ApplicationException("delta") : Delta(in prior, in current));
        CaptureSession session = new();
        Domain domain = new() { Value = 1 };
        CaptureContext first = session.BeginCapture();
        first.AddRoot(domain, Schema, Capture, binding);
        session.Accept(first.Seal());
        CapturedGraph previous = session.Current!;
        CaptureContext context = session.BeginCapture();
        domain.Value = 2;
        context.AddRoot(domain, Schema, Capture, binding);
        CapturedGraph candidate = context.Seal();
        Assert.Throws<ApplicationException>(() => session.Prepare(candidate));
        Assert.Equal(1, baseCalls);
        Assert.Same(previous, session.Current);
        fail = false;
        Assert.True(Assert.Single(session.Prepare(candidate).Objects).DeltaBody!.HasChanges);
        Assert.Equal(2, baseCalls);
        session.Discard(candidate);
    }

    [Theory]
    [InlineData(0)] // Missing prior binding.
    [InlineData(1)] // Same descriptor, different binding instance.
    [InlineData(2)] // Same key, different fields.
    [InlineData(3)] // Different version.
    [InlineData(4)] // Wrong prior DTO.
    [InlineData(5)] // Different content kind.
    [InlineData(6)] // Same derived fields, different exact ancestor.
    public void FullPreflightRejectsPriorMismatchBeforeFirstBody(int mismatch) {
        int calls = 0;
        var binding = new StatePreparationBinding<State>(Schema,
            (in State state) => { calls++; return Base(in state); }, Delta);
        DurableSchema priorSchema = mismatch switch {
            2 => new(Schema.SchemaId, Schema.Version, new DurableFieldInfo(1, TypeTag.UInt32)),
            3 => new(Schema.SchemaId, 2, Schema.Fields.ToArray()),
            6 => new(Schema.SchemaId, 1, Schema.Fields.ToArray(), new DurableSchema("ancestor", 1)),
            _ => Schema,
        };
        IStatePreparationBinding? priorBinding = mismatch switch {
            0 => null,
            1 => Binding,
            _ => binding,
        };
        ObjectStateRecord prior = mismatch == 5 ? new ObjectStateRecord(new ObjectId(2), "x")
            : new ObjectStateRecord(new ObjectId(2), priorSchema, mismatch == 4 ? (object)7L : new State(1, default), priorBinding);
        CaptureSession session = new();
        // Deliberately forge only the private test seam to exercise states ordinary registration cannot create.
        typeof(CaptureSession).GetProperty(nameof(CaptureSession.Current))!.SetValue(session,
            new CapturedGraph([], [prior]));
        CaptureContext context = session.BeginCapture();
        CapturedGraph candidate = new([], [new ObjectStateRecord(new ObjectId(1), Schema, new State(1, default), binding),
            new ObjectStateRecord(new ObjectId(2), Schema, new State(1, default), binding)]);
        typeof(CaptureContext).GetProperty("Candidate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(context, candidate);
        Assert.Throws<InvalidOperationException>(() => session.Prepare(candidate));
        Assert.Equal(0, calls);
        session.Discard(candidate);
    }

    [Fact]
    public void CurrentDtoMismatchAlsoFailsBeforeAnyBody() {
        CaptureSession session = new();
        CaptureContext context = session.BeginCapture();
        CapturedGraph candidate = new([], [new ObjectStateRecord(new ObjectId(1), Schema, 7L, Binding)]);
        typeof(CaptureContext).GetProperty("Candidate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(context, candidate);
        Assert.Throws<InvalidOperationException>(() => session.Prepare(candidate));
        session.Discard(candidate);
    }

    [Fact]
    public void WrongResolvedAndNullCandidatesAreRejected() {
        CaptureSession session = new();
        CapturedGraph candidate = CaptureGraph(session, new Domain());
        Assert.Throws<ArgumentNullException>(() => session.Prepare(null!));
        Assert.Throws<InvalidOperationException>(() => new CaptureSession().Prepare(candidate));
        session.Prepare(candidate);
        session.Accept(candidate);
        Assert.Throws<InvalidOperationException>(() => session.Prepare(candidate));
        CapturedGraph discarded = CaptureGraph(session);
        session.Discard(discarded);
        Assert.Throws<InvalidOperationException>(() => session.Prepare(discarded));
    }

    [Fact]
    public void LateFailureKeepsCandidateAndCurrentAndRetryDoesNotBurnIds() {
        bool fail = true;
        var binding = new StatePreparationBinding<State>(Schema,
            (in State state) => state.Value == 2 && fail ? throw new ApplicationException("late") : Base(in state), Delta);
        CaptureSession session = new();
        CaptureContext context = session.BeginCapture();
        Domain first = new() { Value = 1 };
        Domain second = new() { Value = 2 };
        context.AddRoot(first, Schema, Capture, binding);
        context.AddRoot(second, Schema, Capture, binding);
        CapturedGraph candidate = context.Seal();
        Assert.Throws<ApplicationException>(() => session.Prepare(candidate));
        Assert.Null(session.Current);
        fail = false;
        Assert.Equal(2, session.Prepare(candidate).Objects.Count);
        Assert.Equal(2, session.Prepare(candidate).Objects.Count);
        session.Accept(candidate);
        using CaptureContext next = session.BeginCapture();
        Assert.Equal(new ObjectId(3u), next.AddRoot(new Domain(), Schema, Capture, binding));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReentrantResolutionIsRejectedBeforeMutationAndGuardAlwaysRecovers(bool swallow) {
        CaptureSession session = new();
        CaptureContext old = session.BeginCapture();
        session.Accept(old.Seal());
        CapturedGraph previous = session.Current!;
        CaptureContext context = session.BeginCapture();
        CapturedGraph? candidate = null;
        bool reenter = true;
        var binding = new StatePreparationBinding<State>(Schema, (in State state) => {
            if (reenter) {
                old.Dispose(); // A resolved context is detached and remains harmless.
                Assert.Throws<InvalidOperationException>(() => session.Accept(candidate!));
                Assert.Throws<InvalidOperationException>(() => session.Discard(candidate!));
                Assert.Throws<InvalidOperationException>(() => session.Prepare(candidate!));
                Assert.Throws<InvalidOperationException>(() => session.BeginCapture());
                Assert.Same(previous, session.Current);
                if (swallow) {
                    Assert.Throws<InvalidOperationException>(() => context.Dispose());
                }
                else {
                    context.Dispose();
                }
            }
            return Base(in state);
        }, Delta);
        context.AddRoot(new Domain(), Schema, Capture, binding);
        candidate = context.Seal();
        if (swallow) {
            Assert.Single(session.Prepare(candidate).Objects);
        }
        else {
            Assert.Throws<InvalidOperationException>(() => session.Prepare(candidate));
        }
        Assert.Same(previous, session.Current);
        reenter = false;
        Assert.Single(session.Prepare(candidate).Objects);
        session.Accept(candidate);
        Assert.Same(candidate, session.Current);
        context.Dispose();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NullCallbackResultFailsWithoutResolvingCandidate(bool delta) {
        bool returnNull = false;
        var binding = new StatePreparationBinding<State>(Schema,
            (in State state) => returnNull && !delta ? null! : Base(in state),
            (in State prior, in State current) => returnNull && delta ? null! : Delta(in prior, in current));
        CaptureSession session = new();
        Domain domain = new();
        CaptureContext first = session.BeginCapture();
        first.AddRoot(domain, Schema, Capture, binding);
        session.Accept(first.Seal());
        CapturedGraph previous = session.Current!;
        CaptureContext context = session.BeginCapture();
        context.AddRoot(domain, Schema, Capture, binding);
        CapturedGraph candidate = context.Seal();
        returnNull = true;
        Assert.Throws<InvalidOperationException>(() => session.Prepare(candidate));
        Assert.Same(previous, session.Current);
        returnNull = false;
        Assert.Single(session.Prepare(candidate).Objects);
        context.Dispose();
        Assert.Same(previous, session.Current);
    }
}
