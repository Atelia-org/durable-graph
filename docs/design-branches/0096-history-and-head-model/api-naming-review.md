# DB-096：API 命名原则、竞争方案与审阅结果

> 2026-10-18。用户要求在早期优化自明性和可预期性，避免后续设计被临时名字牵引。本轮修订对象为 [api-sketch](api-sketch.md)，推荐命名仅约束后续设计讨论，不是代码改名或行为实施记录。

## 1. 从前人方法中借什么

这些来源提供审阅方法，不替 DG 选择具体词汇。下面的项目应用均是本轮推论。

| 一手来源与先例 | 借用的思路 | 在 DG 上怎样检查 | 不照搬的部分 |
|---|---|---|---|
| [Swift API Design Guidelines](https://www.swift.org/documentation/api-design-guidelines/) | 优先判断调用点是否清晰；清晰胜于单纯简短；给每项操作写一句说明，难以说明可能意味着职责有问题 | 隐去长文，只读 Restore、ReadCurrent、After 是否能预测对象、效果和方向；比较实际代码而非孤立词表 | 不移植 Swift 的外部参数标签语法或所有默认参数偏好；DG 必须显式选 Schema 归属 |
| [.NET Framework Design Guidelines Digest](https://github.com/dotnet/runtime/blob/main/docs/coding-guidelines/framework-design-guidelines-digest.md#general-design-principles) | 先写用户希望写出的场景代码；让陌生使用者尝试；提供容易发现的功能入口。文中的 Stopwatch.StartNew 是从测时场景倒推调用形状的例子 | 用五个故事检查 Repository 的发现路径，以及定位值如何流向预览、加载、分叉和提交 | 不照搬旧式默认构造/Create-Set-Call、继承或集合细则，不据此改资源所有权 |
| [Microsoft Member Overloading](https://learn.microsoft.com/en-us/dotnet/standard/design-guidelines/member-overloading) | 同名重载应表达同一类操作，语义一致 | 不把读描述、恢复业务对象、取得编辑资格塞成 Open(id, flags)；State/Event 根类型相同也不能靠重载猜角色 | 指南不能裁决所有双入口与 enum 选择；需看真实差异和调用误读 |
| [Git 2.23 原始发布记录](https://github.com/git/git/blob/master/Documentation/RelNotes/2.23.0.adoc) | switch/restore 从 checkout 中拆出不同职责，体现多义动词会提高理解负担 | 保留 Fork 的持久创建与 WorkingCopy 的内存编辑分离；清除只加载却叫 Restore 的误读 | 不照抄 Git restore 的词义，更不引入 index、全局当前分支、唯一分支名或 merge |

本轮据此采用五项检查：**调用目的能辨认、读写效果能预测、相似操作同语法、参数角色不靠猜、名字不掩盖成本和失败边界**。名称无法完整证明耐久性、对象所有权或错误合同；这些仍需要简短准确的说明和后续测试。

## 2. 核心词汇竞争

| 候选族 | 优点 | 本项目的主要代价 | 本轮取舍 |
|---|---|---|---|
| Line / Save / SaveView | 短，贴近游戏对话 | Line 不说明分叉入口；Save 是动作还是记录不清；View 把小描述、State、Event、owner混在一起 | 退出主草图，旧故事保留占位映射 |
| Branch / Checkpoint / WorkingCopy | 熟悉；分支与可编辑副本直观，已有项目用语 | Event-only Checkpoint 容易被理解成完整可恢复的状态检查点 | 有力备选；现源码用法继续如实记录，设计主词不选它 |
| Timeline / HistoryEntry / EditSession | HistoryEntry 能自然容纳 State/Event | Timeline 容易像时间排序的完整序列；EditSession 容易像退出时提交/回滚的事务；Entry也接近底层HeadEntry | 不选；并非错误，而是要解释的额外预期较多 |
| Branch / Revision 或 Version / WorkingCopy | 历史位置与可变入口分开 | Revision/Version 容易被读成某一对象状态的新版本；当前已有图内容版本和Schema版本，不宜再扩大歧义 | 暂不占用这些词 |
| **Branch / Commit / WorkingCopy** | 入口、一次业务保存、内存编辑对象三者分明；State/Event 均可提交，fork 可以没有新提交 | Commit 容易关联Git内容哈希或整个底层事务；WorkingCopy也需说明不落地成另一个磁盘工作目录 | **推荐**，以明确的业务提交边界和内存生命周期约束其含义 |

Commit 的定义是一次已接受的**业务 State 或 Event 保存记录**。Schema 联合更新可以伴随它，但内部管理 Entry、创建分支和修改标签不因此变成业务 Commit；它没有内容哈希身份。Branch 表达分出后各自追加，不承诺唯一标签、rewind或merge。WorkingCopy 是可以多次提交的内存对象，不是一个延迟到 Dispose 才完成的事务。

这不是按 subagent 票数决定：一次独立方案推荐 Checkpoint，另一次推荐 Commit；主线程用 Event-first 和 fork 无新记录两个反例裁决，随后交叉质疑确认 Commit 的业务边界可以解释。保留 Repository、State/Event、Summary、CommitState/CommitEvent 等已有且职责清楚的词，不为了整齐全部改名。

## 3. 名字背后的职责修正

**去掉万能 Restore/SaveView。** 准确 State、准确 Event 和“某点之前最近已保存 State”是三种可预测操作，分别以 LoadState、LoadEvent、LoadLatestSavedState 表达。后者返回选点与实际 State 来源。合并为 LoadCommit 若强制恢复 State，会让只读 Event 者承担无关历史搜索和失败；全懒加载则增加隐藏错误时机。底层准备/缓存仍可共享，无须统一为一个公开大视图。

`LoadStateAt(E2)` 在 `S0,E1,E2` 中可能被理解为执行 E1/E2 后的状态，因此采用稍长的 `LoadLatestSavedState(atOrBefore: E2)`。Saved 是有效限定，不承诺重放；准确 State 查询另有 LoadState，Kind 不匹配就失败。这里是公开职责细分建议，不宣称新返回类型已经实现。

**“没有提交能力”不等于“深度只读”。** 当前 [Checkpoint](../../../src/DurableGraph.Persistence/Checkpoint.cs) 与 [Repository 独立读取](../../../src/DurableGraph.Persistence/Repository.cs)允许修改独立恢复对象的内存和 Transient；共享比较另有只读合同。旧草图称 SaveView 为只读过强。本轮纠正文字，不偷选新的共享/可变实现。

**编辑策略写出 Schema。** 推荐 `OpenWorkingCopy(branchId, schemaMode: SchemaMode.Private)`；参数必填，不恢复旧的隐含 null/default。与 CheckoutWithPrivateSchema / CheckoutWithSharedSchema 双入口相比，它减少同一操作的名字分叉，且明确打开的是局部工作副本。双入口不是错误方案；真正不能丢的是选择时机和 Shared/Private 的准确语义。Private 会持久化，不改叫 Local 或 Temporary。

**方向按祖先关系表达。** HistoryStart.AtOrBefore / Before 对应包含/排除选点并朝祖先走；Page 改成 CommitHistoryPage，Next 改成 Continuation。Before 不基于墙上时钟，Continuation也不证明还有结果。

## 4. 旧草图到推荐用语

此表是设计符号映射，不是当前 C# 声明重命名表；真正施工时另核对声明和用途。

| 旧草图 | 新草图 | 性质／理由 |
|---|---|---|
| LineId / SaveId | BranchId / CommitId | 持久写目标与准确记录分开，不增加身份材料 |
| LineInfo / SaveInfo | BranchInfo / CommitInfo | 描述与运行时活对象分开 |
| Label / 来源 | DisplayLabel / ForkedFromCommitId | 明示非唯一显示文字与准确分叉位置 |
| ListLines / ReadLine | ListBranches / ReadBranchInfo | 易发现实体与描述 |
| ReadCurrent / ReadSave | ReadCurrentCommit / ReadCommitInfo | 前者保留选中 BranchId，后者按准确 CommitId 查询 |
| CreateRoot(initialRecord, …) | CreateRootBranchFromState / FromEvent | 两个业务角色，不能靠相同根类型重载区分 |
| Fork | ForkBranch(sourceCommitId, displayLabel) | 输入准确记录，产出持久分支 |
| Restore / SaveView | LoadState / LoadEvent / LoadLatestSavedState + LoadedState | **职责细分**，不是等价替换；不再承诺对象深度只读 |
| CheckoutShared / CheckoutPrivate / Checkout | OpenWorkingCopy(…, schemaMode) / WorkingCopy | 推荐一个必选策略参数，明确返回编辑对象 |
| Tip / StateSaveId（恢复结果） | SelectedCommitId / StateCommitId | 选中位置与 State 来源可不同 |
| BaseSaveId（工作副本） | BaseCommitId，另列 StateSourceCommitId | 逻辑末端不等于最近 State |
| Including / After | HistoryStart.AtOrBefore / Before | 向祖先，包含性明确 |
| ReadHistory / HistoryPage / Next | ReadHistoryPage / CommitHistoryPage / Continuation | 小页而非全历史；续查不保证非空 |

## 5. 审阅证据与局限

三个有界调查包分别覆盖原则/先例、消费故事竞争方案和独立概念审阅；主线程核对一手来源、当前读取合同并整合，随后对最终文档作独立反向审阅。另一个未获得仓库历史的 agent 只看7个候选调用预测效果：能辨认描述读取、业务加载与分叉写入，但指出 LoadLatestState 仍可能包含重建、Schema.Private边界不全、Fork返回的耐久性与Dispose行为不能仅凭名字断言。追加比较后，它认为 Saved 限定减少了自动重放预期；相应说明已放入主草图。

这是 Coding Agent 的调用点预测和文档审阅，**不是人类可用性实验、编译或运行验收**。下一步仍须用最终签名写真实消费者；目前没有修改运行时代码、wire、依赖pin或底层创建协议。Private新根、分页部分结果、加载对象寿命与可变性继续未决，不靠名字替代合同裁决。
