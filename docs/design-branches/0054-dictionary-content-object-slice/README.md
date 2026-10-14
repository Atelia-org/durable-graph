# DB-054：Dictionary<TKey, TValue> 内容对象与键寻址 Delta

> 文档整理：2026-10-14；来源为 Git `8ef9fca` 中的 `docs/design-branches/0054-dictionary-content-object-slice.md`，正文保留当片合同与历史验收含义。
> 阅读范围：白名单双槽、历史 Upgrade 和九条包证据见 §10；BCL 适配的实验性定位保留，复合值 Key 由 DB-055 承接。
> 当前能力与施工焦点见 [PROJECT-STATE](../../../src/PROJECT-STATE.md)；旧稿中的“下一步”不自动成为当前工作单。

> 状态：**Implemented / G0–G4 已通过验收**，2026-09-10。
> 问题：利用确定的键对应关系，接入普通 BCL Dictionary 的冻结、增量保存、历史读取、显式升级与领域恢复，是否能避免 List 的序列匹配复杂性？
> 最小成功见证：同一个共享 `Dictionary<string, Point>` 连续增删改并 Commit；冷重开保留键引用、值状态和 comparer 语义；Point 升版后只归一化一次、强制 Base，随后恢复 Delta/NoChange。
> 用户采纳先用白名单建设主体，并明确 BCL Dictionary 适配仍为实验性；复合值 Key 是后续重点。
> 2026-09-10 用户已授权完整实施本分片；完成与验证以 §10 施工账本为准，不把后续复合 Key 纳入本片。

## 1. 结论与现有依据

推荐作为下一片。Dictionary 的条目匹配比 List 简单，但整个功能仍有两项新的语义：键比较策略，以及恢复时建立索引的条件。
TValue 已有的投影、完整 exact 布局、融合 Delta 和值 Upgrade 可以直接组合；无需新研究 Diff 算法。

当前可复用的代码：

| 机制 | 当前依据 | 本片增量 |
|---|---|---|
| 内容对象与静态投影 | [ListObjectBinding](../../../src/DurableGraph/Runtime/Containers/ListObjectBinding.cs)、[IStateOps / IValueProjection](../../../src/DurableGraph/Runtime/Binding/StateValueBinding.cs) | key/value 两套已闭合能力 |
| owned 状态、历史 body | [FrozenListState](../../../src/DurableGraph/Runtime/Containers/FrozenListState.cs)、[ListStateReader](../../../src/DurableGraph/Runtime/Containers/ListStateReader.cs) | 无序 entries、键寻址稀疏 Delta |
| exact/current 绑定 | [List snapshot](../../../src/DurableGraph.Persistence/StateModelSnapshot.Lists.cs) | 两槽完整依赖与内建 Dictionary 路由 |
| 容器 owner Upgrade | [ListUpgrade](../../../src/DurableGraph/Runtime/Binding/StateBindingContext.ListUpgrade.cs) | 分开的 key/value 工具与升级后键冲突检查 |
| 持久表示 | [SchemaStore](../../../src/DurableGraph.Persistence/SchemaStore.cs)、[catalog codec](../../../src/DurableGraph.Persistence/SchemaCatalogWireCodec.cs) | 一个双实参内建构造及双槽目录记录 |
| 恢复/下一次基线 | [WorldWorkspace](../../../src/DurableGraph.Persistence/WorldWorkspace.cs) | 沿用全分配后 Hydrate；不把枚举位置当持久身份 |

Microsoft 的 [Dictionary 合同](https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.dictionary-2?view=net-10.0)
规定 comparer 决定键相等性，键在容器内不能发生影响 hash 的变化，枚举顺序没有保证。
本设计据此保存映射内容和已支持的比较策略，不保存 buckets、容量、hash 值或枚举顺序。

## 2. 推荐支持范围与需采纳的边界

支持 exact `System.Collections.Generic.Dictionary<TKey, TValue>`，不开放子类、接口字段或任意 object 槽。
TValue 使用完整现有槽闭包：13 种标量、显式 enum、Nullable、递归 inline/generic struct、string、durable class、数组、List，
再加 Dictionary 引用；支持容器、泛型、数组的递归组合。共享和循环沿统一 ObjectId 路径处理。

