# DB-096：以存档定位值贯通五个调用故事

> 状态：2026-10-17，设计推演，未实施。用户已认可从下游调用故事精炼 handle/anchor 能力；本文不是公开 API 定稿。
> 接续[应用合同稿](application-design.md)；提炼给上游的最小能力集中在[新的 HeadStore 请求](headstore-save-handles-request.md)。源码核对基线：上游 `06ff3d9a80c938d0d1af7d4895cd26b9ec04b488`。

## 1. 两类身份与一层运行时对象

| 对象 | 回答的问题 | 跨重开保存什么 |
|---|---|---|
| 世界线定位值 | 继续写哪条线？ | 完整 HeadId，或 DG 的薄包装 |
| 存档定位值 | 查看、恢复、从哪个准确位置分叉？ | 完整 HeadEntryId，或 DG 的薄包装 |
| 存档视图 / checkout | 当前 owner 下读到什么、怎样编辑和提交？ | 不直接保存对象、缓存或活着的枚举器 |

新 C 从 A 的 a1 分叉但尚未续写时，`current(C).Id == a1.Id`，而写入目标仍是 C。一个 EntryId 不能同时替代这两种身份。HeadEntryId 中的 HeadId 保持实际 Entry 归属，不写入用户选中的视图上下文。

本文用 `LineId`、`SaveId` 表示 DG 角色，尚不决定新增类型或最终名称。推荐薄包装只增加业务角色和调用便利，不再生成一套独立 GUID，也不绑定某次 Open。包装的构造/解码不等于目标永久有效；DG 在使用时验证业务 Entry 描述、正式世界线资格与所需内容。

仓库路径由应用选择，定位值携带的库身份用于匹配与拒绝串库；它不自动发现文件路径。拥有相同持久身份的仓库副本如何由应用选择，不纳入地址 codec。

以下均为伪 API。`Decode` 无 I/O；`Resolve/Read/Checkout/Fork` 才进入 owner 与持久状态检查。示例的 `Commit` 成功指经过实际发布，不包含未知结果下的透明重试。

## 2. 故事 H1：继续上次世界线

```text
打开用户选择的仓库
line = 用户选择的正式世界线
应用偏好保存 Encode(line.Id)

关闭重开后：
lineId = DecodeLineId(偏好中的字节)
line = DG.ResolveLine(lineId)
preview = DG.Current(line)        // {LineId=C, SaveId=a1, ...}
checkout = DG.Checkout(line, schemaIntent)
修改并 CommitState(checkout, state, summary)
```

**最低持久值是 LineId。** 仓库定位配置另存；若应用想提示“离开后有新保存”，可以另存 last-seen SaveId，但它是比较基线，不替代写入目标。

反例：保存 `preview.SaveId.HeadId` 会在空 C 情形保存 A，重开后误续写父线。`ReadHead(localId)` 也不能在返回源 Entry 时恢复 C 的完整身份，因此公开 HeadId 编码不是冗余便利。

继续读取的是 C 此时的 current；若想回到固定旧状态，进入 H2/H4。缺失模型、Schema 不可解释或 owner 故障应明确失败；不要静默选择其他世界线或更旧的健康存档。

## 3. 故事 H2：固定存档书签

```text
selected = 历史列表中的准确存档
bookmark = { title: "获得小绿瓶之前", position: Encode(selected.SaveId) }
应用将 bookmark 存入设置、数据库，或 DG 中的业务对象

重开后：
saveId = DecodeSaveId(bookmark.position)
description = DG.ReadSave(saveId) // Kind、摘要、准确图定位；不默认物化 State
按用户选择：读附件 / 恢复只读内容 / 从该点 fork
```

**最低持久值是 SaveId。** 标题和书签是否可改绑由应用或 DG 书签 API 决定。原线推进不改变目标；同值的两次真实保存仍是不同书签位置。它不是“上次所在世界线”的记录，也不捕获多个 Head 的当前值，无需为了一个准确存档强制创建 HeadSetTag。

