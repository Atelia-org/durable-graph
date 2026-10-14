> 历史快照：2026-09-06，来源提交 `54a33df`。正文按原稿保存，仅重定位 Markdown 相对链接。
> 文中的当前、下一步和状态均属于当时语境，不作为当前规格或排期。
> 续工从 [产品工作集](../../../src/PROJECT-STATE.md) 开始；本文件冻结，仅修复引用或史实错误。

# DurableGraph 目标设计与原型实验计划

> 状态：Target Design / Prototype Working Note  
> 目标读者：项目作者与参与实现的 Coding Agent  
> 工作名：`Atelia.DurableGraph`  
> 本文性质：记录目标、核心不变量、原型切片与待验证问题；不是当前实现事实，也不意味着所有 API 已冻结。

2026-09-05 校准：用户选择所有受支持引用对象（含 string、数组、BCL 容器）统一引用身份，
值成员嵌套布局、class base-first、对象头 TypeCodec。生成器/静态设施的当前草图见
[DB-018](../../design-branches/0018-generated-graph-codec-shape/0018-generated-graph-codec-shape.md)，产品进度见 [src/PROJECT-STATE.md](../../../src/PROJECT-STATE.md)。
祖先 Schema/history 的 metadata 分片已由 [DB-019](../../design-branches/0019-schema-ancestry-implementation-slice/README.md)落地；
typed 值槽位与 SZ/rank-2 元素循环已由 [DB-020](../../design-branches/0020-typed-slot-array-binding-slice/README.md)落地；
[DB-021](../../design-branches/0021-generated-primitive-body-slice/README.md)已生成当前 bool/int/long class 的继承分段 body；
按 stored Schema 的运行时 decoder 分派、SG 泛型 body、完整数组对象 codec 与图恢复仍是后续目标。

后续校准：[DB-022](../../design-branches/0022-versioned-state-dto-capture/README.md)选择领域图先捕获 Versioned DTO，
后续比较、估算与编码消费捕获状态；已实现 scalar readonly Vn、current Capture、各版本 typed DTO Read/Write，
取代 DB-021 直接领域 body。运行时历史 Schema 分派、DTO 升级、领域恢复与 StateStore 基线管线尚未实现。
[DB-023](../../design-branches/0023-scalar-schema-dto-slice/README.md)进一步将 Schema/历史/DTO 贯通到 13 种标量，
下一候选是 string 引用 Capture；string 仍不能作为 DTO 内联字段编码。

---

## 1. 背景与动机

本项目综合继承三个方向的经验：

- **StateJournal**：内存对象图、持久身份、增量提交、append-only frame、commit/head、故障恢复与版本历史。
- **SessionJournal**：raw authority、exact head、不可变事实、派生 Recap/Memory、来源围栏以及 fail-closed recovery。
- **DramaBoard**：长期存在且不断演化的领域对象世界；HistoryLog、认知状态、索引、缓存和大量可重建派生信息。

旧 StateJournal 证明了增量持久化对象图的可行性，但应用层需要把领域模型翻译为 `DurableDict`、`DurableDeque` 等专用容器构成的持久化 DSL，并依赖运行时 ChangeTracker 维护变更信息。这会把大量机械负担暴露给业务代码和 Coding Agent。

曾考虑通过改造 Lua VM，使整个 Lua realm 在顶层事务结束、执行栈安静时增量保存。但该路线会把项目核心问题扩大为语言运行时改造：closure、coroutine、userdata、宿主资源、VM 版本演化与调试工具都会成为持久化协议的一部分。

当前方案放弃修改 VM，继续以 C# 为领域语言，利用 Roslyn Source Generator 和必要时的 `DynamicMethod` 获得类似编译器/VM 的类型发现、强类型遍历、比较和序列化能力。

## 2. 一句话心智模型

**DurableGraph 是一个 C# 原生、以持久身份连接对象、以版本化 Schema 解释数据、以 append-only commit 保存长期领域世界的嵌入式框架。**

应用仍然编写普通的 C# 领域对象与领域逻辑；框架负责：

- 为 durable object 分配并恢复长期身份；
- 生成规范化 Schema、对象图遍历、强类型比较与 codec；
- 在保存时比较当前内存对象图与上次提交对象图；
- 只为新增或变化的对象追加新版本；
- 保存实际涉及的 Schema；
- 加载历史版本并执行显式升级；
- 重建未持久化的索引和缓存；
- 区分权威状态、不可变素材与可删除派生数据；
- 以单一 commit publication boundary 保证四个 Store 的一致性。

## 3. 项目定位

DurableGraph 不是：

- ORM；
- 关系数据库替代品；
- 任意 CLR heap、线程或 continuation 的 checkpoint；
- 完全透明的 orthogonal persistence runtime；
- 依靠事件重放才能恢复状态的纯 event-sourcing 框架；
- 通用分布式数据库；
- 第一版就支持任意并发写者和跨机器事务的系统。