TKey 的**可表示性**和**可恢复的键比较能力**分别判定。首片支持：

| TKey | 可接受的实例 comparer | 说明 |
|---|---|---|
| 13 种已有标量 | `EqualityComparer<TKey>.Default` | 包括浮点；查找相等与持久按位相等仍是两回事 |
| 显式 Durable enum | `EqualityComparer<TKey>.Default` | 完整八种整数底层类型，未知值/Flags 不裁剪 |
| string | 默认、`StringComparer.Ordinal`、`StringComparer.OrdinalIgnoreCase` | 默认与 Ordinal 归为同一个持久策略；保存实际键的 ObjectId |
| string、durable class、数组、List、Dictionary 等受支持引用类型 | 显式 `ReferenceEqualityComparer.Instance` | 不调用领域 Equals/GetHashCode，允许键与值、World 共享或形成循环 |

表中 comparer 指公共 `Dictionary.Comparer` 所报告的已知策略。只识别明确白名单，不调用任意 comparer 的 Equals 来判断它是否“等同默认”，
也不依赖 BCL 内部随机 hash 包装的私有类型。未知/custom/culture comparer 明确拒绝，不能换成默认比较器。
可规范化的是列出的策略，不承诺恢复 comparer 实例本身的引用身份、内部优化或 hash 数值。
字符串大小写语义由受支持 .NET 比较器实现；本片不提供跨运行库 Unicode 版本的独立比较器实现。

首片暂不支持自定义 struct 的默认/自定义 comparer，也不支持 durable class 自定义值相等 comparer。
这不是它们无法形成 DTO：历史 key body、值升级和 Delta 已能表达它们；欠缺的是可验证的领域索引重建合同。
例如 `GetHashCode()` 读取 Transient、另一个尚未填充的字典，或者持久字段相同而只按 Transient 区分两个键。
即使增加“所有普通对象 Hydrate 后再 Add”的第三阶段，也不能解决任意容器依赖环。

替代方案是允许任意 comparer，由用户承诺它只依赖完整恢复的稳定状态。其覆盖面更宽，但无法兑现同样的防误用性，
而且需要定义比较器身份、代码保留及依赖调度。暂不推荐混入本片。
另一条后继是证明一部分 struct 的递归默认比较可恢复；需要检查自定义 Equals/GetHashCode/IEquatable、Transient 和所有闭合字段，
不能仅检查 `IsValueType`。这项能力应独立设计，不能把结构 StateEquals 静默冒充 BCL 默认 Equals。

Nullable key 暂不列入首片矩阵：BCL 声明 `TKey : notnull`，但这不是 CLR 禁止非空 Nullable key 的证明；
如有使用需求，可单独接入非空包装及编译诊断。Nullable TValue 完整支持。
null key 一律拒绝，nullable reference 注解不授予 null key 支持。

该矩阵是先建设主体的阶段性范围，不是产品最终 Key 能力的上限。TValue 闭包不缩减。
标量、enum、string 是首片主要使用见证；表中明确 ReferenceIdentity 的直通路径可以保留，
但 string 以外引用 Key 的进一步能力优先级低于复合值 Key，不为其增加内容比较、可变键追踪或索引调度。
纯 ReferenceIdentity 本身不受目标字段变化影响；可变性困难在于以目标内容决定 hash/equality，不能混称。

### 2.1 复合值 Key 是明确后续能力

后继 [DB-055](../0055-composite-dictionary-key-design/README.md) 已修订为“持久状态与当前业务比较分开”的设计，方向已采纳、代码未实施。
本文的白名单、历史 lookup 保证及替代方案分析保留为 DB-054 当时的合同；不能据此认定任意当前 comparer 必须保留旧业务代码。

用户确认组合多个字段构成键是典型领域需求，不能把它留在“有消费者才考虑”的末端。
本片主体完成后，优先设计至少一条方便、可恢复的复合值 Key 路径；允许有限支持，
不要求第一次就接受任意用户比较逻辑。候选包括已有 Durable struct 的受控结构比较、有限 record struct，
以及随后可评估的 `ValueTuple<...>` 系列；先选哪条由生成器改造量与调用方使用成本决定。

