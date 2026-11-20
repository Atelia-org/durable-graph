using Atelia.DurableGraph;
using RegisteredNodes;

if (args is not [var stage, var path, var receipt] || stage is not ("upgrade" or "verify")) {
    throw new ArgumentException("Expected upgrade|verify, store and receipt paths.");
}
using var repo = stage == "upgrade" ? Repository.OpenExisting(path, Probe.Models()) : Repository.OpenReadOnlyExisting(path, Probe.Models());
Book Read(string tag) => (Book)repo.LoadState(repo.ResolveTag(tag));
var left = Read("second");
var right = Read("second");
Probe.Check(left, 21);
Probe.Check(right, 21);
Probe.Require(left != right && left.Home != right.Home && left.Home.Next != right.Home.Next && left.Order != right.Order &&
    left.Named != right.Named && left.Home.Links != right.Home.Links && left.Order[1] != right.Order[1], "Independent reads share mutable instances.");
left.Home.Rating = 99;
left.Order.Clear();
left.Named.Clear();
left.Home.Links[0] = null;
Probe.Check(right, 21);
if (stage == "upgrade") {
    using var working = repo.OpenWorkingCopy(repo.ListBranches().Single().Id);
    var current = (Book)working.State!;
    Probe.Check(current, 21);
    var a = current.Home;
    var b = current.Named["other"];
    a.Rating = 34;
    a.Next = b;
    b.Next = b;
    a.Links[1] = b;
    current.Order[2] = current.Named["home"] = b;
    repo.CreateTag("third", working.CommitState().Id);
}
Probe.Receipt(receipt, stage, Probe.Check(Read("first"), 10), Probe.Check(Read("second"), 21), Probe.Check(Read("third"), 34));
