# DB-087：meta 自举与共享私有 Schema 视图

> 2026-10-24 后继：[DB-098](../0098-readpair-retirement/README.md)已退役 ReadPair 及跨图共享规划；本文 Pair 资格与共享测试只保历史意义。准确 reader 环境、独立 Checkpoint、State 准备槽及 owned 物理缓存继续有效。

> 2026-10-19 后继：[DB-096](../0096-history-and-head-model/private-removal.md)已删除 Private/ToShared，统一使用仓库 Catalog；本页及正文保留旧路线的实现与资格。meta 自举、准确历史 Schema、source/prior 与发布/fault 纪律继续适用。

> 2026-10-17 后继：[DB-096 HeadStore 接入](../0096-history-and-head-model/headstore-adaptation.md)采用三入口布局和 DG 书签目录；本文两根初始化及上游 bytes tag 的既有证据保留历史范围，不作为新布局合同。

> 原结论与适用性：有限 P1/P2 已完成；当时保留完整 private/ToShared，特例退出与当时的新普通包资格由 DB-088 接续。该双路线现已退出。
> 本次整理来源 Git `8ef9fca`；原主入口 `docs/design-branches/0087-meta-bootstrap-refactoring-route.md`。

问题是以一套共同对象版本保存核心与一个发布 owner 支撑 meta 自举及完整共享/私有视图。

打开/编辑会话的公开外观由 [DB-095](../0095-repository-open-intent/README.md) 接续：移除 Open nullable Schema 模式，由命名编辑入口选归属；准确来源、完整目录、共同保存与故障合同继续适用。旧正文的 Open 模式说明保其历史含义。

- [详细合同、有限停点与源码导航](0087-meta-bootstrap-refactoring-route.md)：准确环境、来源、共同保存及故障仍在此查。
- [2026-10-13 归档（Git 历史）](../0091-retired-material-cleanup/README.md#git-history "Git 6dbaf0e:docs/archive/2026-10-13/0087-meta-bootstrap-refactoring-route.md")：冻结源 ecc80683，保 P0/P1；不包含后来 P2 全部原合同。
- 原 P2 完整合同从 Git `d8c0ecb:docs/design-branches/0087-meta-bootstrap-refactoring-route.md` 的 §9 恢复。
- [开发流程/特例退出 DB-088](../0088-development-schema-workflow/README.md)；[环境寿命/查询收窄 DB-089](../0089-checkout-environment-and-factory-query-refactoring/README.md)。

原包与退旧证据按删除前范围解释；后继新包验收不回写冻结稿，ProcessCrashOnly、断电、包和性能资格分别判断。
当前能力与焦点见 [PROJECT-STATE](../../../src/PROJECT-STATE.md)；长期选择和剩余事项分别见[目标设计](../../DurableGraph-target-design-v0.md)与[路线图](../../DurableGraph-research-roadmap.md)。
本入口只定位本问题的合同与有限证据，不接管产品排期；旧正文中的计划和数字保留当时含义。