`record struct` 是有价值的候选，但该关键字本身不是安全比较能力的证明。
其合成 Equals/GetHashCode 使用每个实例字段的默认比较器，也允许用户提供相应实现；
这套字段集合不自动等于 Durable 持久字段集合。见 [C# record struct 比较规则](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/language-specification/structs#16533-equality-members)。
ValueTuple 同样逐分量使用默认比较器，不能仅凭外壳名称放行任意分量，见 [ValueTuple.Equals](https://learn.microsoft.com/en-us/dotnet/api/system.valuetuple-2.equals?view=net-10.0)。

当前 SG 的 [形状检查](../../../src/DurableGraph.Generator/DurableGraphGenerator.Ancestry.cs) 拒绝 IsRecord，
[成员枚举](../../../src/DurableGraph.Generator/DurableGraphGenerator.cs) 跳过隐式字段。
支持位置式 record struct 因而还须明确主构造参数/自动属性的持久成员标注、FieldId 与历史映射；
不能只移除 IsRecord 检查，也不意味着要为其执行用户构造器或 getter/setter。
已有普通 inline struct 路径则可作为较小改造的候选。

后继应比较两条路线：验证一个默认比较行为可恢复的有限键类型集合，或由框架生成按明确规则工作的强类型 comparer。
后者需要明确字段集合、string 内容/其他引用身份、浮点、递归组合及 hash 一致性，
不能把普通 EqualityComparer.Default 或整块 struct 内存比较称为框架受控语义。
首个复合 Key 见证至少含 `(int, int)` 与 `(string, enum)` 等实际组合、同值新建键查询、
非关键状态差异的裁决，以及键布局升级/碰撞拒绝；元组记号在此仅表示需求，不表示已选 ValueTuple 外观。

### 2.2 BCL 外观保持实验性

需要持久化的无序映射能力，与最终采用哪个 CLR 容器外观，是两个选择。
本片先适配 BCL Dictionary；后继可以保留它、提供固定 comparer 的工厂，或改为近似 API 的自定义 IDictionary 实现。
单纯薄包装加 EqualityComparer.Default 不能封堵键类型自己提供的比较逻辑；
若采用包装，其收益应来自明确的比较规则和更早的防误用，而非名称变化。

冻结双槽、key-body 寻址 Delta 和 owner Upgrade 应尽量保留为可复用机制；不预建通用 Map 插件体系，
不为将来可能的替换新增程序集或接口层。实现首个 BCL binding 时在其 XML doc 标明实验性，
并用一条 TODO 链到本节及复合 Key 后继；不把这种代码文档标记扩大成警告属性或新的分析器。
未来容器替换仍须裁决 nominal 类型、注册绑定与历史表示的关系；接口近似不等于可透明互换、可无条件读取旧格式，
也不因此现在授予任意 IDictionary 接口字段持久化能力。

## 3. 类型、状态与 comparer 位置

实施形状：

```text
TypeExpr.Dictionary(keyNominal, valueNominal)
DictionaryLayout(KeySlot=1, ValueSlot=2, CodecVersion=1)
FrozenDictionaryState<TKeyState, TValueState>
    ComparerKind
    owned Entry<TKeyState, TValueState>[]
```

两种 State 均 unmanaged，引用为 ObjectId，inline 值递归冻结；不保留可变领域 key/value。
Entries 在语义上无序，只读访问不等于承诺内部 buffer 排列。Count 属于对象状态。
内建 KeyValuePair/entry 只是框架存储部件，不顺带开放独立 BCL KeyValuePair 值类型。

comparer 是**对象内容元数据**，不进入 nominal 类型或 DictionaryLayout。
同一个 `Dictionary<string, Node>` CLR 类型可以同时存在 Ordinal、OrdinalIgnoreCase、ReferenceIdentity 实例；
现有按 CLR Type 缓存的 current binding 因而仍可唯一，Allocate 从当前 frozen state 选择 comparer。
四种封闭策略为 ScalarDefault=0、StringOrdinal=1、StringOrdinalIgnoreCase=2、ReferenceIdentity=3。
historical ScalarDefault 接受 builtin scalar，或符合 enum 表示规范的 exact InlineValue：
非泛型、无 base、唯一 FieldId=1、八种整数底层槽之一；按该整数槽操作，不需要旧领域 enum CLR。
reader 验证的是持久布局，不能证明历史 CLR 声明是 enum：DB-053 没有持久 enum 标记，等价单整数 struct 的表示同样可读。
这不授予当前 struct 的默认 key 能力；current binding/Capture 仍要求矩阵中的真实 CLR 类型。
其余策略也必须核对 exact key 槽，不能接受任意布局。

Base body 写 comparer 标签；同实例普通 Delta 继承 prior comparer，不改变它。
Prepare 若发现相同对象身份的 comparer 策略变化，明确拒绝，而不默默生成 NoChange/普通 Delta。
本片 Upgrade 同样保留策略；变更比较规则的业务迁移另行设计。

SchemaCatalog 增加 Dictionary 行，两个 exact 槽及 codec 版本共用既有整数依赖/注册批次。
Base v4 仍只写 RepresentationId，Delta 仍继承终止 Base 的 exact 表示，Storage 不理解 Dictionary。
引用 Dictionary 的 owner 只锁定双实参 nominal 约束；Dictionary 自己锁定 key/value 的 exact inline 布局，引用目标版本独立。
SG、Shared history、Build、nominal wire codec 与目录格式须一起接入双实参构造，不能只让 Runtime 识别 CLR 类型。
新增 nominal 构造码 10、目录 row kind 5；新写 history/manifest v7，保留已接受旧版本文件及 hash。
SCB1 v2 与 Base v4 不变，没有增加旧存储格式兼容 reader。

## 4. 两种键相等性与最小 Delta

**查找相等性**用于领域 Dictionary 的唯一键约束；**持久键相等性**用于对应两份 frozen 状态。
后者使用同一 exact KeySlot 的 canonical Base body bytes：引用按 ObjectId、浮点按位、inline 按持久字段编码，忽略 CLR padding。
不在 Prepare/历史 Apply 中调用领域 comparer，也不需要先恢复 key 指向的对象。

例：删除旧 string 键，再加入内容相同但实例不同的新 string。对 Ordinal Dictionary 它们是同一查找键，
但键的持久 ID 已变，必须 Remove(old ID)+Add(new ID)，不能沿用旧键导致引用身份丢失。
同理 `+0/-0` 或不同 NaN bits 在默认查找语义下可能等价，持久表示不同仍按删除新增处理。
这可能少复用一次 value Delta，是正确的局部效率取舍。

实现每次准备临时生成 key body 并建立哈希索引；hash 只加速，碰撞必须比较完整 bytes，不能作持久身份。
不新增 SG StateHash，不在整个库引入 comparer 插件。该索引及 key bytes 是可释放的操作内存，不能引用可变领域 key。
无需全量排序；匹配平均成本为所有 key 的编码与哈希扫描，加实际变化 value 的融合编码。
哈希碰撞下仍须正确，不声称无条件线性最坏时间。大 key 的临时 bytes 和重复传输是首片接受的代价。

raw Base grammar：

```text
comparerKind
count
(key Base, value Base) * count
```

raw Delta grammar：

```text
removeCount
(key Base) * removeCount
patchCount
(key Base, value Delta) * patchCount
addCount
(key Base, value Base) * addCount
```

三种原语为 Remove、PatchValue、Add，由分组位置区分，不另外加入 KeyPatch、Move、Rename 或操作日志。
相同持久 key 直接调用 TValue 的融合 `PrepareDelta`，用 HasChanges 决定是否产生 PatchValue，复用返回 bytes；
不先 StateEquals 再重复比较一次。引用 value 的 child-only 变化仍由目标对象独立 Delta，字典本身可 NoChange。
新键/缺失键分别 Add/Remove；持久键变化为 Remove+Add。不变时三个计数为零、HasChanges=false，不追加对象 Delta。

每条 key/value 由 exact 静态 body 消费；复用既有 canonical 数字、全消费和 nested Delta 规则。
Apply 只读 prior，生成新的 owned entries；可以保持 prior 幸存项顺序后追加新项。
NoChange 按无序完整映射判定，后续 Prepare 也按 key 寻址，因此不要求 Capture 和 Decode 的内部枚举位置相同。
相同 frozen 输入产生确定输出；逻辑相等但插入历史不同的 Dictionary 不承诺生成完全相同的 Base bytes。

拒绝：重复 canonical key、重复/跨组重复操作键、Remove/Patch 缺失键、Add 已存在键、无变化子 Patch、
计数溢出或越界、截断、非规范编码、尾随 bytes、布局/策略错误。三个操作组相互独立且都针对 prior：
不能通过先 Remove 再 Add **相同持久 key** 绕过规则；writer 应生成 Patch 或 NoChange。

### 为什么不选稀疏 ordinal Delta

`WorldWorkspace.Stage` 用 `NormalizedRevision.FromCandidate(candidate)` 建立下一基线，Install 直接 Accept 原 candidate，
不会重新 Apply 已写 Delta。若 Delta 只删除旧 ordinal、追加新项，磁盘恢复顺序可能与 candidate 的枚举顺序不同；
第二次 Commit 再按 candidate ordinal 编码就可能指错条目。
修正需全量结果排列、强制规范排序或长期 entry ID。key body 寻址省掉这套状态，代价只是变化项重复携带 key。
这次两路审查最初考虑 ordinal，交叉检查该基线接缝后共同推荐上述方案。

## 5. 冻结、引用验证与恢复

Capture 仍在静止领域图上枚举，先检查 comparer 白名单，再分别静态投影 key/value；不保存 Dictionary 内部结构。
冻结后检查持久键唯一性；后续编辑领域字典或 struct TValue 不能污染 candidate/Base/Delta。
key 和 value 都参与 VisitReferences、目标类型验证和可达性；键引用也能让对象存活。

必须区分两层验证：

1. 独立 body reader 检查 grammar、canonical key 唯一、策略/槽兼容和局部可判定键约束。
2. 完整 exact Revision 已解码字符串等目标行后，再校验查找键唯一性；Normalize 后在 current 视图重复检查。
   string 内容策略需要目标字符串内容；ReferenceIdentity 用实际恢复身份，包含 Empty 规范化例外。

不得借助当前领域 key CLR 来校验历史 enum。ScalarDefault 使用已知标量/历史 enum 整数槽的比较能力，
浮点重复判断遵循 .NET 默认查找相等而非 body bytes；两个 string ID 可内容相等，应在内容 comparer 下拒绝重复。
所有 source 行都接受完整 exact/current 验证，包括之后不可达的字典，沿用当前 source 处理合同。
具体接缝是 [RevisionDecoder.ReadCore](../../../src/DurableGraph.Persistence/RevisionDecoder.cs) 在完整目标表和引用校验后、
返回 DecodedRevision 前检查全部 source 字典；[NormalizedRevision.Create](../../../src/DurableGraph.Persistence/NormalizedRevision.cs)
在所有 Normalize 及 current 引用校验后检查全部 current 字典。不能只在可达 World 的 Hydrate/Add 中验证。
局部 typed reader 不承诺在缺少引用目标表时完成跨对象内容验证，Revision/World API 才提供完整图级结果。

特殊反例：ReferenceIdentity 的 string 字典含两个独立空串键。全局规则会把它们归一成 Empty，
因此 Capture 或完整读取校验必须拒绝键碰撞，不能覆盖丢条目；非空同内容的不同实例则必须保留两个键。

当前全 Allocate → Hydrate 可满足推荐矩阵：标量/enum 的 key 在 Add 前由槽完整还原；string 不可变；
ReferenceIdentity 不读取目标对象字段，因此即使目标尚未 Hydrate、或存在循环，hash/equality 也稳定。
用构造器创建空 Dictionary，按对应策略 Add，重复键仍作为最后一道拒绝检查；不把 indexer 覆盖当恢复。
不新增一般 Transient hook、任意 comparer 调度器或恢复依赖图。任何失败不交付部分 World。

## 6. 历史 Upgrade

复用容器 owner 的显式工具模式，使用彼此独立的 `UseDictionaryKeyUpgrades` / `UseDictionaryValueUpgrades`。
同一 nominal Dictionary 的 source→current 两个完整槽分别绑定；未变槽直接保留，变化槽必须显式选择规则集。
key 的首片升级见证为 enum；value 覆盖 inline/generic struct、Nullable/enum 等完整既有值能力。
保留历史 DTO/body，不需要旧 enum/struct 领域 CLR；不自动寻找邻接升级路径或 latest 填缺。

全部 key/value 工具及完整 exact 依赖闭包在该对象首次业务 callback 前预检，包括空字典和缓存 plan。
一次归一化每个共享 Dictionary，保留 ObjectId、条目数和 comparer 策略，显式转换 key/value 后再检验两种唯一性。
两个旧键被转换成同一查找键时，失败；不采用 first/last wins，不自动合并、丢弃条目或创造新对象。
跨 exact 布局仍 live 的字典强制 Base，下一次保存恢复普通 Delta；引用 key 的目标升级仍是目标对象自己的事情。
Context 按现有容器 owner 方式提供 DictionaryCount 和 Source/TargetObjectLayout，子工具继承 owner；
不授予查询其他对象、分配 ID 或跨对象升级能力。批次后校验碰撞不意味着业务 callbacks 能自动撤销副作用。

## 7. 对其他映射容器的复用范围

| 类型 | 可复用 | 必须另行定义 |
|---|---|---|
| Dictionary | 双槽投影、owned entries、键寻址 Delta、显式工具、统一引用/目录 | 本片白名单 comparer 和无序映射语义 |
| SortedDictionary | 上述无序内容匹配、值 Patch、历史两槽 reader 的模式 | `IComparer<TKey>.Compare==0` 的键等价、比较器持久策略、排序索引建立；不能把 equality comparer 直接换名 |
| 泛型 OrderedDictionary | 双槽投影、键唯一性、值 Patch 与条目对应 | 顺序是公开状态；必须保存序列并表达插入位置/移动/排列变化，不能用本片忽略顺序的 NoChange |

[SortedDictionary 文档](https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.sorteddictionary-2?view=net-10.0)
与 [OrderedDictionary 文档](https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.ordereddictionary-2?view=net-10.0)
分别支持上述排序和索引语义区别。这里讨论泛型 OrderedDictionary，不顺带支持非泛型 object 容器。
用户关于“核心模式可复用”的判断成立；不承诺三个完整容器仅修改工厂即可支持。
出现第二个 consumer 后再提取实际重复代码，本片不预建通用 Map 框架。

## 8. 建议施工顺序与验收

| 阶段 | 文件/职责范围 | 最小可观察结果 |
|---|---|---|
| G0 合同与类型链 | TypeExpr、Shared history、Build、SG、DictionaryLayout、SchemaCatalog | 双实参及双 exact 槽跨代码/历史/目录往返；码位与策略标签冻结；非法形状和重复布局拒绝 |
| G1 frozen/body | FrozenDictionaryState、历史 reader、静态双槽 body | 独立 Base/Delta golden；Remove/Add/Patch；无序 NoChange、错误 prior、碰撞、截断；prior/candidate 隔离 |
| G2 当前图接入 | ObjectBinding、snapshot、Capture/引用验证、World 恢复 | 全 value 闭包、完整 key 矩阵、同 CLR 不同 comparer 共存、键值共享循环、增删改续存与冷读 |
| G3 历史升级 | Dictionary owner 工具、依赖证书、normalized 验证 | enum key/struct value 升版、缺规则/空容器/late conflict、碰撞拒绝、升级 Base 后 Delta |
| G4 包及审查 | DictionaryConsumer、相关旧包 lanes、文档 | 真 PackageReference 两代、删除旧 key/value CLR、历史 exact/current 读取与同实例连续 Commit |

subagents 可在 G0 合同固定后分别承担 Runtime body、SG/history、图/Upgrade、测试/真实包；独占文件，主线程集成并串行运行 dotnet。
必须专门覆盖：

- 同 key 新 value、同内容新 string key、OrdIgnoreCase 等价键替换、ReferenceIdentity 同内容不同非空 string；
- 标量浮点的查找等价/持久位差异、enum unknown bits、Empty 规范化冲突；
- 删除后新增导致枚举排列变化，连续至少三次 Commit，并从各 Revision 冷读核对，防止 ordinal 错配复发；
- 仅 Capacity 或枚举排列变化无伪 Delta；child-only 变化只更新目标；键/值末条边移除导致循环岛 Remove；
- 字典作为字段/数组/List 元素/泛型参数、Dictionary 嵌套、共享字典只 Upgrade 一次；
- 完整图验证拒绝查找等价的重复键，不只检查 body bytes 重复；失败不替换已提交基线；
- 计数型 IStateOps 见证每个匹配 key 恰调用一次 value PrepareDelta、零 StateEquals；新增/删除 key 不调用 value Delta；
- 与 List 一样复用 PreparedBase/PreparedDelta 实际 bytes、原 Base/Delta 策略与发布故障合同，不新增 Storage 语义。

验收包括根 solution build、相关与全量 tests、真实包及独立审查；实际结果见 §10。
性能只需见证改一项的 Delta 不随未变化项数线性膨胀、无 List 搜索预算；不预设吞吐门槛。
后续实测若大 key 编码成为热点，再考虑缓冲复用/StateHash 或字节索引缓存；不以此为首片前置。

## 9. 本轮设计审阅

两路 subagent 分别检查 key/comparer 恢复语义和 List/StateStore 管线复用，主线程核对源码后形成方案；
双方又各审阅一遍完整草稿。交叉审查修正了 ordinal 与安装基线的顺序耦合、历史 enum 来源不可判定，
并明确完整 source/current 字典验证的位置。初稿修订后未发现设计阻塞。
初稿提交 `336d97b` 修改五份 Markdown，403 个本地链接和 36 个锚点检查通过；未运行产品 build/tests。
后续用户讨论采纳白名单试验路线，明确复合 Key 的优先级与 BCL 容器外观可替换性；本次据此补充 §2.1–2.2，
没有将 record struct/ValueTuple 或自定义容器描述为现有能力。

## 10. 实施合同与验收账本

基线 `430f9ed` 工作区干净；根 build 零警告/错误，全量 1828 tests 通过（Runtime 1039、StateStore 531、Serialization 103、Storage 155）。
日志位于测试项目 ignored obj 下 `db054-baseline.log`。

G0 固定最小接缝：TypeExpr/共享模式构造码 10，双操作数 `Dictionary(key,value)` / `d(key,value)`；
新写 history/manifest v7，旧 accepted 1–6 按各版语法保留原文件/hash。
SCB1 保持 v2，Dictionary row kind=5、codec=1，两个槽隐含 FieldId 1/2；不修改 Base v4 或 Storage wire。
comparer 为 body byte，ScalarDefault=0、StringOrdinal=1、StringOrdinalIgnoreCase=2、ReferenceIdentity=3。
FrozenDictionaryState<K,V> 拥有 entry 数组与 comparer，内部排列不构成映射语义；现有 IStateOps 保持不变。
Dictionary reader/current binding 分别提供内部 ValidateLookupKeys(row, resolver)，调用方在完整目标表可用时验证；
不往通用基类增加容器 hook，不在 DTO 保存执行能力或领域引用。

| 要求 | 实施落点 | 验收证据 |
|---|---|---|
| 双实参/Schema 引用/common Runtime | TypeExpr/ObjectLayout/ObjectStateRecord/引用与模板替换 | [生成图测试](../../../tests/DurableGraph.Tests/DictionaryGeneratedGraphTests.cs)：13 标量与八整数 enum 键、复合 value、泛型/容器及不调用领域相等 |
| SG 与历史 v7 | Generator/Shared/Build | [生成测试](../../../tests/DurableGraph.Tests/DictionaryGeneratorTests.cs)、[history 测试](../../../tests/DurableGraph.Tests/DictionaryHistoryTests.cs)：旧格式/文件保留、嵌套语法、诊断 |
| 双槽持久目录 | SchemaStore/catalog/nominal wire | [目录测试](../../../tests/DurableGraph.Persistence.Tests/DictionaryCatalogTests.cs)：独立 golden、双端依赖、冲突原子性/冷重开 |
| frozen/body/key policy | Dictionary 新 Runtime 文件 | [body 测试](../../../tests/DurableGraph.Tests/DictionaryBodyTests.cs)、[policy 测试](../../../tests/DurableGraph.Tests/DictionaryKeyPolicyTests.cs)：融合计数、排列、截断、浮点/Empty/策略拒绝 |
| current/exact 图接入 | snapshot/registry/Seal/decoded/normalized | [绑定测试](../../../tests/DurableGraph.Persistence.Tests/DictionaryBindingCatalogTests.cs)、[Repository 测试](../../../tests/DurableGraph.Persistence.Tests/DictionaryRepositoryTests.cs)：全部 source/current 校验、连续保存、晚期依赖冲突 |
| 显式 key/value Upgrade | DictionaryUpgrade/UpgradeContext | [升级测试](../../../tests/DurableGraph.Tests/DictionaryUpgradeTests.cs)：独立规则、空/absent、late conflict、碰撞、Context 与失败隔离 |
| 真实包两代 | DictionaryConsumer/runner | [包见证](../../../experiments/PackageConsumerProbe/DictionaryConsumer/README.md)：旧 enum/struct 删除、96 callbacks、两字典 Base 后 Delta |
| 旧包回归与独立审查 | 主线程串行验证，独立 reviewer 两轮审查 | 下述九条包 lane 全部通过；无未解决阻塞，无跳过/削弱旧断言 |

最终根 build 零警告/错误；全量 **2029 tests 通过**（Runtime 1190、StateStore 581、Serialization 103、Storage 155），
比基线新增 201 个用例。最终日志为 `tests/DurableGraph.Tests/obj/db054-build.log` 与 `db054-full-final.log`。
初次集成发现的是测试夹具的 internal 可见性、params 类型推导/Nullable 警告及异常继承假设，均已修正；
未通过扩大产品访问权限、兼容旧存储格式或放宽任意异常来绕过测试。
metadata Validate 保持不编码，key 查重/lookup 验证位于各自内容阶段；新增计数回归验证该屏障。

包均由主线程串行实际执行；前八条共用八依赖包 feed，版本 `0.0.0-dictionary-e2e.20260909162417.14064`，
位于 Dictionary artifact 的 `feed`。基础 Run-Probe 无复用 feed 参数，按原入口独立 pack/run。
artifact 相对 `experiments/PackageConsumerProbe/obj/`：

| lane | artifact 目录 |
|---|---|
| Dictionary | `dictionary-20260909162416-14064-08f1929b` |
| Generic | `generic-20260909162605-21652-ec2d3a04` |
| Nullable | `nullable-20260909162628-21652-8b16f275` |
| Enum | `enum-20260909162646-21652-7bec2bbb` |
| List | `list-20260909162703-21652-11dc7e07` |
| Array | `array-20260909162721-21652-cc43e9f8` |
| InlineStruct | `inline-struct-20260909162739-21652-425a11b1` |
| ValueUpgrade | `value-upgrade-20260909162801-21652-094a7b0f` |
| 基础 PackageConsumer | `run-20260909162850-4200` |

新写 v7 与保留旧 accepted hash 的合同分别由 tests/实际包验证。存储仍是 SCB1 v2、Base v4、Storage wire v3。
正文的实验性定位和后继 TODO 已进入 DictionaryObjectBinding；没有一般 comparer 插件或容器框架。
文档收尾检查覆盖 11 份 Markdown、500 个本地链接及 49 个锚点，全部通过；staged diff 空白检查通过。

后续复合值 Key、record struct/ValueTuple、任意 comparer、替换容器外观均不进入本轮实施。
