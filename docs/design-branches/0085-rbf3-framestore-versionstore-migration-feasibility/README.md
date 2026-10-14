# DB-085：RBF3 Binary FrameStore VersionStore 可行性

> 结论与适用性：可行性与四项上游 API 复核完成；历史前置门由 DB-086 裁决接续，不再要求严格打开/全库确认等价或旧数据迁移。
> 本次整理来源 Git `8ef9fca`；原主入口 `docs/design-branches/0085-rbf3-framestore-versionstore-migration-feasibility.md`。

问题是替换存储接缝是否可行，需要哪些真实前置证据，而非把新上游能力直接算作产品迁移。

- [初次可行性调研](0085-rbf3-framestore-versionstore-migration-feasibility.md)：保首次 e989ba6 输入与当时判断。
- [隔离机制与源码验证证据](0085-storage-migration-probe-evidence.md)：Linux/WSL 见证，非 DG 迁移完成。
- [四项上游 API 增量复核](0085-upstream-api-refinement-review.md)：保 daf7e1c 等准确来源与复核范围。
- [后继实施 DB-086](../0086-new-storage-refactoring-plan/README.md)：用户取消旧前置要求后的实际合同与当片验收。

首次“Feasible with prerequisites”保历史含义；本入口不复活其 T0–T7 施工队列。
当前能力与焦点见 [PROJECT-STATE](../../../src/PROJECT-STATE.md)；长期选择和剩余事项分别见[目标设计](../../DurableGraph-target-design-v0.md)与[路线图](../../DurableGraph-research-roadmap.md)。
本入口只定位本问题的合同与有限证据，不接管产品排期；旧正文中的计划和数字保留当时含义。
