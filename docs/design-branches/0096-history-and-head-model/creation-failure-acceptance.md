# DB-096：创建登记的剩余故障验收

2026-10-19 只读预研；未新增或运行本表测试。分页完成后的下一片收敛为以下定向窗口。已有创建、组提交及6＋3进程 Kill 证据不重做，也不扩成全平台或掉电矩阵。当前实现优先于早期候选措辞；本表记录待补资格，不重新选择创建协议。

## 已有基础

- [NewPublicationFailureTests](../../../tests/DurableGraph.Persistence.Tests/NewPublicationFailureTests.cs)：新根有 S 增长时，最终 D/S 的 BeforeDataConfirm、首个 CU End、AfterConfirmed 与冷开；Tag 的真实 NotPublished/Unknown/Published 与冷 Resolve。
- [BranchDirectoryLookupTests](../../../tests/DurableGraph.Persistence.Tests/BranchDirectoryLookupTests.cs)：新根无 S 与 fork 的 DG 登记前/后失败，不提前安装 D 投影，冷开以实际发布 D 为准。
- [SplitRepositoryHistoryTests](../../../tests/DurableGraph.Persistence.Tests/SplitRepositoryHistoryTests.cs)：fork 登记前后失败、准确来源和 S 保持；DB078FailureTests 保 Event-first 创建阶段及冷恢复。
- [DB082LeafReuseFailureTests](../../../tests/DurableGraph.Persistence.Tests/DB082LeafReuseFailureTests.cs) 的 PublicationFailureNeverInstallsCandidateLeafTable 有真实 fork AfterAppend/Unknown，但该例尚缺冷开 D/current 验证。

以上是当前测试体核对，不是本轮重新执行记录。普通已有分支 B/S 保存的故障测试、工厂三控制 Head 初始化不能代替业务 Head→D 登记。

## 最小补充清单

| 路径 | 注入点 | 需要证明 |
|---|---|---|
| 新根、fork，各2例 | 物理 BeforeNewHeadRename、物理 AfterConfirmed | 还未尝试 D 登记，DG 为 NotPublished、旧 owner 停用；冷 D/S/T 不变。物理文件可能已成立但未登记，不猜造未返回的身份 |
| 新根无 S，2例 | 最终登记 BeforeDataConfirm、AfterAppend | 前者 NotPublished/冷旧D，后者 Unknown/冷新D；S/T Entry和旧Branch不变，无返回结果 |
| 新根有 S，1例 | 最终 D/S 第二个 AfterAppend | Unknown但冷 D/S全新；新Branch准确Commit/Schema、加载和续写成立；补既有首个End全旧的另一侧 |
| fork，1例 | 最终D的BeforeDataConfirm | header及候选已产生但冷开仍未登记；S/T不变 |
| fork、tag，各1例 | 资源层BeforeInstall | Published、owner停用、无结果；冷开发现准确成员/书签 |
| 新根，1例 | BeforeMemberInstall | D/S已接受、State尚未安装；Published、owner停用，冷开正确 |

共11个定向用例，另补强已有 fork Unknown 用例的冷开：D已登记、current仍是原source Commit、没有c0、S/T不变。触发最终登记的HS hook应在DG BeforePublication后安装，避免把物理创建与登记混为一个窗口。

统一断言优先复用实际 D/S/T Entry、正式成员集合、已经取得的候选身份、冷描述/加载/续写及旧完成帧保持；不为每个窗口复制完整图恢复体系。现有hooks应足够；如运行发现不同结果，先按真实发布阶段裁决，不放宽预期以换取通过。

## 后续收口

实施后替换本文的待验清单为实际结果，并复核早期 application-design、api-sketch 的“尚需证明创建”措辞。完整目标、最终包门禁与合入顺序只维护在 [PROJECT-STATE](PROJECT-STATE.md)。
