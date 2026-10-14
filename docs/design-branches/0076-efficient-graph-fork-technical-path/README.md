# DB-076：高效领域图 fork 技术路径

> 结论与适用性：路线已校准；077→078-A/B/C→079→080 的交付、081 的否定停点与082 默认关闭分别查各片结果。
> 本次整理来源 Git `8ef9fca`；原主入口 `docs/design-branches/0076-efficient-graph-fork-technical-path.md`。

问题是怎样保持 named Fork 的历史选点、可编辑图隔离和发布纪律，再依据实测选择准备与物化优化。

- [技术路径与辩证裁决](0076-efficient-graph-fork-technical-path.md)：保原分片依赖及论证，不据其中“下一片”恢复旧排期。
- [公共用户故事 DB-083](../0083-checkpoint-api-user-stories/README.md)；[API 三阶段 DB-078](../0078-editable-checkpoint-fork-slice/README.md)。
- [共用恢复核 DB-079](../0079-shared-graph-restoration-core-slice/README.md)；[单 State 准备槽 DB-080](../0080-prepared-checkpoint-reuse-slice/README.md)。
- [热准入否定 DB-081](../0081-hot-commit-restoration-material-slice/README.md)；[叶复用成本裁决 DB-082](../0082-prepared-immutable-leaf-reuse-slice/README.md)。

本入口不复制分片路线或续写 fork backlog；各片原实验条件与实施事实保在上述证据。
当前能力与焦点见 [PROJECT-STATE](../../../src/PROJECT-STATE.md)；长期选择和剩余事项分别见[目标设计](../../DurableGraph-target-design-v0.md)与[路线图](../../DurableGraph-research-roadmap.md)。
本入口只定位本问题的合同与有限证据，不接管产品排期；旧正文中的计划和数字保留当时含义。
