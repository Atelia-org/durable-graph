using Atelia.FrameStore;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using System.Reflection;
using Atelia.DurableGraph.Build;
using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Persistence.MetaBootstrapP0;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    [Theory]
    [InlineData("byte")]
    [InlineData("long")]
    public void GeneratedEnumValueUpgradeUsesHistoricalSingleIntegerDtoAndExplicitOwner(string underlying) {
        using AncestryHistoryDirectory history = new();
        Assembly assembly = EnumUpgradeAssembly(history, underlying);
        Assert.Null(assembly.GetType("OldMode"));
        StateBindingContext snapshot = EnumHistoryRegistry(assembly).Snapshot();
        StateModelBinding model = snapshot.ResolveCurrentModel(assembly.GetType("World")!);
        ObjectStateRecord source = EnumUpgradeSource(assembly);
        // Stored-exact decoding preserves the old width and never calls business code.
        ObjectStateRecord decoded = snapshot.ResolveReader(source.Schema!).Read(source.Id, new StateModelBodySource([3]));
        Assert.Equal((byte)3, ValueStateField(StateModelField(decoded, "Segment0Field1")!, "Segment0Field1"));
        Assert.Empty(EnumUpgradeCalls(assembly));

        ObjectStateRecord result = model.Normalize(decoded);
        object number = ValueStateField(StateModelField(result, "Segment0Field1")!, "Segment0Field1");
        Assert.Equal(103L, Convert.ToInt64(number));
        Assert.Equal(underlying == "byte" ? typeof(byte) : typeof(long), number.GetType());
        Assert.Equal(new[] { "owner", "mode" }, EnumUpgradeCalls(assembly));
        Assert.Equal(2, result.Schema!.Fields[0].InlineSchema!.Version);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void EnumMissingOrWrongProviderCannotUseKeepExactOrHideBehindAbsentNullable(bool optional, bool wrongProvider) {
        using AncestryHistoryDirectory history = new();
        Assembly assembly = EnumUpgradeAssembly(history, "byte", wrongProvider ? "wrong" : "missing");
        StateBindingContext snapshot = EnumHistoryRegistry(assembly).Snapshot();
        StateModelBinding model = snapshot.ResolveCurrentModel(assembly.GetType(optional ? "Optional" : "World")!);
        ObjectStateRecord source = EnumUpgradeSource(assembly, optional);
        Assert.Throws<InvalidDataException>(() => model.Normalize(source));
        Assert.Empty(EnumUpgradeCalls(assembly));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EnumAbsentNullableBindsToolButDoesNotInvokeValueCallback(bool present) {
        using AncestryHistoryDirectory history = new();
        Assembly assembly = EnumUpgradeAssembly(history, "byte");
        StateModelBinding model = EnumHistoryRegistry(assembly).Snapshot().ResolveCurrentModel(assembly.GetType("Optional")!);
        object prior = assembly.GetType("Host")!.GetMethod("Optional")!.Invoke(null, [present])!;
        DurableSchema mode = EnumUpgradeMode(1);
        DurableSchema old = new("Optional", 1, DurableFieldInfo.Nullable(1, new(1, TypeTag.InlineValue, inlineSchema: mode)));
        ObjectStateRecord result = model.Normalize(new(new(2), old, prior));
        object state = StateModelField(result, "Segment0Field1")!;
        Assert.Equal(present, (bool)state.GetType().GetProperty("HasValue")!.GetValue(state)!);
        Assert.Equal(present ? new[] { "optional", "mode" } : new[] { "optional" }, EnumUpgradeCalls(assembly));
    }

    [Theory]
    [InlineData(false, "missing")]
    [InlineData(false, "wrong")]
    [InlineData(true, "missing")]
    [InlineData(true, "wrong")]
    public void EnumEmptyContainerStillRejectsMissingOrWrongValueCapability(bool array, string provider) {
        using AncestryHistoryDirectory history = new();
        Assembly assembly = EnumUpgradeAssembly(history, "byte", provider);
        StateModelRegistry registry = EnumHistoryRegistry(assembly);
        Type rules = assembly.GetType("Rules")!;
        registry.UseArrayElementUpgrades(rules);
        registry.UseListElementUpgrades(rules);
        StateBindingContext snapshot = registry.Snapshot();
        ObjectStateRecord source = EnumUpgradeCollection(assembly, array, 0);
        Type domain = assembly.GetType("CurrentMode")!;
        Type collection = array ? domain.MakeArrayType() : typeof(List<>).MakeGenericType(domain);
        Assert.True(snapshot.TryGetCurrentObjectBinding(collection, out ObjectBinding? binding));
        Assert.Throws<InvalidDataException>(() => binding!.Normalize(source));
        Assert.Empty(EnumUpgradeCalls(assembly));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EnumCachedEmptyContainerPlansStayPinnedToExactViewsAndRejectConflictingHistoricalDependency(bool array) {
        using AncestryHistoryDirectory history = new();
        Assembly assembly = EnumUpgradeAssembly(history, "byte");
        StateModelRegistry registry = EnumHistoryRegistry(assembly);
        Type rules = assembly.GetType("Rules")!;
        registry.UseArrayElementUpgrades(rules);
        registry.UseListElementUpgrades(rules);
        using RawBaseDirectory files = new();
        using RawBaseDirectory otherFiles = new();
        // Independent repositories can contain the same key with different complete definitions.
        // Standalone meta views publish no business refs and do not amend an accepted shared catalog.
        P0CatalogView original = StoreView(files.Path, EnumUpgradeMode(1));
        P0CatalogView conflicting = StoreView(otherFiles.Path,
            new("Mode", 1, SchemaKind.InlineValue, new DurableFieldInfo(1, TypeTag.SByte)));
        Assert.NotSame(original.Snapshot!.SourceOwner, conflicting.Snapshot!.SourceOwner);

        static P0CatalogView StoreView(string path, DurableSchema oldMode) {
            FrameAddress address;
            using (P0Repository repository = P0Repository.CreateNew(path)) {
                P0CatalogWorkspace workspace = new(repository.EnsureEditingAllowed);
                workspace.Set(oldMode);
                workspace.Set(EnumUpgradeMode(2));
                P0PreparedCatalog prepared = workspace.Prepare();
                address = repository.Store.Append(P0GraphKind.Meta, null, null, prepared.Write!).Address;
                Assert.Null(repository.SharedSchemaRoot);
                repository.Data.ConfirmDurable();
            }
            using P0Repository reopened = P0Repository.OpenReadOnlyExisting(path);
            Assert.Null(reopened.SharedSchemaRoot);
            return P0CatalogView.Load(reopened.Store, address);
        }
        Assert.Equal(EnumUpgradeMode(1), original.Schemas[new(TypeExpr.Named("Mode"), 1)]);
        Assert.Equal(TypeTag.SByte, Assert.Single(conflicting.Schemas[new(TypeExpr.Named("Mode"), 1)].Fields).TypeTag);
        StateModelRegistry.FrozenConfiguration configuration = registry.FreezeConfiguration();
        StateBindingContext snapshot = configuration.CreateSnapshot(schemaLookup: key => original.Schemas.GetValueOrDefault(key));
        Type domain = assembly.GetType("CurrentMode")!;
        Type collection = array ? domain.MakeArrayType() : typeof(List<>).MakeGenericType(domain);
        Assert.True(snapshot.TryGetCurrentObjectBinding(collection, out ObjectBinding? binding));
        ObjectStateRecord source = EnumUpgradeCollection(assembly, array, 0);
        ObjectStateRecord current = binding!.Normalize(source);
        Assert.Equal(2, (array ? current.Layout.Array!.ElementSlot : current.Layout.List!.ElementSlot).InlineSchema!.Version);
        Assert.Equal(current.Layout, binding.Normalize(source).Layout); // Warm empty-container plan in S0.
        Assert.Empty(EnumUpgradeCalls(assembly));

        // Separate immutable S does not rebind the old plan. Empty inputs still preflight exact history.
        StateBindingContext other = configuration.CreateSnapshot(schemaLookup: key => conflicting.Schemas.GetValueOrDefault(key));
        Assert.True(other.TryGetCurrentObjectBinding(collection, out ObjectBinding? wrong));
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => wrong!.Normalize(source));
        Assert.IsType<SchemaConflictException>(error.InnerException);
        Assert.Empty(EnumUpgradeCalls(assembly));
        Assert.Equal(current.Layout, binding.Normalize(source).Layout); // The failed S1 environment did not mutate S0.
        Assert.Empty(EnumUpgradeCalls(assembly));
    }

    private static Assembly EnumUpgradeAssembly(AncestryHistoryDirectory history, string underlying, string provider = "valid") {
        GeneratorTestRun first = RunGenerator("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            [DurableType("Mode",1)] public enum OldMode:byte { Named=1 }
            [DurableType("World",1)] public partial class World:IDurableObject { [DurableField(1)] public OldMode Value; }
            [DurableType("Optional",1)] public partial class Optional:IDurableObject { [DurableField(1)] public OldMode? Value; }
            """);
        AssertSchemaOnlyCompiles(first);
        new SchemaHistoryTool().Publish(history.WriteManifest(first), history.History);
        string valueProvider = provider == "missing" ? "" : $$"""
                [DurableValueUpgrade(typeof(Rules),"Mode",1,2)]
                public static void Mode(in M.{{(provider == "wrong" ? "V2" : "V1")}} prior,out M.V2 next,UpgradeContext context) {
                    Calls.Add("mode"); next=new(({{underlying}})(prior.Segment0Field1+100));
                }
            """;
        GeneratorTestRun run = RunGenerator($$"""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            using W=Atelia.DurableGraph.Generated.Family_576F726C64;
            using O=Atelia.DurableGraph.Generated.Family_4F7074696F6E616C;
            using M=Atelia.DurableGraph.Generated.Family_4D6F6465;
            [DurableType("Mode",2)] public enum CurrentMode:{{underlying}} { Renamed=101 }
            [DurableType("World",2)] public partial class World:IDurableObject { [DurableField(1)] public CurrentMode Value; }
            [DurableType("Optional",2)] public partial class Optional:IDurableObject { [DurableField(1)] public CurrentMode? Value; }
            [ValueUpgradeRuleSet(AllowKeepExact=true,AllowNullableLifting=true)] public sealed class Rules;
            public static class Upgrades {
                public static readonly System.Collections.Generic.List<string> Calls=new();
                [DurableUpgrade(typeof(World),1)]
                [UpgradeDependency("value",typeof(Rules),"World",1,"World",1)]
                public static void World(in W.V1 prior,out W.V2 next,UpgradeContext context) {
                    Calls.Add("owner"); next=new(context.GetValueUpgrade<M.V1,M.V2>("value")(in prior.Segment0Field1));
                }
                [DurableUpgrade(typeof(Optional),1)]
                [UpgradeDependency("value",typeof(Rules),"Optional",1,"Optional",1)]
                public static void Optional(in O.V1<NullableState<M.V1>> prior,out O.V2<NullableState<M.V2>> next,UpgradeContext context) {
                    Calls.Add("optional"); next=new(context.GetValueUpgrade<NullableState<M.V1>,NullableState<M.V2>>("value")(in prior.Segment0Field1));
                }
                {{valueProvider}}
            }
            public static class Host {
                public static object Prior()=>new W.V1(new M.V1(3));
                public static object Optional(bool present)=>new O.V1<NullableState<M.V1>>(present?new NullableState<M.V1>(new M.V1(3)):default);
                public static object Collection(bool array,int count) {
                    var values=new M.V1[count];
                    return array
                        ? new FrozenArrayState<M.V1>(new ArrayShape(count),values)
                        : new FrozenListState<M.V1>(values);
                }
                public static string[] Calls()=>Upgrades.Calls.ToArray();
            }
            """, history.ReadAdditionalTexts());
        AssertSchemaOnlyCompiles(run);
        return EmitAndLoad(run.OutputCompilation);
    }

    private static DurableSchema EnumUpgradeMode(int version) => new("Mode", version, SchemaKind.InlineValue, new DurableFieldInfo(1, TypeTag.Byte));
    private static ObjectStateRecord EnumUpgradeSource(Assembly assembly, bool optional = false) {
        DurableFieldInfo slot = new(1, TypeTag.InlineValue, inlineSchema: EnumUpgradeMode(1));
        DurableSchema schema = new(optional ? "Optional" : "World", 1, optional ? DurableFieldInfo.Nullable(1, slot) : slot);
        object prior = assembly.GetType("Host")!.GetMethod(optional ? "Optional" : "Prior")!.Invoke(null, optional ? [false] : null)!;
        return new(new(1), schema, prior);
    }
    private static ObjectStateRecord EnumUpgradeCollection(Assembly assembly, bool array, int count) {
        object state = assembly.GetType("Host")!.GetMethod("Collection")!.Invoke(null, [array, count])!;
        DurableFieldInfo slot = new(1, TypeTag.InlineValue, inlineSchema: EnumUpgradeMode(1));
        return array
            ? new(new(3), new ArrayLayout(TypeExprKind.VectorArray, slot), state)
            : new(new(3), new ListLayout(slot), state);
    }
    private static string[] EnumUpgradeCalls(Assembly assembly) =>
        assembly.GetType("Host")!.GetMethod("Calls")!.CreateDelegate<Func<string[]>>()();
}