DurableGraph 更接近：

- selectively persistent / reachability-based object graph；
- object-level MVCC snapshot；
- append-only state journal；
- versioned schema registry；
- immutable artifact store；
- rebuildable materialized views。

它有意不追求完全正交持久化。哪些内容 durable、哪些内容 transient、哪些内容属于 artifact、哪些内容只是 derived projection，必须是可检查、可解释的设计决定。

## 4. 设计目标

### 4.1 主要目标

1. **领域模型与存储模型尽量一体化**  
   应用直接使用 C# 类、接口和集合表达领域；不要求手工翻译为通用字典树。

2. **Coding Agent 友好**  
   常规业务开发应表现为普通强类型 C#。Schema 错误尽量在编译期诊断，运行时失败应为封闭的 typed outcome。

3. **长期可读与可升级**  
   盘上数据必须携带足够的 Schema 事实。不能只依赖“当前程序集恰好还能反序列化旧字节”。

4. **默认 fail closed**  
   未知 Schema 版本、同版本 SchemaHash 不一致、缺失升级器、引用损坏、来源围栏不匹配时拒绝加载或拒绝消费，不能猜测。

5. **持久身份与对象图语义**  
   支持共享引用、循环引用、稳定身份、从 root 计算 reachability，以及按对象独立保存新版本。

6. **不依赖运行时 ChangeTracker**  
   普通字段写入不需要 setter hook、`MarkDirty()` 或代理拦截。保存时通过生成代码比较当前对象与 committed baseline。

7. **权威数据与派生数据分离**  
   Schema、State、Artifact 是不可删除的 append-only authority；Derived 可删除、可失效、可重建。

8. **明确的 durable commit**  
   四个逻辑 Store 共享一个最终发布点。一次成功 commit 精确说明哪些 Schema、State 和 Artifact 共同构成本次权威状态。

### 4.2 第一阶段非目标

- 多写者分布式一致性；
- 任意查询语言；
- 自动生成面向全部历史版本的业务兼容逻辑；
- 自动推断破坏性 Schema migration；
- closure、delegate、线程、Task、文件句柄、Socket 等执行/外部资源的持久化；
- 跨 Repository 自动保留对象身份；
- 在未验证需求前优化到亚线性全图扫描；
- 直接替代 SessionJournal 当前所有生产代码。

## 5. 核心不变量

以下规则优先于 API 便利性和短期性能。

### 5.1 Authority 不变量

- 只有已发布 `CommitManifest` 指向的 candidate 才属于权威状态。
- SchemaStore、StateStore、ArtifactStore 中未被已发布 commit 引用的数据只是 orphan candidate。
- DerivedStore 永远不能反向成为 State、Artifact 或 Schema 的权威来源。
- “文件存在”“序号较新”“时间戳最新”均不足以获得 authority。

### 5.2 Schema 不变量

- 每个 durable type 必须有稳定 `TypeId`。
- 每个 durable member 必须有稳定 `MemberId`。
- `SchemaVersion` 必须显式递增；历史版本不得静默改写。
- 规范化 Schema bytes 必须能计算稳定 `SchemaHash`。
- `(TypeId, SchemaVersion)` 相同而 `SchemaHash` 不同是硬错误。
- 盘上版本未知、源码版本缺失或升级路径不完整时拒绝加载。

### 5.3 Identity 不变量

- 一个在相邻提交中持续存活的对象保留同一 ObjectId；ObjectId 的查找语义属于指定 StateRevision。
- 同一图内 clone 产生独立对象身份；fork 的视图作用域及是否保留数字编号另行定义。
- 回收后的数字 ID 可以复用；旧 revision 仍按自己的 ObjectHeadMap 解释。复用后的新占用者不能继承旧对象的 Delta 链。
- 同一 commit materialization 中，一个 ObjectId 只能对应一个 CLR 对象实例。
- CLR reference identity 用于当前视图的实例登记；持久查找需要 Store、exact StateRevision 和 ObjectId，裸 ID 不是全历史实体键。

### 5.4 Commit 不变量

- Four stores, one commit。
- candidate data 必须先写入并完成规定的 durability barrier，之后才能发布 manifest/head。
- commit 失败不能被模糊成“可能成功，直接重试”；必须返回 publication state，并要求 reopen/reconcile。
- 成功返回 commit 后，在声明支持的故障模型内，数据应满足 durability 承诺。

### 5.5 Derived 不变量

- 删除全部 DerivedStore 不影响权威状态加载。
- 每个 derived artifact 必须记录精确输入围栏与 builder/recipe 版本。
- stale derived data 必须表现为 miss/unfulfilled，而不是被 latest/global scan 重新选中。

