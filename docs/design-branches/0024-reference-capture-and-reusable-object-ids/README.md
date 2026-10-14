# DB-024：引用 Capture、revision 内身份与可复用 ObjectId

> 历史迁移（2026-10-14）：原路径 `docs/design-branches/0024-reference-capture-and-reusable-object-ids.md`；来源修订 `8ef9fca`，正文保留原时期语义。
> 适用范围：首片 Capture 已实现，回收/恢复与 struct 候选保留历史上下文；空字符串后继裁决见 DB-025，不把复用建议读作已选算法。 今日事实从 [PROJECT-STATE](../../../src/PROJECT-STATE.md) 续接。

> 历史混合材料（2026-09-06 治理）：首片施工、后续回收候选和 struct TODO 保留原始上下文，正文不再同步进度。
> 当前事实见[PROJECT](../../../src/PROJECT-STATE.md)，未完成事项统一见[路线图](../../DurableGraph-research-roadmap.md)。
> 下文“string 独立分配仍需核验”等早期陈述已被 DB-025 的 Empty 规范化裁决取代，不能重新当作实施前置条件。

> 状态：string 引用 Capture 与单调 ID 首片已实现 / 回收及恢复延期 — 2026-09-06。
>
> 用户已启动实施 Goal，并明确采纳 SG 生成根登记适配器、Runtime 统一会话与候选管理方案。
> 承接 DB-018/022/023；本文区分用户已明确的语义与尚待讨论的实施建议。
> 后续 DB-025 用户裁决：所有空字符串在 Capture/读取时统一为 string.Empty；本文身份规则中的 string 指非空情形，空串为显式例外。

## 1. 已明确与本轮建议

- 用户明确 ObjectId 的解释经过 StateRevision，允许回收后复用。不能继续把“全仓库永不复用”当作必要不变量。
- 已接受的统一引用身份与 DTO 捕获方向保持：string 也按 ReferenceEquals 区分；引用字段存 ID，
  对象内容进入独立条目；完成 Capture 后，比较/估算/编码消费同一个冻结候选。
- 已按本文顺序先实现内存 string 引用 Capture 与对象列表；后续单独闭合加载引用保真，
  最后接真实 StateStore 发布。首片不用回收池；API 已验证，string 独立分配仍需后续核验。

## 1.1 首片初始目标（本次用户确认后的收窄）

范围是“标量 + string 的领域 roots → ID 化 Versioned DTO → 封闭候选”，
以及单会话、单在途候选的 accept/discard 内存见证。string 作为独立内容条目，引用槽不 inline 内容。
不在首片实现字符串对象解码/领域 Restore、Durable 对象互引/循环、ID 回收池或完整 Save。
G0 裁决与首片结果保留在本文 §3.1–3.2；当前实现见 [CaptureSession](../../../src/DurableGraph/Runtime/Capture/CaptureSession.cs)，
回归见 [会话测试](../../../tests/DurableGraph.Tests/ReferenceCaptureSessionTests.cs)和[生成器测试](../../../tests/DurableGraph.Tests/ReferenceCaptureGeneratorTests.cs)。

- 一个 CaptureSession 从非零 uint 域单调分配；0 表示 null。分配过的号在该 session 内不再发放。
  实施建议：失败/discard 允许消耗号码，高水位不回退；这免去首片的号段回滚与回收状态。
- parent 的有效实例绑定只用于保持相邻已接受图中持续存活对象的 ID；新绑定由候选持有。
  Capture 出错或 discard 不改变 parent；accept 安装原候选 DTO，不能重新读取已经变化的领域对象。
- 仍计算完整 live 集合及 `parentLive - candidateLive`，accept 后去掉退役实例映射，避免会话无限持有领域对象。
  清理映射不等于回收数字：对象重新入图会取得更大的新号。
- 首片从空会话开始，不承诺跨进程/跨 session 单调，不增加持久 high-water mark；
  不用 `max(live IDs)+1` 冒充全历史分配器。恢复计数器属于后续存储接入。
