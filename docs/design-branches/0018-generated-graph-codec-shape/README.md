# DB-018：统一引用身份、TypeCodec 与 Serializer 形状

> 结论：保留当时已接受的引用身份原则、候选形状与独立泛型机制见证；不将整份 Open 草稿视为已实现合同。
> 适用范围：直接领域 Read/Write 示例已由 DB-022 的 DTO 路线取代；泛型技术后继见 DB-038。
> 来源：迁移前主文与两份附件位于 `docs/design-branches/`，来源修订 `8ef9fca`（2026-10-14）；正文保原时期语义。

本问题讨论统一引用身份和生成代码如何组织。按所需问题选择材料，不需要顺序通读。

## 阅读顺序

1. [历史主草稿](0018-generated-graph-codec-shape.md)：已接受原则、混合候选与早期代码示意；其“当前”不是今日能力说明。
2. [运行时泛型 binding 见证](0018-runtime-binding-witness.md)：验证运行时闭合与 typed ref；不证明通用 codec registry 已实现。
3. [泛型 DTO 技术备忘](0018-generic-dto-binding-followup.md)：记录被认可的技术方向及表示映射素材；不作为下一施工单。

DTO 接续见 [DB-022](../0022-versioned-state-dto-capture/README.md)，泛型完整方案接续见 [DB-038](../0038-generic-schema-state-and-binding-design/README.md)。
当前能力从 [PROJECT-STATE](../../../src/PROJECT-STATE.md) 续接；延续原则和剩余问题分别见[目标设计](../../DurableGraph-target-design-v0.md)与[路线图](../../DurableGraph-research-roadmap.md)。