## 6. 持久数据分类

为了避免把所有 C# 对象都当成同一种存储实体，初步区分五类：

### 6.1 Durable Entity

- 继承 `DurableObject` 或通过未来确定的等价机制获得 durable identity。
- 有独立 `DurableId` 和独立 object version。
- 可以被多个对象共享引用，也可以参与循环图。
- 修改后只产生自己的新 object version；引用它的父对象若引用身份未变，不必重写。

统一 identity 的范围还包括受支持 string/数组/BCL 容器；这些对象不必继承有 ID 字段的基类，
外层映射可以保存其身份。非空 string 内容相等不合并实例，引用成员只保存 ID，字符串记录自己保存内容。
空字符串是明确例外：Capture 与读取均规范化为 string.Empty，不保留零长度实例之间的引用区别；
null 仍与空串区分。规则见 [DB-025](../../design-branches/0025-string-object-decoding-slice/0025-string-object-decoding-slice.md)。

### 6.2 Durable Value

- 没有独立持久身份，按值嵌入 owner payload。
- 适合小型 struct、record-like value、坐标、时间范围等。
- owner 比较和序列化时递归处理。

### 6.3 Artifact

- 不可变、可寻址、通常体积较大或自然形成历史序列。
- 典型内容：LLM Session Messages、DramaBoard HistoryLog、附件、历史输入输出 envelope。
- State 通过 `ArtifactRef<T>` 或等价 typed address 引用它，而不是把完整内容常驻在 State 对象图中。
- Artifact 可以 lazy load，并提供只读 `View` / `Query`。

### 6.4 Transient

- 不落盘。
- 典型内容：索引、cache、反向引用、运行期查找表、锁、服务引用。
- 在加载和迁移完成后由 `RebuildTransient` 重建。

### 6.5 Derived

- 可由 State + Artifact + Schema + recipe 重建。
- 典型内容：Recap、DynamicMemory、embedding、搜索索引、代价较高的聚合结果。
- 与普通 transient 的区别是 Derived 可以跨进程缓存，但不具备 authority。

## 7. 建议的编程模型草图

API 名称尚未冻结，以下只表达 Shape。

```csharp
[Durable(TypeId = "drama.character", Version = 2)]
public sealed partial class CharacterState : DurableObject, ICharacterState {
    [DurableMember(1)]
    public required string Name { get; init; }

    [DurableMember(2)]
    public PlaceState? CurrentPlace { get; set; }

    [DurableMember(3)]
    public ArtifactRef<MessageHistory> History { get; set; }

    [Transient]
    private Dictionary<string, RelationshipState> _relationshipByName = new();

    protected override void RebuildTransient(RebuildContext context) {
        _relationshipByName = Relationships.ToDictionary(x => x.Name);
    }
}
```

历史版本源码保留：

```csharp
[Durable(TypeId = "drama.character", Version = 1)]
internal sealed partial class CharacterStateV1 : DurableObject, ICharacterState {
    [DurableMember(1)]
    public required string DisplayName { get; init; }
}
```

升级器显式注册：

```csharp
[DurableUpgrade(TypeId = "drama.character", From = 1, To = 2)]
internal static CharacterState Upgrade(CharacterStateV1 old, UpgradeContext context) {
    // 显式承认旧语义，并创建新版本对象。
}
```

### 7.1 Base class 与 Attribute 的关系

第一版可以同时要求：

- `DurableObject`：承载 `DurableId`、repository lifetime、必要的内部状态和 hook；
- `[Durable]`：声明稳定 TypeId、SchemaVersion，并触发 Source Generator；
- `[DurableMember(id)]`：声明稳定字段编号；
- `[Transient]`：明确排除成员。

长期应评估强制基类占用 C# 唯一继承槽的代价。若 Source Generator 足够成熟，可考虑支持 `[Durable] partial class + IDurableObject`，但这不是首个原型必须解决的问题。

## 8. VersionedSchema

### 8.1 SchemaDescriptor

每个版本的 Schema 至少应描述：

- `TypeId`
- `SchemaVersion`
- CLR binding hint（仅用于诊断或当前实现绑定，不作为长期身份）
- 直接基类的 exact Schema 绑定（递归闭合祖先布局）
- durable member 列表
- 每个 member 的稳定 `MemberId`
- value/reference/artifact/transient 分类
- 内嵌 struct 的 exact Schema 依赖（其版本变化要求 owner 及 inline/base 依赖者递增版本）
- nullability 与必要约束
- collection/value codec kind
- 允许的 polymorphic type set 或 type family
- canonical schema format version
- `SchemaHash`

### 8.2 Canonicalization

SchemaHash 不能依赖：

- reflection 枚举顺序；
- 源码声明顺序；
- metadata token；
- 编译器生成成员名；
- AssemblyVersion、MVID 等构建偶然值；
- 当前进程随机化 hash。