- 在 uint 域耗尽时明确失败，不回绕为 0 或旧号；用 near-limit 测试接缝验证，不分配数十亿对象。
- string Capture 可保留原不可变实例，null/空串/孤立代理项都不需要内容解码。
  多个独立空串的恢复问题因此不会阻塞本片，但不宣布它已经解决。

下文第 4 节及第 5 节的数字回收/预留解除策略均为延期素材，不是本片实现要求。

## 2. ObjectId 的作用域

持久查找的完整含义为 `(Store, exact StateRevision, ObjectId)`；候选阶段用所属 CapturedGraph 表示上下文。
同一持续存活对象在相邻保存中保留数字 ID，以支持不变对象/引用的复用；这不意味着裸 uint 是全历史实体身份。
同一 revision 内一个非零 ID 只有一个对象；正常 Capture 对规范化后的一个 CLR 实例只登记一个 ID。
读取输入允许多个空串 ID 均解析为 string.Empty，是 DB-025 的显式例外。0 表示 null，无独立 null 条目。
不同 ID 的非空 string/其他引用条目不能在该图的恢复过程中合并成一个 CLR 实例，即使内容相等。
空串按 DB-025 的后续裁决统一 string.Empty；正常 Capture 先规范化再登记，只产生一个存活空串条目。

例如 R10 的 7 号为旧字符串，R11 移除 7，R12 的 7 号为新字符串。
R10 和 R12 的查询分别经自己的 ObjectHeadMap，保留 R10 不妨碍 R12 复用数字 7。
分支或同时打开的旧 revision 使用各自的工作映射；跨 revision 的裸 ID 相等不能证明对象相同。
业务若需要脱离 revision 的长期实体键，使用业务字段或另行定义的句柄，不能暗用 ObjectId。

**引用解析上下文是所加载的目标 revision，不是 owner payload 最初写入的 revision。**
旧 owner body 被新 revision 沿用时，其中的引用号也经新 revision 的 live map 解析；
这样子对象更新不要求 owner 重写。反过来读取旧 revision 必须完整使用旧上下文。
缓存 decoded graph 必须带 revision/view；原始 DTO/字节缓存可以按 exact record 地址和布局缓存，
但不能把已解析的 CLR 引用跨 view 当成同一份结果。Frame 可能含多个对象，record key 还须含对象定位信息。

## 3. Capture 的目标形状

每个工作视图拥有 parent 基线，以及按 ReferenceEqualityComparer 建立的 `object -> uint` 映射。
首次打开/加载时拟由同一 materialization 的 ID→实例表建立反向映射；多个空串 ID 的规范化问题
见 DB-025 §4，该导入路径尚未实施，不能直接反转含空串别名的字典。不用对象自身字段承载 ID，
也不建立进程全局跨 Store、跨 revision 的字符串池。

一次候选 Capture：

1. 调用方在 Capture 期间提供稳定的领域视图；首片串行、一个在途候选。
2. 从显式 roots 登记对象。先确定其 ID 并登记，再将未处理对象入队；重复引用只返回相同 ID。
3. 处理对象时调用实际类型对应的生成 Capture，复制标量，并将引用成员交给同一上下文登记。
   发现与字段复制可以合并；不在 Write 阶段再次扫描领域对象。
4. string 是无出边的叶对象：独立条目保存 string 内容，可以保留原 string 实例作为不可变内容，
   不需要再复制其字符数组。相等非空内容的不同实例仍有不同条目；空串按 DB-025 统一 Empty。
5. 队列排空后封闭候选：roots、ID→条目、完整 live 集合、候选新增绑定与待移除集合。
   可按 ID 排序供下游消费，但列表下标不成为 ObjectId，分配顺序不声明图同构 canonicalization。
6. 后续只读取候选。Write 直接编码 DTO 中的 ID；不需要 Type 查表或回头查询领域引用。

生成式形状草图（仅示意，不是现有 API）：