若定位值进入 DurableGraph 序列化对象，DG 需通过公开 codec 提供标量/包装的序列化适配，或先显式保存编码字节。不能依赖读取 HeadId 的私有字段或假设当前 Generator 已自动支持这些上游 struct；这部分接线归 DG，不要求 HeadStore 引用 DG 或增加序列化框架扩展点。

解码可以在仓库尚未打开时完成；真正使用时仍可能因错库、缺失、损坏或发布资格失败而拒绝。这里的持久化稳定性指受支持编码格式内的跨进程读回，不承诺所有未来格式版本无条件兼容。

## 4. 故事 H3：历史分页选点

```text
line = 选中的世界线 C
fixedTop = DG.Current(line).SaveId
page1 = DG.ReadOlder(fixedTop, includeAnchor=true, limits)
page2 = DG.ReadOlder(page1.LastScannedSaveId, includeAnchor=false, limits)
选择某项 → ReadSave / ForkWorldline
```

语义要求：首次固定准确上界，向旧方向推进；之后父线/子线继续保存，不把新后缀插入当前浏览。下一页不从今日 current 重新扫描已浏览前缀。世界线 C 的标题与选择上下文另保留，不能因翻到 A 的存档就悄悄切换续写目标。

上游只提供 inclusive 准确起点，也足够实现基础分页：下一页重读上一页最后处理的 Entry，仅略过首次出现的同一 anchor，再收集更早项。这个略过依据准确 ID，不按 payload 去重；预算需包含重读 anchor。公开 exclusive 模式是便利项，不是首版正确性的必要条件。

### 4.1 展示进度、扫描进度与完成性

DG 若只展示 State，可能已读很多 Event 却没有展示项。继续边界应记录**最后处理的已接受业务 Entry**，不能只记录最后展示的 State，否则会重复扫描事件区间。尚未交付任何 Entry 时不能伪造继续位置。

页满后的主动停止不证明还有下一项，也不证明已到根。返回中必须区分完成、主动停止、预算不足、取消和错误。UI 可显示“继续加载”；若需要精确 HasMore，可由调用层多读并保留一项，不能丢弃这个位置导致漏项。

最后交付 ID 只记录 Entry 进度，不记录合法未成立 CU 等无输出物理扫描进度。若某区间的资格工作超过每次工作预算，原样重试可能一直停在同一位置。首版允许明确中断或提高预算；不承诺任意小预算下都能持续前进。要保存任意扫描/CU 子进度的 cursor 是另一项需求，本轮不预建。

回调中收集小描述和 ID，退出历史调用后再做 fork/发布；沿用上游同步 visitor 的重入限制。页面结果不持有跨 UI 等待的文件 lease 或活跃枚举器。

## 5. 故事 H4：从旧点创建新世界线

```text
sourceId = 书签或历史项中的 SaveId
C = DG.ForkWorldline(sourceId, label="尝试另一种选择")
应用记住 C.Id，立即列出并预览 C
关闭重开 → C 当前仍是 sourceId
checkout = DG.Checkout(C, schemaIntent)
c1 = CommitState(checkout, newState, newSummary)
```

输入只需准确源存档，输出必须是新世界线身份。即使从 B 的视图选中 A 的 a1，C 仍挂在 A 下。空 C 预览与 a1 相同，标签不同；fork 不新增领域存档。第一次新保存写 C，不写 sourceId 的 owner A。

HeadStore 已有 `ForkHead(HeadEntryId) → HeadId`，不需要另造带“业务存档”名称的底层 fork API。DG 要组合正式成员资格、标签、数据图及 Schema 的解释和创建协议；这些仍按[应用合同稿](application-design.md)的 A/B 候选设计，不由 ID codec 自动解决。

持久 fork 与恢复可编辑对象是两个步骤。若便利 API 将两者合并，必须明确恢复准备与发布先后，以及“线已创建但 checkout 交付失败”的结果，不能误称回滚。没有模型也能解析定位值，不等于无需模型恢复业务对象。

## 6. 故事 H5：durable LLM tool-loop 恢复

