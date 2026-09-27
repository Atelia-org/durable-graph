using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;

namespace Atelia.DurableGraph.Tests;

public sealed class CurrentNormalizationTests {
    private static readonly DurableSchema PriorSchema = new("current.normalization", 1, new DurableFieldInfo(1, TypeTag.Int32));
    private static readonly DurableSchema CurrentSchema = new("current.normalization", 2, new DurableFieldInfo(1, TypeTag.Int32));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExactCurrentAttachesPreparationAndPreservesIdAndValueWithoutCallingHistoricalConversion(bool throws) {
        int conversions = 0;
        var model = Model(row => {
            conversions++;
            return throws ? throw new NotSupportedException("Only historical conversion may execute this callback.")
                : row.GetState<Current>() with { Value = row.GetState<Current>().Value + 1 };
        });
        ObjectStateRecord source = new(new(23), CurrentSchema, new Current(42));

        ObjectStateRecord current = model.Normalize(source);

        Assert.Equal(0, conversions);
        Assert.Equal(source.Id, current.Id);
        Assert.Equal(source.Layout, current.Layout);
        Assert.Equal(new Current(42), current.GetState<Current>());
        Assert.NotSame(source, current);
        Assert.Null(source.Preparation);
        Assert.NotNull(current.Preparation);
        ObjectStateRecord normalizedAgain = model.Normalize(current);
        Assert.Same(current.Preparation, normalizedAgain.Preparation);
        Assert.Equal(new Current(42), normalizedAgain.GetState<Current>());
        Assert.Equal(0, conversions);
    }

    [Fact]
    public void ExactCurrentRequiresCompleteLayoutAndExactDtoBeforeHistoricalCallbackCanRescueIt() {
        int conversions = 0;
        var model = Model(_ => { conversions++; return new(99); });
        DurableSchema wrongLayout = new(CurrentSchema.SchemaId, CurrentSchema.Version, new DurableFieldInfo(1, TypeTag.Int64));

        Assert.Throws<InvalidDataException>(() => model.Normalize(new(new(23), wrongLayout, new Current(42))));
        Assert.Throws<InvalidOperationException>(() => model.Normalize(new(new(23), CurrentSchema, new Prior(42))));
        Assert.Throws<InvalidDataException>(() => model.Normalize(new(new(23), "wrong object kind")));
        Assert.Equal(0, conversions);
    }

    [Fact]
    public void HistoricalConversionPreservesSourceAndIdentityAndPropagatesFailure() {
        int conversions = 0;
        NotSupportedException failure = new("Rejected historical value.");
        var model = Model(row => {
            conversions++;
            Prior prior = row.GetState<Prior>();
            return prior.Value < 0 ? throw failure : new(prior.Value + 1);
        });
        ObjectStateRecord source = new(new(23), PriorSchema, new Prior(42));

        ObjectStateRecord current = model.Normalize(source);

        Assert.Equal(1, conversions);
        Assert.Equal(source.Id, current.Id);
        Assert.Equal(model.CurrentLayout, current.Layout);
        Assert.Equal(new Current(43), current.GetState<Current>());
        Assert.Equal(new Prior(42), source.GetState<Prior>());
        Assert.Equal(PriorSchema, source.Schema);
        Assert.Null(source.Preparation);
        Assert.NotNull(current.Preparation);
        Assert.Same(failure, Assert.Throws<NotSupportedException>(() => model.Normalize(new(new(24), PriorSchema, new Prior(-1)))));
        Assert.Equal(2, conversions);
    }

    private static StateModelBinding<Node, Current> Model(Func<ObjectStateRecord, Current> normalize) => new(
        new CapturedStatePreparation<Current>(CurrentSchema,
            static (in Current _) => throw new NotSupportedException("Normalization does not prepare bodies."),
            static (in Current _, in Current _) => throw new NotSupportedException("Normalization does not prepare bodies.")),
        [new StateReaderBinding<Prior>(PriorSchema,
            static (ref BinaryPayloadReader _) => default,
            static (ref BinaryPayloadReader _, in Prior prior) => prior,
            static (in Prior _, IStateReferenceVisitor _) => { }),
         new StateReaderBinding<Current>(CurrentSchema,
            static (ref BinaryPayloadReader _) => default,
            static (ref BinaryPayloadReader _, in Current prior) => prior,
            static (in Current _, IStateReferenceVisitor _) => { })],
        normalize,
        static () => new(),
        static (Node _, in Current _, ObjectReadTable _) => { },
        static (Node _, CaptureContext _) => default,
        static (in Current _, IStateReferenceVisitor _) => { });

    private sealed class Node : IDurableObject { }
    private readonly record struct Prior(int Value);
    private readonly record struct Current(int Value);
}
