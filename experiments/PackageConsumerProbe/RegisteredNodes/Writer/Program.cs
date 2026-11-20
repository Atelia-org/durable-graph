using Atelia.DurableGraph;
using RegisteredNodes;
using RegisteredNodes.Pages;

if (args is ["verify-old", var existing, var oldReceipt]) {
    using var old = Repository.OpenReadOnlyExisting(existing, Probe.Models());
    var first = (Book)old.LoadState(old.ResolveTag("first"));
    var second = (Book)old.LoadState(old.ResolveTag("second"));
    Probe.Require(first.Home.RetiredLabel == "older" && second.Home.RetiredLabel == "new historical target", "V1 historical edges changed.");
    Probe.Receipt(oldReceipt, "verify-old", Probe.Check(first, 10), Probe.Check(second, 21));
    return;
}
if (args is not [var path, var receipt]) { throw new ArgumentException("Expected store and receipt paths."); }
var a = new Page { Rating = 10, RetiredLabel = "older" };
var b = new Page { Rating = 20 };
var book = new Book { Home = a, Order = [a, b, a], Named = new() { ["home"] = a, ["other"] = b } };
a.Order = b.Order = book.Order;
a.Named = b.Named = book.Named;
a.Next = b;
b.Next = a;
a.Links = [a, null, b, b];

using var repo = Repository.CreateNew(path, Probe.Models());
var branch = repo.CreateRootBranchFromState("pages", book);
using var working = repo.OpenWorkingCopy(branch.Id);
repo.CreateTag("first", working.BaseCommitId);
var checkout = (Book)working.State!;
Probe.Check(checkout, 10);
checkout.Home.Rating = 21;
checkout.Home.Next = checkout.Home;
checkout.Home.RetiredLabel = "new historical target";
repo.CreateTag("second", working.CommitState().Id);
Probe.Receipt(receipt, "write", 10, Probe.Check(checkout, 21));
