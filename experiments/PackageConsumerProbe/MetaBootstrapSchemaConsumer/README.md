# Internal meta Schema package consumer

This DB-087 P1b witness consumes freshly packed Runtime and Persistence packages. An isolated console uses the existing `Atelia.DurableGraph.Persistence.Tests` friend name to access the internal experiment; it grants no public API or migration qualification.

The packaged generator builds the host's Base/Point/Root model. Packaged employee DTOs/bodies persist and recover its full base, inline and Nullable Schema closure, with a `List<Point?>`. Four separate processes seed, read meta before registering business models, continue with real Root/List Delta, and verify old/new State content. The protocol matches `tests/DurableGraph.Tests/MetaBootstrapSchemaClosureGeneratorTests.cs`.

Run from the repository root after the production slice is frozen and source checks pass:

```powershell
./experiments/PackageConsumerProbe/Run-MetaBootstrapSchemaProbe.ps1 -StoragePackageSource <verified-six-package-feed>
# Or consume an existing ten-package feed without repacking:
./experiments/PackageConsumerProbe/Run-MetaBootstrapSchemaProbe.ps1 -StoragePackageSource <verified-six-package-feed> -PackageSource <ten-package-feed> -Version <G>
```

The runner copies the consumer to a fresh external work directory, isolates MSBuild/central-package boundaries and NuGet cache/config, checks actual package versions and loaded DLL hashes, and preserves source hashes, assets, process evidence and `result.json`. `-WorkDirectory` must name a nonexistent directory. No ProjectReference, manual analyzer, workspace source inclusion, fault injection or network publishing is used.
