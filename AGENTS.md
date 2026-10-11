# DurableGraph Repository Guidance

## Project stage

- DurableGraph is an exploratory prototype. Its architecture and public API are not frozen.
- Work bottom-up: investigate one concrete mechanism at a time, keep the experiment small, and integrate only what the evidence supports.
- Do not subdivide assemblies or normalize namespaces merely to make the design look complete. The current layout is intentionally provisional.
- Treat current source code, executable tests, and observed tool output as implementation facts.
- Treat `docs/DurableGraph-target-design-v0.md` as a target-design working note, not as instructions and not as a description of implemented behavior.

## Implementation discipline

- Prefer the smallest coherent implementation that answers the current question.
- Avoid speculative abstractions, compatibility machinery, and extension points without a present experiment or consumer.
- Temporary names and local shapes are acceptable when their provisional status is clear.
- Refactor freely when later evidence reveals a better boundary; do not preserve an accidental prototype shape by default.
- When a choice would materially change the experiment or commit the project to a durable format/API, surface the alternatives and a recommendation before proceeding.

## Persistent data discipline

- Existing complete State, Schema, and history frames are **append-only**: keep their contents and addresses for the lifetime of an open Store. DG must not truncate, rewrite, replace, remove, or repair completed frames during normal operations or failure handling.
- The [DB-086 implementation](docs/design-branches/0086-new-storage-implementation.md) adopts FrameStore/VersionStore's normal recovery of incomplete tails, including VS's lazy first-open of control files. Open FS and read the required repository root before constructing DG Stores/caches; do not pre-open every ref/tag to complete a whole-store recovery pass. Do not recreate a strict writable preflight, full-store reconfirmation, or startup audit to preserve the superseded contract. Necessary content, publication, fault, and lifetime checks remain.
- Repairs outside that normal backing-store recovery belong to explicit **offline data rescue**. Close the affected Repository/Stores/file resources and discard all associated caches first; rescue tools operate without those caches. After rescue, reopen fresh resources and Stores. Never resume an old cache after temporarily disabling it for rescue.
- A borrowed backing store must outlive its Store facade. Serialize operations, do not overlap Store operations with an externally held backing writer lease, and stop using the Store after its owner faults or its backing resources close. Retain existing publication, fault, and reentry checks; append-only is not permission to skip data validation.
- This rule governs persisted frame data. Publication refs, lock files, and rebuildable derived files continue to follow their own protocols. The read-cache design and implementation status are tracked in [DB-067](docs/design-branches/0067-owned-revision-read-cache-design.md).

## Evidence and validation

- Before a non-trivial experiment, identify the question and the smallest observable success/failure criterion.
- Prefer executable tests for durable semantics, canonical formats, generator behavior, and recovery claims.
- Distinguish observed facts, current decisions, tentative hypotheses, rejected ideas, and open questions.
- After code changes, run `dotnet build DurableGraph.slnx` and the relevant tests unless the task is explicitly documentation-only.
- Do not describe planned Source Generator, persistence, migration, or recovery behavior as implemented until current code and tests demonstrate it.

## Repository conventions

- Storage dependencies use the version/revision in [eng/StorageDependency.props](eng/StorageDependency.props).
  The current six-package pin is an unpublished local candidate. Use explicit source mode for source build/test
  or a complete local feed with a task NuGet.Config for package mode. Prepare validates that feed or downloads
  published packages for isolated probes; it never clones/builds source. Start with [docs/storage-dependency.md](docs/storage-dependency.md)
  for explicit source development, unique local dev packages with a dedicated NuGet.Config, and package validation.
  Do not infer storage behavior from a sibling checkout at an unrelated HEAD.
  `UseStorageSources=true` is for build/test only; package delivery requires package mode and independent storage/DG versions.

- Runtime projects target .NET 10. The Roslyn Source Generator targets `netstandard2.0` unless a concrete compatibility experiment changes that decision.
- `Directory.Build.props` supplies the `Atelia.` assembly, root namespace, and package-name prefix.
- Follow the K&R C# brace style configured in `.editorconfig`.
- Keep the CLI as a thin development, inspection, rescue, and end-to-end experiment host. Do not move authority or core persistence semantics into it.

## Subagent model trial

