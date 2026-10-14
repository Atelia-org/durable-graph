# DB-055：复合 Dictionary Key 的持久状态与当前比较行为

> 文档整理：2026-10-14；来源为 Git `8ef9fca` 中的 `docs/design-branches/0055-composite-dictionary-key-design.md`，正文保留当片合同与历史验收含义。
> 阅读范围：Default/Application、完整 DTO 配对与当前 TryAdd 的 G0–G3 证据见 §11；旧固定字段 comparer 推荐已冻结归档，历史数据读取不承诺重现任意旧比较代码。
> 当前能力与施工焦点见 [PROJECT-STATE](../../../src/PROJECT-STATE.md)；旧稿中的“下一步”不自动成为当前工作单。

> 状态：**已实施 / G0–G3 验收完成**，2026-09-10。
> 用户已授权实施本文；模式、API 与包装按下述合同落地，完成情况以 §11 的实现验收记录为准。
> 实施前代码基线：`821e602`（DB-054 白名单）。旧推荐来自 `77e4341`，已[冻结归档（Git 历史）](../0091-retired-material-cleanup/README.md#git-history "Git 6dbaf0e:docs/archive/2026-09-10/0055-composite-dictionary-key-design-v1.md")。
> 问题：允许普通/generic struct 的领域相等性忽略部分持久字段，同时完整保存 Key，能否复用既有 Delta 并保持普通 Dictionary 的易用性？
> 最小见证：Key 的三个字段均持久，Equals 忽略 Timestamp；普通 new()、同值查询、连续 Commit、冷重开均工作；旧 Key CLR 删除后仍能读历史 DTO，当前规则冲突则明确拒绝交付 World。

## 1. 核心选择与需求来源

**领域 comparer 属于当前程序的行为；DTO 按全部持久信息配对。框架恢复整个对象图，应用只提供需要的当前 comparer。**
不生成框架规定的领域 Equals/Hash，不要求用户改用固定比较语义的容器，不保存任意比较代码的历史。
普通 BCL Dictionary 的使用、已有 owned entries、ObjectId、键寻址 Delta 与双槽 Upgrade 保留。

| 来源 | 约束或选择 |
|---|---|
| 用户已采纳的新分层 | Timestamp 等字段可以持久而不参与领域查找；普通/generic struct 是主要需求；数据保存不承担任意旧业务代码重现 |
| 用户已采纳的补充 | 当前恢复用 TryAdd，碰撞失败而不覆盖；框架保留共享、循环与会话身份；不把完整恢复转嫁给宿主 |
| 当前源码和回归 | DTO 已是 owned 条目数组；Delta 以完整 canonical key body 寻址；标准 comparer 的同 CLR 多实例选择已有消费者 |
| 延续的产品合同 | exact 历史 DTO 不需要旧领域 Key CLR；单对象显式 Upgrade、完整引用/Schema 校验；恢复不调用领域构造器与 Transient hook |
| 本文的施工推荐 | 保留标准选择 0–3，增加 CurrentDefault / Application；typed 登记加一个泛型 resolver；新增模式不验证历史业务 lookup |
| 有限边界 | 一个闭合 Dictionary 类型的 Application 实例共享一份当前恢复规则；引用内容比较的额外恢复阶段、命名策略、record/tuple 外观另排 |

这里没有已发布数据兼容负担。保留标准模式是保留当前功能，不是建设旧格式兼容层。
上述模式编码与 API 接线经设计审阅选择，实施对应 §9 的见证；实际验证结果集中在 §11。

## 2. 三种职责及正确性条件

| 职责 | 使用的信息 | 不承担的事 |
|---|---|---|
| 领域查询 | 当前 Dictionary 的 Equals/Hash；用户可只比较部分持久字段 | 不要求与 DTO 持久相等相同 |
| 冻结、配对、Delta | 同 exact KeySlot 的完整 canonical Base bytes；引用为 ObjectId，浮点按位，inline 按持久字段 | 不调用领域 Equals/Hash，不访问引用目标的内容 |
| 当前领域恢复 | 按对象的恢复模式选择当前 comparer，逐个 TryAdd | 不重现任意旧业务方法；不合并或静默丢弃冲突条目 |

现有 [FrozenDictionaryState](../../../src/DurableGraph/Runtime/Containers/FrozenDictionaryState.cs) 已持有独立 entries，
[DictionaryStateReader.Index](../../../src/DurableGraph/Runtime/Containers/DictionaryStateReader.cs) 已使用 key bytes 和完整相等检查。
Hash 仅加速查找；不落盘、不作持久身份。内部数组承载无序映射，不改用有序 List 的算法，也不增加 entry ID。

设 P(k) 为冻结后的完整 key body。用户比较规则若满足：

```text
P(a) = P(b)  ⇒  领域 comparer 认为 a、b 相等
```

则合法领域字典中的不同键不会成为重复持久键。只按部分持久字段比较通常满足该条件；
它不要求反向成立。比如旧 key `(7, A, 100)` 被实际替换为 `(7, A, 101)`，领域查找相同，DTO 仍 Remove+Add。
这是完整保存 Timestamp 的正确结果，接受少复用一次 value Delta 的代价。

“不依赖 Transient”不是任意代码的静态证明：例如两种 Empty 实例按 identity 区分，但全局 Capture 会归一它们。
保留实际 canonical 重复检查并拒绝即可，不建设方法分析器；用户仍须遵守合法等价关系和 Equals⇒同 hash。
若连完全相同的持久 key 也要作为不同条目保存，就需另一套 multimap/序列合同，本片不接受这种输入。

## 3. 普通使用方式与支持范围

以下 Key 可以自然使用默认 Dictionary；Timestamp 不必移到 Value，也不必新增 KeyField 属性：

```csharp
[DurableType("OrderKey", 1)]
public partial struct OrderKey : IEquatable<OrderKey> {
    [DurableField(1)] public int Tenant;
    [DurableField(2)] public long Number;
    [DurableField(3)] public long Timestamp;

    public bool Equals(OrderKey other) => Tenant == other.Tenant && Number == other.Number;
    public override bool Equals(object? other) => other is OrderKey key && Equals(key);
    public override int GetHashCode() => HashCode.Combine(Tenant, Number);
}

var orders = new Dictionary<OrderKey, Order>();
```

这依赖当前 CLR 的 Default / IEquatable 机制，不需要 SG 生成业务比较代码。
[Microsoft Dictionary 合同](https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.dictionary-2?view=net-10.0)
规定默认比较器选择及键相等/hash 稳定性。框架不持久化 hash，也不要求跨进程 hash 数值相同。

| 形状 | 本片推荐支持与约束 |
|---|---|
| 普通/generic Durable struct | 显式 DurableType；已有持久字段布局、私有/readonly、递归 inline 均沿用；允许用户 Equals/Hash/IEquatable |
| 已支持的标量/enum | 沿用标准 Default；外置自定义比较可以选择 Application，例如按浮点位模式区分键 |
| Nullable 成分 | 复用 NullableState；absent 不读取内部字段；根 Nullable key 仍延后 |
| string 成分 | 保留实际引用与 UTF-16 内容；当前业务可按内容、大小写模式或身份比较；null 成分合法 |
| 其他已支持引用成分 | 保存 ObjectId；本片恢复保证可用引用身份，不保证引用目标内容已 Hydrate |
| Transient/未持久状态 | 不进 DTO；用户不得依赖未恢复的状态决定 Equals/Hash；不是通过 SG 检查每个方法证明 |
| 根 string/引用 key | 非 null，Empty 合法；保留已知标准模式。当前用户代码也受同一恢复时机合同约束，不能凭 Default 自动保证内容比较安全 |
| Dictionary 的 TValue、数组/List/泛型外层 | 继续使用完整已有槽闭包，字典共享和循环不变 |

普通非 readonly struct 可以作为 Key；框架不增加 readonly 资格要求。可变状态影响 hash 的问题仍由普通 Dictionary 的使用合同约束。
空持久 struct 只有一种持久 key 表示，最多一条；phantom 类型参数不凭空产生持久字段。
record struct、ValueTuple、其他 CLR/BCL 类型的序列化能力仍未实现，不能因为现在允许其比较方式就提前放行类型。
不扩展跨程序集生成、接口/object 通配槽、数组协变或 Dictionary 子类。

## 4. 保存选择，不保存任意比较代码

### 4.1 推荐保留对象级恢复模式

继续使用对象 body 中的 `DictionaryComparerKind`，增加两值；它不进入 DictionaryLayout/RepresentationId。
表中名字和码值已落实；0–3 不改变既有含义。

| 模式 | 捕获来源 | 恢复时的含义 | 历史 lookup 校验 |
|---|---|---|---|
| 0 ScalarDefault | 现有标量/enum Default | 既有框架确定的标量规则；不能借此调用任意 struct Default | 保留现有检查 |
| 1 StringOrdinal | string Default 或 Ordinal | 当前运行库 Ordinal | 保留现有检查 |
| 2 StringOrdinalIgnoreCase | 已知 OrdinalIgnoreCase | 当前运行库 OrdinalIgnoreCase | 保留现有检查 |
| 3 ReferenceIdentity | 已知 ReferenceEqualityComparer.Instance | 引用身份 | 保留现有检查与 Empty 规范化处理 |
| 4 CurrentDefault | 其余受支持 Key 的 Default | 当前 CLR 的 EqualityComparer<TKey>.Default，包括用户代码 | 不解释旧业务规则 |
| 5 Application | 其他 comparer，或恢复后带此模式的内部包装 | 本次模型 snapshot 为该闭合 Dictionary 登记的当前 comparer | 不解释旧业务规则 |

0–3 仍只通过明确的已知实例身份识别，不探测或调用任意 comparer.Equals 来判断策略相同。
标准模式不能被 Application 的配置覆盖；CurrentDefault 也不被外置配置覆盖。
普通 new() 使用 Default，不要求先创建 Registry。未指定 comparer 的默认使用与 Application 缺配置是两回事。

标准模式的保留有现成证据：[同闭合类型的多比较策略回归](../../../tests/DurableGraph.Persistence.Tests/DictionaryRepositoryTests.cs)。
若全部删掉模式，一个只有 `"MiXeD"` 的 IgnoreCase 字典用 Default 恢复时不会碰撞，却已改变查询行为。
TryAdd 无法检测这种配置丢失。保留模式只选择当前内建规则，并不保证跨运行库的任意历史行为复现。

### 4.2 Application 的明确有限边界

**同一个 snapshot、同一个闭合 Dictionary<TKey,TValue> 的全部 Application 实例，共用一个当前恢复选择。**
当前源实例可以是用户自行 new(customComparer)；无需来自框架工厂，也不核对其与注册实例 ReferenceEquals。
登记表示应用接受这个当前恢复选择，不证明它与原 comparer 的所有行为相同。
只改变比较函数逻辑、而不改变数据布局时，无需为 comparer 保存历史代码或自动升级 Schema。

两个同型 Application 实例如果需要恢复成两种不同规则，仅凭 entries、Type 或 ObjectId 无法推导其业务角色。
字段路径也不可靠：字典可能共享、有多个入边、位于数组或嵌套容器中。
首片不新增命名 recipe、持久 comparer 对象或角色注册表；需要这种差异时再设计一个小的实例选择标记。
当前模式能区分标准选择、CurrentDefault 与 Application；不能宣传为任意 custom 实例配置的忠实恢复。

## 5. 当前代码登记与泛型组合

### 5.1 最小公开入口

```csharp
// 配置只服务 Application；不更改现有领域字典的 Comparer。
models.UseDictionaryComparer<OrderKey, Order>(OrderKeyComparer.Instance);

// 领域对象仍按普通 BCL 方式创建，可以复用上面的同一实例。
var orders = new Dictionary<OrderKey, Order>(OrderKeyComparer.Instance);
```

注入 `IEqualityComparer<TKey>` 即可，暂不开放任意 Dictionary factory。
精确类型、空实例、容量及 ObjectId 分配都由框架控制，不让用户接管两阶段恢复。
构造器和字段初始化器不会在领域恢复时运行，不能靠 World 字段里的 new(customComparer) 补回选择。

Application 未配置时，实际 Capture 在准备条目之前报错；Load 在实际分配该字典时也报错。
空的实际字典同样检查。异常包含对象 ID、闭合 Dictionary 类型和缺失配置，不能默换 Default。
只读历史 DTO 或只 Normalize 不可达字典，不要求这个当前行为配置，见 §7。

### 5.2 一个按需 resolver 覆盖外置泛型 comparer

Default 的 `Key<T> : IEquatable<Key<T>>` 由 CLR 随实际 T 自动工作，不需要任何 resolver。
只有外置 comparer 需要一个当前闭合入口：

```csharp
// 输入总是受支持的闭合 Dictionary<K,V> CLR Type。
models.UseDictionaryComparerResolver(static dictionaryType => {
    Type keyType = dictionaryType.GetGenericArguments()[0];
    if (keyType.IsGenericType && keyType.GetGenericTypeDefinition() == typeof(TenantKey<>)) {
        return Activator.CreateInstance(
            typeof(TenantKeyComparer<>).MakeGenericType(keyType.GetGenericArguments()));
    }
    return null;
});
```

签名为 `Func<Type, object?>`。框架在已闭合的 Dictionary<K,V> helper 中检查结果可赋值给 `IEqualityComparer<K>`。
这里用的是当前领域 K，既不是 DTO 类型，也不是待编码的 TypeExpr。
`List<Dictionary<TenantKey<int>, Item[]>[]>` 与直接字段共用同一字典绑定，不枚举外层组合。
应用可在 resolver 中组合预制的泛型 comparer；框架不另建 pattern 匹配、泛型参数映射或程序集扫描平台。

选择规则只有两级：精确 typed 登记优先；无精确项才调用唯一的 resolver。
重复的相同登记可幂等，冲突登记拒绝；另一个 resolver 不静默替换已选 resolver。
选中 Application 后，缺失、返回 null、类型不匹配、泛型约束失败或用户异常均失败，不能回退 Default 或另一策略。
resolver 的 Type 反射只在首次需要这种 Application comparer 时发生，正常查找和 DTO 热循环不调用它。

### 5.3 时机、缓存与生存期

配置随现有模型 snapshot 复制；按闭合 Dictionary 类型缓存成功解析的 comparer。
`StateModelSnapshot` 创建 current binding 时只传入惰性解析入口，不立即调用 resolver。
实际 Capture 的 Application 预检或 Allocate 才触发解析；已有成功缓存的闭合类型，随后重复 Prepare 不重复运行 resolver。
标准模式与 CurrentDefault 不触发 Application resolver。
同型 Application 字典共享成功解析的 inner comparer 实例；不保证每个字典有一份独立 comparer 状态，也不把 resolver 当逐对象 factory。
解析失败是否缓存留给实现，不承诺失败重试的回调次数。

这冻结的是选择表、委托和 comparer 引用，不冻结它们捕获的任意外部状态。
应用保证行为在加载、捕获与字典使用期间稳定，且 comparer/resolver 不依赖尚未交付的恢复图、不重入当前操作。
不做全局静态注册，不缓存带 ObjectId 的业务上下文，不向 SchemaStore 登记可执行 comparer。
Runtime binding 只接收 comparer 取得能力，Registry/Snapshot 的选择仍归 StateStore，避免反向程序集依赖。

## 6. 数据格式、回捕与增量保存

### 6.1 body 和槽能力

Dictionary codec 1 grammar、catalog kind 5、SCB1 v2、Base v4、Storage wire v3、history v7 均不变；
只扩展 comparerKind 取值，旧程序读新模式明确拒绝，不建设兼容写法。
现有 Remove / PatchValue / Add 分组和完整 key Base body 寻址不变。
匹配条目只对 value 调用一次融合 PrepareDelta；枚举重排和 Capacity 改变仍为 NoChange。

新模式的历史资格由 exact KeySlot 的可表示性决定，不反推旧 CLR 是否 enum、是否实现 IEquatable。
根引用仍禁 null、根 Nullable 仍拒绝；这两项与业务 lookup 查重分开保留。
不能继续使用当前“非 ScalarDefault 都是单个非零 ObjectId”的分支来读复合 key。
泛型参数、子 inline/Nullable、引用槽的表示与引用遍历均复用已有能力。

所有模式都保留 canonical key 唯一、操作组合法、完整消费、计数/长度及 exact 引用校验。
如果 exact key 的完整编码只有一种可能（例如递归空 struct），count>1 在 entries 数组分配前拒绝；
Delta 也先检结果 count。这与业务 comparer 无关，防止零字节 key/value 绕过最小 payload 长度预检。

### 6.2 恢复模式必须经过下一次 Capture 保持

一个具体反例：Base 是 Application，当前 resolver 返回 StringComparer.Ordinal；直接 new 字典后，
下一次 Capture 识别为 StringOrdinal，prior 仍为 Application，现有 PrepareDelta 会拒绝模式变化。
CurrentDefault 也可能遇到同类问题：历史 struct 的同 nominal inline 后来成为 enum，当前 Default 会被识别为 ScalarDefault。

恢复 4/5 时使用一个内部封闭的 `DictionaryRestoreComparer<TKey>`，保存模式并转发 inner 的 Equals/Hash：

```text
Allocate(CurrentDefault) -> wrapper(4, EqualityComparer<K>.Default)
Allocate(Application)    -> wrapper(5, 当前解析的 IEqualityComparer<K>)
Capture                 -> 先识别框架 wrapper 并保留 4/5，再识别普通 comparer
```

它只保存选择，不生成或修改业务相等规则，不验证能力来源；框架包装不额外捕获 Repository、DTO 或 Context，
inner comparer 的捕获关系由应用负责。
新增模式的正常领域查询多一次转发，是本片为简化状态所有权接受的成本；不据此宣传零额外调用。
不承诺 `Dictionary.Comparer` 的 CLR 类型、实例身份或它与 Default 的 ReferenceEquals；承诺所选 inner 的比较行为。
无需新增会话 object→mode 表，也不让 Capture 借旧 DTO 猜当前选择。

同实例普通 Delta 继续要求模式不变；Upgrade 也保留模式。
0–3 必须分别检查 stored 槽兼容性和 current 创建能力：历史 ScalarDefault 单整数布局即使与当前普通 struct 相同，
也不能默换成该 struct 的任意 Default。模式与 stored 槽或 Normalize 输出槽本身不兼容，仍在 body/DTO 阶段拒绝；
槽合法但当前 CLR 无法按原标准模式创建 comparer，才在实际 Allocate 拒绝。模式迁移另行设计。
4/5 则按当前规则恢复，可以有业务逻辑变化。只改比较函数而内容未变时，不要求为此制造对象 Delta。

## 7. 验证、Upgrade 与领域交付

| 阶段 | 所有模式 | 新增 4/5 的行为边界 |
|---|---|---|
| Capture | 完整冻结 key/value、canonical 唯一、根 null 与引用合法性 | Application 检查当前配置；不重建第二个领域字典验证业务代码 |
| 独立 body / Apply | grammar、布局/模式合法、持久重复、非法操作、全消费 | 不调用当前 comparer、不构建历史 lookup 解释器 |
| exact Revision | 所有 stored 行与完整引用都验证，包括不可达行 | 无需旧 Key CLR 或当前 Application 配置 |
| DTO Normalize | 双槽显式 Upgrade、完整 exact 依赖预检、输出持久重复与完整引用验证 | 不触发 resolver/Equals/Hash，不验证当前业务 lookup；不可达行不要求 comparer |
| 可达图 Allocate | 每 ID 一个准确、独立的领域实例，先全部分配 | 4 取当前 Default；5 懒解析并包装；空字典亦如此 |
| Hydrate | 还原每个 key/value 并 TryAdd；成功后才交付完整 World | 当前碰撞或 comparer 异常失败，不覆盖、不返部分 World |

0–3 的已有框架 lookup 检查继续保留；它们不需要执行旧用户代码。
**不再承诺所有 source/current DTO 都满足任意当前业务 comparer 的唯一性。**
历史 DTO 能读回、能升级，不代表当前领域 Dictionary 一定能接纳它；这是已采纳的数据/行为边界。
新规则可使两个不同 canonical key 碰撞，此时可达字典 Load 失败；不可达字典不因当前业务规则而额外失败。
库不回滚用户 comparer/resolver 的外部副作用，因此这些回调应不发布恢复中对象或修改业务世界。

保留现有全 Allocate → Hydrate，不引入 Transient hook、任意回调调度或依赖拓扑。
key 的 inline 值已经赋好，string 已完整可用，引用身份已存在；不保证其引用目标的字段/数组/List/Dictionary 内容已填好。
业务代码只依赖这些在当前阶段就绪的比较依据。比如 GetHashCode 读取 key.Node.Id，即使 Id 持久，也超出本片恢复时机保证。
若以后确有该需求，先评估固定“非字典先 Hydrate、字典后填充”；它仍不能自动解决字典内容间的比较依赖环。

Key/Value 的 exact 布局变化继续分别选显式 Upgrade 工具，空容器也预检完整闭包，缓存命中仍核对活的 SchemaStore。
保留 ObjectId/count/模式；仍 live 的布局升级强制 Base，随后 NoChange/Delta；不创建新 ID、不授予跨对象读取。
升级产生重复 canonical key 在 DTO 阶段失败；仅当前业务 lookup 碰撞在 TryAdd 阶段失败，二者不能混称。
恢复完成后导入现有 CaptureSession，继续使用同一领域实例及 owned DTO 基线；不能通过事后替换字典实例破坏身份接续。

## 8. 相比旧稿删除什么，以及实现接缝

| 机制 | 结论与理由 |
|---|---|
| SG 生成持久字段领域 comparer、子 comparer 工厂及来源证书 | 删除旧提议；用户负责当前业务相等，不再需要框架证明其与历史规则一致 |
| 复合历史 lookup token/UTF-16 解释器 | 删除旧提议；新增模式只需要已有 canonical bytes 与实际恢复 TryAdd |
| 完整 exact/引用验证、owned entries、键寻址 Delta | 保留；数据完整性与业务规则独立，不能随 lookup 检查一起删 |
| 0–3 标准模式 | 保留；已有同 CLR 多策略消费者，不是旧数据兼容负担 |
| 4/5 透明模式包装 | 新增局部机制；防止 Load→Capture→Commit 改变模式；不承载相等语义权威 |
| 任意空 Dictionary factory | 暂不引入；comparer 足够，框架能直接创建正确空实例 |
| 全部交给上层重建 | 不作普通路径；会让宿主接管共享引用、分配和会话身份；历史 DTO 查询可用于专门救援 |
| 命名 Application recipe、自建容器、通用 comparer pattern 平台 | 延后；同型多自定义角色或具体 API 痛点出现时重访 |

| 代码接缝 | 推荐改动 |
|---|---|
| [DictionaryKeyPolicy](../../../src/DurableGraph/Runtime/Containers/DictionaryKeyPolicy.cs)、[ComparerKind](../../../src/DurableGraph/DictionaryComparerKind.cs) | 分开可表示 key、模式识别、stored 资格与 current comparer 创建；扩展 4/5，标准规则不泛化成任意 Default |
| [Generator.TemplateHistory](../../../src/DurableGraph.Generator/DurableSchemaGenerator.TemplateHistory.cs) | 放开已知 Durable struct key 的局部资格拦截，保留不支持形状诊断；Dictionary 双实参原本已触发 Family |
| [StateModelRegistry](../../../src/DurableGraph.Persistence/StateModelRegistry.cs)、[Dictionary snapshot](../../../src/DurableGraph.Persistence/StateModelSnapshot.Dictionaries.cs) | typed 选择、一个 resolver、snapshot 配置与惰性缓存；不扩展历史 reader 工厂 |
| [DictionaryObjectBinding](../../../src/DurableGraph/Runtime/Containers/DictionaryObjectBinding.cs) | Capture 识别/预检，Allocate 注入并包装当前 comparer；Hydrate 保持同实例 TryAdd |
| [DictionaryStateReader](../../../src/DurableGraph/Runtime/Containers/DictionaryStateReader.cs)、[FrozenDictionaryState](../../../src/DurableGraph/Runtime/Containers/FrozenDictionaryState.cs) | 新模式资格、根 null/零宽检查；新模式跳过业务 lookup，保留 canonical 索引/所有编码操作 |
| [DictionaryUpgrade](../../../src/DurableGraph/Runtime/Binding/StateBindingContext.DictionaryUpgrade.cs)、[RevisionDecoder](../../../src/DurableGraph.Persistence/RevisionDecoder.cs)、[NormalizedRevision](../../../src/DurableGraph.Persistence/NormalizedRevision.cs) | exact 与新业务边界分开；不在只读/归一化阶段执行 comparer resolver；策略保留和碰撞诊断准确 |
| [WorldWorkspace](../../../src/DurableGraph.Persistence/WorldWorkspace.cs) | 核对分配/填充及回捕边界，原则上不增加全图阶段或第二份模式状态 |

## 9. 建议施工顺序与最小验收

下表为实施与验收的阶段划分；完成结果和测试位置见 §11。

| 阶段 | 最小可观察结果 |
|---|---|
| G0：默认复合 Key 纵向闭环 | Timestamp 示例以真实 SG 的普通/generic struct 运行；Default 零配置；完整捕获/冷重开/连续三次 Commit |
| G1：模式与当前配置 | 标准多模式保留，Application typed/resolver、缺失/错类型/异常、空实例；4/5 回捕模式稳定 |
| G2：数据验证与历史 | 无当前 comparer 的 exact/Normalize、不运行不可达 resolver；持久重复与业务冲突分阶段拒绝；root null/零宽/非法 Delta |
| G3：真实包与历史演进 | 两代泛型 Key/嵌套 struct，删除旧 CLR，显式双槽升级、强制 Base 后恢复增量；验收完整程序集边界 |

必要见证：

1. 普通/泛型、私有 readonly、Nullable/string/identity 成分；Ignore Timestamp 的自定义 IEquatable；Transient 不同而持久状态相同不产生变化。
2. 实际替换 key 的 Timestamp 或同内容 string 实例，生成 Remove+Add 并保留字段/引用；普通查询不 Capture、不分配 ObjectId。
3. 同 CLR 的 Ordinal/IgnoreCase/ReferenceIdentity 并存，单元素无碰撞例也保留查询差异；标准模式不调用 Application resolver。
4. Application 可以使用按位浮点 comparer 保存 ±0/不同 NaN；不可误走旧 ScalarDefault 的 lookup 检查；同型不同 custom 按统一当前配置恢复的边界有见证。
5. Application resolver 返回 Default、Ordinal 或用户 comparer；Load 后 NoChange、value Delta 及下一轮冷重开均保持模式 5。
6. CurrentDefault 持有模式 4 的恢复/回捕；历史 inline 从 struct 到 enum 的可表示变化不误分类为 0；反向无法承载标准 0 时明确拒绝而非默换业务规则。
7. typed 精确项优先、唯一 resolver 按闭合类型成功缓存；泛型 comparer 位于数组/List/泛型外层仍一次闭合；异常、null、错类型不得回退。
8. 空的实际 Application Capture/Allocate 缺配置失败；exact 读取和不可达 Normalize 不调用 resolver；全部引用/Schema 要求仍验证。
9. 两键因丢弃 Transient 或 Empty 规范化产生同 canonical body 时拒绝；零宽 key/value 的 count>1 在 entries 分配前拒绝。
10. 旧 DTO key 经显式升级后 canonical 冲突在 Normalize 失败；canonical 不同但当前 comparer 合并时在 TryAdd 失败，旧 DTO 仍可读取且不交付半个 World。
11. Key→World/Dictionary 的 identity 环、共享数组/容器及 child-only 修改沿用 ObjectId；不得靠字典填充顺序偶然让引用内容 hash 测试通过。
12. 真正 PackageReference 两代 Key：旧 CLR 删除、既有 accepted history 文件/hash 保留、完整 key/value Upgrade 与所有相关生成路径。
13. 不改 Schema、不触发 Upgrade，只在两代程序中改变当前 Equals/comparer：旧 exact DTO 仍可读，Normalize 不调用 comparer，原本不同的键在当前 TryAdd 碰撞而使 Load 失败。

验收运行根 solution build、相关/完整 tests 及真实包回归；§11 保存结果，避免在每条设计合同后重复测试日志。

## 10. 审阅结论与后续裁决

实施前先由需求/易用性、最小架构、恢复语义三路独立分析，再交叉检查标准策略丢失、Application 回捕漂移和不可达恢复边界。
同 CLR 多标准策略有现成回归；因此保留选择标签比无条件删除更符合当前功能。
新增模式包装只为解决恢复模式在下一次 Capture 改变的具体反例，不恢复旧稿的 comparer 语义证明体系。
三路完整主稿复审完成；定稿补清 inner 的用户捕获边界、格式/领域失败阶段、共享 comparer 实例及不改 Schema 的行为变更见证。
设计阶段未发现需要新增恢复阶段或 comparer 平台的阻塞，未进行性能测量。
设计阶段修改 8 份 Markdown；510 个本地链接、46 个锚点及 git diff --check 通过。
归档与 `77e4341` 原稿核对一致，仅增加归档说明和调整相对链接。后续产品施工证据独立记录于 §11。

只有以下需求变化才应重新选择结构：

- **同闭合类型的多种 Application 配置必须分别恢复**：需要持久角色标记或显式外部身份规则，不能在当前单策略合同下假装支持。
- **comparer 必须读取引用目标内容**：需要确定恢复阶段及允许依赖，可能先做固定字典后填充；不能仅靠“不读 Transient”推导安全。
- **必须保留历史业务查找语义**：需要另外的代码/策略保留合同，不能要求本片新增模式承担。

ValueTuple/record struct 先完成其值布局、成员标注和历史能力，再自然使用当前 Default/自定义比较。
Tuple 的元素名称不应成为键身份；record 的合成比较可能包含未持久字段，应用仍须遵守本片状态/行为合同。
SortedDictionary 的排序与 OrderedDictionary 的顺序状态独立设计，不因共享 key/value DTO 就自动获得支持。

## 11. 实施与验收记录

本片限定为 §3–7 的合同，不扩充 tuple/record、同型多 Application 角色、引用内容比较恢复阶段或 comparer 历史代码。
依赖仍是 StateStore → Runtime；配置由 Registry/Snapshot 拥有，Runtime 仅取得惰性 comparer 能力。

| 要求 | 实现归属 | 验收入口 | 状态 |
|---|---|---|---|
| G0：普通/generic struct key 与完整 DTO 差分 | Generator 资格、Runtime key policy | [真实 SG 纵向 tests](../../../tests/DurableGraph.Tests/CompositeDictionaryGeneratorTests.cs) | 已验证 |
| G1：模式 4/5、当前配置、回捕稳定 | Runtime binding/wrapper；StateStore Registry/Snapshot | [body tests](../../../tests/DurableGraph.Tests/CompositeDictionaryBodyTests.cs)、[Repository tests](../../../tests/DurableGraph.Persistence.Tests/CompositeDictionaryRepositoryTests.cs) | 已验证 |
| G2：历史数据/业务验证分层、Upgrade、零宽/根 null | Runtime reader/Upgrade、既有图验证 | [Upgrade tests](../../../tests/DurableGraph.Tests/CompositeDictionaryUpgradeTests.cs)、上述 body/Repository tests | 已验证 |
| G3：删除旧 CLR、两代历史与同 Schema 行为变化 | PackageConsumerProbe | [真实包见证](../../../experiments/PackageConsumerProbe/CompositeDictionaryConsumer/README.md) | 已验证 |

实现保持局部：SG 仅放开受支持 inline key 的资格；没有生成领域 comparer，没修改历史模板/Schema 目录格式，
也没有给 Upgrade 或图恢复增加阶段。Registry 提供 typed 入口和单 resolver，Runtime 的可选惰性能力保持依赖方向。
0–3 仍守原有框架 lookup 合同；0 历史单整数布局不能借同形普通 struct 的 Default 偷换标准行为。

验证记录（2026-09-10）：

- 实施前根 build 为 0 warnings / 0 errors，基线 **2029** 项测试通过。
- 最终 `dotnet build DurableGraph.slnx --no-restore`：**0 warnings / 0 errors**。
- 最终 `dotnet test DurableGraph.slnx --no-restore`：**2091 passed，0 failed，0 skipped**，其中 Runtime/SG 1235、StateStore 598、Serialization 103、Storage 155；新增 62 项。
- `Run-CompositeDictionaryProbe.ps1` 通过两代真实 PackageReference 的 Publish/Verify。history **6 → 9**，原 filename/hash 不变；192 次显式 key/part/value 回调，两个升级字典 Base 后恢复 Delta。另一个 Schema V1 完全不变的仓库，只改 Equals 即在 TryAdd 失败，而失败前后 exact DTO 均可读取且不执行比较。
- 新模式的空配置、成功缓存、标准策略隔离、不可达行完整引用、Transient/Empty 规范化重复、零宽分配前限制、模式 4/5 回捕及两类升级碰撞均有通过的回归。
- 独立审阅核对产品 diff、生成器、四组新测试与包见证，未留下阻塞发现。首轮仅修复新增测试绕过 Capture 阶段的用法及一处测试分析器警告；未放松产品阶段约束。
- 复用同一份包 feed 的 `Run-DictionaryProbe.ps1` 与 `Run-GenericProbe.ps1` 均通过，保留既有标准 comparer、历史键值升级、开放泛型与删除旧 inline CLR 的交付能力。
- 集成文档与差异核对完成；8 份 Markdown 的 481 个本地链接、45 个锚点以及 `git diff --check` 通过。

本次真实包产物位于忽略目录 `experiments/PackageConsumerProbe/obj/composite-dictionary-20260910041040-18892-8573e918`，
运行入口与断言维护在消费者 README。不把运行日志或生成文件纳入源代码。
