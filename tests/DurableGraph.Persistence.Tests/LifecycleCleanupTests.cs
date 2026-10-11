using System.Reflection;
using System.Runtime.CompilerServices;
using Atelia.DurableGraph.Storage;

namespace Atelia.DurableGraph.Persistence.Tests;

public sealed class LifecycleCleanupTests : IDisposable {
    private const string Prefix = "durable-lifecycle-cleanup-";
    private readonly string _root = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));

    [Fact]
    public void PublicDisposeAttemptsBothActualBackingOwnersAndRetainsTheirErrorOrder() {
        using GraphResources resources = GraphResources.CreateNew(_root);
        StateRevisionStore retained = resources.States;
        FrameAddress address = retained.Append(StateRevision.CreateObjectHeadMapBase(null, [], []));
        resources.Data.ConfirmDurable();
        retained.Read(address);
        retained.ReadLiveObjectHeadMap(address);
        IOException versionError = new("VersionStore owner lock close failed.");
        IOException dataError = new("FrameStore owner lock close failed.");
        List<string> attempted = [];
        using IDisposable originalVersionLock = ReplaceOwnerLock(resources.Versions, versionError, () => attempted.Add("versions"));
        using IDisposable originalDataLock = ReplaceOwnerLock(GetField(resources.Data, "_core"), dataError, () => attempted.Add("data"));

        AggregateException error = Assert.Throws<AggregateException>(resources.Dispose);
        Assert.Equal(new[] { "versions", "data" }, attempted);
        Assert.Collection(error.InnerExceptions,
            first => Assert.Same(versionError, first), second => Assert.Same(dataError, second));
        Assert.Throws<ObjectDisposedException>(resources.RequireAvailable);
        Assert.Throws<ObjectDisposedException>(() => retained.Read(address));
        Assert.Throws<ObjectDisposedException>(() => retained.ReadLiveObjectHeadMap(address));
        Assert.Throws<ObjectDisposedException>(() => retained.Append(StateRevision.CreateObjectHeadMapBase(null, [], [])));
        Assert.Throws<ObjectDisposedException>(resources.Data.EnsureUsable);
        Assert.Throws<ObjectDisposedException>(resources.Versions.EnsureUsable);
        resources.Dispose();

        using GraphResources fresh = GraphResources.OpenExisting(_root);
        Assert.Empty(fresh.States.Read(address).LocalObjects);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SingleActualOwnerCloseErrorPreservesOriginalIdentityAndStack(bool versionOwner) {
        using GraphResources resources = GraphResources.CreateNew(_root);
        IOException injected = new("Single owner lock close failed.");
        object target = versionOwner ? resources.Versions : GetField(resources.Data, "_core");
        int attempts = 0;
        using IDisposable originalLock = ReplaceOwnerLock(target, injected, () => attempts++);

        IOException error = Assert.Throws<IOException>(resources.Dispose);
        Assert.Same(injected, error);
        Assert.Contains(nameof(ThrowAfterClosingLock), error.StackTrace);
        Assert.Equal(1, attempts);
        Assert.Throws<ObjectDisposedException>(resources.Data.EnsureUsable);
        Assert.Throws<ObjectDisposedException>(resources.Versions.EnsureUsable);
        using GraphResources fresh = GraphResources.OpenExisting(_root);
        fresh.RequireAvailable();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OpenCleanupHelperPreservesPrimaryWithActualResourcesAndOptionalCloseFailures(bool failCleanup) {
        using GraphResources resources = GraphResources.CreateNew(_root);
        IOException primary = CapturePrimary();
        IOException versionError = new("VersionStore cleanup failed.");
        IOException dataError = new("FrameStore cleanup failed.");
        IDisposable? originalVersionLock = null;
        IDisposable? originalDataLock = null;
        List<string> attempted = [];
        try {
            if (failCleanup) {
                originalVersionLock = ReplaceOwnerLock(resources.Versions, versionError, () => attempted.Add("versions"));
                originalDataLock = ReplaceOwnerLock(GetField(resources.Data, "_core"), dataError, () => attempted.Add("data"));
            }
            MethodInfo cleanup = typeof(GraphResources).GetMethod("CloseResources", BindingFlags.Static | BindingFlags.NonPublic)!;
            // Open invokes this same helper. Reflection adds only its normal invocation envelope.
            TargetInvocationException invocation = Assert.Throws<TargetInvocationException>(() => cleanup.Invoke(null,
                [primary, resources.States, resources.Versions, resources.Data]));
            if (failCleanup) {
                AggregateException error = Assert.IsType<AggregateException>(invocation.InnerException);
                Assert.Collection(error.InnerExceptions,
                    first => Assert.Same(primary, first), second => Assert.Same(versionError, second), third => Assert.Same(dataError, third));
                Assert.Equal(new[] { "versions", "data" }, attempted);
            } else {
                Assert.Same(primary, invocation.InnerException);
                Assert.Contains(nameof(CapturePrimary), primary.StackTrace);
            }
            Assert.Throws<ObjectDisposedException>(() => resources.States.Append(StateRevision.CreateObjectHeadMapBase(null, [], [])));
            Assert.Throws<ObjectDisposedException>(resources.Data.EnsureUsable);
            Assert.Throws<ObjectDisposedException>(resources.Versions.EnsureUsable);
            resources.Dispose();
            using GraphResources fresh = GraphResources.OpenExisting(_root);
            fresh.RequireAvailable();
        } finally {
            originalVersionLock?.Dispose();
            originalDataLock?.Dispose();
        }
    }

    private static object GetField(object owner, string name) => owner.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;

    private static IDisposable ReplaceOwnerLock(object owner, Exception failure, Action observed) {
        FieldInfo field = owner.GetType().GetField("_ownerLock", BindingFlags.Instance | BindingFlags.NonPublic)!;
        IDisposable original = (IDisposable)field.GetValue(owner)!;
        field.SetValue(owner, new ThrowAfterClosingLock(original, failure, observed));
        return original;
    }

    private sealed class ThrowAfterClosingLock(IDisposable inner, Exception error, Action observed) : IDisposable {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Dispose() {
            observed();
            inner.Dispose();
            throw error;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IOException CapturePrimary() {
        try { throw new IOException("Required open operation failed."); }
        catch (IOException error) { return error; }
    }

    public void Dispose() => SharedReadModel.DeleteFixture(_root, Prefix);
}
