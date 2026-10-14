# DB-078：分支工作副本与 Checkpoint Fork

> 结论与适用性：A/B/C 已实施并验收；自由 E/S、独立 Checkpoint 查询与每 branch 单工作副本/named Fork 以当片证据限定。
> 本次整理来源 Git `8ef9fca`；原主入口 `docs/design-branches/0078-editable-checkpoint-fork-slice.md`。

问题是怎样分阶段交付非泛型公共历史与可编辑 fork，同时保持历史来源、隔离及失败交付边界。

- [三阶段合同](0078-editable-checkpoint-fork-slice.md)。
- [A：非泛型入口与自由历史](0078-a-repository-free-history-implementation.md)。
- [B：独立 Checkpoint 与固定查询](0078-b-checkpoint-history-query-implementation.md)。
- [C：每分支工作副本与 named Fork](0078-c-branch-checkout-fork-implementation.md)。
- [077/078 共同历史评审](../0077-repository-model-environment-slice/0077-0078-api-dialectical-review.md)：旧结论按前言限定，不据此改写 A/B/C 资格。
- [用户故事 DB-083](../0083-checkpoint-api-user-stories/README.md)；后续 tag 与内部复用查 DB-084、079–082。

当前能力与焦点见 [PROJECT-STATE](../../../src/PROJECT-STATE.md)；长期选择和剩余事项分别见[目标设计](../../DurableGraph-target-design-v0.md)与[路线图](../../DurableGraph-research-roadmap.md)。
本入口只定位本问题的合同与有限证据，不接管产品排期；旧正文中的计划和数字保留当时含义。
