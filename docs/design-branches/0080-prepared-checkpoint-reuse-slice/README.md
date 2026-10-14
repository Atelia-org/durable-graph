# DB-080：最近 State 准备材料复用

> 结论与适用性：已实施并独立验收，单槽默认启用；每次完整 Schema 证书复核、mutable 独立，Event-only 绕槽，热提交未接入。
> 本次整理来源 Git `8ef9fca`；原主入口 `docs/design-branches/0080-prepared-checkpoint-reuse-slice.md`。

问题是对同一持久 State 重复 Checkout/Fork，能否省掉已证明的准备工作并保留完整恢复与保存来源。

- [单槽、证书与每次验证合同](0080-prepared-checkpoint-reuse-slice.md)。
- [实施、测量与限定停点](0080-prepared-checkpoint-reuse-implementation.md)。
- [热提交资格 DB-081](../0081-hot-commit-restoration-material-slice/README.md)；[同材料叶候选 DB-082](../0082-prepared-immutable-leaf-reuse-slice/README.md)。

省去恢复准备不等于省去独立物化；本片测量不外推所有读取或热提交。
当前能力与焦点见 [PROJECT-STATE](../../../src/PROJECT-STATE.md)；长期选择和剩余事项分别见[目标设计](../../DurableGraph-target-design-v0.md)与[路线图](../../DurableGraph-research-roadmap.md)。
本入口只定位本问题的合同与有限证据，不接管产品排期；旧正文中的计划和数字保留当时含义。
