# Inline struct package consumer

> 当前 G 门禁暂停：此 lane 仍依赖 `RevisionDecoder` 与 SCB1/raw Store 诊断；`Schemas/States` 已收为 internal，默认 owner 不授旧模式。后继迁移范围见 [DB-087 §36](../../../docs/design-branches/0087-meta-bootstrap-refactoring-route/0087-meta-bootstrap-refactoring-route.md#36-p1d-第七片旧typed恢复束退出)。下文保留匹配历史包版本的验收记录，不能作为当前门禁。

Run from the repository root:

```powershell
./experiments/PackageConsumerProbe/Run-InlineStructProbe.ps1
```

Pass both `-PackageSource` and `-Version` to reuse a matching package feed. The consumer
references the public DurableGraph and Persistence packages; generated code and model history
come from packaged build assets. Each run creates a fresh storage repository and isolated
history, cache and output directories beneath `obj/inline-struct-*`.

The four stages check nested readonly inline values, shared references and cycles; owner and
nominal-child upgrades; and retained exact DTOs after deleting the old inline CLR declarations.
Accepted `.dgschema` files remain unchanged across stages, with history counts **5 → 10 → 11 → 13**.
The last stage restores both a historical graph through V1→V2→V3 and the current head through V2→V3.

V1 deliberately keeps two existing unchanged values wide: the World score and its right Point
value are both 1,000,000,000. With the new fixed 12-byte prior address, this makes the owner Base
larger than its one-leaf nested Delta. It changes seed values without adding fields or altering
the accepted V1 Schema shape. The runner still requires an actual stored owner Delta and a
two-record Base/Delta chain; it does not change the product's representation policy.

The probe checks stored-exact decoding, typed cold reopen, domain instance retention, required
upgrade Base rewrites and absence of false changes after baseline installation. Old storage
formats are outside its scope; model-version history within the new repository remains covered.
