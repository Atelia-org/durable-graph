using System.Reflection;
using Atelia.DurableGraph.Persistence;
using Atelia.FrameStore;
using Atelia.HeadStore;
using Frames = Atelia.FrameStore.FrameStore;

namespace Atelia.DurableGraph.Tests;

public sealed partial class DurableSchemaGeneratorTests {
    private sealed record EvidenceFrameImage(FrameAddress Address, uint Tag, int TailMetaLength, bool Tombstone, byte[] Bytes);
    private sealed record EvidenceRefImage(HeadEntryId Revision, byte[] Bytes);
    private sealed record EvidencePublicationImage(EvidenceRefImage Branches, EvidenceRefImage Shared, EvidenceFrameImage[] Frames, long Appended);
    private static RepositoryResources EvidenceResources(Repository repository) =>
        (RepositoryResources)typeof(Repository).GetField("_resources", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(repository)!;
    private static EvidenceRefImage EvidenceReadRef(Repository repository, uint id) {
        HeadEntry actual = EvidenceResources(repository).Heads.ReadHead(id).Unwrap();
        return new(actual.Id, actual.Payload.ToArray());
    }
    private static void EvidenceAssertSameRef(EvidenceRefImage expected, EvidenceRefImage actual) {
        Assert.Equal(expected.Revision, actual.Revision);
        Assert.Equal(expected.Bytes, actual.Bytes);
    }
    private static EvidencePublicationImage EvidencePublication(Repository repository) =>
        new(EvidenceReadRef(repository, 1), EvidenceReadRef(repository, 2), EvidenceReadFrames(EvidenceResources(repository).Data),
            ReferenceGraphStore(repository).AppendedFrames);
    private static void EvidenceAssertPublicationUnchanged(Repository repository, EvidencePublicationImage before) {
        EvidenceAssertSameRef(before.Branches, EvidenceReadRef(repository, 1));
        EvidenceAssertSameRef(before.Shared, EvidenceReadRef(repository, 2));
        EvidenceFrameImage[] after = EvidenceReadFrames(EvidenceResources(repository).Data);
        Assert.Equal(before.Frames.Select(frame => frame.Address), after.Select(frame => frame.Address));
        EvidenceAssertFrameImages(before.Frames, after);
        Assert.Equal(before.Appended, ReferenceGraphStore(repository).AppendedFrames);
        Assert.False(EvidenceResources(repository).HasPendingSave);
        Assert.False(repository.IsFaulted);
    }
    private static EvidenceFrameImage[] EvidenceReadFrames(Frames data) {
        List<FrameInfo> inventory = [];
        data.Inventory(inventory.Add).Unwrap();
        return inventory.Select(info => {
            using FrameRead read = data.ReadFrame(info.Address).Unwrap();
            return new EvidenceFrameImage(info.Address, read.Tag, read.TailMetaLength, read.IsTombstone, read.PayloadAndMeta.ToArray());
        }).ToArray();
    }
    private static void EvidenceAssertFramesPreserved(Frames data, EvidenceFrameImage[] expected) =>
        EvidenceAssertFrameImages(expected, EvidenceReadFrames(data));
    private static void EvidenceAssertFrameImages(EvidenceFrameImage[] expected, EvidenceFrameImage[] actual) {
        foreach (EvidenceFrameImage old in expected) {
            EvidenceFrameImage current = Assert.Single(actual, frame => frame.Address == old.Address);
            Assert.Equal(old.Tag, current.Tag);
            Assert.Equal(old.TailMetaLength, current.TailMetaLength);
            Assert.Equal(old.Tombstone, current.Tombstone);
            Assert.Equal(old.Bytes, current.Bytes);
        }
    }
    private static Dictionary<string, byte[]> EvidenceFiles(string path) => Directory.GetFiles(path, "*", SearchOption.AllDirectories)
        .ToDictionary(file => file, File.ReadAllBytes);
    private static void EvidenceAssertFilesUnchanged(Dictionary<string, byte[]> expected, Dictionary<string, byte[]> actual) {
        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
        foreach ((string file, byte[] bytes) in expected) { Assert.Equal(bytes, actual[file]); }
    }
}