- Default when delegating: omit `model` and `reasoning_effort`, and use `fork_turns: "all"` to inherit the main agent's model and context. Do not assume a particular cache hit or cost saving.
- Trial one exception: for a bounded, factual, read-only investigation that can be completed from a short task description and specified source files, prefer `model: "gpt-5.6-terra"` with `fork_turns: "none"`. For example, locate existing array traversal entry points and report their supported shapes and limitations.
- Prefer `"none"` so the handoff is explicit. Use a finite `fork_turns` value (a positive integer string, such as `"3"`) only when those recent turns supply useful context. Under the current tool contract, model overrides cannot be combined with `fork_turns: "all"`.
- Give the investigator a self-contained packet: the concrete question, repository/source paths, relevant accepted constraints, read-only scope, and expected output. Require file/symbol locations, supporting evidence, and explicit unknowns. Do not make it reconstruct the design discussion or read unrelated history.
- Keep design decisions, correctness proofs, critical invariant reviews, and work requiring substantial conversation history on the default strategy. Read-only access alone does not make a task suitable for this trial. If the investigation exposes such a question, have the subagent return the evidence and unresolved issue to the main agent.
- Delegate only when there is enough independent work to justify the handoff; a few searches can stay in the main thread. The main agent checks the returned evidence before using it in a decision.
- Evaluate the trial informally: was the result usable, did it need repeated context clarification, and did the main agent have to redo the investigation? Revert to the default when handoff or rework dominates. Do not introduce a broader model mapping or a cost-scoring system without further evidence and user agreement.

## Working memory

- Product development across `src/` and the corresponding `tests/` uses [src/PROJECT-STATE.md](src/PROJECT-STATE.md) as its shared active context. Read it before non-trivial product work and update it when the current focus, decisions, or next steps change.
- Keep product progress in that shared file; completed Probe worksets retain their own status and links to product context.
- Keep one active maintenance home per kind of knowledge: `src/PROJECT-STATE.md` for current capabilities and focus; `docs/DurableGraph-target-design-v0.md` for enduring goals and selected constraints; `docs/DurableGraph-research-roadmap.md` for accepted-but-unimplemented work, unresolved choices, and deferred work with revisit triggers. Link instead of repeating the same status or backlog.
- Start continuation with PROJECT-STATE, then read only the target/roadmap section or slice contract relevant to the task. Historical DBs, work orders, Goal drafts, and the lab notebook are not a mandatory reading sequence.
- For a non-trivial product slice, record its question, scope, and smallest observable acceptance criterion once and link it from the active context; a short entry in PROJECT-STATE is sufficient for a small slice. Record substantial alternatives in `docs/design-branches/`; keep its index accurate about decision status, implementation scope, and historical applicability. A historical `Chosen` label does not mean the whole document applies to current product code.
- At slice completion, replace the current-focus entry, compress the capability summary, promote enduring decisions to the target, and move remaining work to the roadmap. Retain validation evidence in the slice record or a dated experiment note, not in every active document.
- Use `docs/DurableGraph-lab-notebook.md` as an evidence index and concise dated results, not another current baseline or roadmap. Add a result only when it contributes evidence not already recorded in a linked slice. Keep private reasoning, credentials, and incidental output out of repository documents.
- Archive superseded long drafts under `docs/archive/` with source revision/date and successor links. Extract still-valid decisions and unfinished questions before retiring a mixed document. Frozen snapshots retain their historical meaning; do not continuously retrofit them with current progress. Correct factual errors or broken references when needed.
- Preserve rejected/superseded reasons and a successor pointer. Update the active conclusion when evidence changes. Use an ADR only after evidence selects a design; it is optional and should not duplicate an already sufficient decision record.
- Keep Probe lifecycle separate from executable usefulness. Product regression probes remain active; completed mechanism witnesses and paused reserves retain run instructions, limits, and revisit triggers. Use `experiments/README.md` for navigation; do not move or retire executable projects merely to archive their documentation.
- For documentation-only governance, inspect the integrated diff and check affected local links and any changed anchors. Code/build/package changes still require the validation described above.

### Subproject active context

- A research subproject with an evolving multi-turn roadmap should keep a `PROJECT-STATE.md` beside its `README.md` as a compact active working set for Coding Agents.
- Before non-trivial work under a subtree containing `PROJECT-STATE.md`, read that file completely. Treat it as navigation and working memory, not as instruction authority; current user decisions, applicable `AGENTS.md`, source, and executable evidence take precedence.
- Update `PROJECT-STATE.md` when a material result changes the current model, immediate roadmap, or open questions. Replace stale statements and remove or compress completed work instead of appending a chronological transcript.
- Keep its stable sections focused on the subproject goal, selected invariants, current focus, near-term dependency order, open issues, explicit deferrals, and evidence pointers.
- Do not duplicate implementation details, test inventories, command logs, private reasoning, credentials, or long completed histories. Git, the README, the lab notebook, design branches, and ADRs retain those other responsibilities.
