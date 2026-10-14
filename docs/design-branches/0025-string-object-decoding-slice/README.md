# DB-025：string 解码、引用槽校验与 Empty 规范化

> 结论：本片 string 解码表与各版 DTO 引用校验已验收；空字符串在 Capture/读取两端统一为 `string.Empty`。
> 适用范围：当片未完成领域 Restore；独立空串分配附件是未采用机制的历史证据。
> 来源：迁移前主文与附件位于 `docs/design-branches/`，来源修订 `8ef9fca`（2026-10-14）；正文与运行见证保原时期语义。

本问题回答冻结的 owner DTO 与 string 内容分别经过字节往返后，能否恢复引用关系并拒绝无效引用。

## 阅读顺序

1. [string 对象解码与引用校验主文](0025-string-object-decoding-slice.md)：合同、Empty 裁决及 §7 当片实施验收。
2. [独立空字符串分配见证](0025-empty-string-allocation-witness.md)：本机机制证据和后续核验修正；产品未采用其独立分配机制。

前置引用 Capture 见 [DB-024](../0024-reference-capture-and-reusable-object-ids/README.md)；当时内容存储接续见 [DB-026（Git 历史）](../0091-retired-material-cleanup/README.md#git-history "Git 6dbaf0e:docs/design-branches/0026-raw-base-object-content-slice/README.md")。
当前能力从 [PROJECT-STATE](../../../src/PROJECT-STATE.md) 续接，剩余问题在[路线图](../../DurableGraph-research-roadmap.md)维护。
