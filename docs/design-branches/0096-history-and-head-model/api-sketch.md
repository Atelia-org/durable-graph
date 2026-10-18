# DB-096：世界线与存档的最小 API 草图

> 2026-10-18，讨论稿，未实施、未冻结公开名称或持久格式。承接[五个调用故事](save-handle-stories.md)；本轮验收是用同一套角色走通这些故事，并暴露创建、恢复和分页的未决合同，不是编译这些伪 API。

## 1. 一页调用面

`LineId` 与 `SaveId` 暂指两种参数角色：底层分别是紧凑 HeadId 与 HeadEntryId。推荐最终使用不增加持久字节的薄类型包装，以避免应用混淆管理 Head、世界线和存档；是否新增类型与最终命名留到签名收敛。不附加16B库身份，仓库由调用方选择，解码不证明成员资格或目标健康。

```text
repo.ListLines()                         → LineInfo[]
repo.ReadLine(lineId)                    → LineInfo
repo.ReadCurrent(lineId)                 → { LineId, Save: SaveInfo }
repo.ReadSave(saveId)                    → SaveInfo
repo.ReadHistory(start, limits)          → HistoryPage

repo.CreateRoot(label, initialRecord,
                schemaIntent)           → LineInfo
repo.Fork(saveId, label)                 → LineInfo

repo.Restore(saveId)                     → SaveView（只读恢复）
repo.CheckoutShared(lineId)              → Checkout
repo.CheckoutPrivate(lineId)             → Checkout
checkout.CommitState(state, summary)    → SaveInfo
checkout.CommitEvent(event, summary)    → SaveInfo
```

这是职责草图而非 C# 声明：异步、取消、预算和错误签名尚未定稿。`initialRecord` 表示第一个真实 State 或 Event，不是为创建世界线补的占位记录。`schemaIntent` 表达创建时的编辑归属；新根是否支持 Private、如何提供其初始 Schema 仍待核对，不由这个占位参数自动扩展能力。普通 fork 固定继承旧存档，Schema 编辑意图留给后续 checkout。

| 返回角色 | 最少应表达的内容 | 生命周期与权威 |
|---|---|---|
| LineInfo | Id、Label、来源（根为无，子线为准确来源 SaveId） | 一次读取的目录描述；不是可写实例，不缓存并宣称 current 永久有效 |
| SaveInfo | Id、Kind、小摘要，以及定位内容所需的内部材料 | 一个准确存档的描述；不自动反序列化 State，不暴露内部材料为新公共必填字段 |
| SaveView | Tip、State 及其来源 StateSaveId；显式读取该点 Event 的能力 | 已恢复读取视图，不拥有世界线写入资格；资源寿命仍受 Repository 约束 |
| Checkout | TargetLineId、BaseSaveId、可编辑 State 及保存所需基线 | 明确选中线的工作副本；提交成功才推进其基线；保留既有占用/fault 纪律 |
| HistoryPage | Items、继续位置、停止原因 | 已物化的小页，不持有等待 UI 的活跃遍历器或文件 lease |

读当前返回 `{LineId=C, Save.Id=a1}` 是有意保留的两个事实。若应用先预览再 checkout，期间又保存了内容，checkout 以签出时的 current 为基线；要恢复精确 a1，应 `Restore(a1)` 或 `Fork(a1, ...)`，不要让 `Checkout(lineId, oldSaveId)` 暗中表达 rewind。

## 2. 用五个故事检查调用面

```text
// 继续上次：应用另存仓库位置与 LineId，标签不参与定位。
lineId = DecodeLineId(preferences.lastLine)
preview = repo.ReadCurrent(lineId)
using edit = repo.CheckoutShared(lineId)
Advance(edit.State)
saved = edit.CommitState(edit.State, EncodeSummary(edit.State))

// 固定书签：原线推进不改变它；不需要 HeadSetTag。
bookmark = EncodeSaveId(saved.Id)
info = repo.ReadSave(DecodeSaveId(bookmark))
view = repo.Restore(info.Id)

// 历史选点：入口固定，不在下一页重新读取今日 current。
top = repo.ReadCurrent(lineId).Save.Id
page = repo.ReadHistory(Including(top), limits)
if page.Next is present:
    nextPage = repo.ReadHistory(page.Next, limits)

// 旧点另开：先得到持久新线，之后才恢复可编辑内容。
child = repo.Fork(selectedSave.Id, "尝试另一种选择")
preferences.lastLine = EncodeLineId(child.Id)
// 即使在此关闭重开，child 仍可发现，current 仍是 selectedSave。
using childEdit = repo.CheckoutPrivate(child.Id)
childEdit.CommitState(newState, newSummary)

// LLM：恢复结果告诉应用阶段，不替应用重放工具。
using run = repo.CheckoutShared(runLineId)
ResumeAccordingToApplicationPhase(run.State)
```

