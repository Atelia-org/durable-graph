# DB-083：Repository 与 Checkpoint 用户故事

> 结论与适用性：目标语义已选；A/B/C 与 DG tag 已交付，材料是产品语义依据，原 API 草图不替代当前源码。
> 本次整理来源 Git `8ef9fca`；原主入口 `docs/design-branches/0083-repository-checkpoint-api-user-stories.md`。

问题是从实际应用故事推导公共入口、Event-first、nearest PreviousX 和分支 Fork，而非沿用旧交替 API。

- [主设计与语义裁决](0083-repository-checkpoint-api-user-stories.md)。
- 故事附件：[LLM tool loop](llm-tool-loop.md)、[rollout](rollout.md)、[DramaBoard](dramaboard.md)。
- [公共 API 三阶段验收 DB-078](../0078-editable-checkpoint-fork-slice/README.md)；[跨重开 tag DB-084](../0084-eventjournal-immutable-tags-slice/README.md)。
- 内部恢复与单 State 准备材料见 [DB-079](../0079-shared-graph-restoration-core-slice/README.md)／[DB-080](../0080-prepared-checkpoint-reuse-slice/README.md)；叶复用见 [DB-082](../0082-prepared-immutable-leaf-reuse-slice/README.md)。

地址限同一次打开；tag 名可跨重开解析。优化完成、候选实现及默认启用资格分别解释。
当前能力与焦点见 [PROJECT-STATE](../../../src/PROJECT-STATE.md)；长期选择和剩余事项分别见[目标设计](../../DurableGraph-target-design-v0.md)与[路线图](../../DurableGraph-research-roadmap.md)。
本入口只定位本问题的合同与有限证据，不接管产品排期；旧正文中的计划和数字保留当时含义。