```csharp
// Name 对应 nominal String 字段；DTO 里是 uint 引用槽位。
internal static VCurrent Capture(Character value, CaptureContext context) =>
    new(value.Level, context.CaptureString(value.Name));

// DTO body 不必接收图上下文：对象查找已经在 Capture 完成。
internal static void Write(ref BinaryPayloadWriter writer, in VCurrent state) {
    writer.WriteInt32(state.Level);
    writer.WriteUInt32(state.NameId);
}
```

Vn 的物理 uint 字段不改变声明 Schema 中 String 的类型约束。
ReadVn 先还原 ID 值；对象存在性/声明类型相容性由外层图验证与 Restore 处理。
继承的 Capture 共享上下文；以后自定义 struct 的 Capture 递归接收相同上下文，输出嵌套 DTO。
成员级仍静态绑定；混合类型的对象列表可在对象边界暂存 boxed readonly DTO，不因此引入逐字段动态分派。
首片条目容器已按消费者收敛到 §3.1/3.2，不冻结通用 registry/interface。

例如两字段引用同一个 `s1`，第三字段引用内容相等但不同实例的 `s2`：

```text
roots: [1]
1 -> CharacterDTO { NameId=2, AliasId=2, CaptionId=3, OptionalId=0 }
2 -> StringState { Content="Ada" }   // 对应 s1
3 -> StringState { Content="Ada" }   // 对应 s2，不能与 2 合并
```

条目至少携带明确的内存 kind（String 或 Durable + exact Schema）和内容。
它对应未来对象头 TypeCodec 的职责，但首片不冻结数组/泛型 TypeCodec，也不假造完整磁盘图格式。

## 3.1 G0 接缝：生成 root 适配器（用户已采纳）

2026-09-06 核验基线 `8816f0c`，开工工作树干净。主代理与独立只读子代理检查了
`DurableSchemaGenerator.BinaryBody.cs`、`DurableBase.cs`、现有 DTO 测试与真实包消费者。
以下方案随后获用户明确采纳；实施中的代码与验收状态另见工作单，不把方案确认当作运行证据。

两种入口可以共用同一个 Runtime 泛型登记方法，差别不需要上升为两套 capture 框架：

| 方案 | 调用方责任 | 取舍 |
|---|---|---|
| 调用方显式 binding | 每处传入领域值、Schema、typed Capture 函数 | SG 改动略少，但三者配对依赖调用方；容易把错误版本的 Schema 交给正确 DTO |
| **推荐：SG 生成 root 适配器** | 调用每个 concrete 类型的 `AddRoot(context, value)` | 生成器配对当前 Schema、DTO 和 Capture；仅多一个 internal 静态包装方法，无全局 registry |

推荐的用户侧调用形状（位于领域类型所在的下游程序集）：

```csharp
var session = new CaptureSession();
using var capture = session.BeginCapture();
uint characterId = Character.__DurableBinaryBody.AddRoot(capture, character);
uint itemId = Item.__DurableBinaryBody.AddRoot(capture, item);
var candidate = capture.Seal();
// 检查或消费 candidate；此后领域对象变化不影响它。
session.Accept(candidate); // 或 session.Discard(candidate)
```

`__DurableBinaryBody` 和 Vn 继续 internal；首片无需给领域类增加 public/virtual/interface 成员。
领域库之外的调用方如有需要，可由领域库提供普通 public 包装方法；不在首片自动公开 DTO 或建立跨库类型发现。
本片多 concrete roots 指同一消费程序集中的显式 typed 调用，不是 `IEnumerable<DurableBase>` 的自动多态分派。

建议的 Runtime public 边界均放在现有 `Atelia.DurableGraph`，构造权尽量留在 Runtime：

| 类型 | 必要成员（签名提案） |
|---|---|
| `CaptureSession` | public 构造；`CapturedGraph? Current { get; }`；`CaptureContext BeginCapture()`；`void Accept(CapturedGraph candidate)`；`void Discard(CapturedGraph candidate)` |
| `CaptureContext : IDisposable` | internal 构造；下述泛型 `AddRoot`；`uint CaptureString(string? value)`；`CapturedGraph Seal()`；`void Dispose()` |
| `CapturedGraph` | internal 构造；`IReadOnlyList<uint> RootIds`；`IReadOnlyList<CapturedObject> Objects`，公开视图无可写集合旁路 |
| `CapturedObject` | internal 构造；`uint Id`；`CapturedObjectKind Kind`；`DurableSchema? Schema`；`TState GetState<TState>() where TState : unmanaged`；`string StringContent` |
| `CapturedObjectKind` | 内存枚举 `Durable`、`String`；不是持久 TypeCodec 编号 |

