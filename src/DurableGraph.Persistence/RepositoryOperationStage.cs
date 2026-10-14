using Atelia.FrameStore;
using Atelia.DurableGraph.Storage;

namespace Atelia.DurableGraph.Persistence;

// Internal deterministic fault injection; never part of the host publication contract.
internal enum RepositoryOperationStage {
    AfterPrepare, BeforeStateAppend, AfterStateAppend,
    BeforePublication, AfterPublication, BeforeInstall
}
