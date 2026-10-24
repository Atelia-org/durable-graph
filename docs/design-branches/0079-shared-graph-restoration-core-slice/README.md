# DB-079：共用图恢复准备与物化核心

> 2026-10-24 后继：[DB-098](../0098-readpair-retirement/README.md)退役 ReadPair、跨图共享规划与专用比较接线，旧实现保留 Git tag。本文相关共享断言、测试与验收仅解释来源版本；独立图/Checkpoint 的完整准备、两阶段物化、工厂实例守卫及 State 增量合同继续有效。

> 结论与适用性：已实施并独立验收；统一完整单图材料和两阶段物化，独立图/Pair 规划仍分开，不含跨操作驻留。
> 本次整理来源 Git `8ef9fca`；原主入口 `docs/design-branches/0079-shared-graph-restoration-core-slice.md`。

问题是所有读图用途能否使用同一恢复核心，同时保持只读交付和可编辑身份导入的区别。

- [内部恢复合同与验收矩阵](0079-shared-graph-restoration-core-slice.md)。
- [实施接缝与实际验收](0079-shared-graph-restoration-core-implementation.md)。
- [后继单 State 驻留 DB-080](../0080-prepared-checkpoint-reuse-slice/README.md)；[公共语义 DB-083](../0083-checkpoint-api-user-stories/README.md)。

本片只重组恢复职责；完成核心抽取不证明加速，也不授跨操作领域实例共享。
当前能力与焦点见 [PROJECT-STATE](../../../src/PROJECT-STATE.md)；长期选择和剩余事项分别见[目标设计](../../DurableGraph-target-design-v0.md)与[路线图](../../DurableGraph-research-roadmap.md)。
本入口只定位本问题的合同与有限证据，不接管产品排期；旧正文中的计划和数字保留当时含义。
