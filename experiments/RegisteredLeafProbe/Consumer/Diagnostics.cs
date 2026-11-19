using System.Reflection;
using System.Security.Cryptography;
using Atelia.Binary;
using Atelia.DurableGraph;
using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Serialization;
using Atelia.Text;
using Probe.Bitmap;

namespace Probe.Consumer;

internal static class Diagnostics {
    private const BindingFlags InternalStatic = BindingFlags.Static | BindingFlags.NonPublic;
    internal static object Run(string path) {
        var trajectories = new List<object>();
        var isolatedCodecs = new List<object>();
        foreach (int units in new[] {4096,65536}) {
            foreach (bool external in new[] {false,true}) {
                var counts = new Counters();
                FieldInfo builtinObserver = typeof(DurableText).Assembly.GetType("Atelia.DurableGraph.Runtime.TextBodyCodec")!
                    .GetField("BaseEncodingObserver",InternalStatic)!;
                if (!external) builtinObserver.SetValue(null,(Action)(()=>counts.Base++));
                try {
                    trajectories.Add(Measure.RunOne(Path.Combine(path,$"read-{units}-{external}"), external,units,16,8,counts));
                } finally { if (!external) builtinObserver.SetValue(null,null); }
                var domain = external ? typeof(TextEditor) : typeof(DurableText);
                var capture = domain.GetMethod("CaptureState",BindingFlags.Instance|BindingFlags.NonPublic)!;
                var codec = domain.Assembly.GetType(external ? "Atelia.Text.TextBodyCodec" : "Atelia.DurableGraph.Runtime.TextBodyCodec")!;
                isolatedCodecs.Add(typeof(Diagnostics).GetMethod(nameof(Codec),InternalStatic)!.MakeGenericMethod(domain,capture.ReturnType)
                    .Invoke(null,[units,external,capture,codec])!);
            }
        }
        return new { trajectories, isolatedCodecs };
    }

    private delegate TState ReadDelta<TState>(ref BareValueReader reader,TState previous);

    // Reflection binds typed delegates once. No reflection invocation or graph metadata falls inside the codec measurements.
    private static object Codec<TDomain,TFrozen>(int units,bool external,MethodInfo capture,Type codec)
        where TDomain:class where TFrozen: class {
        Func<TDomain,TFrozen> freeze;
        if(external) {
            var withAcceptance = capture.CreateDelegate<Func<TDomain,LeafAcceptance?,TFrozen>>();
            freeze = value => withAcceptance(value,null);
        } else { freeze = capture.CreateDelegate<Func<TDomain,TFrozen>>(); }
        var restore=typeof(TDomain).GetMethod("Restore",InternalStatic)!.CreateDelegate<Func<TFrozen,TDomain>>();
        var prepareBase=codec.GetMethod("PrepareBase",InternalStatic)!.CreateDelegate<Func<TFrozen,PreparedBaseBody>>();
        var prepareDelta=codec.GetMethod("PrepareDelta",InternalStatic)!.CreateDelegate<Func<TFrozen,TFrozen,PreparedDeltaBody>>();
        var replay=codec.GetMethod("ApplyDelta",InternalStatic)!.CreateDelegate<ReadDelta<TFrozen>>();
        var replace=typeof(TDomain).GetMethod("Replace")!.CreateDelegate<Action<TDomain,int,int,string>>();
        var content=new string('a',units);
        var source=(TDomain)Activator.CreateInstance(typeof(TDomain),[content])!;
        var prior=freeze(source);
        var editor=restore(prior);
        var expected=content.ToCharArray();
        for(int i=0;i<16;i++) {
            int at=(i+1)*units/17;
            replace(editor,at,1,"Z");
            expected[at]='Z';
        }
        var current=freeze(editor);
        byte[] delta=prepareDelta(prior,current).Body.ToArray();
        TFrozen ReadOne() {
            var reader=new BareValueReader(delta);
            var state=replay(ref reader,prior);
            reader.EnsureFullyConsumed();
            return state;
        }
        Program.Need(restore(ReadOne()).ToString()==new string(expected),"isolated codec replay mismatch");
        const int iterations=16;
        double BytesPer(Action action) {
            action();
            long before=GC.GetAllocatedBytesForCurrentThread();
            for(int i=0;i<iterations;i++) action();
            return (GC.GetAllocatedBytesForCurrentThread()-before)/(double)iterations;
        }
        return new { external, units, changedPieces = 33, deltaLength=delta.Length,
            deltaSha256=Convert.ToHexString(SHA256.HashData(delta)),
            baseBodyBytes=prepareBase(prior).Body.Length, changedBaseBodyBytes=prepareBase(current).Body.Length,
            baseAllocated=BytesPer(()=>GC.KeepAlive(prepareBase(current))),
            deltaAllocated=BytesPer(()=>GC.KeepAlive(prepareDelta(prior,current))),
            replayAllocated=BytesPer(()=>GC.KeepAlive(ReadOne())) };
    }

    internal sealed class Counters : IDurableLeafRegistration {
        internal int Base,Delta;
        private StateModelRegistry? _models;
        internal StateModelRegistry Models() {
            var models=Program.Models(text:false);
            _models=models;
            try { TextEditorPersistence.Register(this); }
            finally { _models=null; }
            return models;
        }
        public void RegisterLeaf<TDomain,TFrozen>(DurableLeafCodec<TDomain,TFrozen> codec)
            where TDomain : class where TFrozen : notnull =>
                _models!.RegisterLeaf(new CountingCodec<TDomain,TFrozen>(codec,this));
    }

    private sealed class CountingCodec<TDomain,TFrozen>(DurableLeafCodec<TDomain,TFrozen> codec,Counters c) :
        DurableLeafCodec<TDomain,TFrozen> where TDomain : class where TFrozen : notnull {
        public override TFrozen Capture(TDomain value,LeafAcceptance? acceptance) => codec.Capture(value,acceptance);
        public override TDomain Restore(TFrozen state) => codec.Restore(state);
        public override PreparedBaseBody PrepareBase(TFrozen state) {c.Base++; return codec.PrepareBase(state);}
        public override PreparedDeltaBody? PrepareDelta(TFrozen previous,TFrozen current) {c.Delta++;return codec.PrepareDelta(previous,current);}
        public override TFrozen ReadBase(int version,ref BareValueReader reader) => codec.ReadBase(version,ref reader);
        public override TFrozen ApplyDelta(int version,ref BareValueReader reader,TFrozen previous) => codec.ApplyDelta(version,ref reader,previous);
        public override TFrozen FinishReplay(TFrozen state) => codec.FinishReplay(state);
        public override long? EstimateBaseSize(TFrozen state) => codec.EstimateBaseSize(state);
        public override IReadOnlyList<int> HistoricalVersions => codec.HistoricalVersions;
    }
}