成员应在声明 Schema 段内按稳定 `MemberId` 规范排序；基类按 exact 绑定组合，不能取当前 latest 定义。
基类 exact 绑定改变要求受影响的派生类显式递增版本。字符串编码、整数编码、TypeId 表达和空值规则必须固定。

### 8.3 Source Generator 的职责

Source Generator 为每个 durable type 生成：

- canonical `SchemaDescriptor`；
- object reference visitor；
- strong typed comparer；
- serializer / deserializer；
- materializer；
- durable value comparer/codec；
- migration registration glue；
- transient rebuild dispatch；
- 编译期 diagnostics。

应尽量在编译期拒绝：

- 重复 TypeId；
- 同类型重复 MemberId；
- 不受支持的字段类型；
- durable 字段指向未注册类型；
- SchemaVersion 非法；
- transient/durable 标记冲突；
- 缺失 partial/base/interface 等结构要求。

### 8.4 DynamicMethod 的定位

`DynamicMethod` 可以用于：

- 无法在编译期看到的插件类型；
- 原型期快速验证反射发现的 codec；
- 与 Source Generator 结果做 differential test；
- JIT 环境下的运行时性能路径。

但它不能拥有另一套 Schema 解释。Source Generator 与 DynamicMethod 必须消费同一逻辑 `SchemaDescriptor`，并产生相同 canonical bytes。

## 9. Schema 演化与版本升级

### 9.1 加载规则

加载某个 object version 时：

1. 读取其 `(TypeId, SchemaVersion, SchemaHash)`。
2. 从 SchemaStore 读取 exact SchemaDescriptor。
3. 检查内存中是否注册 exact 历史版本。
4. 同版本 Hash 不一致则立即失败。
5. 若不是当前工作版本，查找显式 upgrade path。
6. 逐级执行迁移并验证每一级结果。
7. 全图迁移和引用修复完成后，才执行 `RebuildTransient`。

### 9.2 历史版本源码

- 已用于持久化的版本定义不得直接删除或改写。
- 若需重构代码布局，应保持 TypeId、MemberId 和 canonical Schema 不变。
- 历史类可以是 internal，不要求暴露给普通领域代码。
- 升级器应尽量是纯函数：无网络、无 LLM、无时间依赖、无隐式读取 latest state。
- 升级失败不修改权威 Store。

### 9.3 版本共存策略：待实验

目标设计允许不同版本类型实现共同领域接口，使领域逻辑面向能力而不是具体版本。首个原型必须比较两种策略：

**策略 A：State 物化后统一升级到 current version**

- 旧类负责 exact decode；
- 正常可编辑 State graph 只包含当前版本；
- 下一次 commit 保存新版本；
- 领域逻辑简单，但加载会产生迁移成本。

**策略 B：多个历史版本作为活领域对象共存**

- 不必立即迁移全部对象；
- 共同接口承诺可用行为；
- 适合大量历史对象或逐步迁移；
- 但每个历史类都成为长期可执行语义，接口演化和不变量维护成本可能持续增长。

建议原型默认验证 A；Artifact 允许长期保留历史版本，并通过 View/Adapter 读取。只有出现明确收益时，才让普通 State graph 默认采用 B。

## 10. Durable identity 与对象图

### 10.1 DurableId

第一版需要确定：

- ID 是 repository-scoped monotonically allocated integer，还是 globally unique 128-bit value；
- 是否包含 RepositoryId；
- 跨 Repository copy 默认重新分配还是保留 origin identity；
- fork/clone 的明确行为。

2026-09-06 用户澄清：ObjectId 经 StateRevision 解释，允许回收复用，取代早稿“ID 永不复用”。
同图共享/循环引用必须保持；不同 revision 中的相同数字不必表示同一对象。具体复用时机、
候选隔离与内存/物理 GC 分离见 [DB-024](../../design-branches/0024-reference-capture-and-reusable-object-ids/README.md)，尚未实施。
用户随后选择首片采用 session 内单调分配，ID 回收与持久计数器另片推进；不恢复全历史永不复用约束。

### 10.2 Reachability

- 每个 commit 有一个或少量显式 root。
- 从 root 经 durable reference 遍历得到 live object set。
- 新 commit 的 StateMap 只包含本次可达对象。
- 不可达对象不再属于新 commit，但其旧 object versions 仍由历史 commit 引用，因此 append-only history 不被破坏。
- ArtifactRef 不把完整 Artifact 内容纳入 State graph reachability，只纳入精确 artifact address。

### 10.3 集合语义

用户已选择受支持引用类型统一 identity，包含普通 BCL 容器、数组和 string；
相同实例的共享关系及循环属于恢复目标，不因“按内容保存”变成字段内嵌副本。
BCL adapter 保存和重建内容，不能序列化 bucket/capacity 等内部实现来替代内容合同。

