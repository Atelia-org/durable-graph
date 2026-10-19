# DB-096：最终交付验收

2026-10-19，**未完成、未合入**。本表用于对照完整目标收口，不把已通过的局部分片当作最终交付。当前工作停点只维护在 [PROJECT-STATE](PROJECT-STATE.md)，此处集中保存最终验收来源、门禁与合入证据。

## 要交付的完整能力

| 要求 | 当前实现/证据入口 | 最终复核要求 |
|---|---|---|
| Branch/Commit/WorkingCopy 替代旧公开 API | [整体切换](branch-cutover-plan.md)、Repository/WorkingCopy/薄 ID | 普通包API负例准确拒绝旧入口；源码无旧public类型/适配层，保当前便利读取与Checkpoint |
| 多分支原子提交、统一 Catalog | [组提交验收](group-commit-integration.md) | 当前G的Group消费者与完整源码反例，不能只用单业务B/S Kill外推 |
| 多根/子树 | [树查询验收](branch-tree-queries.md) | 当前G的空fork冷浏览，正式成员/真实来源/整森林错误边界 |
| 固定历史分页 | [分页验收](history-pagination.md) | 当前G公开调用与预算/取消/清理源码反例，明确工作预算限度 |
| 创建/登记失败与重开 | [创建故障验收](creation-failure-acceptance.md) | 补齐11项和既有Unknown冷开；真实物理/D登记两层结果及完整帧保持 |
| 旧消费者和文档迁移 | [原迁移验收](package-cutover-validation.md)、README/术语表/target/产品工作集 | 当前文档与API一致，历史证据保原G/S含义，不能借旧绿色结果资格化最终G |
| 正常恢复与必要Kill见证 | [Crash迁移](process-crash-migration.md) | 保原6＋3窗口证据与实际产品差异的适用范围；不要求掉电或新平台 |
| 正式合入 | 本文末节 | 主分支包含所有验收产物，工作树状态明确，不只停留在隔离分支 |

附件BLOB、ID durable字段、rewind、merge、GC、多writer、跨库事务与规模性能专项保明确后置，不加入本轮完成条件。

## 最终源码与普通包门禁

需要记录最终产品源码commit、测试commit、S版本/来源、G版本和feed provenance；如果只改测试/文档，明确产品字节与前片的关系，不混淆不同G。当前S为 `0.1.3-dev.20261019.g70b406c` / `70b406cd8d1117733eee81a33e7f978399aa98b2`。

最终根solution build及四个测试项目结果待填。source mode和普通PackageReference资格分别记录；源码通过不替代下面的活动入口。

当前 [PackageConsumerProbe](../../../experiments/PackageConsumerProbe/README.md) 共12个活动runner（原11个加Group）：

| 门禁 | 最终结果 |
|---|---|
| Run-SplitRepositoryProbe | 待运行；可由此受控打包并提供最终普通feed/provenance |
| Run-GroupCommitProbe | 待运行；覆盖组提交、树与分页 |
| Run-PrivateContainerProbe | 待运行；路径保历史名称，内容为Shared容器升级 |
| Run-DevelopmentSchemaProbe | 待运行；完整独立库副本及准确DG0015 |
| Run-RepositoryApiProbe | 待运行；逐项精确负例后恢复正例重新构建 |
| Run-ExplicitStorageClassificationProbe | 待运行；准确DG0024及正例恢复 |
| Run-EventHistoryRecoveryProbe | 待运行；公开XML与冷热应用恢复 |
| Run-TagProbe | 待运行；准确书签、历史fork与冷继续 |
| Run-ImmutableLeafProbe | 待运行；当前生成器资格与sibling反例 |
| Run-MetaBootstrapSchemaProbe | 待运行；明确内部friend资格边界 |
| Run-ReadmeQuickStartProbe | 待运行；候选导出/搬迁/空缓存，使用独立唯一G，原文命令不由helper改写 |
| Run-Probe | 待运行；单Runtime、原负例与独立生成输出 |

所有runner串行执行，独立新运行目录/缓存；可复用准确不可变feed，不覆盖已存在G。README导出可生成单独G，必须保同一最终产品来源及独立资格，不把两批产物混为一个版本。每次实际执行后核回执、断言覆盖与已加载包来源；已有全部门禁不因此改造为新测试框架。

## 合入前审计

- 核对API草图、用户故事和施工计划的显式能力/边界均有当前源码及对应实际测试证据。
- 检查最终集成diff、旧public API搜索、修改文档链接/锚点；保留历史标识符检索和版本证据，不做全库词语替换。
- 检查待办只剩明确后置项；源/包证据能追溯准确输入。回归失败及修复前记录保其范围。
- 合入前确认主分支与隔离分支关系、无无关工作树变更，采用可审阅的普通Git合入；不推送或公开发包来替代本地交付。
- 合入后记录commit、工作树状态、最终产品与验收版本关系；只有整表闭合才标记0096完整目标完成。

当前没有最终合入记录。下一步以工作集为准。
