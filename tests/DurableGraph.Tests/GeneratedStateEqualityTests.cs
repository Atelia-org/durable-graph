using Atelia.Binary;
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using System.Reflection;
using Atelia.DurableGraph.Build;
using Atelia.DurableGraph.Serialization;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    [Fact]
    public void TraditionalNestedComparisonMatchesDeltaWithoutPreparingPayloads() {
        string source = InlineBodySource.Replace("public static bool RefHydration()", """
            public static bool Equal(byte[] a,byte[] b) {
                var readerA=new BareValueReader(a); var readerB=new BareValueReader(b);
                var left=Item.__DurableState.ReadBaseBodyV1(ref readerA);
                var right=Item.__DurableState.ReadBaseBodyV1(ref readerB);
                return Item.__DurableState.StateEquals(in left,in right);
            }
            public static long Allocations(byte[] bytes) {
                var reader=new BareValueReader(bytes);
                var state=Item.__DurableState.ReadBaseBodyV1(ref reader);
                for(int i=0;i<1000;i++) if(!Item.__DurableState.StateEquals(in state,in state)) throw new Exception();
                long start=GC.GetAllocatedBytesForCurrentThread();
                for(int i=0;i<1000;i++) if(!Item.__DurableState.StateEquals(in state,in state)) throw new Exception();
                return GC.GetAllocatedBytesForCurrentThread()-start;
            }
            public static bool RefHydration()
            """);
        GeneratorTestRun run = RunGenerator(source);
        AssertSchemaOnlyCompiles(run);
        Type host = EmitAndLoad(run.OutputCompilation).GetType("FusedDelta.Host")!;
        var equal = host.GetMethod("Equal")!.CreateDelegate<Func<byte[], byte[], bool>>();
        var prepare = host.GetMethod("Prepare1")!.CreateDelegate<Func<byte[], byte[], PreparedDeltaBody>>();
        byte[][] states = new[] { "00000000000000", "00000000008000", "00000100C07F00", "00000200C07F00", "01020000008001" }
            .Select(Convert.FromHexString).ToArray();
        foreach (byte[] left in states) foreach (byte[] right in states) {
            Assert.Equal(left.SequenceEqual(right), equal(left, right));
            Assert.Equal(!prepare(left, right).HasChanges, equal(left, right));
        }
        Assert.Equal(0, host.GetMethod("Allocations")!.CreateDelegate<Func<byte[], long>>()(states[0]));
        AssertPrepareDeltaDoesNotPrecompare(GeneratedSource(run, "DurableStates.g.cs"));
    }

    [Fact]
    public void HistoricalGenericComparisonIgnoresPaddingAndWorksWithoutPriorDomainClrType() {
        using AncestryHistoryDirectory history = new();
        GeneratorTestRun first = RunGenerator("""
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            [DurableType("Pair",1)] public partial struct Pair<T> {
                [DurableField(1)] public byte Prefix;
                [DurableField(2)] public T Value;
                [DurableField(3)] public string Name;
            }
            [DurableType("Empty",1)] public partial struct Empty { }
            [DurableType("Box",1)] public partial class Box<T>:IDurableObject { [DurableField(1)] public T Value; }
            """);
        AssertSchemaOnlyCompiles(first);
        new SchemaHistoryTool().Publish(history.WriteManifest(first), history.History);
        GeneratorTestRun current = RunGenerator("""
            using System;
            using System.Runtime.CompilerServices;
            using System.Runtime.InteropServices;
            using Atelia.DurableGraph;
            using Atelia.DurableGraph.Schema;
            using Atelia.DurableGraph.Runtime;
            using Atelia.DurableGraph.Serialization;
            using PairState=Atelia.DurableGraph.Generated.Family_50616972;
            using EmptyState=Atelia.DurableGraph.Generated.Family_456D707479;
            using BoxState=Atelia.DurableGraph.Generated.Family_426F78;
            [DurableType("Box",1)] public partial class Box<T>:IDurableObject { [DurableField(1)] public T Value; }
            public static class EqualityHost {
                public static bool Probe() {
                    var schema=new DurableSchema(TypeExpr.Named("Pair",TypeExpr.Builtin(TypeTag.Single)),1,SchemaKind.InlineValue,
                        new DurableFieldInfo(1,TypeTag.Byte),new DurableFieldInfo(2,TypeTag.Single),new DurableFieldInfo(3,TypeTag.String));
                    var slot=new DurableFieldInfo(1,TypeTag.InlineValue,inlineSchema:schema);
                    PairState.V1<float> left=default,right=default;
                    MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref left,1)).Fill(0x11);
                    MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref right,1)).Fill(0x22);
                    Unsafe.AsRef(in left.Segment0Field1)=Unsafe.AsRef(in right.Segment0Field1)=7;
                    Unsafe.AsRef(in left.Segment0Field2)=Unsafe.AsRef(in right.Segment0Field2)=BitConverter.UInt32BitsToSingle(0x7FC00001);
                    Unsafe.AsRef(in left.Segment0Field3)=Unsafe.AsRef(in right.Segment0Field3)=new ObjectId(9);
                    if(MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref left,1)).SequenceEqual(MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref right,1)))) return false;
                    if(!PairState.BodyV1<float,SingleStateOps>.StateEquals(in left,in right,slot) ||
                        PairState.BodyV1<float,SingleStateOps>.PrepareDelta(in left,in right,slot).HasChanges) return false;
                    var encoded=PairState.BodyV1<float,SingleStateOps>.PrepareBase(in left,schema);
                    var reader=new BareValueReader(encoded.Body);
                    var restored=PairState.BodyV1<float,SingleStateOps>.ReadBase(ref reader,slot);
                    if(!PairState.BodyV1<float,SingleStateOps>.StateEquals(in left,in restored,slot)) return false;
                    Unsafe.AsRef(in right.Segment0Field2)=BitConverter.UInt32BitsToSingle(0x7FC00002);
                    if(PairState.BodyV1<float,SingleStateOps>.StateEquals(in left,in right,slot) ||
                        !PairState.BodyV1<float,SingleStateOps>.PrepareDelta(in left,in right,slot).HasChanges) return false;
                    var ownerSchema=new DurableSchema(TypeExpr.Named("Box",schema.Type),1,new DurableFieldInfo(1,TypeTag.InlineValue,inlineSchema:schema));
                    var ownerLeft=new BoxState.V1<PairState.V1<float>>(left);
                    var ownerRight=new BoxState.V1<PairState.V1<float>>(right);
                    if(BoxState.BodyV1<PairState.V1<float>,PairState.BodyV1<float,SingleStateOps>>.StateEquals(in ownerLeft,in ownerRight,ownerSchema)) return false;
                    var emptySchema=new DurableSchema("Empty",1,SchemaKind.InlineValue);
                    var emptySlot=new DurableFieldInfo(1,TypeTag.InlineValue,inlineSchema:emptySchema);
                    EmptyState.V1 empty=default;
                    if(!EmptyState.BodyV1.StateEquals(in empty,in empty,emptySlot) || EmptyState.BodyV1.PrepareDelta(in empty,in empty,emptySlot).HasChanges) return false;
                    for(int i=0;i<1000;i++) if(!PairState.BodyV1<float,SingleStateOps>.StateEquals(in left,in restored,slot)) return false;
                    long start=GC.GetAllocatedBytesForCurrentThread();
                    for(int i=0;i<1000;i++) if(!PairState.BodyV1<float,SingleStateOps>.StateEquals(in left,in restored,slot)) return false;
                    return GC.GetAllocatedBytesForCurrentThread()==start;
                }
            }
            """, history.ReadAdditionalTexts());
        AssertSchemaOnlyCompiles(current);
        Assembly assembly = EmitAndLoad(current.OutputCompilation);
        Assert.Null(assembly.GetType("Pair`1"));
        Assert.Null(assembly.GetType("Empty"));
        Assert.True(assembly.GetType("EqualityHost")!.GetMethod("Probe")!.CreateDelegate<Func<bool>>()());
        AssertPrepareDeltaDoesNotPrecompare(GeneratedSource(current, "DurableGenericStates.g.cs"));
    }

    private static void AssertPrepareDeltaDoesNotPrecompare(string generated) {
        MethodDeclarationSyntax[] methods = CSharpSyntaxTree.ParseText(generated).GetRoot().DescendantNodes()
            .OfType<MethodDeclarationSyntax>().Where(method => method.Identifier.ValueText is "PrepareDeltaBody" or "PrepareDelta").ToArray();
        Assert.NotEmpty(methods);
        foreach (MethodDeclarationSyntax method in methods) Assert.DoesNotContain("StateEquals(", method.ToString());
        foreach (MethodDeclarationSyntax method in CSharpSyntaxTree.ParseText(generated).GetRoot().DescendantNodes()
            .OfType<MethodDeclarationSyntax>().Where(method => method.Identifier.ValueText == "StateEquals")) {
            Assert.DoesNotContain("PrepareDelta", method.ToString());
            Assert.DoesNotContain("new ", method.ToString());
            Assert.DoesNotContain("MemoryMarshal", method.ToString());
        }
    }
}
