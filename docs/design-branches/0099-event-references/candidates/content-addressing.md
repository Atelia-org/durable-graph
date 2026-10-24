# 候选 C：共享长字符串正文，Event 不承担内容身份

本候选独立形成，未阅读其他候选。依据为 DG `66f2cf3`（产品变更 `a8f6ed1`）与 ConsoleAgent `437a586` 的当前源码；版本由工作包给定，本次未执行 Git、构建或运行实验。下文是待验证设计，不是现有能力。

**建议把“同库长字符串 Base 正文按值去重”保留为最强替代路线。** 它能在 Agent 原有模型与提交节奏下，使同一长 JSON/工具文本只占一份物理正文；State 的持久记录引用内容帧，内存仍独立恢复。若目标扩大为“整个类型化动作子图只存一次”，此方案不如直接引用 Event 中的事实载荷。

## 事实与切入点

Agent 的 [Dispatch](../../../../../atelia-console-agent/src/ConsoleAgent/AgentSession.cs) 是 `Prepare → CommitEvent → Apply → CommitState`；恢复时读取最近 State 后唯一待处理 Event，再 Apply、保存 State。[SavedAction](../../../../../atelia-console-agent/src/ConsoleAgent/SavedMessages.cs) 把原生消息序列化为一个 `Json` 字符串，[PrepareReceiveModel](../../../../../atelia-console-agent/src/ConsoleAgent/AgentStateMachine.cs) 将同一个 `e.Action` 放进历史。工具结果则由字符串数组保存。因此内存对象相同仍会经过两次独立图捕获。

DG 的 [BusinessWorkspace.Capture](../../../../src/DurableGraph.Persistence/BusinessWorkspace.cs) 为独立快照建立新 CaptureSession；[GraphRecordStore.AppendCore](../../../../src/DurableGraph.Persistence/GraphRecordStore.cs) 要求 Event 的所有对象为自有批次的 Base，且无 State source。[GraphFrameCodec.EncodeBatch](../../../../src/DurableGraph.Persistence/GraphFrameCodec.cs) 当前直接嵌入每条 body。复用整批会混入 ObjectId、SchemaRoot 与来源关系，不能靠给批次加哈希解决。

## 两条内容路线的取舍

| 路线 | 能解决什么 | 本轮代价 |
|---|---|---|
| 独立不可变 Content，Event/State 都保存 ContentRef | 内容有独立寿命；类型化整载荷可复用，可服务无 Event 的 State | 新公开引用类型、编码/解析与闭包、内容写入准入；若使用全局 CAS，还要持久查找索引 |
| 物理正文按字节去重（本候选） | 同一长字符串无需改应用模型即可共享；State-only 也受益 | 新内部内容帧与正文引用编码、读验证、成本核算；结构性包装仍各存一份 |

选择第二条，并只做字符串叶。内容帧写入**同一个 FrameStore**，不建旁路文件、Content 分支、第三种 CommitKind 或单独的发布事务。Event/State 的本地对象行仍是完整 Base；只是 Base 的字符串正文选择 inline 或外置内容地址。Event 不依赖另一张业务图，也不恢复 ReadPair，但必须承认 DB-097 的“全部物理字节自有”收窄成“完整逻辑图加显式内容闭包”，这需要格式与验证合同变更。

## 身份、索引与 Schema

内容只包含 [StringBodyPreparer](../../../../src/DurableGraph.Serialization/StringBodyPreparer.cs) 现行无损编码得到的完整字节，带独立格式版本、长度和类型标记。正文引用保存同库 FrameAddress、长度与摘要。哈希用 `SHA-256(domain/version || body)` 加速候选查找；命中后必须读取并逐字节比较，不以摘要相等证明值相等。碰撞进入同摘要的另一地址，绝不能合并；读引用时校验帧种类、版本、长度、摘要及字符串完整解码。首次实验可注入恒定哈希验证碰撞路径。

**地址是物理身份，摘要不是公开 ContentId。** 不做 Unicode 归一化、JSON 语义归一化或跨库身份承诺。两个值即使都显示“相同文字”，只要编码不同就不合并。字符串使用固定内建布局；[TypedBaseBodyCodec](../../../../src/DurableGraph.Persistence/StoredGraphBodies.cs) 已为 string 使用内建路线，应用 Schema 不成为正文身份的一部分。包含图内引用的普通对象与依赖动态 descriptor 的 body 均不准入，否则相同字节可能表示不同对象或 Schema。