Durable 条目携带 exact Schema，私下装箱 DTO；`GetState<TState>()` 只按准确 DTO 类型返回副本，
不用 public `object Payload` 暴露装箱存储。String 条目 Schema 为 null，StringContent 返回不可变内容。
访问错误 kind 或错误 DTO 类型明确抛错，不返回默认值。Objects 按 ID 升序，RootIds 保留输入顺序及重复/null。
条目集合本身即 live 集合，不再公开第二份可独立变动的 membership；按 ID 查找可先由消费者枚举完成。

生成代码调用的底层方法建议为：

```csharp
public uint AddRoot<TDomain, TState>(
    TDomain? value,
    DurableSchema schema,
    Func<TDomain, CaptureContext, TState> capture)
    where TDomain : DurableBase
    where TState : unmanaged;

// 下面位于 Character.__DurableBinaryBody，假设当前版本为 V1。
internal static uint AddRoot(CaptureContext context, Character? value) =>
    context.AddRoot<Character, V1>(value, V1.Schema,
        static (source, shared) => Capture(source, shared));
```

`unmanaged` 是本片 DTO 不含托管引用的编译期边界：当前 13 标量和 string 转成的 uint 槽均满足它。
它不把领域对象改为值类型，也不是一般自定义 struct 支持；以后布局确实需要托管内容时再重审。
这一泛型方法因下游生成代码调用而必须 public，但不是对任意手写 codec 的格式/Schema 正确性证明；
SG 负责 Schema/DTO/字段内容配对，Runtime 负责会话、身份与生命周期。只写 `where TState : struct`
不足以阻止 DTO 间接保留可变领域引用；首片无需为任意 DTO 建深拷贝或逐字段反射验证器。

本次核验出的行为边界：

- 非 null root 在登记前检查 `value.GetType() == typeof(TDomain)`，包括重复 root；
  `Base` binding 接收 `Derived` 必须失败。该检查不枚举成员，不作 Type 哈希查找。
  **不能把 exact 检查塞进现有 Capture**：现有基类 Capture 的测试明确传入派生实例。
  abstract 类型只生成段级 Capture，不生成独立 root 适配器。
- 泛型登记由 Runtime 在回调前分配/登记 ID，重复 root 只追加根引用，不重复捕获 body。
  建议 Begin/AddRoot 阶段只登记根，Seal 按首次登记次序调用各根 Capture；string 在该次 Capture 内登记。
  这样 roots 先占号，字段仍 base-first/FieldId 顺序。稳定领域视图必须覆盖整个 Begin 至 Seal 阶段。
  相同实例的重复 binding 必须一致；不能采用“第一次决定，其余错误 Schema/DTO 静默忽略”。
- 对象边界每对象一次 typed 委托可接受；字段仍是静态原语和 `CaptureString` 调用。
  Context 不公开原始 ID 分配、占位后任意 Complete 或可写字典，避免让调用方拼装未封闭的对象列表。
- `CaptureString` 是生成代码的引用槽入口，在 Seal 执行 Capture 回调期间有效；
  不提供独立 string root，null root 经 typed root 适配器登记为 0。
  回调期间拒绝 AddRoot/Seal 的重入；本片没有 Durable 字段互引，不需提前实现通用遍历队列。
- 有 string 的 Capture 显式接收共享 context，基类需要它时逐层传递；纯标量 `Capture(value)` 保留。
  历史 String Schema 仍生成 uint DTO/UInt32 body；历史领域 Capture 不生成。