`Fork` 与 checkout 分开，避免“线已创建、模型恢复失败”被误报为创建回滚。提交返回的 SaveInfo 同时可供更新预览与保存书签；不再需要先从写入结果反查准确 ID。

`Restore(E1)` 对 `S0,E1,E2` 返回 Tip=E1、StateSource=S0，不执行 E1。若历史只有 Event，只有完整查到根才能报告 State 不存在。恢复预算耗尽和内容损坏均为明确失败，不返回貌似完整的空 State。State 内容重建依赖与最近 State 的定位如何持久承载，需要下一份内部布局合同；API 草图不把长历史恢复当作固定成本。

## 3. 两个有意缩小的查询选择

**历史首版返回所有 State/Event 描述，不内置按 Kind 补满一页的筛选。** 应用可以筛选展示，但继续位置由框架按最后扫描项生成，不能取最后显示的 State。这样页大小只表达收集上限，不隐含为凑满20个 State 扫描多少 Event 的承诺。

`Including(id)` 与 `After(id)` 只是 `{SaveId, 是否包含起点}` 的轻量边界角色，不是新的持久游标服务。页满/主动停止返回 `After(lastScanned)`；完整抵达根才没有继续位置。After 在底层重读 inclusive anchor 并跳过首项，预算必须包含它；最后一页之后允许再读一页得到空且 Complete，不提供未经探测的精确 HasMore。

若上游在交付部分条目后耗尽预算，DG 必须选定一种公开结果：保留已验证前缀并明确标 BudgetLimited，或整个调用失败。本草图推荐前者，但需实施前核对错误与部分结果的接线；损坏和取消不伪装成正常页。零新条目的预算失败不能伪造进度，重复同预算也不保证推进。具体返回类型仍未冻结。

**目录首版可以完整返回小集合，再在内存按来源建树。** 不先引入子树持久索引和祖先查询服务。LineInfo 的准确来源来自 HS header 的解析，不另存一份可漂移的 Parent；解析能力和逐线读成本应在接入时验证。缺失父线或坏来源是错误，不转换成新根。目录标签与 current 摘要分开读取，展开哪组线再读哪组预览。

## 4. 创建协议的推荐方向

推荐继续细化[候选 B：DG 登记](application-design.md)，而不是把上游创建属性作为前置依赖。原因不仅是标签可编辑：**登记可为世界线创建提供 DG 层的最终可见点，尤其能与新根引入的 shared Schema 一起发布。** 上游创建属性候选 A 仍不能单独解决这一联合发布。

```text
旧点 fork：验证正式来源 → ForkHead(准确来源)
           → 发布含新标签的登记 → 返回新 LineInfo

新根：准备准确 Schema、首个真实图和摘要 → 持久化所需内容
       → CreateHead(首个存档描述)
       → 联合发布登记与必要的 shared Schema 更新 → 返回新 LineInfo
```

这个协议尚需耐久顺序、故障结果与内存安装的证明，不宣称伪代码就是已完成的原子实现。DG 只认正式登记成员：登记前中断可遗留未登记物理 Head；登记后交付失败不能声称未创建，不透明重试。新 Head 中的准确 Schema 引用必须独立足以解释存档，不能依赖 shared 当前值补齐。

登记只保存成员与标签等创建属性，不保存每线 current 或每次保存的摘要。**普通高频保存仅推进业务 Head，以及确有增长的 shared Schema；不会为更新末端重写世界线目录。** 这把目录更新成本限制在创建等低频管理操作，但不证明整表目录一定合适：下一步需按实际 payload 上限、标签字节上限和1000条线核算，再决定整表是否足够。此处不预建每线一个元数据 Head、持久树索引或 GC。

采用 B 会让改标签更容易，但首片仍可固定；不要把“有能力改”自动变成新实施任务。创建时间若用于近期排序，应是独立创建属性，不能用来源旧存档的记录时间冒充。

## 5. 下一步需要收敛什么

1. 用 fork、新根无 Schema 增长、新根有 shared 增长三条路径证明登记方案；列明创建结果不明时如何重新发现。核算目录编码容量和枚举成本。
2. 定义存档描述与恢复材料：准确 Schema、State/Event 内容来源、summary/BLOB 的持久关系。摘要推荐显式提供或缺省；缺省不自动复制上一 State 的摘要。缩略图按需读，附件 API 与错误边界单独定稿。
3. 将上述合同落成真正 C# 签名，核对 thin ID codec/序列化适配、只读恢复视图、Private 创建边界和分页失败结果，再制定实施切片。

本轮没有改运行时代码、依赖 pin 或持久格式，没有重新验证上游 HEAD；上游可用能力依据[既有交付复核](headstore-delivery-review.md)。以上是建议而非用户已批准的新协议，后续取舍从此继续。