第一实验采用不淘汰的 owner 内存索引 `摘要/长度 → 地址候选`：写入或读取外置正文时登记，owner 关闭即丢弃，不作全库启动扫描。正常 Event 写入后 State 即可命中；崩溃重开先读取待处理 Event，也会登记同一内容再保存 State。这个保证只覆盖已写入/已读取的正文；未触及的历史同值内容可能重复，**不宣称全库 CAS**。索引每唯一正文有元数据成本，先测量再决定准入阈值和淘汰；以后若淘汰，必须另保 Event→State 窗口的命中保证。

## 提交、崩溃与发布

1. Event 保存：准备字符串，查索引并完整比对；未命中则追加内容帧，随后追加引用它的 Base 批次、目录与图。所有内容追加必须纳入同一 owner 的写入计数、重入与故障守卫；不能在现有计数之外偷偷写帧。
2. 继续现有 [CommitSaveGroup](../../../../src/DurableGraph.Persistence/RepositoryResources.GroupSave.cs)：追加 Schema/业务图后调用 PublishHeads，再安装基线。DG 的 [RepositoryResources](../../../../src/DurableGraph.Persistence/RepositoryResources.cs) 明确发布使用 HeadStore 数据先行屏障；候选要求该屏障覆盖同一 FrameStore 的新内容帧。无需先发布内容头，也无需额外 ConfirmDurable。
3. Event 发布后 Apply，State 保存时引用已确认的相同内容地址，再按原协议发布 State。发布前中断可能留下完整孤儿帧；不能回写/截断已完成帧。Event 已发布、State 尚未发布时，现有恢复路径读取 Event 内容并重做 Apply。State 已发布时两个逻辑图均可独立解析。
4. 发布结果未知、写后异常或安装异常沿用现有 fault/reopen 纪律。索引只能登记完成且验证过的帧；发生 owner fault 后全部丢弃。重开以实际已发布历史为准，不能以索引证明一次操作成功。

上述为设计推导；尚未注入进程终止或 I/O 故障验证，尤其要复核新增内容追加是否完整纳入现有 fault 判定。

## 回收、导出与明确反例

回收根仍来自保留的分支、标签、历史；访问 Base 时新增“正文引用 → 内容帧”边。内容不能随首次写入它的 Event 一起删除：保留 State 就必须保留它引用的内容。完整库复制自然带上帧；未来子集导出必须遍历此闭包并重写地址，或把正文重新内联。只复制 Event 的 owned batch 将不再足够。当前路线图将[物理 GC/compaction](../../../DurableGraph-research-roadmap.md)列为延期，本片只规定保活依赖，不顺带实现回收器或导出器。

与 EventRef 相比，一个明确胜例是：同一大工具说明/文本跨多个 Event、分支及 State-only 快照复用；把身份归于某条 Event 会引入任意“首次拥有者”，而物理内容共享无需业务选主。反例是 64K 工具调用参数：它在 `SavedAction.Json` 中是转义后的子串，在 `State.Calls.Input` 中是另一整串，按整字符串去重不能消除二者重叠。未来若载荷改成大结构化对象图，仍需存两份包装、数组与引用行；这时 EventRef 的类型化子图引用更合适。不要为补齐该反例自动加入分块、Rabin 边界或通用对象图 canonicalization。

## 最小实验与决策门槛

只对业务图超过一个试验阈值的 string Base 开启外置共享，保留内联对照；不改 Agent Schema、生成器与提交角色。用真实 `SavedAction.Json`、工具文本各跑一次 Event→State，并在 Event 发布后终止、冷恢复后保存 State。验收应同时满足：目标正文帧恰好一份；双方冷读字节完全一致；State 增量与 Event 全 Base 语义保持；错 tag/长度/摘要/地址及强制哈希碰撞均受控拒绝或不合并；坏正文不能在 Normalize/Upgrade/用户工厂回调后才被发现。

记录实际文件增长、额外读帧、哈希/比对时间、索引驻留及重复子串残量；读放大计量须计入正文帧，不能以引用长度冒充重建成本，新增物理字节另计。若同一长串的占比足够高且应用想保持自含持久模型，本候选可优先于公开 EventRef；若用户要求类型化完整载荷引用、精确子树选择或最少恢复读取，则让 EventRef 胜出。只有跨进程未触及内容仍须保证全库唯一，才重访持久 CAS 索引；只有测得结构性重复成为主成本，才重访独立类型化 Content。