```text
State = { phase=准备调用, operationId, request, context, ... }
保存 State，并确认发布结果
调用外部工具(operationId, request)
State = { phase=结果已记录, operationId, result, 消费进度, ... }
保存 State，并确认发布结果

重启：按 LineId 签出当前 State，按应用 phase 决定下一步
对照试验：选择准确旧 SaveId，fork 新线并执行应用自己的分支策略
```

应用持久状态表达阶段、请求、结果与消费进度，字段不由 DG 规定。只保存 HeadEntryId 不能补回从未保存的工具结果。工具成功与结果落盘之间的中断，需要应用的幂等键、结果查询或人工处置；DG 不自动重放工具，也不提供跨外部系统 exactly-once。旧状态 fork 后是否继续复用 operationId 是业务选择，框架不能自动生成或复用来掩盖这个问题。

纯 State 连续保存足以成立；Event 可选，不强制一 Event 一 State。若有 `S0,E1,E2`，从 E1 恢复取得逻辑末端 E1 与最近 State S0，不执行 E1；若仅有 `E0,E1`，可确实没有 State。必须完整走到根才可断言没有 State，预算耗尽或坏 State 不得被解释为“没有”或更旧健康状态。

每个存档的内容按其准确 Schema 解释，而非当前 shared Schema。正常保存需要的 Schema meta Head 联合发布归 DG；只要业务 Entry 能定位完整恢复上下文，SaveId 不必携带所有 Schema Heads 的当前位置，也不需要在应用层组成大快照。

细粒度保存形成长历史。当前预览和保存的历史管理部分不应扫描累计历史；历史选择按固定起点取近期窗口。对象 Delta 重建、最近 State 搜索和 CU 必要工作另计，不承诺所有恢复都是固定成本。

## 7. 可发现性：由正式入口逐层进入

```text
应用选择 Repository
  → DG 正式世界线（标签、来源）
    → 所选 Head 的 current
      → 准确 Entry 起点的历史
        → 预览 / 附件 / 内容恢复 / 新世界线

应用保存的 LineId / SaveId → 在所选 Repository 中直接重新定位
```

HS `ListHeads` 提供物理 Head 集合，DG 排除管理 Head，并按选定创建方案判断正式世界线；它不是新的全库 Entry catalog。历史返回 EntryId，自然为后续读取、保存书签和分叉提供 handle。两条世界线可以共享同一 Entry，不需要为每个视图复制存档身份。

`ListHeadOrigins` 的声明边可建立父子视图，但未检查的 source ticket 不能冒充目标健康证明。五个主故事都能从 current、历史或书签获得准确位置，暂不要求调用方自行拼接 source ticket 和 codec 字节。若后续要直接点击来源边跳到分叉点，再比较窄的来源解析便利接口。

不提供无上下文的“下一存档”：某点可能有原线后续和多个分支。最近使用由应用记录 LineId；最新保存时间只作展示依据，不代表跨线全序或新空 fork 的创建时间。

## 8. 分层收敛与验收场景

| 层 | 负责 | 不下沉的职责 |
|---|---|---|
| HeadStore | Head/Entry 定位值、准确读/写/fork、准确起点继承历史、发布资格 | 世界线标签、State/Event、Schema 解释、UI、外部工具流程 |
| DG | 业务定位包装、正式成员、存档小描述、Schema 与内容恢复、创建发布组合、查询页面适配 | 自动证明摘要业务正确、自动重放外部副作用 |
| 应用 | 路径选择、上次世界线、书签标题、展示筛选、LLM phase 与工具协议 | 手工解析 Head 文件、根据 source owner 猜续写目标 |

最小跨层见证：冷重开继续空 C 而不误写 A；固定 a1 书签不随后续移动；分页跨 Head/同值提交/State 筛选不重不漏；坏 anchor 不回退；Event-only 与坏 State 明确区分；fork 成功立即完整可见；工具成功但结果未落盘的恢复分支不被框架自动重放。

三个只读调查包覆盖调用故事、分页 anchor、编码与发现边界；主线程依据当前源码整合，并作独立反向审阅。本轮不执行运行、性能或故障注入实验，不宣称新 API 已实现。上游请求包含具体可执行验收题，后续实现再取得资格。
