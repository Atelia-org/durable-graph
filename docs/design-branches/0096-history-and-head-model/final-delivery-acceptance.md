# DB-096：最终交付验收

2026-10-19，**完整目标已验收并合入本地 master**。产品与测试来源为 `440bccfcb856758f53013697ca853ea6e7b4df85`；后续收尾提交仅维护文档。当前工作停点只维护在 [PROJECT-STATE](PROJECT-STATE.md)，此处集中保存最终验收来源、门禁与合入证据。

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

## 最终审计尾修

独立审计发现 WorkingCopy.StateRevisionAddress 仍为 public，与已接受的“公开定位使用StateSourceCommitId、物理图地址留在内部”合同冲突。已收为internal，内部与友元调用保持；源码和普通包API门禁均增加精确访问负例，正例继续使用StateSourceCommitId。没有增加兼容入口或新存储格式。主线程重建0警告/0错误，RepositoryPublicApiTests的17例通过，新负例实际为CS1061；最终新G的19项普通包负例均已通过；新增项准确命中 `CS1061`、`StateRevisionAddress`、第4行第165列。

同时修正根README早期“origin/feed未完成”提示、恢复消费者对退役模型资产的误称，以及DB-096早期续工队列。尾修已由以下最终版本验收，没有沿用审计前产品来源的绿色记录。

## 最终源码与普通包门禁

最终产品和测试来源均为 `440bccfcb856758f53013697ca853ea6e7b4df85`，打包时工作树干净，202项生产输入与该提交逐项匹配。

| 产物 | 准确版本/来源 |
|---|---|
| Storage S | `0.1.3-dev.20261019.g70b406c` / `70b406cd8d1117733eee81a33e7f978399aa98b2` |
| 主门禁 G | `0.0.0-public-history-e2e.20261019104632.5816` / DG `440bccf` |
| README 候选 G | `0.0.0-db096-final-readme.20261019110000.28648` / 同一 DG `440bccf` |
| 主 feed | `W:\TEMP\db096-final-4b25f2b0ba374839bbe38e9e268ea1d7\split\feed` |
| 可搬迁候选 | 同工作根下 `candidate`，含相对路径 NuGet.Config、QuickStart 和11个原包 |

根 `dotnet build DurableGraph.slnx` 的 Release Rebuild 为0警告、0错误。四个测试项目共 **2992/2992通过、0失败、0跳过**：Persistence 1098，Runtime/Generator 1725，Serialization 133，Storage 36。本次根回归是 **DG源码项目＋准确打包Storage S**（`UseStorageSources=false`）；此前显式Storage源码模式证据保其分片范围，不能把本次根回归称为DG普通包消费者。

当前 [PackageConsumerProbe](../../../experiments/PackageConsumerProbe/README.md) 共12个活动runner，全部通过；候选准备是额外一步，不计作第13个消费者：

| 门禁 | 最终结果与覆盖 |
|---|---|
| Run-SplitRepositoryProbe | 通过；打包来源与10包哈希、5个独立PID、升级/重写/冷继续，CS0122负例后恢复合法源码重建 |
| Run-GroupCommitProbe | 通过；真实组保存、冷读两个Schema、准确Tag、Event保State、根/子树和分页 |
| Run-PrivateContainerProbe | 通过；历史路径名称下的Shared容器升级故事 |
| Run-DevelopmentSchemaProbe | 通过；独立库副本与DG0015预期拒绝；拒绝日志另有CS0234，不宣称唯一诊断 |
| Run-RepositoryApiProbe | 通过；19项准确编译负例，恢复合法源码Rebuild/Verify后再运行正例 |
| Run-ExplicitStorageClassificationProbe | 通过；Default/Family准确DG0024，修正后Publish/Verify、独立写入/冷读 |
| Run-EventHistoryRecoveryProbe | 通过；公开XML及冷热应用恢复 |
| Run-TagProbe | 通过；准确书签、历史fork与冷继续，覆盖含Tag异常尾修的最终包 |
| Run-ImmutableLeafProbe | 通过；当前生成器资格与sibling反例 |
| Run-MetaBootstrapSchemaProbe | 通过；四独立PID、ABI5、9对象Delta与准确来源；限内部friend消费者 |
| Run-ReadmeQuickStartProbe | 通过；独立G候选导出、含空格路径搬迁、空cache、原文命令、纯State历史/旧点fork/冷续写、可选Event、Upgrade与Clean后Verify |
| Run-Probe | 通过；单Runtime、既有负例及独立生成输出 |

