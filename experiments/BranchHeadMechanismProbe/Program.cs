using System.Text;
using System.Text.Json;
using Atelia.HeadStore;
using DataStore = Atelia.FrameStore.FrameStore;

// This is a composition witness, not a DG implementation or a durable wire-format proposal.
string output = Path.GetFullPath(args.Length == 1 ? args[0] : ".artifacts/db096-head-mechanism/runs");
string run = Path.Combine(output, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(run);
var probe = new Probe(run);
try {
    probe.RegistrationAndFork();
    probe.AnchorBudget();
    probe.WriteReceipt(true, null);
    Console.WriteLine($"PASS: {probe.Assertions} assertions; {run}");
    return 0;
}
catch (Exception error) {
    probe.WriteReceipt(false, error.ToString());
    Console.Error.WriteLine(error);
    return 1;
}

sealed class Probe(string run) {
    public int Assertions { get; private set; }
    private readonly List<object> _budgetRows = [];
    private readonly List<string> _checks = [];
    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
    private static string Text(HeadEntry entry) => Encoding.UTF8.GetString(entry.Payload);
    private static string Key(HeadId id) {
        byte[] bytes = new byte[HeadId.EncodedSize];
        if (!id.TryWrite(bytes)) { throw new InvalidDataException("Cannot encode HeadId."); }
        return Convert.ToHexString(bytes);
    }
    private static byte[] DirectoryPayload(Dictionary<string, string> entries) => JsonSerializer.SerializeToUtf8Bytes(entries);
    private static Dictionary<string, string> Members(HeadStore store, HeadId directory) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(store.ReadHead(directory).Unwrap().Payload)!;
    private void Check(bool condition, string description) {
        if (!condition) { throw new InvalidOperationException(description); }
        Assertions++;
        _checks.Add(description);
    }
    private void Confirmed(PublicationOutcome outcome) => Check(outcome == PublicationOutcome.Confirmed, "publication confirmed");
    private static void Cold(string path, Action<HeadStore> read) {
        using var data = DataStore.OpenReadOnly(Path.Combine(path, "data"));
        using var store = HeadStore.OpenReadOnly(Path.Combine(path, "heads"), data);
        read(store);
    }
    private static void Write(string path, Action<HeadStore> write) {
        using var data = DataStore.Open(Path.Combine(path, "data"));
        using var store = HeadStore.Open(Path.Combine(path, "heads"), data);
        write(store);
    }

    public void RegistrationAndFork() {
        string path = Path.Combine(run, "registration");
        Directory.CreateDirectory(path);
        HeadId directory, schema;
        HeadEntryId a0, a1 = default, a2 = default, c1 = default, c2 = default, grown = default;
        HeadId child = default, abandoned = default;
        var members = new Dictionary<string, string>();
        using (var data = DataStore.Create(Path.Combine(path, "data")))
        using (var store = HeadStore.Create(Path.Combine(path, "heads"), data, reservedHeadIdUpperBound: 2)) {
            directory = store.CreateHead(1, DirectoryPayload(members), out var outcome).Unwrap().HeadId;
            Confirmed(outcome);
            schema = store.CreateHead(2, Bytes("schema:0"), out outcome).Unwrap().HeadId;
            Confirmed(outcome);
            a0 = store.CreateHead(Bytes("State:A0;schema:0"), out outcome).Unwrap();
            Confirmed(outcome);
            Check(store.ListHeads().Unwrap().Contains(a0.HeadId) && !Members(store, directory).ContainsKey(Key(a0.HeadId)),
                "root physically exists before registration and is not a member");
        }
        Cold(path, store => {
            Check(Members(store, directory).Count == 0, "cold directory excludes prepared root");
            Check(Text(store.ReadEntry(a0).Unwrap()) == "State:A0;schema:0", "hidden root is physically readable");
        });
        Write(path, store => {
            members.Add(Key(a0.HeadId), "root A");
            store.PublishHead(directory, DirectoryPayload(members), out var outcome).Unwrap();
            Confirmed(outcome);
            a1 = store.PublishHead(a0.HeadId, Bytes("Event:A1;schema:0"), out outcome).Unwrap();
            Confirmed(outcome);
            child = store.ForkHead(a1, out outcome).Unwrap();
            Confirmed(outcome);
        });
        Cold(path, store => {
            Check(Members(store, directory).Count == 1 && !Members(store, directory).ContainsKey(Key(child)), "cold directory excludes unregistered fork");
            Check(store.ReadHead(child).Unwrap().Id == a1, "empty fork current is exact inherited Entry, no clone");
        });
        Write(path, store => {
            members.Add(Key(child), "child C");
            store.PublishHead(directory, DirectoryPayload(members), out var outcome).Unwrap();
            Confirmed(outcome);
            a2 = store.PublishHead(a0.HeadId, Bytes("Event:A2;schema:0"), out outcome).Unwrap();
            Confirmed(outcome);
        });
        Cold(path, store => {
            Check(Members(store, directory).ContainsKey(Key(child)), "registered empty fork is listed after reopen");
            Check(store.ReadHead(child).Unwrap().Id == a1 && store.ReadHead(a0.HeadId).Unwrap().Id == a2,
                "parent advance does not move empty child current");
            Check(Text(store.ReadHead(child).Unwrap()) == "Event:A1;schema:0", "empty fork previews inherited payload");
            var inherited = ReadPage(store, a1, before: false, count: 10, workBudget: 1000);
            Check(inherited.Ids.SequenceEqual(new[] { a1, a0 }) && inherited.Complete, "inherited history reaches root");
        });
        Write(path, store => {
            c1 = store.PublishHead(child, Bytes("Event:C1;schema:0"), out var outcome).Unwrap();
            Confirmed(outcome);
            c2 = store.PublishHead(child, Bytes("State:C2;schema:0"), out outcome).Unwrap();
            Confirmed(outcome);
            Check(c1.HeadId == child && c1 != a1, "first child publication has child identity");
            grown = store.CreateHead(Bytes("State:G0;schema:1"), out outcome).Unwrap();
            Confirmed(outcome);
            abandoned = store.CreateHead(Bytes("State:unused;schema:0"), out outcome).Unwrap().HeadId;
            Confirmed(outcome);
        });
        Cold(path, store => {
            Check(!Members(store, directory).ContainsKey(Key(grown.HeadId)), "growth root hidden before D/S publication");
            Check(Text(store.ReadHead(schema).Unwrap()) == "schema:0", "schema not advanced merely by creating growth root");
        });
        Write(path, store => {
            members.Add(Key(grown.HeadId), "root G");
            var results = store.PublishHeads(new Dictionary<HeadId, ReadOnlyMemory<byte>> {
                [directory] = DirectoryPayload(members), [schema] = Bytes("schema:1")
            }, out var outcome).Unwrap();
            Confirmed(outcome);
            Check(results.Count == 2, "D/S registration publishes two existing heads");
        });
        Cold(path, store => {
            var listed = Members(store, directory);
            Check(listed.Count == 3 && listed.ContainsKey(Key(grown.HeadId)), "cold registered roots and child are visible");
            Check(!listed.ContainsKey(Key(directory)) && !listed.ContainsKey(Key(schema)) && !listed.ContainsKey(Key(abandoned)),
                "directory excludes internal and abandoned physical heads");
            Check(store.ListHeads().Unwrap().Contains(abandoned), "abandoned complete head remains present");
            Check(Text(store.ReadHead(schema).Unwrap()) == "schema:1", "cold D/S successful publication agrees");
            Check(Text(store.ReadEntry(grown).Unwrap()) == "State:G0;schema:1", "new root retains selected schema marker");

            var first = ReadPage(store, c2, before: false, count: 2, workBudget: 1000);
            var second = ReadPage(store, first.Ids[^1], before: true, count: 2, workBudget: 1000);
            var last = ReadPage(store, second.Ids[^1], before: true, count: 2, workBudget: 1000);
            Check(first.Ids.Concat(second.Ids).SequenceEqual(new[] { c2, c1, a1, a0 }), "cross-fork pages have exact order and no duplicate anchor");
            Check(last.Ids.Count == 0 && last.Complete, "page full at root permits empty complete next page");
            Check(!first.Ids.Concat(second.Ids).Contains(a2), "parent suffix excluded from inherited child history");
            var full = ReadPage(store, c2, before: false, count: 10, workBudget: 1000);
            Check(full.Ids.SequenceEqual(new[] { c2, c1, a1, a0 }) && full.Complete, "complete child history has no synthetic fork Entry");
            Check(full.Payloads.SequenceEqual(new[] { "State:C2;schema:0", "Event:C1;schema:0", "Event:A1;schema:0", "State:A0;schema:0" }),
                "State/Event markers survive inherited history");

            // Find a real budget failure after at least one visitor delivery, not just a preflight failure.
            bool sawPartialFailure = false;
            for (int budget = 1; budget < 100; budget++) {
                try { ReadPage(store, c2, false, 10, budget); }
                catch (PageFailure error) when (error.Code == "HeadStore.HistoryBudgetExceeded") {
                    if (error.Visited == 0) { continue; }
                    Check(error.Visited < full.Ids.Count, "budget exhaustion occurs after a real partial prefix");
                    sawPartialFailure = true;
                    break;
                }
            }
            Check(sawPartialFailure, "materializer rejects whole page after partial budget failure");
            Check(ReadPage(store, c2, false, 10, 1000).Ids.SequenceEqual(full.Ids), "adequate-budget retry is unambiguous after failed page");
        });
    }

    public void AnchorBudget() {
        string path = Path.Combine(run, "anchor-budget");
        Directory.CreateDirectory(path);
        HeadEntryId anchor;
        using (var data = DataStore.Create(Path.Combine(path, "data")))
        using (var store = HeadStore.Create(Path.Combine(path, "heads"), data)) {
            anchor = store.CreateHead(Bytes("State:0"), out _).Unwrap();
            for (int index = 1; index < 20; index++) {
                anchor = store.PublishHead(anchor.HeadId, Bytes($"Event:{index}"), out _).Unwrap();
            }
        }
        int previousSuffix = 0;
        foreach (int suffix in new[] { 0, 100, 10000 }) {
            Write(path, store => {
                for (int index = previousSuffix; index < suffix; index++) {
                    store.PublishHead(anchor.HeadId, Bytes($"newer:{index}"), out _).Unwrap();
                }
            });
            previousSuffix = suffix;
            // Every attempted budget opens a fresh owner. This is not an OS cache eviction.
            int cold = MinimumBudget(budget => {
                Page? result = null;
                Cold(path, store => result = ReadPage(store, anchor, false, 20, budget));
                return result!;
            });
            int warm = 0;
            Cold(path, store => {
                store.ReadEntry(anchor).Unwrap(); // retain target in owner cache
                warm = MinimumBudget(budget => ReadPage(store, anchor, false, 20, budget));
            });
            _budgetRows.Add(new { newerSuffix = suffix, coldOwnerMinimumWork = cold, warmOwnerMinimumWork = warm, pageItems = 20 });
            Console.WriteLine($"suffix {suffix}: minimum work cold={cold}, warm={warm}");
        }
    }

    private int MinimumBudget(Func<int, Page> attempt) {
        for (int budget = 1; budget <= 1000; budget++) {
            try {
                var page = attempt(budget);
                Check(page.Ids.Count == 20 && page.Payloads[0] == "Event:19" && page.Payloads[^1] == "State:0", "fixed old anchor page unaffected by newer suffix");
                return budget;
            }
            catch (PageFailure error) when (error.Code == "HeadStore.HistoryBudgetExceeded") { }
        }
        throw new InvalidOperationException("No successful budget below 1001.");
    }

    // The method creates a return value only after success; no partial page escapes an error.
    private static Page ReadPage(HeadStore store, HeadEntryId start, bool before, int count, int workBudget) {
        var ids = new List<HeadEntryId>();
        var payloads = new List<string>();
        int visited = 0;
        var result = store.ReadEntryHistory(start, new(count + (before ? 1 : 0), workBudget), entry => {
            visited++;
            if (before && visited == 1) {
                if (entry.Id != start) { throw new InvalidDataException("Exact anchor changed."); }
                return true;
            }
            ids.Add(entry.Id);
            payloads.Add(Text(entry));
            return ids.Count < count;
        });
        if (result.IsFailure) { throw new PageFailure(result.Error!.ErrorCode, visited); }
        return new Page(ids, payloads, result.Value == HeadHistoryEnd.Complete);
    }
    public void WriteReceipt(bool passed, string? error) => File.WriteAllText(Path.Combine(run, "receipt.json"),
        JsonSerializer.Serialize(new {
            passed, assertions = Assertions, storageRevision = "f8868b57e8f012745413bb38a83d67f1dba29f26",
            qualification = "Source mode; public HeadStore APIs; illustrative payload; clean close/reopen; no fault injection or DG recovery",
            budgetRows = _budgetRows, checks = _checks, error
        }, new JsonSerializerOptions { WriteIndented = true }));
    private sealed record Page(List<HeadEntryId> Ids, List<string> Payloads, bool Complete);
    private sealed class PageFailure(string code, int visited) : Exception(code) {
        public string Code { get; } = code;
        public int Visited { get; } = visited;
    }
}
