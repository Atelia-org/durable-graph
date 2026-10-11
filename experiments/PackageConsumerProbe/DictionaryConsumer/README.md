# Dictionary package consumer

Each run creates a new shared FrameStore/VersionStore repository. The 32-entry container
keeps enough unchanged content for the sparse edits to remain real Deltas with the fixed 12B
prior address. The lane retains exact model-schema history and its immutable file/hash checks.

DB-054 delivery witness. Run from the repository root:

```powershell
./experiments/PackageConsumerProbe/Run-DictionaryProbe.ps1
```

The runner packs nine local dependency packages into an isolated feed/cache. The consumer has
only ordinary Runtime and Storage PackageReference entries; generation and history publication
arrive through package assets. Pass both `-PackageSource` and `-Version` to reuse a matching feed.
Artifacts stay beneath the unique ignored `obj/dictionary-*` directory.

V1 saves a shared `Dictionary<string, LegacyPoint>` and a `Dictionary<LegacyMode, LegacyPoint>`.
The same Dictionaries also appear through a List, vector and generic Box. Values retain a Node
whose World back-reference and self-reference form a cycle. A separate OrdinalIgnoreCase
Dictionary preserves content lookup, while a ReferenceIdentity Dictionary retains two distinct
equal-content string keys and shares those exact key instances with World fields.

Continuous commits patch one value, remove/add a key, patch another value, reverse enumeration
order while increasing Capacity, and patch again. Every meaningful Dictionary edit must produce
an actual key-addressed Delta. The order/Capacity-only commit and unchanged commit write no local
objects. Reading every earlier revision checks that a changed enumeration position never redirects
a subsequent Delta. Saved Base and Delta content survives clearing the source
Dictionaries before reading it back. All source instances remain ordinary BCL Dictionaries.

V2 removes both old domain declarations. Their replacements retain durable identities but advance
the enum from int to long and Point from int to long. Stored-exact reading first retrieves the
old key/value DTOs without business callbacks. Explicit independent key and value rule sets then
add 1000 to each old number. The expected 96 leaf callbacks are 32 values for the shared string
Dictionary, plus 32 keys and 32 values for the enum Dictionary. Every callback observes its actual
Dictionary owner and Count=32. Multiple incoming references do not cause repeated normalization.

Only these two Dictionaries must rewrite Base. World, Box, array and List retain their original
nominal reference layouts. The next commit is unchanged, a value edit resumes ordinary Delta,
and cold reopening does not run the old conversion again. The original revision stays readable.

The runner checks Publish and Verify builds, new history format v9 and exact file counts
**5 -> 7**. Previously accepted filenames and SHA256 hashes must remain unchanged; retained
history is never rewritten to a newer format. Required stage outputs follow executable assertions:

```text
DictionarySeed:True:SharedComparersAndCycles:True:KeyAddressedDelta:True:UnorderedNoChange:True:FrozenPreparation:True
DictionaryUpgrade:True:HistoricalExact:True:DeletedKeyAndValueTypes:True:IndependentKeyValueRules:True:SharedOwnerOnce:True:ForcedBaseThenDelta:True:ColdReopen:True
```

This is a package/functional witness. Focused product tests cover malformed wire bodies,
unsupported comparer/key shapes, floating-point lookup collisions, empty-string canonicalization
collisions, missing upgrade tools and upgrade-induced duplicate keys. Performance measurement and
future compound key support are separate work. See [DB-054](../../../docs/design-branches/0054-dictionary-content-object-slice.md)
for the selected experimental BCL adapter boundary and acceptance evidence.