- Seal 后 Context 不再可写。Accept/Discard 由所属 session 消费候选；Dispose 放弃仍在构建或尚未裁决的候选，
  已 accept/discard 后 Dispose 幂等。任何构建失败终止本次候选，允许下一次 Begin；烧号但 parent 不变。
  构建器/会话私下持有实例绑定；终止候选应清理回调闭包和临时实例引用，保留旧候选数据不应保留其领域实例。

**G0 裁决**：用户明确采纳“SG 生成根登记适配器，Runtime 提供统一会话与候选管理方案”。
命名和内部集合实现可按验证结果调整，不另外冻结新程序集、通用 codec 接口或未来多态入口。
G1–G3 以真实 SG 编译、错误路径、包消费者及独立审查证明该方案成立。

## 3.2 首片实施结果（2026-09-06）

G0 推荐方案按用户决定落地：Runtime 的 CaptureSession/Context/CapturedGraph/CapturedObject，
以及 SG 的 concrete AddRoot、current string Capture、current/history UInt32 ID DTO/body 已实现。
上下文采用先登记根、Seal 顺序调用根 Capture 的最小流程；未实现一般 Durable 引用遍历队列或循环。
相同 root 重复登记会核对 exact Schema、DTO 类型和委托相等性；不要求委托实例 ReferenceEquals。
已封闭候选上的非法写入会拒绝但不破坏既有候选；构建中的非法操作/异常终止候选并保留 parent。

对象条目私下装箱 unmanaged DTO、按值取出；图仅保存冻结内容与根 ID，管理实例表留在会话当前/未决上下文。
已修复审查时实际发现的只读集合漏洞：Array.AsReadOnly 可经 ICollection.SyncRoot 暴露底层数组，
当前使用私有只实现 IReadOnlyList/IEnumerable 的数组包装，两种集合都有反旁路测试。

历史结果（2026-09-06；从 Git `6dbaf0e` 的首片工作单 §5 提取）：根 build 0 warnings/errors；ReferenceCapture 聚焦 37/37；
全套 503/503、零跳过；独立只读审查覆盖身份/隔离/历史/包边界，无剩余阻塞。真实单包消费者通过，准确输出
`BinaryBody:012154:True:ReferenceCapture:True`，当次忽略产物位于
`experiments/PackageConsumerProbe/obj/run-20260906024101-5228`。当前回归由上述会话和生成器测试维护，历史包结果不授予当前包资格。
这些结果不证明字符串对象解码、领域恢复、ID 回收或持久 Save；后续边界仍按第 7/8 节分别排期。

## 4. 回收与复用时机：延期素材，首片不实施

以下是用户决定延期回收之前的候选方案，待重新排期时复审。若实施回收，可把 parent 的所有 live IDs 暂时保留；新对象只从 parent 已空闲且未被本候选预留的 ID 中分配。
本轮发现的 Removes 在发布成功后才释放，下一个候选可以立即复用；不会等待全部历史 revision 被删除。
首个内存分片只显式模拟 accept/discard，不将其称为实际 durable commit。

优点是可一遍 Capture 并写入最终 ID，parent 基线无需提前修改。
代价是构建候选时需要容纳 parent live 与新增对象的并集；ID 域已满时，哪怕本轮删除很多对象也可能分配失败。
该边界必须明确报错；不能整数回绕或偷偷抢占尚未完成可达性判断的 ID。

同一次候选内回收并复用也是正确的可选方案，并非原则上禁止。需要先确定哪些 parent 对象仍可达，
再把已死对象的号分配给新对象（例如引用发现/标记一遍，再 Capture），或使用临时身份再重定位 DTO。
否则边遍历边抢旧 ID 可能抢走尚未访问到的存活对象；同号新旧对象的分类也不能只看 parent.ContainsKey(id)。
在出现接近容量上限的消费者前，建议避免首片承担这层中间状态。

无论何时复用，新的占用者都是 Insert，必须写自己的 Base，不能延续该号上一占用者的 Delta 链或冷读统计。
即使 Schema/内容恰好相同，也不能只凭 ID 和字节相等认作上一对象的 NoChange。
未来 lineage 若按 ID 向前寻找，必须在本次新建边界停下；ID 数字相等不足以跨空档连接历史。

