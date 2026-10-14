# DB-081：热 State 提交恢复资格实验

> 结论与适用性：资格实验形成否定结论，热准入未实施；Capture/factory/旧证书不能证明当前 reader body 全部依赖，DB-080 冷路径保持。
> 本次整理来源 Git `8ef9fca`；原主入口 `docs/design-branches/0081-hot-commit-restoration-material-slice.md`。

问题是提交侧 owned DTO 在不读回 typed body、不预执行物化时，能否替代新 State 的完整冷恢复准备。

- [原实验合同与证明停点](0081-hot-commit-restoration-material-slice.md)：保重启要求，不作为已接线事实。
- [可执行反例、否定裁决与重启条件](0081-hot-commit-restoration-material-experiment.md)：优先阅读。
- [正常冷准备与单槽 DB-080](../0080-prepared-checkpoint-reuse-slice/README.md)。

后继 [DB-089](../0089-checkout-environment-and-factory-query-refactoring/README.md) 收窄 raw 查询 API，未证明任意 reader 的完整可恢复性，不能据此重开热准入。
当前能力与焦点见 [PROJECT-STATE](../../../src/PROJECT-STATE.md)；长期选择和剩余事项分别见[目标设计](../../DurableGraph-target-design-v0.md)与[路线图](../../DurableGraph-research-roadmap.md)。
本入口只定位本问题的合同与有限证据，不接管产品排期；旧正文中的计划和数字保留当时含义。
