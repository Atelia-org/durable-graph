# DB-088：开发 Schema 草稿 fork 与定稿

> 2026-10-19 后继：[DB-096](../0096-history-and-head-model/private-removal.md)已删除 Private/ToShared，开发试验改用关闭后的完整 Repository 副本。以下固定 baseline→private 草稿 fork→定稿流程及验收只保历史范围，不作为当前使用指引。

> 结论与适用性：固定 V1→独立 V2 草稿 fork→定稿 V2 的有限故事已验收；生产特例退出及三条新 G 普通包通过，不预建主/次版本或 release 平台。
> 本次整理来源 Git `8ef9fca`；原主入口 `docs/design-branches/0088-development-schema-workflow.md`。

问题是从同一已发布 baseline 建多个保身份的草稿，再只接受一个定稿，能否复用正常分支、private 保存与 Upgrade。

- [用户决定、开发合同与限定结果](0088-development-schema-workflow.md)：§7 保源码/包分层证据及不覆盖范围。
- [前驱自举与完整 private 合同 DB-087](../0087-meta-bootstrap-refactoring-route/README.md)。
- [后继 checkout 环境与 factory 查询 DB-089](../0089-checkout-environment-and-factory-query-refactoring/README.md)。

同版本异形只在不同 fork 从尚未接受该 key 的 baseline 独立新增；完整 ToShared 不是只发布最后版本的过滤器。
当前能力与焦点见 [PROJECT-STATE](../../../src/PROJECT-STATE.md)；长期选择和剩余事项分别见[目标设计](../../DurableGraph-target-design-v0.md)与[路线图](../../DurableGraph-research-roadmap.md)。
本入口只定位本问题的合同与有限证据，不接管产品排期；旧正文中的计划和数字保留当时含义。