当前 Storage 的 Local/Removed 集合要求互斥，不能编码同一 revision 的“Remove 7 + Insert 7”双动作；
若将来选择同候选复用，最终 membership 可以只表现为新 Base 对 7 的覆盖，但上层必须保留新占用者的事实。
推荐的隔发布复用无需改变当前 Storage 合同；这不是产品已验证完整复用/lineage 的声明。

## 5. 三种回收与失败边界

| 层面 | 判断/动作 | 与 ID 复用的关系 |
|---|---|---|
| 领域持久图可达性 | 从 durable roots 标记，`parentLive - candidateLive` 得到 Removes | 决定新 revision 不再包含哪些对象；不用 CLR GC 结果决定 |
| 内存 ID/对象槽位回收 | 成功安装候选后释放旧映射/槽位，后续分配复用 | 可以在旧 revision 仍保留时完成 |
| 磁盘历史/物理 GC | 检查保留 revisions 及其重建依赖后才能删除 records/segments | 与数字 ID 空闲分开；不在当前分片实施 |

只有 root 所属持久图的可达性参与标记；side table 为管理而持有的强引用不算领域 root。
发布成功后移除不可达对象的反向映射，释放持有的领域实例；旧基线若被显式保留，拥有自己的视图/映射。
应用仍持有已退役 CLR 实例，之后重新挂入根图时，它按当前会话中的未登记对象处理，不能取回已分配给别人的旧号。
如果业务希望离图期间仍保留身份，需要显式 pin/root 语义，属于另一个产品合同。

候选新增映射/预留使用候选所有权，未成功发布前不删除 parent 绑定。
明确失败或放弃：丢弃候选、解除本候选预留，parent 不变。
成功：安装实际提交的冻结候选与对应映射，然后释放旧槽位；不能重新 Capture 领域对象替代提交内容。
发布结果不确定：保留/封锁相关预留，reopen/reconcile 后再裁决，不能当作失败立即复用。

首片不向应用暴露可跨安装继续使用的裸池 index/ref；需要的读取总通过指定 view。
如未来要提供脱离 revision 的会话 handle，再选择 scope token/generation 与失效合同；
不必因此把 generation 预先塞进持久 uint ObjectId。

## 6. StateJournal 素材评估

已读取相邻仓库 `atelia/src/StateJournal/Pools/` 的 SlabBitmap、部分 Impl、SlotPool、SlotHandle，
并检查 GcPool 的组合形状；本轮未构建/运行该仓库，也未复制或修改其代码。

- SlabBitmap 使用 4096-bit slab、word/slab 摘要、位计数及集合/枚举操作，可作为 live/free/marked 集合的机制素材。
- SlotPool 以 free bitmap 定位较小空闲槽；value slab 按需分配，全空时释放；支持按既有 handle/value 重建。
  可借鉴低号优先、占用与内容分离、释放引用和重建的机制。
- 它不是整个引用身份屏障：仍需要 ReferenceEqualityComparer 的反向对象映射、领域引用遍历及候选隔离。
- 当前 SlotHandle 是 24-bit index + 8-bit generation，SlotPool 容量上限约 16M，generation 自然回绕；
  这不是全部非零 uint ObjectId 域，也不是无限期 stale-handle 保证，不宜原样定为持久格式。
  SlabBitmap 自身使用 int capacity/index，若用于 uint 域，还需单独校验可寻址范围。
- GcPool 的文档合同是 BeginMark 与 Sweep 间不 Store；与本设计“Capture 时发现新对象并分配”不直接吻合。
  可借鉴其位图求差/回收机制，不能未经改造直接调用其生命周期。
- 其 MoveSlot/compaction 会移动 index；DurableGraph 的活对象 ID 不应随内部槽位压实静默改变。
  本片不需要迁入 compaction、回滚日志和通用池框架，也不增加对 StateJournal 整个项目的依赖。

## 7. 实施候选与验收问题

分片顺序与当前状态：

