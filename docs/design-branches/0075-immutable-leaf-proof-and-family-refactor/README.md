# DB-075：ImmutableLeaf 完整证明与 Family

> 结论与适用性：候选规则与生成最终结构核对已实施并修正复验；Family 合格非泛型叶可达 true，不实现缓存或 DeepImmutable。
> 本次整理来源 Git `8ef9fca`；原主入口 `docs/design-branches/0075-immutable-leaf-proof-and-family-refactor.md`。

问题是同轮其他生成器追加状态能否逃过分类，以及 Family 路由能否交付同一保守资格。

- [主设计、验收矩阵与机制裁决](0075-immutable-leaf-proof-and-family-refactor.md)：完整候选与最终核对合同。
- [施工约束、阶段记录与复核修正](0075-immutable-leaf-implementation-work-order.md)：用户 C1–C6 与实际证据。
- [前驱 DB-072](../0072-generator-immutable-leaf-classification-slice/README.md)：初次分类和后来被修复的限制。

旧模型程序集内已生成的 true 常量需重编译；单独升级 runtime 不使原证据自动成立。
当前能力与焦点见 [PROJECT-STATE](../../../src/PROJECT-STATE.md)；长期选择和剩余事项分别见[目标设计](../../DurableGraph-target-design-v0.md)与[路线图](../../DurableGraph-research-roadmap.md)。
本入口只定位本问题的合同与有限证据，不接管产品排期；旧正文中的计划和数字保留当时含义。
