# Internal meta Schema package consumer

This DB-087 P1b witness consumes freshly packed Runtime and Persistence packages. An isolated console uses the existing `Atelia.DurableGraph.Persistence.Tests` friend name to access the internal experiment; it grants no public API or migration qualification.

The packaged generator builds the host's Base/Point/Root model. Packaged employee DTOs/bodies persist and recover its full base, inline and Nullable Schema closure, with a `List<Point?>`. The host also contains `List<long?>` and `List<List<int>>`; every outer position shares one inner `List<int>`. Arrays add a 128-element `int?[]` and a 16×8 `Point?[,]`.

Four separate processes seed, read meta before registering business models, continue with seven real Delta records, and verify old/new State content. The three builtin-only List headers and the builtin Array header, including their actual Base/Delta bodies, are decoded with an empty model registry and no catalog. Complete business graph reading still uses its exact Schema root. Wide Int64 values, Nullable changes, the outer resize, every element and shared inner identity are checked. Both arrays retain their zero-based dimensions; sparse changes must preserve their IDs and exact priors. The user Point array uses the dynamic route and its exact descriptor ObjectId. The catalog remains exactly 3 Schemas and 5 descriptors; builtin-only Lists and the builtin Array must have no descriptor.

This combined host extends the original protocol in `tests/DurableGraph.Tests/MetaBootstrapSchemaClosureGeneratorTests.cs` with the List protocol in `MetaBootstrapBuiltinListGeneratorTests.cs` and the Array protocol in `MetaBootstrapArrayGeneratorTests.cs`. It is not identical to those standalone fixtures: the original Root/List changes remain, so all seven rows advance by Delta. Each run creates a new store and three independent model histories; the second build verifies those accepted history bytes without changing them. A receipt for an earlier host does not qualify this expanded model or the new wire.

Run from the repository root after the production slice is frozen and source checks pass:

```powershell
./experiments/PackageConsumerProbe/Run-MetaBootstrapSchemaProbe.ps1 -StoragePackageSource <verified-six-package-feed>
# Or consume an existing ten-package feed without repacking:
./experiments/PackageConsumerProbe/Run-MetaBootstrapSchemaProbe.ps1 -StoragePackageSource <verified-six-package-feed> -PackageSource <ten-package-feed> -Version <G>
```

The runner copies the consumer to a fresh external work directory, isolates MSBuild/central-package boundaries and NuGet cache/config, checks actual package versions and loaded DLL hashes, and preserves source hashes, assets, history hashes, process evidence and `result.json`. `-WorkDirectory` must name a nonexistent directory. Each lane is a normal process that closes all resources; this is not ProcessCrashOnly qualification. No ProjectReference, manual analyzer, workspace source inclusion, fault injection or network publishing is used.