全部构建/runner串行执行，使用各自新目录/缓存和不可变feed。主线程检查真实回执、包与生产输入哈希；无结构化回执的Group/Recovery/Tag/Immutable/Runtime另外检查实际assets版本、PackageReference闭包与输出DLL等于feed内字节。独立审阅复核Split/Group、API/Development/Classification、Meta以及README的日志与来源。不同runner的证据粒度不同，不把输出DLL核验冒称每个进程均有加载回执。

Meta的SchemaVersion3回执没有Passed字段；临时汇总器首次因此停止，但子脚本已exit0、四lane及最终marker均通过。核实所有断言、包与进程证据后修正汇总判据，接续余下门禁，没有改产品、放宽探针断言或重跑成功项。

## 本机证据与重跑入口

- 汇总目录 `.artifacts/db096-final/`：`root-final-build.log`、`root-final-tests/*.trx`、`root-summary.json`、`package-status.json`、`package-*.log`、`final-audit.json`；较早的`root-tests/`为审计尾修前2991例，不作为最终证据。
- 普通包工作根 `W:\TEMP\db096-final-4b25f2b0ba374839bbe38e9e268ea1d7`：Split/Containers/Development/API/Classification/Meta/README的result、各命令日志和独立进程证据。其余runner工件路径见各`package-*.log`末尾Artifacts。
- 最终审计确认202项输入、10个主feed包、12runner及候选准备。README回执SHA256：`42299830EAF6EEBA7CD6AE9723A7AE050E823CA4F75B138F539CFB01F128AF6B`。
- 工件是本机运行证据，未加入源码管理；受版本管理的runner、精确版本与验收结论保在本文。重跑从[依赖指南](../../storage-dependency.md)准备准确S，再按[活动runner](../../../experiments/PackageConsumerProbe/README.md)生成新的唯一G/目录，不覆盖本批产物。

## 完整目标核验与正式合入

| 完整目标复核 | 裁决依据 |
|---|---|
| 多根、旧点分叉、父子继续、冷重开 | Repository创建/fork/WorkingCopy；SplitRepositoryHistoryTests及当前Split普通包 |
| 正式成员、真实来源、空fork树 | ReadBranchForestCore/ValidateBranchForest；BranchTreeQueryTests/BranchOriginQueryTests及Group普通包 |
| 摘要独立发布、不加载业务对象浏览 | CommitInfo/BusinessEntryPayloadCodec；分页摘要隔离/禁止模型回调反例 |
| 准确加载、继承历史、分页 | QualifyBusiness/Repository.HistoryPage；SelectedGraphIntegrityTests/CommitHistoryPageTests及失败反例 |
| 多分支共同发布及失败边界 | Repository.GroupCommit/共同Catalog；BranchGroupCommit/Failure/Integrity测试及Group普通包 |
| 创建、登记与重开 | BranchPhysicalCreationFailureTests四例、BranchRegistrationFailureTests七例及DB082 Unknown冷开补强 |
| 旧API替代及原有便利读取 | 旧入口编译负例；保留Checkpoint/Pair/Tag/事件枚举的对应真实回归 |

正常恢复、完整帧append-only、owner/fault/lifetime及清理纪律由当前完整回归覆盖。既有[进程Kill](process-crash-migration.md)保留6个保存＋3个初始化窗口的实际证据；核对`e5bf886..440bccf`产品差异，后继只有只读树/分页接线、Tag异常透传和属性可见性收窄，没有改变B/S持久写入协议。没有重复宣称新的Kill运行，也不外推到掉电、其他平台或任意多业务组组合。

2026-10-19，确认原master `866ce89705888af5fea56a5532c7d1986a21ca65` 为隔离分支祖先后，执行 `git merge --ff-only db096/branch-repository-cutover`，本地master快进至已验收 `440bccfcb856758f53013697ca853ea6e7b4df85`。首次checkout写README遇到权限错误；恢复目标提交并核实工作树/索引均与440完全一致后，重试快进成功。合入后工作树干净，随后只维护本文及活动导航文档，产品/测试/打包脚本不再变更。未推送、未公开发布包。

收尾的10份Markdown经集成diff检查，621个本地链接、64个锚点均有效，`git diff --check`通过；合入及文档修改后再次核对202项生产输入，全部仍匹配已验收来源。

完整目标已闭合；后续由真实消费者反馈选择新切片。附件BLOB、ID durable字段、改标签、rewind/merge/GC、多writer、跨库事务、创建新分支加入组及规模性能专项仍按原边界后置。终态工作集不再保留最终门禁/合入为待办。
