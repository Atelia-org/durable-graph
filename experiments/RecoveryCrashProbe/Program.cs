using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Atelia.DurableGraph;
using Atelia.DurableGraph.Persistence;
using Atelia.DurableGraph.Cli;
using Atelia.Rbf.Internal;

namespace Atelia.RecoveryCrashProbe;

internal static class Program {
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };
    private static void Emit(object value) { Console.WriteLine(JsonSerializer.Serialize(value, Json)); Console.Out.Flush(); }
    private static void Ready(string point, object? detail = null) {
        Emit(new { type = "READY", pid = Environment.ProcessId, point, detail });
        // External controller terminates this process. No finally/Dispose runs past this point.
        Thread.Sleep(Timeout.Infinite);
    }

    private static int Main(string[] args) {
        try {
            if (args.Length < 2) { throw new ArgumentException("Explicit mode and fixture path required."); }
            string mode = args[0], root = Path.GetFullPath(args[1]);
            string point = args.Length > 2 ? args[2] : "";
            switch (mode) {
                case "seed":
                    using (Repository repository = Repository.CreateNew(root, ProbeModel.Models())) {
                        using var branch = repository.CreateBranch("main", ProbeModel.Graph<ProbeModel.OldNode>(10));
                        branch.CommitEvent(ProbeModel.Graph<ProbeModel.OldNode>(11));
                        Emit(new { type = "seed", acknowledged = true, graph = Inspect(repository) });
                    }
                    break;
                case "commit":
                    using (Repository repository = Repository.OpenExisting(root, ProbeModel.Models())) {
                        using var branch = repository.Checkout("main");
                        repository.Checkpoint = checkpoint => {
                            if (point == "commit:" + checkpoint) { Ready(point); }
                        };
                        InstallWriteHooks(root, point);
                        bool state = args.Length > 3 && args[3] == "state";
                        var graph = ProbeModel.Graph<ProbeModel.NewNode>(state ? (byte)20 : (byte)21);
                        var head = state ? branch.CommitState(graph) : branch.CommitEvent(graph);
                        if (point == "SuccessReturnBeforeAck") { Ready(point, new { address = head.Address.ToString() }); }
                        Emit(new { type = "commit", acknowledged = true, graph = Inspect(repository) });
                    }
                    break;
                case "recover":
                case "continue":
                    RepositoryRecovery.Checkpoint = checkpoint => {
                        Emit(new { type = "checkpoint", point = checkpoint });
                        bool refAlias = point.EndsWith("ConfirmFile:ref", StringComparison.Ordinal) &&
                            checkpoint.StartsWith(point[..point.LastIndexOf(':')] + ":journal/refs/objects/", StringComparison.Ordinal);
                        if (checkpoint == point || refAlias) { Ready(point, new { actualCheckpoint = checkpoint }); }
                    };
                    var report = RepositoryRecovery.Recover(root, ProbeModel.Models());
                    // Never serialize the live Repository and its ownership graph.
                    var fields = report.GetType().GetProperties().Where(p => p.Name != "Repository")
                        .ToDictionary(p => p.Name, p => p.GetValue(report));
                    Emit(new { type = "recovery", report = fields });
                    if (report.Repository is null || report.Status.ToString() != "Succeeded") { return 2; }
                    using (var repository = report.Repository) {
                        Emit(new { type = "recovered", graph = Inspect(repository) });
                        if (mode == "continue") {
                            using var branch = repository.Checkout("main");
                            branch.CommitState(ProbeModel.Graph<ProbeModel.OldNode>(30));
                            branch.CommitEvent(ProbeModel.Graph<ProbeModel.OldNode>(31));
                            Emit(new { type = "continued", acknowledged = true, graph = Inspect(repository) });
                        }
                    }
                    break;
                case "inspect":
                    using (var repository = Repository.OpenReadOnlyExisting(root, ProbeModel.Models())) {
                        Emit(new { type = "inspect", readOnly = repository.IsReadOnly, graph = Inspect(repository) });
                    }
                    break;
                case "continue-strict":
                    using (var repository = Repository.OpenExisting(root, ProbeModel.Models())) {
                        Emit(new { type = "recovered", graph = Inspect(repository) });
                        using var branch = repository.Checkout("main");
                        branch.CommitState(ProbeModel.Graph<ProbeModel.OldNode>(30));
                        branch.CommitEvent(ProbeModel.Graph<ProbeModel.OldNode>(31));
                        Emit(new { type = "continued", acknowledged = true, graph = Inspect(repository) });
                    }
                    break;
                case "cli-report-kill":
                case "cli-report-fail":
                    int exit = MaintenanceCli.Run(["rescue", root, "--apply", "--json"],
                        new ReportBoundaryWriter(root, mode == "cli-report-kill"), Console.Error);
                    Emit(new { type = "cli-exit", exit });
                    return exit;
                case "inventory":
                    var qualification = RepositoryRecoveryQualification.Qualify(root);
                    var files = qualification.Files.Select(file => {
                        byte[] bytes = File.ReadAllBytes(Path.Combine(root, file.RelativePath));
                        int eof = checked((int)file.VisibleEof);
                        var boundaries = new List<long>();
                        for (int offset = 4; offset < eof;) {
                            boundaries.Add(offset);
                            offset = checked(offset + (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4)) + 4);
                            if (offset > eof) { throw new InvalidDataException("Frame boundary outside complete prefix."); }
                        }
                        return new { path = file.RelativePath, length = bytes.LongLength, completeLength = file.VisibleEof,
                            sha256 = Convert.ToHexString(SHA256.HashData(bytes.AsSpan(0, eof))), addresses = boundaries };
                    }).ToArray();
                    Emit(new { type = "inventory", status = qualification.Status.ToString(), files });
                    break;
                default: throw new ArgumentException("Unknown mode.");
            }
            return 0;
        } catch (Exception ex) {
            Emit(new { type = "error", error = ex.ToString() });
            return 1;
        }
    }

    private sealed class ReportBoundaryWriter(string root, bool kill) : TextWriter {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
        public override void WriteLine(string? value) => BeforeWrite();
        public override void Write(string? value) => BeforeWrite();
        private void BeforeWrite() {
            // The maintenance report must have no live repository lock or RBF handles.
            var paths = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
            foreach (string path in paths) {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            if (kill) { Ready("cli:BeforeReport", new { resourcesReleased = true, exclusiveFilesChecked = paths.Length }); }
            Emit(new { type = "output-failure", resourcesReleased = true, exclusiveFilesChecked = paths.Length });
            throw new IOException("Probe report writer deliberately failed before delivering report bytes.");
        }
    }

    private static object Inspect(Repository repository) {
        var head = repository.GetHead("main");
        var checkpoint = repository.ReadCheckpoint(head);
        object selected, previous;
        if (checkpoint is EventCheckpoint e) {
            selected = ProbeModel.Check(e.Event); previous = ProbeModel.Check(e.PreviousState);
        } else {
            var s = (StateCheckpoint)checkpoint;
            selected = ProbeModel.Check(s.State); previous = ProbeModel.Check(s.PreviousEvent);
        }
        var events = repository.EnumerateEvents(head).Select(address => {
            var view = (EventCheckpoint)repository.ReadCheckpoint(address);
            return new { address = address.Address.ToString(), revision = address.RevisionAddress.ToString(),
                graph = ProbeModel.Check(view.Event), previousState = ProbeModel.Check(view.PreviousState) };
        }).ToArray();
        return new { head = head.Address.ToString(), kind = head.Kind.ToString(), revision = head.RevisionAddress.ToString(),
            selected, previous, events };
    }

    private static void InstallWriteHooks(string root, string point) {
        string[] parts = point.Split(':');
        if (parts.Length != 2 || parts[0] is not ("partial" or "written" or "flushed")) { return; }
        bool Match(string? path) {
            if (path is null) { return false; }
            string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            return parts[1] switch {
                "schema" => relative == "schemas.rbf",
                "state" => relative.StartsWith("state/") && relative.EndsWith(".rbf"),
                "event" => relative.StartsWith("journal/events/") && relative.EndsWith(".rbf"),
                "ref" => relative.StartsWith("journal/refs/objects/") && relative.EndsWith(".rbf"),
                _ => false
            };
        }
        RbfWriteInstrumentation.Current = new RbfWriteHooks {
            BeforeWrite = request => parts[0] == "partial" && Match(request.Path) ? 2 : request.RequestedBytes,
            AfterWrite = observation => {
                if (parts[0] is "partial" or "written" && Match(observation.Path)) {
                    Ready(point, new { path = observation.Path, observation.Offset, observation.RequestedBytes, observation.WrittenBytes });
                }
            },
            AfterFlush = request => { if (parts[0] == "flushed" && Match(request.Path)) { Ready(point, new { request.Path }); } }
        };
    }
}