具体支持哪些容器、comparer、元素顺序以及依赖尚未恢复字段的 key，仍须按类型裁决，
不能因此声称已支持任意 CLR collection。分配壳、引用登记、内容填充和索引重建的形状见 DB-018。

## 11. 保存时对象图比较

### 11.1 基本算法

保存时以当前 published commit 为 baseline：

1. 从 root 遍历当前内存对象图，按 `DurableId` 建立 current live map。
2. 从 baseline `StateMap` 查找同 ID 的上一 object version。
3. 新 ID：生成新的 object version。
4. 已有 ID：调用生成的强类型 comparer，与 committed payload 比较。
5. SchemaVersion 或 SchemaHash 改变：即使字段值等价，也写新 object version。
6. 相同：沿用旧 object version address。
7. 不再可达：从新 StateMap 移除，但不删除历史记录。
8. 生成新的 StateMap root 和 candidate CommitManifest。

### 11.2 比较语义

- scalar/value field 按其 canonical durable equality 比较；
- durable reference 默认比较 `DurableId`，子对象内容变化由子对象自己的版本负责；
- artifact reference 比较 exact artifact address；
- transient field 完全忽略；
- unordered collection 必须有明确 canonical ordering 或集合 equality；
- 浮点数、NaN、负零、字符串 normalization 等必须显式定义；
- hash 可以作为快速否定或定位工具，但第一版不能仅凭非密码学 hash 宣告对象完全相同。

### 11.3 性能立场

首个版本接受 `O(live object graph)` 的保存扫描，以换取：

- 不漏记字段修改；
- 无 setter interception；
- 无业务热路径 ChangeTracker；
- 可测试的集中 comparer；
- 对 Coding Agent 更简单的编程模型。

若真实性能数据证明扫描成为瓶颈，可加入：

- 上次提交 canonical payload cache；
- per-object fingerprint；
- 分层 graph fingerprint；
- 按页读取 baseline；
- 明确的 immutable/frozen subgraph 快路径。

这些优化不得改变 correctness authority，也不应重新要求业务代码报告 dirty。

## 12. 四个 Store

### 12.1 SchemaStore

性质：append-only、权威、可去重。

保存：

- canonical SchemaDescriptor；
- Schema format version；
- TypeId + SchemaVersion + SchemaHash；
- 必要的 codec/type family 信息。

同一个 SchemaHash 可以只保存一次。CommitManifest 或 object version 必须能精确引用所用 Schema。

### 12.2 StateStore

性质：逻辑可变、物理 append-only、权威。

