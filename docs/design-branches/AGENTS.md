# Design-branch document maintenance

These rules supplement the repository guide for documents in this subtree.
The rationale and completed migration evidence are in [DB-090](0090-document-governance/README.md).

## Layout and reading entry

- Before opening a new DB, search the index and the relevant existing problem. Reuse the problem when its question and acceptance boundary still apply; a small product change may only need a PROJECT-STATE entry.
- DBs use stable `NNNN-topic/README.md` directories; new problems take the next unused DB number. Keep reviews, work orders, results, and data with the relevant problem, not directly in this directory. Adding material to an old DB should first check its existing entry and add a file only when necessary. Do not rename a problem directory merely because its status changes.
- Each problem has one reading entry, identified by the index. Its opening explains the question, the bounded conclusion and applicability, and where to find the governing contract and evidence. A small problem can live entirely in README; add detailed design, review, results, or data files only when independently useful. File names do not determine authority.
- Shared material lives once, with links from every relevant problem. Do not copy a joint review into several DBs. Directory names do not encode changing status or date.
- New index entries contain the DB/topic, its reading entry, and a short applicability or successor hint when needed. Detailed decisions and implementation evidence belong at the linked entry; future product work belongs in the roadmap.

## Maintain with the task

- **Open or reopen:** state the question, scope, and smallest observable acceptance criterion once. For product work, link the contract from PROJECT-STATE. A new scope can use a successor DB pointing to old evidence; do not reactivate a completed work order just because it contains a historical “next”.
- **Change a conclusion:** replace the entry's current explanation and preserve the reason and evidence. Check whether this changes the applicability of a cited predecessor; if so, add a bounded successor notice at that old entry. Leave frozen evidence bodies in their historical meaning.
- **Finish or stop:** write one compact final result with evidence and limits; replace the ongoing plan or current explanation instead of appending round-by-round progress. Distinguish experiment completion, implementation, and default enablement. For product work, update only the affected knowledge homes: current capability/focus in PROJECT-STATE, enduring decisions in the target, unfinished work and revisit triggers in the roadmap. The DB links those homes rather than maintaining another backlog. A documentation-only task need not change product state.
- **Move or compress:** treat a historical move as its own bounded work package. Preserve the exact source revision/date and coverage of frozen content; extract valid decisions and unresolved questions first. Find affected incoming links as well as outgoing links, repair relative paths, and check changed anchors and their evidence/version destinations. An empty old anchor or a link to an archive that lacks the relevant period does not preserve evidence. Update repository consumers directly; retain a redirect only for a known consumer that cannot be updated, with no second copy of conclusions.

Before finishing, inspect the integrated diff, check affected local links and changed anchors, and run `git diff --check`. Documentation alone does not require product build/tests. Scripts may verify mechanical reachability; they do not establish approval, implementation, current applicability, or evidence strength.
