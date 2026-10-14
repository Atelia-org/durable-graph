# DB-086：新存储栈替换与正常恢复

> 结论与适用性：R0–R5 当片已验收；单根／SCB1／合帧由 DB-087 替代，底座正常恢复、必要验证及寿命约束继续有效。
> 本次整理来源 Git `8ef9fca`；原主入口 `docs/design-branches/0086-new-storage-refactoring-plan.md`。

问题是删除旧存储栈、DG cursor 和全库打开屏障后，能否在新建库保持真实生成模型的保存、冷开、升级、选点与故障结果。

- [当片已选重构合同](0086-new-storage-refactoring-plan.md)。
- [三角色简化评审与保留理由](0086-new-storage-simplification-review.md)：设计裁决，非运行验收。
- [R0–R5 实施与有限验收](0086-new-storage-implementation.md)：只验新库，不授旧存档迁移或公开发布资格。
- [格式与共同保存后继 DB-087](../0087-meta-bootstrap-refactoring-route/README.md)。

不得为保留旧严格打开合同恢复 startup audit 或全库确认；完整持久帧 append-only 及必要内容、发布、fault、reentry 检查仍成立。
当前能力与焦点见 [PROJECT-STATE](../../../src/PROJECT-STATE.md)；长期选择和剩余事项分别见[目标设计](../../DurableGraph-target-design-v0.md)与[路线图](../../DurableGraph-research-roadmap.md)。
本入口只定位本问题的合同与有限证据，不接管产品排期；旧正文中的计划和数字保留当时含义。
