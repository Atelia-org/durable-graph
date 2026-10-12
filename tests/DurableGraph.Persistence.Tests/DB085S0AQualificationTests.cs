using System.Buffers.Binary;
using Atelia.Data;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Storage;
using Atelia.Rbf;
using Atelia.Rbf.Internal;
using Atelia.RbfSegmentStore;
using SegmentStore = Atelia.RbfSegmentStore.RbfSegmentStore;
using FrameAddress = Atelia.DurableGraph.Storage.FrameAddress;
using Node = Atelia.DurableGraph.Persistence.Tests.SharedReadModel.Node;

namespace Atelia.DurableGraph.Persistence.Tests;

/// <summary>Read-only qualification witnesses, including synthetic persistence models, not power-loss tests.</summary>
public sealed class DB085S0AQualificationTests : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), "durablegraph-db085-s0a-" + Guid.NewGuid().ToString("N"));
    private static readonly ReadAmplificationBaseBudgetParameters NoRebase = new(1000000, 1);
    private const uint ModelTag = 0xF200;

    public DB085S0AQualificationTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void CompleteSchemaRejectionRemainsRejectionBeforePhysicalShortTail(bool unknownVersion, bool shortTail) {
        string completePath = Path.Combine(_root, "complete-schema.rbf");
        SizedPtr invalidTicket;
        var row = SchemaCatalogEntry.ForArray(new(2), new(TypeExprKind.VectorArray, new(1, TypeTag.Int32)));
        using (var file = RbfFile.CreateNew(completePath)) {
            new SchemaStore(file).RegisterRepresentations([row.Layout!]);
            // A duplicate catalog ID is a complete semantic contradiction; 255 is an unknown format.
            invalidTicket = file.Append(SchemaCatalogWireCodec.RbfTag,
                unknownVersion ? new byte[] { 255 } : CatalogTestData.Encode([row])).Unwrap();
            file.DurableFlush();
        }
        byte[] completeBytes = File.ReadAllBytes(completePath);
        var complete = InspectUnchanged(completePath);
        Assert.Equal(RbfPrefixStatus.Complete, complete.Status);
        Assert.Equal(completeBytes.LongLength, complete.LastCompleteBoundary);
        using (var file = RbfFile.OpenReadOnlyExisting(completePath)) {
            using var frame = file.ReadPooledFrame(invalidTicket).Unwrap();
            Assert.Equal(SchemaCatalogWireCodec.RbfTag, frame.Tag); // Physical CRC validation succeeded.
            var error = Assert.ThrowsAny<InvalidDataException>(() => new SchemaStore(file, readOnly: true));
            Assert.Equal(invalidTicket.Offset, error.Data["DurableGraph.Offset"]);
            Assert.Equal(unknownVersion, error.Data.Contains("DurableGraph.UnsupportedFormat"));
        }
        Assert.Equal(completeBytes, File.ReadAllBytes(completePath));
        if (!shortTail) { return; }

        // A separate writer output supplies a genuine final-image prefix after the illegal complete fact.
        string writerPath = Path.Combine(_root, "following-writer.rbf");
        File.WriteAllBytes(writerPath, completeBytes);
        SizedPtr following;
        using (var file = RbfFile.OpenExisting(writerPath)) {
            following = file.Append(SchemaCatalogWireCodec.RbfTag, CatalogTestData.Encode([row])).Unwrap();
            file.DurableFlush();
        }
        byte[] writerBytes = File.ReadAllBytes(writerPath);
        string interruptedPath = Path.Combine(_root, "schema-with-short-tail.rbf");
        File.WriteAllBytes(interruptedPath, writerBytes[..checked((int)following.Offset + 2)]);
        var candidate = InspectUnchanged(interruptedPath);
        Assert.Equal(RbfPrefixStatus.IncompleteSuffix, candidate.Status);
        Assert.Equal(completeBytes.LongLength, candidate.LastCompleteBoundary);
        Assert.Equal(completeBytes, File.ReadAllBytes(interruptedPath)[..completeBytes.Length]);
        // The candidate boundary includes the illegal fact. The untouched, separately materialized
        // complete image above is rejected by the real upper reader; no fixed-EOF facade or repair is used.
        Assert.True(invalidTicket.Offset < candidate.LastCompleteBoundary);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompleteLastStateOrphanIsPhysicalCompleteButAuditRejectsItsFormatOrParent(bool unknownVersion) {
        string repositoryPath = Path.Combine(_root, "repository");
        using (var repository = Repository.CreateNew(repositoryPath, SharedReadModel.Models(),
            new() { NewStoreLayout = RbfSegmentStoreLayout.Flat })) {
            using var branch = repository.CreateBranch("main", new Node { Value = 1 }, NoRebase);
        }
        long invalidOffset;
        using (var segments = SegmentStore.OpenExisting(Path.Combine(repositoryPath, "state"))) {
            if (unknownVersion) {
                using var writer = segments.OpenActiveWriter();
                invalidOffset = writer.File.Append(StateRevisionStore.RbfTag, [255]).Unwrap().Offset;
                writer.File.DurableFlush();
            }
            else {
                using var states = new StateRevisionStore(segments);
                var missing = new FrameAddress(1, SizedPtr.Create(4, 24));
                invalidOffset = states.AppendDurably(StateRevision.CreateObjectHeadMapBase(missing, [], [])).FrameTicket.Offset;
            }
        }
        string stateFile = Directory.GetFiles(Path.Combine(repositoryPath, "state"), "*.rbf", SearchOption.AllDirectories).Single();
        var physical = InspectUnchanged(stateFile);
        Assert.Equal(RbfPrefixStatus.Complete, physical.Status);
        Assert.Equal(new FileInfo(stateFile).Length, physical.LastCompleteBoundary);
        var before = Directory.GetFiles(repositoryPath, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);
        var report = RepositoryMaintenance.Audit(repositoryPath);
        Assert.False(report.IsHealthy);
        Assert.Contains(report.Findings, f => f.RelativePath.StartsWith("state/") && f.Offset == invalidOffset &&
            f.Kind == (unknownVersion ? RepositoryAuditFindingKind.UnsupportedFormat : RepositoryAuditFindingKind.InvalidData));
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(repositoryPath, "*", SearchOption.AllDirectories).Order());
        foreach (var pair in before) { Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key)); }
    }

    [Fact]
    public void F2FullLengthAndDurableTailDoNotProvePayloadOrReliableFlush() {
        byte[] payload = Enumerable.Range(1, 64).Select(i => (byte)i).ToArray();
        var image = CreateModelImage("payload", payload);
        var persistence = new PersistenceModel(image.Bytes[..checked((int)image.Ticket.Offset)]);
        persistence.Write(image.Bytes);
        byte[] reordered = image.Bytes.ToArray();
        reordered.AsSpan(checked((int)image.Ticket.Offset + sizeof(uint)), payload.Length).Clear();
        // Model a dishonest/unreliable completed barrier: length/head/tail landed, payload did not.
        persistence.Confirm(reordered);
        string crashed = Materialize("unreliable-flush.rbf", persistence.Crash());
        Assert.Equal(image.Bytes.LongLength, new FileInfo(crashed).Length);
        Assert.Equal(image.Bytes.AsSpan(checked((int)image.Ticket.Offset + sizeof(uint) + payload.Length)).ToArray(),
            File.ReadAllBytes(crashed).AsSpan(checked((int)image.Ticket.Offset + sizeof(uint) + payload.Length)).ToArray());
        Assert.Equal(RbfPrefixStatus.Invalid, InspectUnchanged(crashed).Status);
        using (var file = RbfFile.OpenReadOnlyExisting(crashed)) {
            Assert.IsType<RbfCrcMismatchError>(file.ReadPooledFrame(image.Ticket).Error);
        }

        // Under the reliable-barrier model the same acknowledged writer image survives exactly.
        persistence.Write(image.Bytes);
        persistence.Confirm();
        string reliable = Materialize("reliable-flush.rbf", persistence.Crash());
        Assert.Equal(RbfPrefixStatus.Complete, InspectUnchanged(reliable).Status);
        Assert.Equal(payload, ReadPayload(reliable, image.Ticket));

        // A distinct F2 case needs no dishonest completed barrier: the next append has not been
        // confirmed, and its head/tail reach storage before its payload. The old confirmed fact survives.
        string pendingWriter = Materialize("pending-writer.rbf", image.Bytes);
        SizedPtr pendingTicket;
        using (var file = RbfFile.OpenExisting(pendingWriter)) {
            pendingTicket = file.Append(ModelTag, payload).Unwrap();
            file.DurableFlush(); // Produces the fixture image, not a claimed barrier in the model.
        }
        byte[] pendingImage = File.ReadAllBytes(pendingWriter);
        persistence.Write(pendingImage);
        pendingImage.AsSpan(checked((int)pendingTicket.Offset + sizeof(uint)), payload.Length).Clear();
        string reorderedPending = Materialize("pending-nonprefix.rbf", pendingImage);
        Assert.Equal(image.Bytes, pendingImage[..image.Bytes.Length]);
        var pendingPhysical = InspectUnchanged(reorderedPending);
        Assert.Equal(RbfPrefixStatus.Invalid, pendingPhysical.Status); // F1 safely stops; this is not F2 completion.
        Assert.Equal(image.Bytes.LongLength, pendingPhysical.LastCompleteBoundary);
        Assert.Equal(image.Bytes, persistence.Crash()); // Last reliable barrier still protects the old range.
        Assert.Equal(payload, ReadPayload(reorderedPending, image.Ticket));
        using var pendingFile = RbfFile.OpenReadOnlyExisting(reorderedPending);
        Assert.IsType<RbfCrcMismatchError>(pendingFile.ReadPooledFrame(pendingTicket).Error);
    }

    [Fact]
    public void F2LaterWriteDamagingConfirmedOldRangeCannotBeFixedByRollbackWithoutFactLoss() {
        byte[] acknowledgedPayload = Enumerable.Repeat((byte)0xA5, 64).ToArray();
        var image = CreateModelImage("confirmed-old", acknowledgedPayload);
        var persistence = new PersistenceModel(image.Bytes);
        string writerPath = Materialize("append-writer.rbf", image.Bytes);
        SizedPtr next;
        using (var file = RbfFile.OpenExisting(writerPath)) {
            next = file.Append(ModelTag, Enumerable.Repeat((byte)0x5A, 128).ToArray()).Unwrap();
            file.DurableFlush();
        }
        byte[] writerBytes = File.ReadAllBytes(writerPath);
        byte[] interrupted = writerBytes[..checked((int)next.Offset + 2)];
        persistence.Write(interrupted);
        string protectedPath = Materialize("protected-old.rbf", interrupted);
        var protectedPrefix = InspectUnchanged(protectedPath);
        Assert.Equal(RbfPrefixStatus.IncompleteSuffix, protectedPrefix.Status);
        Assert.Equal(image.Bytes.LongLength, protectedPrefix.LastCompleteBoundary);
        Assert.Equal(acknowledgedPayload, ReadPayload(image.Path, image.Ticket));
        Assert.Equal(image.Bytes, interrupted[..image.Bytes.Length]);
        Assert.Equal(image.Bytes, persistence.Crash()); // Reliable barriers protect the previously acknowledged range.

        // Synthetic shared-sector fault changes an already confirmed byte during the later append.
        interrupted[checked((int)image.Ticket.Offset + sizeof(uint))] ^= 1;
        string damagedPath = Materialize("old-range-damaged.rbf", interrupted);
        var damaged = InspectUnchanged(damagedPath);
        Assert.Equal(RbfPrefixStatus.Invalid, damaged.Status);
        Assert.Equal(image.Ticket.Offset, damaged.LastCompleteBoundary);
        // Strict Open rejects the nonaligned short EOF; use an independently materialized full image
        // for the checked-read CRC witness, with the same corruption in the old frame.
        byte[] damagedComplete = writerBytes.ToArray();
        damagedComplete[checked((int)image.Ticket.Offset + sizeof(uint))] ^= 1;
        string damagedCompletePath = Materialize("old-range-damaged-complete.rbf", damagedComplete);
        using (var file = RbfFile.OpenReadOnlyExisting(damagedCompletePath)) {
            Assert.IsType<RbfCrcMismatchError>(file.ReadPooledFrame(image.Ticket).Error);
        }
        // A hypothetical prefix rollback is materialized only as a new model file. It is physically
        // readable but cannot retain the acknowledged ticket, so it is not a successful recovery.
        string rollback = Materialize("hypothetical-loss.rbf", interrupted[..checked((int)damaged.LastCompleteBoundary)]);
        Assert.Equal(RbfPrefixStatus.Complete, InspectUnchanged(rollback).Status);
        using (var file = RbfFile.OpenReadOnlyExisting(rollback)) { Assert.False(file.ReadPooledFrame(image.Ticket).IsSuccess); }
        Assert.Equal(acknowledgedPayload, ReadPayload(image.Path, image.Ticket));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void F2ReadableUnconfirmedDependencyNeedsBarrierBeforePublishingAcrossSecondFault(bool confirmDependency) {
        byte[] sourcePayload = [7, 11, 19, 23];
        var source = CreateModelImage("source", sourcePayload);
        var sourcePersistence = new PersistenceModel(source.Bytes[..checked((int)source.Ticket.Offset)]);
        sourcePersistence.Write(source.Bytes);
        string firstRestart = Materialize("first-restart.rbf", sourcePersistence.Visible);
        Assert.Equal(RbfPrefixStatus.Complete, InspectUnchanged(firstRestart).Status);
        Assert.Equal(sourcePayload, ReadPayload(firstRestart, source.Ticket));
        if (confirmDependency) { sourcePersistence.Confirm(); }

        byte[] dependency = new byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(dependency, source.Ticket.Serialize());
        var publication = CreateModelImage("publication", dependency);
        var publicationPersistence = new PersistenceModel(publication.Bytes[..checked((int)publication.Ticket.Offset)]);
        publicationPersistence.Write(publication.Bytes);
        publicationPersistence.Confirm();
        string secondSource = Materialize("second-source.rbf", sourcePersistence.Crash());
        string secondPublication = Materialize("second-publication.rbf", publicationPersistence.Crash());
        Assert.Equal(dependency, ReadPayload(secondPublication, publication.Ticket));
        Assert.Equal(RbfPrefixStatus.Complete, InspectUnchanged(secondPublication).Status);
        Assert.Equal(RbfPrefixStatus.Complete, InspectUnchanged(secondSource).Status);
        using var sourceFile = RbfFile.OpenReadOnlyExisting(secondSource);
        var referencedTicket = SizedPtr.Deserialize(BinaryPrimitives.ReadUInt64LittleEndian(ReadPayload(secondPublication, publication.Ticket)));
        Assert.Equal(source.Ticket, referencedTicket);
        var target = sourceFile.ReadPooledFrame(referencedTicket);
        Assert.Equal(confirmDependency, target.IsSuccess);
        if (target.IsSuccess) {
            using var frame = target.Unwrap();
            Assert.Equal(sourcePayload, frame.PayloadAndMeta.ToArray());
        }
        else {
            Assert.Equal(source.Ticket.Offset, new FileInfo(secondSource).Length);
            // A complete durable publication is still present, but its original dependency is absent.
            Assert.Equal(source.Ticket.Serialize(), BinaryPrimitives.ReadUInt64LittleEndian(ReadPayload(secondPublication, publication.Ticket)));
        }
    }

    private (string Path, byte[] Bytes, SizedPtr Ticket) CreateModelImage(string name, byte[] payload) {
        string path = Path.Combine(_root, name + ".rbf");
        SizedPtr ticket;
        using (var file = RbfFile.CreateNew(path)) {
            ticket = file.Append(ModelTag, payload).Unwrap();
            file.DurableFlush();
        }
        return (path, File.ReadAllBytes(path), ticket);
    }

    private string Materialize(string name, byte[] bytes) {
        string path = Path.Combine(_root, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] ReadPayload(string path, SizedPtr ticket) {
        using var file = RbfFile.OpenReadOnlyExisting(path);
        using var frame = file.ReadPooledFrame(ticket).Unwrap();
        return frame.PayloadAndMeta.ToArray();
    }

    private static RbfPrefixInspection InspectUnchanged(string path) {
        byte[] before = File.ReadAllBytes(path);
        var result = RbfPrefixInspector.Inspect(path);
        Assert.Equal(before.LongLength, result.OriginalLength);
        Assert.Equal(before, File.ReadAllBytes(path));
        return result;
    }

    // This model explicitly distinguishes readable bytes from bytes protected by a reliable barrier.
    // Alternate persisted images model violated assumptions; they are not observations of this host FS.
    private sealed class PersistenceModel(byte[] confirmed) {
        private byte[] _confirmed = confirmed.ToArray();
        public byte[] Visible { get; private set; } = confirmed.ToArray();
        public void Write(byte[] image) => Visible = image.ToArray();
        public void Confirm(byte[]? persistedImage = null) => _confirmed = (persistedImage ?? Visible).ToArray();
        public byte[] Crash() => _confirmed.ToArray();
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
