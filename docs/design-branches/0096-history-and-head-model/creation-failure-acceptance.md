# DB-096：创建登记的剩余故障验收

2026-10-19：11个新增定向用例与既有fork Unknown冷开补强已实际通过，发现并修复Tag重复包装已分类发布异常。公开签名与持久格式不变；新的普通包资格交由下一片最终门禁，不沿用修复前G。已有创建、组提交及6＋3进程 Kill 证据不重做，也不扩成全平台或掉电矩阵。当前实现优先于早期候选措辞；本表记录本次源码资格，不重新选择创建协议。

## 已有基础

- [NewPublicationFailureTests](../../../tests/DurableGraph.Persistence.Tests/NewPublicationFailureTests.cs)：新根有 S 增长时，最终 D/S 的 BeforeDataConfirm、首个 CU End、AfterConfirmed 与冷开；Tag 的真实 NotPublished/Unknown/Published 与冷 Resolve。
- [BranchDirectoryLookupTests](../../../tests/DurableGraph.Persistence.Tests/BranchDirectoryLookupTests.cs)：新根无 S 与 fork 的 DG 登记前/后失败，不提前安装 D 投影，冷开以实际发布 D 为准。
- [SplitRepositoryHistoryTests](../../../tests/DurableGraph.Persistence.Tests/SplitRepositoryHistoryTests.cs)：fork 登记前后失败、准确来源和 S 保持；DB078FailureTests 保 Event-first 创建阶段及冷恢复。
- [DB082LeafReuseFailureTests](../../../tests/DurableGraph.Persistence.Tests/DB082LeafReuseFailureTests.cs) 的 PublicationFailureNeverInstallsCandidateLeafTable 有真实 fork AfterAppend/Unknown，本片已补冷开 D/current、准确source与首次子线保存验证。

以上基础也随本轮完整Persistence重跑通过。普通已有分支 B/S 保存的故障测试、工厂三控制 Head 初始化不能代替业务 Head→D 登记。

## 已完成的定向窗口

| 路径 | 注入点 | 已验证结果 |
|---|---|---|
| 新根、fork，各2例 | 物理 BeforeNewHeadRename、物理 AfterConfirmed | 还未尝试 D 登记，DG 为 NotPublished、旧 owner 停用；冷 D/S/T 不变。物理文件可能已成立但未登记，不猜造未返回的身份 |
| 新根无 S，2例 | 最终登记 BeforeDataConfirm、AfterAppend | 前者 NotPublished/冷旧D，后者 Unknown/冷新D；S/T Entry和旧Branch不变，无返回结果 |
| 新根有 S，1例 | 最终 D/S 第二个 AfterAppend | Unknown但冷 D/S全新；新Branch准确Commit/Schema、加载和续写成立；补既有首个End全旧的另一侧 |
| fork，1例 | 最终D的BeforeDataConfirm | header及候选已产生但冷开仍未登记；S/T不变 |
| fork、tag，各1例 | 资源层BeforeInstall | Published、owner停用、无结果；冷开发现准确成员/书签 |
| 新根，1例 | BeforeMemberInstall | D/S已接受、State尚未安装；Published、owner停用，冷开正确 |

新增11个定向用例全部通过，另补强已有 fork Unknown 用例的冷开：D已登记、current仍是原source Commit、没有c0、S/T不变。触发最终登记的HS hook应在DG BeforePublication后安装，避免把物理创建与登记混为一个窗口。

统一断言核对实际 D/S/T Entry、正式成员、已取得的候选身份、冷描述/加载/续写及旧完整帧地址/内容保持。物理4例还核hook恰一次、DG登记阶段零次，以及全关后正式Head数量与旧Head字节。仅使用现hooks，未新增通用故障框架。

## 实施与实际验证

新增 [BranchPhysicalCreationFailureTests](../../../tests/DurableGraph.Persistence.Tests/BranchPhysicalCreationFailureTests.cs) 4例和 [BranchRegistrationFailureTests](../../../tests/DurableGraph.Persistence.Tests/BranchRegistrationFailureTests.cs) 7例；物理新根夹具复用已有Schema，最终登记另有明确S增长路径。DB082原Unknown反例补齐冷开，不新增fake结果或放宽期望。

首轮全solution测试发现Tag BeforeInstall例失败：资源层已经抛出含Published与原IOException的 RepositoryPublicationException，`CreateTagCore` 又包装一次。仅在 [Repository.MetaBootstrap.History](../../../src/DurableGraph.Persistence/Repository.MetaBootstrap.History.cs) 增加已分类异常透传，和现有Fork路径一致；原始异常仍沿旧分类逻辑，外层before/after hook及owner停用不变。直接 InnerException 现在保原故障，无API或wire变化。

主线程最终根 `dotnet build DurableGraph.slnx -c Release -p:UseStorageSources=true -p:StorageSourceRoot=E:/repos/Atelia-org/atelia-storage -m:1 -nr:false` 通过，0警告/0错误。DG Release、上游source Debug；准确S为70b406c。修复后完整Persistence **1098/1098、0跳过**，相关Runtime三类 **16/16、0跳过**。此前全solution轮的Runtime **1724/1724**、Serialization **133/133**、Storage **36/36**通过，但当轮Persistence有上述1个失败；不能把当轮报告写成全套绿色。

证据 `.artifacts/db096-creation-failures/`：最终 `build-2.log`、`persistence-fixed.log`、`runtime-fixed.log` 与 `tests/{persistence-fixed,runtime-fixed}.trx`。首轮 `build-1.log`、`root-tests.log` 和四个时间名TRX保失败/修复前范围，`initial-root-results.json`为其索引。独立审阅检查hooks时机、真实CU结果与冷开断言，主线程依据实际产物裁决并运行。

本片是Windows/source确定性故障资格；不冒充新增进程Kill、掉电、全部物理I/O或跨平台覆盖。既有6＋3Kill证据继续保原范围。

## 后续收口

登记专项到此闭合。活动文档的旧API、业务Parent、Move、地址寿命、Private和“组提交尚未实现”表述已按当前代码清理；历史DB/准确版本证据保留。下一片执行[最终交付清单](final-delivery-acceptance.md)，为本次产品尾修制作新G，完成12个活动包门禁和最终源码回归、审计及正式合入。完整顺序只维护在 [PROJECT-STATE](PROJECT-STATE.md)。
