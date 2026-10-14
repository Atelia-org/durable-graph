using System.Buffers.Binary;
using System.Reflection;
using Atelia.Data;
using Atelia.DurableGraph.Storage;
using Atelia.DurableGraph.Persistence.MetaBootstrapP0;
using Atelia.VersionStore;
using DataStore = Atelia.FrameStore.FrameStore;
using PublicationStore = Atelia.VersionStore.VersionStore;

namespace Atelia.DurableGraph.Persistence.Tests;

internal static class StorageTestData {
    internal static long DataBytes(DataStore data) {
        long length = 0;
        data.Inventory(frame => length += frame.PayloadLength + frame.TailMetaLength).Unwrap();
        return length;
    }

    internal static FrameAddress TestAddress(uint file = 1, long offset = 128, int length = 32) {
        Span<byte> bytes = stackalloc byte[FrameAddress.EncodedSize];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, file);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[4..], SizedPtr.Create(offset, length).Packed);
        if (!FrameAddress.TryRead(bytes, out var address)) { throw new InvalidOperationException("Invalid test address."); }
        return address;
    }

    internal static FrameAddress DifferentTicket(FrameAddress address) {
        Span<byte> bytes = stackalloc byte[FrameAddress.EncodedSize];
        address.TryWrite(bytes);
        SizedPtr ticket = SizedPtr.FromPacked(BinaryPrimitives.ReadUInt64LittleEndian(bytes[4..]));
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[4..], SizedPtr.Create(ticket.Offset, ticket.Length + 4).Packed);
        FrameAddress.TryRead(bytes, out var result);
        return result;
    }

    internal static void WriteAddress(BareValueWriter writer, FrameAddress address) {
        Span<byte> bytes = stackalloc byte[FrameAddress.EncodedSize];
        if (!address.TryWrite(bytes)) { throw new InvalidOperationException("Invalid fixture address."); }
        writer.WriteRawBytes(bytes);
    }

    internal static GraphResources ResourcesOf(Repository repository) =>
        (GraphResources)typeof(Repository).GetField("_resources", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(repository)!;

    internal static void InstallPublicationFault(PublicationStore versions, string stage, Action action, bool tag = false) {
        FieldInfo field = typeof(PublicationStore).GetField("_testHooks", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object hooks = Activator.CreateInstance(field.FieldType, nonPublic: true)!;
        if (stage == "BeforeDataConfirm") {
            Set("ConfirmDataDurable", (Action)(() => { action(); ResourcesData(versions).ConfirmDurable(); }));
        } else if (stage == "AfterAppend") {
            Set(tag ? "AfterTagRecordAppended" : "AfterRefRecordAppended", (Action<SizedPtr>)(_ => action()));
        } else if (stage == "BeforeNewRefRename") {
            Set("BeforeNewRefRename", action);
        } else if (stage == "AfterConfirmed") {
            Set("AfterPublicationConfirmed", action);
        } else { throw new ArgumentOutOfRangeException(nameof(stage)); }
        field.SetValue(versions, hooks);
        void Set(string name, Delegate callback) => field.FieldType.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(hooks, callback);
    }

    internal static void ClearPublicationFault(PublicationStore versions) =>
        typeof(PublicationStore).GetField("_testHooks", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(versions, null);

    private static DataStore ResourcesData(PublicationStore versions) =>
        (DataStore)typeof(PublicationStore).GetField("_data", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(versions)!;
    internal static void DeleteTestPath(string path) {
        if (Directory.Exists(path)) { Directory.Delete(path, recursive: true); }
        else { File.Delete(path); }
    }

    internal static byte[] ReadSelectedSchemaPayload(string root) {
        using GraphResources resources = GraphResources.OpenReadOnlyExisting(root);
        using FrameRead frame = resources.Data.ReadFrame(resources.SharedSchemaRoot!.Value).Unwrap();
        return frame.Payload.ToArray();
    }

    // A graph frame and its owned object batch have different addresses in the split format.
    internal static IReadOnlyList<ObjectVersionRecord> LocalRecords(GraphRecordStore store, FrameAddress graph) =>
        store.ReadGraph(graph).OwnedBatch is { } batch ? store.ReadBatch(batch).Records : [];

}
