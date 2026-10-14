# DB-084：不可变 tag 与跨重开定位

> 结论与适用性：旧 EventJournal 栈的消费接入已实施；底座由 DB-086 替换，当前 tag 由 VS 承担，CheckpointAddress 仍非外部可序列化地址。
> 本次整理来源 Git `8ef9fca`；原主入口 `docs/design-branches/0084-eventjournal-immutable-tags-slice.md`。

问题是固定名字能否跨重开定位原检查点，且后续分支提交或移动不改变名字绑定。

- [原上游消费要求与接入合同](0084-eventjournal-immutable-tags-slice.md)。
- [DG 接入、准确旧 pin 与验收](0084-durablegraph-tags-implementation.md)：保旧栈严格打开和发布的历史证据。
- [底座替换 DB-086](../0086-new-storage-refactoring-plan/README.md)；[公共语义 DB-083](../0083-checkpoint-api-user-stories/README.md)。

不把旧公开包来源或严格打开证据当作新 VS 底座资格；新包与当前行为按实际来源查证。
当前能力与焦点见 [PROJECT-STATE](../../../src/PROJECT-STATE.md)；长期选择和剩余事项分别见[目标设计](../../DurableGraph-target-design-v0.md)与[路线图](../../DurableGraph-research-roadmap.md)。
本入口只定位本问题的合同与有限证据，不接管产品排期；旧正文中的计划和数字保留当时含义。