StateStore 当前选定的多历史 Segment 地址、rollover、OVD/ObjectVersion 与恢复目标，详见
[`MultiSegmentStateStoreProbe/TARGET-DESIGN.md`（归档）](../../../experiments/ARCHIVE.md#multi-segment "原路径：experiments/MultiSegmentStateStoreProbe/TARGET-DESIGN.md")。
正式子系统分层和产品化入口见
[`STATESTORE-SUBSYSTEM-DESIGN.md`（归档）](../../../experiments/ARCHIVE.md#multi-segment "原路径：experiments/MultiSegmentStateStoreProbe/STATESTORE-SUBSYSTEM-DESIGN.md")。
产品当前进展统一维护在 [src/PROJECT-STATE.md](../../../src/PROJECT-STATE.md)。
本节只保留 DurableGraph 全局职责，不重复冻结该探针的 provisional 类型与 wire。

保存：

- object version record；
- DurableId；
- exact Schema key；
- canonical payload；
- durable reference table；
- StateMap / root map；
- commit metadata。

“更新对象”不是覆写旧记录，而是追加该 DurableId 的新 object version，并由新 commit 的 StateMap 指向它。

### 12.3 ArtifactStore

性质：append-only、不可变、权威、addressable、允许 lazy load。

典型 API：

```csharp
ArtifactAddress Append<T>(T artifact);
ArtifactView<T> View<T>(ArtifactAddress address);
ArtifactQuery<T> Query<T>(ArtifactRange range);
```

待确定：

- address 是 content hash、append address，还是 logical ArtifactId + RevisionAddress；
- 是否原生支持 chunk/segment；
- 是否允许一个 artifact 引用其他 artifact；
- Artifact schema 是否复用 SchemaStore；
- HistoryLog/Message stream 使用逐项 artifact，还是 append chunk。

StateStore 中只能保存 exact `ArtifactRef<T>`，不得通过模糊 latest 查询恢复引用。

### 12.4 DerivedStore

性质：可删除、可重建、非权威。

每条记录至少包含：

- derived key；
- exact StateCommitId；
- exact Artifact input addresses/head；
- relevant SchemaHash set；
- RecipeVersion；
- BuilderVersion / model route；
- build status 与必要 provenance。

读取规则：输入围栏不匹配即视为 miss/stale。正常 runtime 不得从全库扫描一个“看起来最新”的结果冒充当前派生状态。

## 13. Four Stores, One Commit

四个 Store 是逻辑职责，不是四个独立发布 authority。

建议的 candidate publication 顺序：

1. 生成本次所需 Schema candidate，写入 SchemaStore。
2. 写入本次新增 Artifact candidate。
3. 写入 changed/new object versions 和 candidate StateMap。
4. 生成 candidate `CommitManifest`。
5. 对所有被引用 candidate 完成 durability barrier。
6. 校验 expected parent/head 未变化。
7. 原子发布 branch/ref/head，使 manifest 成为 authority。
8. best-effort 追加恢复辅助日志；其 authority 低于已发布 head。

DerivedStore 不参与上述事务。

### 13.1 CommitManifest 草图

```text
CommitFormatVersion
RepositoryId
CommitId / CommitAddress
ParentCommitAddress
RootDurableId
StateMapRootAddress
SchemaCatalogRootAddress or exact schema refs
ArtifactCatalogHead or exact artifact refs
CreatedAt (diagnostic, not authority ordering)
OptionalNote
Checksum / integrity metadata
```

字段最终以最小原型实际需要为准。

### 13.2 Commit failure

至少区分：

- candidate 尚未产生；
- candidate 已产生但未完成 durable flush；
- 已 flush、尚未发布；
- primary ref 可能已经发布；
- 已发布但辅助 metadata/reflog 失败。

若 publication state 不确定，Repository 进入 poisoned 状态。调用方必须 dispose/reopen，并将实际 head 与 expected parent、candidate address 精确比较；不能透明 retry。

## 14. 加载流程

建议固定为以下阶段：

1. 打开 Repository，只读取 authority metadata。
2. 选择 exact commit/head。
3. 读取 CommitManifest 并验证 parent/integrity。
4. 加载本次涉及的 exact SchemaDescriptor。
5. 建立 DurableId → object version / placeholder map。
6. 分配对象实例或版本化 materialization placeholder。
7. 反序列化字段并解析共享/循环引用。
8. 执行 Schema upgrade。
9. 验证 durable invariants。
10. 执行 transient rebuild。
11. 暴露 root 给应用。

Artifact 默认仍保持 address/reference，只在 `View` / `Query` 时加载。

## 15. RebuildTransient

`RebuildTransient` 只负责便宜、确定、内存内的重建工作。

必须满足：

- deterministic；
- idempotent；
- 无网络、无 LLM、无文件系统外部副作用；
- 不写权威 Store；
- 可在测试中重复调用；
- 失败时不把半重建对象图交给应用。

单个 virtual method 可能不足以处理跨对象索引和依赖顺序。原型应验证：

- 单对象 `RebuildTransient(context)`；
- repository-level rebuilder registry；
- two-pass：先本地 cache，再全局 index；
- 明确依赖图或固定 point iteration。

LLM Recap、Embedding、DynamicMemory 等不属于 `RebuildTransient`，而由独立 Derived builder 处理。

## 16. 并发与事务边界

首个原型可以明确采用：

- 单 Repository 单 writer；
- 保存期间对象图必须 quiescent；
- mutation 与 commit 由宿主在同一逻辑临界区协调；
- 框架不承诺回滚任意已经执行的 C# 副作用；
- commit 只决定新状态是否成为 durable authority。

保存时全图比较解决的是“如何发现状态差异”，不自动解决“如何撤销失败领域操作”。若以后需要工作态 rollback，可增加 checkout/fork/copy-on-write，而不应混入首个 prototype。

## 17. 原型用例

构造一个同时带有 DramaBoard 与 Session 特征的小型对象世界：

```text
WorldState (root)
├─ Places
├─ Characters
│  ├─ Character A ──relationship──> Character B
│  └─ Character B ──relationship──> Character A
├─ Shared RuleSet
├─ ArtifactRef<MessageHistory>
└─ transient indexes

DerivedStore
├─ WorldRecap
└─ CharacterDynamicMemory
```

该模型应包含：

- 循环引用；
- 多对象共享同一个子对象；
- 新增、修改、移除、重新挂接；
- inline durable value；
- durable collection；
- Artifact lazy load；
- transient index rebuild；
- derived record 的 exact input fence；
- V1 → V2 Schema migration。

## 18. 分阶段原型计划

### P0：术语与最小契约

产出：

- `DurableId`
- `TypeId` / `MemberId`
- `SchemaVersion` / `SchemaHash`
- `SchemaDescriptor`
- `CommitManifest` 草图
- 对象分类与支持类型清单

验收：

- 每个术语有唯一语义；
- 不借用 CLR 名称作为长期身份；
- 写出第一批 fail-closed 规则。

### P1：Source Generator walking skeleton

产出：

- `[Durable]`
- `[DurableMember]`
- `[Transient]`
- 编译期 SchemaDescriptor
- reference visitor
- serializer/deserializer
- comparer

验收：

- 编译相同源码得到完全相同 canonical schema bytes；
- 重排字段声明不改变 SchemaHash；
- 改字段类型但不升版本会被测试或 generator 拒绝；
- Source Generator 与反射/DynamicMethod 路径产生相同结果。

### P2：单文件对象图 round-trip

产出：

- DurableId 分配；
- root traversal；
- 共享引用和循环引用物化；
- 单次 full snapshot；
- RebuildTransient。

验收：

- reopen 后对象值一致；
- reference sharing 与 cycle 一致；
- transient 字段未落盘且正确重建。

### P3：无 ChangeTracker 的增量 commit

产出：

- baseline StateMap；
- generated strong typed compare；
- object version append；
- unchanged object address reuse；
- unreachable object 从新 StateMap 消失。

验收：

- 只修改一个叶对象时，仅该对象及必要索引/manifest 产生新记录；
- 父对象引用身份未变时不因子对象内容变化而重写；
- 任意普通字段修改无需 MarkDirty 仍能保存；
- 重复保存无变化对象图不产生伪业务 delta。

### P4：VersionedSchema 与 migration

产出：

- SchemaStore；
- 历史版本类；
- upgrade registry；
- unknown/mismatch typed errors。

验收：

- V1 数据由 V2 代码成功加载并升级；
- 删除 V1 定义后加载明确失败；
- 同 Version 修改 Schema 后加载明确失败；
- 升级器失败不发布任何新状态。

### P5：ArtifactStore

产出：

- append artifact；
- exact ArtifactAddress；
- `ArtifactRef<T>`；
- lazy `View` / `Query`；
- State → Artifact 引用。

验收：

- 加载 State 不读取完整 message/history；
- 按 exact address 读取正确 artifact；
- 不存在 latest fallback；
- artifact 不可变。

### P6：DerivedStore

产出：

- derived key；
- exact source fences；
- recipe/builder version；
- stale/missing 判定；
- rebuild demo。

验收：

- 删除 DerivedStore 后权威对象图正常打开；
- 相同 derived key 但 source head 不同不能误命中；
- rebuild 后产生的新 derived record 可被 exact consumer 选中。

### P7：统一 commit 与故障注入

产出：

- Schema/Artifact/State candidate；
- durability barrier；
- expected-head publication；
- poisoned repository；
- reopen/reconcile；
- fault injection matrix。

验收：

- 在每个写入、flush、ref publication 阶段注入故障；
- reopen 后只能看到 parent 或 exact candidate，不出现混合状态；
- orphan candidate 不获得 authority；
- `MayHavePublished` 不被透明 retry。

## 19. 建议直接继承的旧资产

### 19.1 StateJournal

优先评估复用：

- RBF frame 与基础编码；
- append-only segment；
- `CommitAddress` / physical address；
- candidate → durable flush → ref publication；
- expected-head CAS；
- poisoned Repository 与 reopen/reconcile；
- reflog/recovery 辅助；
- fault injection 测试方法。

不应因复用旧代码而强行保留：

- DurableDict/Deque 作为唯一领域建模方式；
- 容器内部 ChangeTracker；
- 旧 ObjectMap/Revision API 的所有历史 Shape；
- 只为旧格式兼容而存在的限制。

### 19.2 SessionJournal

继承设计原则：

- raw/authority 与 derived 分离；
- exact head 与 parent lineage；
- typed recovery outcome；
- 不把 external effect retry 伪装成 exactly-once；
- derived source fence；
- no latest/global scan authority；
- artifact/recap/memory 的认识论区别。

### 19.3 DramaBoard

用作真实压力来源：

- 长期对象身份；
- 图状空间与角色关系；
- HistoryLog；
- 多种 transient index；
- 可重建认知摘要；
- 时间推进后的频繁小规模 commit。

## 20. 主要风险

### 20.1 Schema 规范化漂移

Source Generator、DynamicMethod、不同 .NET 版本或不同构建配置产生不同解释。必须用 golden bytes 和 differential tests 守门。

### 20.2 历史版本成为永久可执行负担

若所有旧版本都长期参与普通领域逻辑，接口与不变量维护成本可能指数增长。需要通过 State-upgrade 与 Artifact-view 策略控制。

### 20.3 四 Store 重新形成分布式事务

若每个 Store 独立发布 head，系统会出现无法裁决的混合状态。必须坚持单一 CommitManifest authority。

### 20.4 全图扫描成本

首版主动接受，先测量真实对象数、字段数、baseline IO 和 commit latency。不要在没有数据前引入复杂 dirty tracking。

### 20.5 集合与第三方类型语义膨胀

任意 CLR 类型支持会迅速扩大 codec、identity、equality 和 migration 表面积。第一版必须有受控类型白名单。

### 20.6 “Durable”名不副实

只有当成功 commit 对声明范围内的 crash/power-loss 故障作出清晰保证时，`DurableObject` 才不仅仅是营销命名。必须写明 durability model 和平台边界。

## 21. 待决策清单

这些问题应通过小实验或 ADR 决定，不宜在首轮实现中凭感觉扩散：

1. `DurableId` 的物理编码与跨 Repository 语义。
2. 强制 `DurableObject` 基类，还是允许 attribute + generated interface。
3. 受支持 BCL collections 的内容/comparer/重建合同（统一 identity 方向已选）。
4. State 历史版本默认立即升级，还是允许活对象共存。
5. ArtifactAddress 采用 content address、append address 或二者组合。
6. SchemaStore 与 StateStore 是否共享底层 segment/frame。
7. CommitManifest 的最小必要字段。
8. baseline comparison 读取 canonical payload，还是加载历史对象实例。
9. branch/fork 是否进入首个可用版本。
10. RebuildTransient 的单 pass、two-pass 或依赖调度模型。
11. derived recipe identity 如何表达模型、提示词和算法版本。
12. 支持的 durability model：process crash、OS crash、power loss、torn write、directory metadata。

## 22. 第一版成功标准

第一版不以 API 数量或支持类型多少衡量，而以以下事实衡量：

- Coding Agent 能用普通强类型 C# 建模并修改领域对象；
- 无 ChangeTracker 仍能可靠发现并保存变化；
- shared reference、cycle 和 durable identity 跨进程保持；
- 盘上每个 object version 都能找到 exact Schema；
- 未知或被篡改的 Schema fail closed；
- V1 → V2 升级由显式源码和 handler 完成；
- Artifact 可被 State 精确引用并 lazy load；
- DerivedStore 可整库删除和重建；
- 四 Store 在故障注入后仍只有一个可裁决 authority；
- 文档与诊断足以让另一个 Coding Agent 在不猜测隐式语义的情况下继续实现。

## 23. 工作名与术语建议

建议库/namespace 工作名：

```text
Atelia.DurableGraph
```

建议核心术语：

```text
DurableObject
DurableId
[Durable]
[DurableMember]
[Transient]
VersionedSchema
SchemaDescriptor
SchemaStore
StateStore
ArtifactStore
DerivedStore
ArtifactRef<T>
CommitManifest
RebuildTransient
```

避免直接把公开项目命名为 `Durable Objects`，以免与 Cloudflare Durable Objects 产生强烈概念冲突。类名 `DurableObject` 在 `Atelia.DurableGraph` namespace 内仍可接受。

## 24. 相关前人系统：用于借鉴，不作为直接依赖

- EclipseStore / MicroStream：Java native object graph、OID、append storage、Type Dictionary、legacy type mapping。
- ZODB：root、persistent identity、ghost/lazy load、transaction cache、history。
- GemStone/S：persistent Smalltalk、ClassHistory、多个类版本与 selective migration。
- PM3：reachability-based orthogonal persistence 与 stabilization。
- PJama：persistent Java、transient fields、checkpoint 与长期系统演化困难。
- Realm .NET：Source Generator 管理 C# persistent objects 与 schema migration。
- Microsoft Orleans serialization：stable member IDs、generated codec、object identity 与 cyclic graph。

这些系统证明持久对象图不是新概念；DurableGraph 的新增价值在于以现代 C# 工具链，把长期 Schema、自描述历史、保存时全图比较、Artifact/Derived 分层和严格 commit authority 组合为一个面向实际 Agent/Game 应用的框架。

---

## 25. 给后续 Coding Agent 的开工提示

开始任何实现前：

1. 先指出当前工作属于 P0–P7 的哪一个阶段。
2. 明确本次改动验证哪个不变量。
3. 不因未来扩展猜测而提前实现未授权功能。
4. 所有格式和 Schema 决策必须配 golden/differential test。
5. 所有 commit/recovery 决策必须配 fault-injection test。
6. Target design 与 checkout code 冲突时，不得把本文愿景描述成当前事实。
7. 若实验否定本文假设，优先记录结果并更新设计，而不是为了维护文档正确而扭曲实现。

最初的正确目标不是“建成一个数据库”，而是证明下面这条闭环可以成立：

> 普通 C# 领域对象 → 生成精确 Schema 与 codec → 保存时发现真实差异 → append 新对象版本 → 单一 manifest 发布 → 用历史 Schema 可靠加载 → 显式升级 → 重建 transient/derived 状态。
