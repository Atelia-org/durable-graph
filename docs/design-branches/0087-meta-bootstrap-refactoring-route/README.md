# DB-087：meta 自举与共享私有 Schema 视图

> 结论与适用性：原有限 P1/P2 已完成；默认 meta 自举、完整 private/ToShared 保留，特例退出与新普通包资格由 DB-088 接续。
> 本次整理来源 Git `8ef9fca`；原主入口 `docs/design-branches/0087-meta-bootstrap-refactoring-route.md`。

问题是以一套共同对象版本保存核心与一个发布 owner 支撑 meta 自举及完整共享/私有视图。

- [详细合同、有限停点与源码导航](0087-meta-bootstrap-refactoring-route.md)：准确环境、来源、共同保存及故障仍在此查。
- [2026-10-13 归档](../../archive/2026-10-13/0087-meta-bootstrap-refactoring-route.md)：冻结源 ecc80683，保 P0/P1；不包含后来 P2 全部原合同。
- 原 P2 完整合同从 Git `d8c0ecb:docs/design-branches/0087-meta-bootstrap-refactoring-route.md` 的 §9 恢复。
- [开发流程/特例退出 DB-088](../0088-development-schema-workflow/README.md)；[环境寿命/查询收窄 DB-089](../0089-checkout-environment-and-factory-query-refactoring/README.md)。

原包与退旧证据按删除前范围解释；后继新包验收不回写冻结稿，ProcessCrashOnly、断电、包和性能资格分别判断。
当前能力与焦点见 [PROJECT-STATE](../../../src/PROJECT-STATE.md)；长期选择和剩余事项分别见[目标设计](../../DurableGraph-target-design-v0.md)与[路线图](../../DurableGraph-research-roadmap.md)。
本入口只定位本问题的合同与有限证据，不接管产品排期；旧正文中的计划和数字保留当时含义。
