# DB-089：checkout 环境寿命与 factory 查询

> 结论与适用性：A/B/C 有限范围已完成；protected raw 查询、准确核心解析与稳定 checkout 经源码/新 G 普通包验收，不授 callback 沙箱或任意 reader 恢复证明。
> 本次整理来源 Git `8ef9fca`；原主入口 `docs/design-branches/0089-checkout-environment-and-factory-query-refactoring.md`。

问题是持续 checkout 能否保稳定执行环境，并收窄普通 factory 对无关 Schema 查询的误用入口。

- [详细合同、接口范围与 A/B/C 结果](0089-checkout-environment-and-factory-query-refactoring.md)：§11 区分源码、编译元数据和普通包资格。
- [Schema 开发定稿前驱 DB-088](../0088-development-schema-workflow/README.md)。
- [热恢复资格否定 DB-081](../0081-hot-commit-restoration-material-slice/README.md)：本片保反例，不据 protected 查询消除其证明缺口。

重新 Checkout 与重新 Open 作用不同；准确目录解析仍可拒绝，原自定义 reader 的完整依赖边界不变。
当前能力与焦点见 [PROJECT-STATE](../../../src/PROJECT-STATE.md)；长期选择和剩余事项分别见[目标设计](../../DurableGraph-target-design-v0.md)与[路线图](../../DurableGraph-research-roadmap.md)。
本入口只定位本问题的合同与有限证据，不接管产品排期；旧正文中的计划和数字保留当时含义。
