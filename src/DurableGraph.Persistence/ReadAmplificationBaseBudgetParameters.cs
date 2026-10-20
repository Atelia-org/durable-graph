namespace Atelia.DurableGraph.Persistence;

/// <summary>
/// Integer threshold for motivating optional Base writes and their soft budget percent.
/// </summary>
/// <remarks>
/// State save operations use (5, 5) when their policy argument is omitted or null.
/// Overrides apply to one call only, including CreateRootBranchFromState; later calls do not inherit them.
/// Events always write complete Base content and do not accept this policy.
/// A lower threshold favors shorter reconstruction chains; a higher one favors fewer Base writes.
/// Under a stable small-Delta model, thresholds 3, 5 and 11 correspond to average reconstruction
/// payloads of 2, 3 and 6 times Base, and write payloads of 1.5, 1.25 and 1.1 times continuous Delta.
/// These are steady-state approximations without budget delays, not bounds on I/O or elapsed time.
/// </remarks>
/// <param name="ReadAmplificationThreshold">
/// At least 1. Optional Base motivation starts when prospective reconstruction payload strictly
/// exceeds this multiple of current Base payload; it is not a hard read-amplification limit.
/// </param>
/// <param name="BaseBudgetPercent">
/// 1 through 100. Soft optional Base budget relative to the complete post-live Base payload.
/// Required Base writes do not consume it; it does not cap total or physical write bytes.
/// </param>
public readonly record struct ReadAmplificationBaseBudgetParameters(
    int ReadAmplificationThreshold,
    int BaseBudgetPercent);