1. **引用 Capture 与候选生命周期（已实现）**：已支持标量的领域 roots + string 字段、继承 Capture 共享上下文；
   产出闭合的混合对象列表与 ID DTO。内存 parent/accept/discard 见证稳定 ID、移除、单调分配和失败隔离。
   首片尚无 Durable 相互引用；只登记根并顺序 Capture，不宣称已支持自环/互环。
2. **字符串对象编码和引用解析（DB-025 已实现并验收）**：见 [DB-025](../0025-string-object-decoding-slice/0025-string-object-decoding-slice.md)。
   保留 ID DTO，以 string 解码表和 SG 各版引用校验完成 typed 字节见证，领域 Restore 另片；
   空串已裁决为两端统一 Empty，不再要求独立分配。引用槽的 uint/0 body 已实现，DB-025 接字符串记录内容；
   为各条目建立加载表再解析 owner DTO，验证存在性/类型/共享。随后才接 Durable 对象壳与循环恢复。
   当前只返回 typed 引用见证，不把 DTO Upgrade/构造规则并入本片。

必需见证：同实例多引用、相等内容不同实例、null/空串/代理项、Capture 后领域引用变更不影响候选；
跨 Capture 存活对象保持 ID；旧实例重新挂入、discard 重试取得新号且不串号；uint 耗尽明确失败。
R10→移除→R12 复用且旧 R10 可读属于回收分片；missing ID/错误引用类型的载入拒绝属于恢复分片。
完整磁盘保存不在这些内存见证的结论范围内。

string 空内容路径返回 string.Empty 已与 DB-025 的后续用户裁决一致：两端显式规范化空串，
无需保留不同零长度实例身份。此前独立分配研究已留作历史附件，不再是恢复前置条件。

首片不提供 pin：accept 后离开 durable roots 的实例绑定结束，数字不回收。恢复完整领域对象另片推进。
隔发布/同候选 ID 复用仍留待后续；空串规范化见 DB-025，公开生成代码接缝见 §3.1/3.2。

## 8. 自定义 struct TODO：可独立排期，共享 Capture 接缝

用户明确：复合值类型嵌套布局，struct 的持久版本变化算作 owner Schema 变化，要求 owner 递增版本。
建议先记入近期 TODO，string Capture 不依赖 struct 先完成；但两片也不是完全零交互：

- 字段描述要从 FieldId+TypeTag 扩为可表达 `InlineValue(exact StructSchema)` 的形状；history 保存 exact 依赖，
  runtime equality/登记与两端历史校验都检查闭包。不能只给 struct 追加一个不带参数的 tag。
- `PositionV2 -> CharacterV4 -> 派生/外层容器值布局` 按 inline/base exact 边传播；
  nominal 引用边不传播目标版本，避免经对象互引构造无限 exact Schema 闭包。
- SG 为 struct 生成 readonly Versioned DTO；owner DTO 嵌套该版本 DTO，按字段顺序嵌套编码，
  不进引用对象表、不分配 ObjectId。历史 owner 不依赖当前 struct CLR 定义/名字仍然存在。
- 含引用 struct 的领域值直接浅拷贝不够：递归 Capture 标量/嵌套 DTO，引用转 ID，使用同一 CaptureContext。
  先做纯标量 struct 时可无上下文；引入引用后才添加所需参数，不预建空框架。
- ref accessor 仍用于真实 struct 字段和数组元素槽位；Write/Read 面向 DTO，未来 Restore 才写回领域 ref。
  明确 readonly struct、构造/填充边界，重审 legacy DG0011 限制，不能照搬 class 直接赋值规则。
- 首片建议非泛型 partial struct + 标量/已支持嵌套值，泛型、boxed struct 身份和数组容器另排。
  验收包含两层嵌套、FieldId 排序、历史 struct 消失、版本未传播拒绝及引用字段后续组合。

排期理由：先确定引用槽的“领域引用 -> ID DTO”边界，后续 struct 就能复用它；
值 Schema 扩充是独立 metadata 工作，不需要为了开始 string 而提前完成。
