using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Testing;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed partial class EventHistoryRepositoryTests {
    [Theory]
    [InlineData(typeof(BoxedMarker))]
    [InlineData(typeof(IDurableObject))]
    [InlineData(typeof(IAdditionalMarker))]
    [InlineData(typeof(UnmarkedClass))]
    public void ReferenceDefinitionRequiresAnActualMarkerClass(Type domainType) {
        StateSchemaTemplate template = new("Admission", 1, SchemaKind.ReferenceObject, 0, []);
        Assert.Throws<ArgumentException>(() => new StateDefinitionBinding(
            "Admission", SchemaKind.ReferenceObject, 0, domainType, [template]));
    }

    [Fact]
    public void ReferenceDefinitionAcceptsInheritedMarkerAndOpenGenericClass() {
        StateSchemaTemplate ordinary = new("Admission", 1, SchemaKind.ReferenceObject, 0, []);
        StateDefinitionBinding inherited = new("Admission", SchemaKind.ReferenceObject, 0,
            typeof(InheritedMarker), [ordinary]);
        Assert.Equal(typeof(InheritedMarker), inherited.DomainTypeDefinition);

        StateSchemaTemplate generic = new("GenericAdmission", 1, SchemaKind.ReferenceObject, 1, []);
        StateDefinitionBinding open = new("GenericAdmission", SchemaKind.ReferenceObject, 1,
            typeof(GenericMarker<>), [generic]);
        Assert.Equal(typeof(GenericMarker<>), open.DomainTypeDefinition);
    }

    [Fact]
    public void MarkerDoesNotMakeStructAReferenceObjectOrForbidInlineRegistration() {
        StateSchemaTemplate template = new("MarkerValue", 1, SchemaKind.InlineValue, 0, []);
        StateDefinitionBinding inline = new("MarkerValue", SchemaKind.InlineValue, 0,
            typeof(BoxedMarker), [template]);
        Assert.Equal(SchemaKind.InlineValue, inline.Kind);
        Assert.Throws<ArgumentException>(() => typeof(StateModelBinding<,>).MakeGenericType(typeof(BoxedMarker), typeof(byte)));
    }

    [Fact]
    public void InterfaceCannotBeRegisteredAsConcreteObjectModel() {
        CapturedStatePreparation<State> preparation = new(Schema,
            static (in State state) => Base(state),
            static (in State prior, in State next) => Delta(prior, next));
        // An interface satisfies the C# class constraint, but object binding still
        // requires a CLR class. No callback may turn a wildcard into an exact model.
        Assert.Throws<ArgumentException>(() => new StateModelBinding<IDurableObject, State>(
            preparation, [], static row => row.GetState<State>(), static () => new Node(),
            static (IDurableObject domain, in State state, ObjectReadTable objects) => { },
            static (domain, context) => default, Visit));
    }

    [Fact]
    public void ErasedEventBoundaryRejectsBoxedAndUnregisteredMarkersBeforePublication() {
        using Repository repository = CreateRepository();
        using BranchCheckout session = repository.CreateBranch("main", new Node(), TestSavePolicies.Baseline);
        CheckpointAddress initial = session.Head;
        IDurableObject boxed = new BoxedMarker();
        Assert.Throws<ArgumentException>(() => session.CommitEvent(boxed, TestSavePolicies.Baseline));
        Assert.Throws<ArgumentException>(() => session.CommitEvent(new UnregisteredMarker(), TestSavePolicies.Baseline));
        Assert.Same(initial, session.Head);
        Assert.Equal(GraphFrameKind.State, session.Head.Kind);
        Assert.False(session.IsFaulted);
        session.CommitEvent(new Node { Value = 7 }, TestSavePolicies.Baseline);
        Assert.Equal((byte)7, ((Node)repository.ReadEvent(session.Head)).Value);
    }

    private struct BoxedMarker : IDurableObject { }
    private interface IAdditionalMarker : IDurableObject { }
    private sealed class UnmarkedClass { }
    private class MarkerAncestor : IDurableObject { }
    private sealed class InheritedMarker : MarkerAncestor { }
    private sealed class GenericMarker<T> : IDurableObject { }
    private sealed class UnregisteredMarker : IDurableObject { }
}
