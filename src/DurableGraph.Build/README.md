# DurableGraph schema history tool

This private .NET 10 console tool is intended for the DurableGraph package's
MSBuild integration. It validates the comment-only manifest emitted by the
Source Generator and either publishes exact schema-history files or verifies
that the current manifest candidates are already present.

```text
DurableGraph.Build publish --manifest <generated.g.cs> --schema-history <directory> [--reference-manifest <references.g.cs>]
DurableGraph.Build verify  --manifest <generated.g.cs> --schema-history <directory> [--reference-manifest <references.g.cs>]
```

The manifest format is strict:

```csharp
// durable-graph-schema-history-manifest:9
// schema-begin
// schema-id-base64:cHJvYmUuY2hhcmFjdGVy
// version:1
// kind:1
// arity:0
// field:1|4
// field:2|18|q(b2)
// schema-end
```

It may contain zero or more schema-history record blocks. Field IDs must be positive,
unique, and sorted. Type tags `1` through `14` retain their existing meanings
and two-column field records. Current format v9 uses canonical type patterns:

- Tag `15`: a third column with the nominal named/array/List/Dictionary reference pattern; no target version.
- Tag `16`: an inline named pattern followed by its fixed historical version.
- Tag `17`: a declaration-scoped type parameter, such as `p0`.
- Tags `19`–`24`: Guid, decimal, TimeSpan, DateOnly, TimeOnly and DateTimeOffset scalar leaves.
- Tag `18`: a Nullable pattern `q(child)`. A named child requires its fixed inline version
  as the fourth column; scalar and parameter children have no version column.

For example, `// field:2|18|q(b2)` is `int?`; `// field:3|18|q(nUG9pbnQ=())|1`
retains the exact Point V1 dependency. Nullable does not have its own definition version.
Arrays use `a1(...)` through `a4(...)`, Lists use `l(...)`, Dictionaries use `d(key,value)`, and named definitions use
`n<canonical UTF-8 Base64 ID>(arguments)`. These constructors compose recursively within
the supported CLR shape constraints. Nullable cannot directly wrap references or another Nullable.

Accepted formats v1–v8 remain readable with their original grammar and immutable filenames/hashes.
Nullable requires v6 or newer, Dictionary v7, Guid/decimal/TimeSpan v8, and the remaining temporal scalars v9, including nested uses.
Legacy `.dgsnapshot` headers and record markers are rejected.

The Source Generator validates the Schema shapes needed to compile generated code. The Build tool is
the canonical file authority: after `CoreCompile` it additionally rejects duplicate keys,
noncanonical filenames, BOM/CRLF bytes, and other representations that cannot be published as exact
history. A project build succeeds only when both layers accept the inputs.

Each checked-in `*.dgschema` uses the same single block with the header
`// durable-graph-schema-history:9` for newly published records. History is canonical UTF-8 without a byte-order
mark, uses LF line endings, and ends in one LF. Its filename contains full
SHA-256 hashes of the decoded SchemaId and canonical history content, plus the
version; a SchemaId never becomes a path component.

Publishing validates the entire manifest and all existing history before it
writes. Exact existing schema-history records are left untouched. New records are written
to a temporary file in the history directory and moved into place without
overwrite. Publish and Verify acquire the same directory-scoped, cross-process mutex
before reading history and retain it through validation and publication. A later identical
publisher re-reads the accepted records and uses the existing already-exact path; a conflicting
publisher fails validation before writing anything. The wait is bounded to 30 seconds;
a timeout reports the history directory and asks the caller to run builds/publishing serially.

Coordination uses a named OS mutex for the same machine/user and normalized absolute path.
It does not write a lock file into history. Ordinary Windows path case and trailing separators
are normalized; physical aliases such as junctions or mapped drives, other users/machines,
old tools and external edits are outside this coordination. Acquisition errors do not fall back
to unlocked publication. The operating system releases ownership after process termination;
every operation still validates disk history, whether or not it observes an abandoned mutex.

Builds sharing history should still run serially. After a Publish build has completed, Verify
builds may run in parallel with separate intermediate/output directories while history remains
unchanged. Generator inputs are selected before this tool runs: a build started during a multi-file
publication can reject a temporarily incomplete history set, and a successful build need not include
history added by another build after its inputs were selected. Wait for the publisher to complete,
then retry the build serially. Off and design-time generation retain their existing behavior.

Publishing several files is not a transaction. A failed or terminated publisher may leave completed
files whose accepted dependency closure is incomplete. The next operation fails validation;
releasing a mutex does not repair history. Never overwrite or delete accepted files to hide a conflict.

For fixed inline fields or exact base schemas supplied by another assembly, package targets pass the separate
`DurableGraphSchemaHistoryReferences.g.cs` file emitted by the same generation. It contains
only selected external records and their declaring assembly identities. The owned
candidate manifest ends with `// references-sha256:<lowercase SHA256>` when these records are
nonempty. The digest covers the complete canonical reference text; missing, stale, malformed,
duplicate or differently owned inputs are rejected, even when there are zero owned candidates.
The reference container remains version 1. Each embedded record is a canonical v9 manifest:
execution contract 1 requires InlineValue, while contract 2 requires ReferenceObject. Unknown
contracts and mismatched schema kinds are rejected. This is build input, not State wire or a second history store.

Validation proceeds in two stages: imported records must close using imports alone; owned
accepted history plus those imports must then close before any current candidate is added.
Both stages follow exact base and fixed inline edges, including retained history with no current
owned candidates. Base edges require ReferenceObject; inline edges require InlineValue. Nominal
reference fields do not require the referenced object's exact version.
An imported definition ID cannot overlap any owned accepted or current definition, even at
another version. Consequently a current candidate cannot repair missing accepted history,
and a library cannot borrow a consumer definition to fill a missing dependency export.
Publish and Verify count only owned records. External history is never copied or rewritten.

Existing direct tool calls without external records still accept the original two arguments;
an explicit empty reference manifest is also accepted. Package builds require exactly one
candidate and one reference file, including an empty reference file. If stale generator output
remains after a package upgrade, inspect the reported `CompilerGeneratedFilesOutputPath` and
remove this project's DurableGraph generator output beneath it. Keep Schema-history files,
then force compilation with `dotnet build -t:Rebuild`; `dotnet clean` alone may leave generator
output behind. The hash detects mismatched generation inputs; it does not detect a
coordinated replacement of all history and generated inputs with a different self-consistent set.
