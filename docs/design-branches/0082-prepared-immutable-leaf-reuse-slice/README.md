# DB-082：单图准备材料 ImmutableLeaf 复用

> 结论与适用性：候选已实现，默认关闭；同 State 只读叶表经行为验收，A/B 未证明稳定完整 Fork 总成本收益。
> 本次整理来源 Git `8ef9fca`；原主入口 `docs/design-branches/0082-prepared-immutable-leaf-reuse-slice.md`。

问题是 DB-080 已复用准备材料后，省去叶 Allocate/Hydrate 能否抵消复用表、身份登记与驻留成本。

- [候选实验合同与启用判据](0082-prepared-immutable-leaf-reuse-slice.md)。
- [实际实现、回归与默认关闭裁决](0082-prepared-immutable-leaf-reuse-implementation.md)。
- [原始 A/B 测量 CSV](0082-prepared-immutable-leaf-reuse-measurements.csv)：与实施记录的输入、重跑说明和限制一起解释。
- [硬前置 DB-080](../0080-prepared-checkpoint-reuse-slice/README.md)；DB-081 热准入不是本片正确性前置。

内部实验开关保留重跑；不增加公共 options，不宣称默认 Fork 已共享叶实例。
当前能力与焦点见 [PROJECT-STATE](../../../src/PROJECT-STATE.md)；长期选择和剩余事项分别见[目标设计](../../DurableGraph-target-design-v0.md)与[路线图](../../DurableGraph-research-roadmap.md)。
本入口只定位本问题的合同与有限证据，不接管产品排期；旧正文中的计划和数字保留当时含义。
