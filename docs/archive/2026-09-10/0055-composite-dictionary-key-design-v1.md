> 冻结归档：原稿来自 `77e4341`，2026-09-10。以下结论已被[修订后的 DB-055](../../design-branches/0055-composite-dictionary-key-design/README.md)替代；旧稿中的 Proposed 与推荐只描述当时的分析，不是当前施工依据。仅调整归档相对链接。

# DB-055：有限复合 Dictionary Key 的比较能力与功能边界

> 状态：**Proposed / 独立分析与交叉审阅后的推荐，尚未实施**，2026-09-10。
> 本轮授权：研究功能边界并形成设计文档；不据此启动产品实现。
> 代码依据：`821e602`，DB-054 已完成。本文的新增 API、比较策略和生成代码均为拟议形状。
> 问题：怎样让自定义 struct / generic struct 成为实用、可恢复的组合键，同时避免承接任意用户比较代码的历史与恢复依赖？
> 最小成功见证：`Dictionary<Key<string, Mode>, Point>` 用新构造的同值键查询成功；连续保存保留原键中的引用身份；删除旧 Key CLR 后仍能 exact 读取、显式升级；升级碰撞拒绝。

## 1. 推荐结论与需求来源

**保留普通 BCL Dictionary，增加显式选择的框架 comparer；按 Key 的全部持久字段递归比较。**
优先支持现有 Durable struct，包括泛型、readonly、私有持久字段、嵌套 struct、Nullable 组件、string 和受支持引用组件。
不要求用户改写自己的 Equals，也不将 record struct 或 ValueTuple 的序列化支持作为前置。

这是扩大有效支持范围的一个固定规则，不是任意 comparer 插件平台。
现有 key-body 寻址 Delta、owned DTO、完整目录验证和双槽 Upgrade 保留；新增工作的中心是查找相等性。

| 来源 | 要保留的要求 / 尚未选择的事项 |
|---|---|
| 本轮用户请求及 DB-054 前置讨论 | 复合值 Key 是实际领域建模需求；必须认真覆盖普通/generic struct，ValueTuple 是后续候选；允许 BCL 或等效自建容器 |
| 已实现 DB-054 | 映射无序、键引用身份保留、完整 source/current 校验、升级碰撞失败；BCL 外观实验性 |
| 当前产品总体约束 | 历史 DTO 不依赖旧领域 CLR；单对象显式 Upgrade；先分配再 Hydrate；无任意对象内容读取与索引依赖调度 |
| 本文新推荐，待用户采纳 | 显式框架 comparer；全部持久字段参与；string Ordinal、其他引用 identity；框架比较与类型自身 Equals 可以不同 |
| 暂不承诺 | 任意用户 comparer、任选 KeyField、逐字段比较选项、根 Nullable key、record/tuple 外观及排序容器 |

当前没有待兼容的已发布复合 Key 数据；不建设双写或旧格式迁移层。
这里有实现接缝依据和反例分析，尚无新增能力的可执行验证或性能测量。

## 2. 将三个问题分开

### 2.1 可保存，不等于能安全重建索引

[StateValueBinding](../../../src/DurableGraph/Runtime/Binding/StateValueBinding.cs) 和既有 inline DTO 已能冻结复合 Key。
[DictionaryStateReader](../../../src/DurableGraph/Runtime/Containers/DictionaryStateReader.cs) 的 Delta 已以完整 canonical key Base bytes 配对。
当前拒绝 struct 的位置主要是 [DictionaryKeyPolicy](../../../src/DurableGraph/Runtime/Containers/DictionaryKeyPolicy.cs) 和 SG 的已知 key 诊断。

真正欠缺的是一份不依赖已删除 CLR、Transient 或目标对象恢复顺序的查找规则。
只让 body 接受 struct，随后在 Hydrate 中调用 Default，并不能提供这份规则。

### 2.2 查找相等性与持久键相等性继续分开

| 用途 | 规则 | 例子 |
|---|---|---|
| 领域查询、唯一键验证 | 本文固定的递归查找规则 | `Key(new string("a"), 1)` 可查询已有同内容键 |
| 两次保存之间配对条目 | 同 exact KeySlot 的 canonical key Base bytes | 字符串换成新实例、ID 改变，仍是 Remove(old)+Add(new) |
| Hash | 各条路径内部的加速手段，不落盘、不作身份 | 热侧 domain hash 与冷侧 lookup hash 无需数值相同 |

因此不能复用 `IStateOps.StateEquals` 作为查找 Equals：前者的浮点按位、引用按 ObjectId，职责不同。
也不能把 Delta 配对改成查找相等，否则替换键中的 string 实例时可能丢失真实引用变化。

### 2.3 容器外观不能替代比较规则

无论普通 Dictionary、薄包装还是自建哈希表，都必须回答上述相等性、历史解释和升级碰撞问题。
包装的额外收益是减少漏传 comparer；它不自动使 `EqualityComparer<TKey>.Default` 受框架控制。

## 3. 支持范围：按成分闭合，避免列举组合

新增策略暂名 **PersistentFieldsV1**。根 Key 是现有支持范围内、显式 `[DurableType]` 的普通 struct；泛型实参按既有 nominal 规则闭合。
不新增 `[DurableKey]` 资格标记；使用这个 comparer 本身就是选择该语义。
既有标量/enum/string/直接引用 Key 策略不变。

| Key 内的成分 | 推荐首片规则 | 领域用途与边界 |
|---|---|---|
| 13 种已有标量 | 已知类型的默认值相等 | 坐标、序号、数值维度；不调用任意用户方法 |
| Durable enum | exact 底层整数相等 | 分类/模式/Flags；未知数值保留 |
| Durable struct / generic struct | 按 FieldId 顺序，递归全部持久字段 | `CellKey`、`Key<TScope,TId>`、嵌套组合 |
| Nullable 组件 | 先比较 HasValue；present 才递归 | 可选维度；absent 不读取内部值、不遍历其引用 |
| string 组件 | Ordinal 内容相等，null 与 Empty 不同 | 名称+分类等高价值组合；同内容新实例可查询 |
| 其他已支持引用组件 | 引用身份相等，null 合法 | scope 对象+序号；停止递归，不读取 class/array/List/Dictionary 的内容 |
| readonly / 私有持久字段 | 与普通字段相同 | 复用 SG 字段读取与恢复能力，不要求额外可变性 |
| Transient / 未持久化字段 | 不参与 Equals/Hash | 缓存、调试信息不改变键资格，也不会成为历史依赖 |
| 自定义 Equals/GetHashCode/IEquatable | 不调用、不要求删除 | 类型在其他业务用途下可以保留自己的相等语义 |

精确定义：

- Half/float/double 用相应默认 Equals 语义：正负零相等，NaN 相等；Hash 必须使用相同等价规则，不能直接 hash 原始位。
- string 固定 Ordinal，不进行大小写或 Unicode 正规化。null 组件与空字符串分开；不同空串实例按全局 Empty 规则相等。
- 非 string 引用使用 `ReferenceEquals` / `RuntimeHelpers.GetHashCode` 的身份语义，不调用目标的虚方法。Hash 数值无需跨进程保持。
- struct 的 Equals/Hash 都忽略相同的非持久字段；不读 getter、不执行构造器、没有原始 CLR 内存或 padding 比较。
- 空持久 struct 合法，只有一个等价类，故字典至多容纳一个这样的键。不存在状态贡献的 phantom 参数不增加比较能力依赖，但仍受既有 nominal 类型支持边界约束。
- 不要求 Key 声明为 readonly：BCL 按值存入 struct；上述引用成分的比较也不依赖可变内容。普通局部副本的修改不会修改已经存入的键。
- 顶层 null key 仍拒绝；允许 struct 内的 null 不等于开放 `Dictionary<Nullable<T>,V>`。

非 string 引用内容比较仍然延后。identity 叶纳入首片是因为已有统一引用路径可直接处理，不增加恢复阶段；
它还能让 `Key<T>` 闭合引用参数时少一条人为限制。不能由此开放 object/interface 通配槽、数组协变或未知 BCL 类型。

这些组合已经覆盖：二维/三维坐标，租户 ID+资源名，分类+编号，泛型强类型 ID，含可选分量的索引，以及作用域对象+局部编号。
ValueTuple 的简洁语法有用，但不是完成这些建模能力的唯一途径。

## 4. 创建与普通查询的拟议形状

```csharp
[DurableType("ItemKey", 1)]
public partial struct ItemKey<TScope> {
    [DurableField(1)] public TScope Scope;
    [DurableField(2)] public string? Name;
}

// 拟议 API；当前尚不存在。先登记生成能力，再创建普通 BCL 容器。
var models = new StateModelRegistry();
Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);
var keyComparer = models.GetPersistentKeyComparer<ItemKey<int>>();
var items = new Dictionary<ItemKey<int>, Item>(keyComparer);

items.Add(new() { Scope = 7, Name = new string('a', 1) }, item);
bool found = items.ContainsKey(new() { Scope = 7, Name = new string('a', 1) }); // true
```

建议先只提供一个显式取得 comparer 的入口。它通过现有代码登记材料做一次闭合，不需要先打开 Repository、创建 Session 或产生 ObjectId。
返回普通 `IEqualityComparer<TKey>`，可以缓存并供多个 Dictionary 使用；最终内部对象必须是框架封闭实现。
日常查询不再访问 registry、SchemaStore、CaptureContext、DTO 或序列化器。

没有 comparer 的 `new Dictionary<ItemKey<int>,V>()` 仍执行 BCL Default；框架不改写它的行为，Capture 明确拒绝不支持的实例 comparer。
即使 Default 在某个类型上恰好与框架规则一致，本片也不尝试证明或默默接管。
建议诊断指向上述创建用法，检查空 Dictionary 的 comparer 不能因无条目而省略。

这项选择确有用户成本：一般需要在模型工厂/构造时传入 comparer，原字段初始化器的简单 `new()` 不再足够。
若实践中重复创建成为负担，可加一行封装的 `CreateDictionary`，不先造第二套注册目录或全局静态 `Serializers<T>`。
若用户要求“默认构造也绝不可能用错”，固定 comparer 的专用容器才有明确价值，见 §8。

## 5. 最小实现结构

### 5.1 热侧：SG 读取领域字段，泛型子比较器在创建时闭合

[GenericProjection](../../../src/DurableGraph.Generator/DurableSchemaGenerator.GenericProjection.cs) 已有按持久字段生成的 ref readonly accessor，
可供普通/generic struct 的比较代码复用。比较与 Hash 使用同一份字段模型，不让一方自行反射枚举。

生成代码形状如下；名字和接线参数是示意，不要求扩展全部 `IStateOps`：

```text
generated KeyComparer<Key<T>>
    持有已经闭合的 IEqualityComparer<T>（仅实际持久成分需要）
    Equals: 逐字段直接读取；已知标量/string/enum 直接调用框架叶操作
            未知 T / 复合子值调用已注入的子 comparer
    GetHashCode: 用完全相同字段与叶规则组合 hash
```

允许未知子类型有一层 comparer 接口调用；不为消除这层调用强迫改造既有 projection/body 全套泛型参数。
已知字段保持直接绑定，不在每次查询时按 Type 查表、反射字段、装箱整个 key、编码 bytes 或分配冻结 DTO。
闭合期的反射/MakeGenericType 与现有 [GenericFactories](../../../src/DurableGraph.Generator/DurableSchemaGenerator.GenericFactories.cs) 模式一致。

建议在 [StateDefinitionBinding](../../../src/DurableGraph/Runtime/Binding/StateDefinitionBinding.cs) 增加可选的 current key-comparer factory，
由 [StateBindingContext](../../../src/DurableGraph/Runtime/Binding/StateBindingContext.cs) 的专用解析入口按需求调用。
这样普通 TValue 的 ResolveCurrentValue 不必建立额外键能力，历史 DTO/binding 也不用携带它。
保持原有显式登记权威；`StateModelRegistry` 的公开入口只是创建一次无 SchemaStore 的快照并解析。

返回的 comparer 冻结完整 current KeySlot、生成能力来源和子 comparer，不持有可变 registry 或 Repository。
Capture 识别 framework wrapper 时应验证：策略、精确 TKey、完整 exact KeySlot，以及对应生成能力来源/子能力组成。
**不能只检查 comparer 实例引用，也不能只检查相同 Schema：布局相同不证明两个工厂读取了相同领域字段。**
不同快照用同一稳定生成能力创建的 comparer 可以互用；同 CLR/同布局却换了另一个手工工厂时明确拒绝。
最小来源记录可使用实际参与闭合的 `StateDefinitionBinding` 所拥有的独立身份 token，覆盖所有 inline 子 helper，按定义 ID 去重。
token 只是内部 object，不回指 Definition；相同 Definition 在快照间提供同一个 token，另一手工 Definition 则不同。
不用直接持有 Definition 本体，因为它还包含整份历史能力及可能捕获外部对象的委托。
生成的 inner comparer 只保存子 comparer/不可变叶配置，不能捕获工厂参数 Context。
Dictionary binding 持有已闭合的期望 comparer 和来源，Capture 只核对，不重复运行 factory；Allocate 直接用此 comparer 创建字典。
当前 Create/CreateComparer 接口没有 Context，G0 应把这一能力从 snapshot 闭合处传入 binding，而非恢复时另起注册解析或回退 Default。
此来源校验只在进程内，不增加持久 factory ID，不把 SchemaStore 变成执行代码注册表。
它不证明任意手写委托的纯度或捕获关系；手工登记能力仍属于既有可信代码边界，不能随外部可变状态改变查找语义。
闭合非 string 引用叶时只取得 nominal 引用槽并选择 identity，不递归闭合其对象 body，避免 Key→Dictionary 环引入闭合递归。

已核对 [UsesGenericTemplate](../../../src/DurableGraph.Generator/DurableSchemaGenerator.TemplateHistory.cs)：
持久 Dictionary 的双实参模式本来就会触发 Family，普通非泛型 struct Key 因而也可走这个入口，无需增加新属性。
完全不含 Dictionary/泛型/Nullable 等条件的旧非 Family 编译没有 Definition 注册能力，本片不顺带改造它；
工厂缺少已登记定义时明确拒绝。跨程序集的生成与登记边界也不借此扩展。
G0 验证纯非泛型 Dictionary 消费者与开放泛型消费者均能闭合；具体 wrapper/factory 的代码形状以该编译见证收敛。

### 5.2 冷侧：按 exact KeySlot 解释现有 key body

不恢复旧领域 Key，也不要求历史 DTO 实现新的比较接口。
复用现有 KOps 生成/读取的 canonical key bytes，由一个局部解释器按 exact Schema 生成临时 lookup key：

1. 标量读成框架已知值；浮点按查找规则归一；enum 自然是其历史整数字段。
2. InlineValue 按 exact 字段顺序递归；Nullable 保留 presence，absent 不读取 child。
3. string 的 ObjectId 从完整 source/current 目录解析为不可变内容；零 ID 表示 null 组件，不能调用当前根 key 禁 null 的 `ReadReference` helper。
4. 非 string 引用保留 ObjectId，比较其身份；引用类型/存在性仍由统一引用验证负责。
5. 按当前 exact 槽的结构保留字段与可空边界，Hash 加速后比较完整 lookup 内容。不能靠简单拼接字符串或未经确认的 hash 判断相等。

临时表示可先用框架拥有的结构化成分列表；也可用规范 lookup bytes，但 string 必须按完整 UTF-16 内容无损表示，
含孤立代理项也不得经替换式 UTF-8 编码而错误合并。它不是第二种持久格式，不给它分配 Schema/RepresentationId。
具体容器不作为产品承诺；首次实现优先简单、完整比较且容易测试的结构化表示。

解释器使用现有规范 body/字节原语并检查完整消费；复用 schema 深度约束。
它只服务字典查重，不扩成通用对象反射/序列化平台。新增分支可以沿用现有临时 key bytes，接受这部分分配。
热侧直接 comparer 与冷侧解释器各自满足 Equals⇒同 hash，二者只须保持等价关系一致。

### 5.3 恢复不增加阶段

key 的值部分经既有投影完整 Hydrate 后再 TryAdd；string 不可变，其他引用只比较身份，故无需等待目标内容 Hydrate。
保留全 Allocate → Hydrate，包含 Key→Dictionary 自身、Key→World 的引用环。
不增加 index rebuild hook、任意 comparer 调度或恢复依赖拓扑。

## 6. 持久格式、验证与升级

建议在现有对象内容 comparer 标签中增加 `PersistentFieldsV1 = 4`；0–3 的意义不变。
该标签固定 §3 的递归查找合同，不保存 comparer CLR 名、hash、字段选择表或任意执行代码。
完整 exact KeySlot 已提供字段布局、inline 版本与引用声明约束，不新增 Schema 元数据或单独的 key Schema。
未来若改变比较规则，必须另给明确策略身份/版本，不能原地改变 V1 含义。

预计 Dictionary codec 1 grammar、目录 kind 5、SCB1 v2、Base v4、Storage wire v3 均可保持。
history 已能表达 struct/generic struct，无需仅为新增 comparer 升级 history v7；能力生成与参数接线仍须真实包验证。
旧程序遇到新 comparer 标签明确拒绝，本原型不增加向后兼容写法。

验证分层沿用 DB-054，但不能照搬“非 scalar 都是一个非空 ObjectId”的现有分支：

| 阶段 | 新增策略的责任 |
|---|---|
| current 闭合 / Capture comparer 检查 | 根及参与字段具备能力；实例策略与生成能力匹配；空字典也检查 |
| 独立 body / Apply | grammar、完整 canonical key、持久重复、策略与 exact 布局合法；无 string 内容依赖时可完成局部 lookup 查重 |
| Capture Seal | 所有目标已捕获且引用合法后，用完整 candidate 检查 lookup 唯一性 |
| exact Revision | 全部 stored 行与引用校验后，对所有字典检查，包括不可达字典；成功后才能返回 DecodedRevision |
| Normalize 后 | 全部 current 行与引用校验后，检查所有字典的新 lookup 唯一性 |
| Hydrate | TryAdd 最终拒绝重复；不能用 indexer 覆盖，也不能以这一步替代前述完整验证 |

string 内容依赖使独立 body reader 仍不承诺单独验证完整查找等价；现有图级 API 保持完整合同。
空 struct 可能产生零字节 key；当 exact 布局可证明键只有一个等价类时，Base count / Delta 结果 count 大于 1，
应在 entries 数组分配前拒绝。特别是 TValue 也为零字节时，不能只依靠现有 payload 最小尺寸预检。
这是一项具体的键唯一性约束，不扩展为全图内存预算；合法的 0/1 条映射继续支持。
当前实际接缝：[DictionaryStateReader](../../../src/DurableGraph/Runtime/Containers/DictionaryStateReader.cs)、
[CaptureContext](../../../src/DurableGraph/Runtime/Capture/CaptureContext.cs)、[RevisionDecoder](../../../src/DurableGraph.Persistence/RevisionDecoder.cs)、
[NormalizedRevision](../../../src/DurableGraph.Persistence/NormalizedRevision.cs)。

Upgrade 保留 [DictionaryUpgrade](../../../src/DurableGraph/Runtime/Binding/StateBindingContext.DictionaryUpgrade.cs) 的双槽独立工具和完整预检：

- Key 的 inline 布局变化沿现有 exact Schema 版本规则处理；显式转换全部条目，保留 ObjectId/count/comparer。
- 不能只查转换后的 canonical bytes；不同 string ID 的同内容键、不同 NaN 位模式等仍可能 lookup 碰撞。
- 遇到碰撞失败，不合并、丢弃或选择获胜条目；仍 live 的升级字典强制 Base，后续恢复 Delta/NoChange。
- 字典 owner 的 nominal 引用约束不随键内的 inline 升版而自动升级；所有 exact 依赖检查不能因 comparer/plan 缓存命中省略。
- 不为 Key Upgrade 开放字符串内容读取、新字符串对象创建或新 ID；若业务转换需要这些能力，仍属于跨对象升级后继。

## 7. 不变式与最小反例

| 必须保留的区别 | 删除后的失败见证 |
|---|---|
| lookup 相等与持久 bytes 相等 | 同内容的新 string 查询失败，或保存新 key 实例时错误保留旧 ID |
| 热侧不 Capture | TryGetValue 一个不存在的键会分配对象 ID、要求活动会话 |
| 全部持久字段与 Transient 分开 | 两键仅凭丢弃的缓存区分，恢复时不可逆地碰撞 |
| Equals 与 Hash 使用同一叶规则 | Equals 认为 +0/-0 相等但 hash 不同，查询可能漏项 |
| null 组件与 Empty 分开 | `(null, 1)` 和 `("", 1)` 被误合并；或合法 null 组件被根键检查拒绝 |
| 引用只按身份，不访问目标内容 | Key 指向尚未 Hydrate 的节点/字典，建立的 hash 随恢复内容变化 |
| 完整 source/current 检查 | 不可达的坏字典被忽略；升级后两个不同 key body 的 lookup 碰撞漏过 |
| comparer 来源与 exact 布局分别核对 | 相同 KeySlot 的另一个工厂按不同 CLR 字段比较，却被当作同能力接纳 |
| 全部参与字段闭合 | 空字典、Nullable absent 掩盖缺失能力，第一次插入或恢复才失败 |

## 8. 候选方案及功能取舍

| 路线 | 可实现性与主要成本 | 使用端代价 | 建议 |
|---|---|---|---|
| A：严格验证 Default 的安全子集 | 可行；无用户 Equals/GetHashCode/IEquatable、全部实例字段持久、递归限定安全叶；省 SG comparer 热路，但要维护资格检查 | 普通 new() 即可；添加 Transient 或常见 IEquatable 优化会丧失资格，可能迫使另造 Key 类型 | 保留为更窄备选，不与 B 同时建设 |
| B：框架持久字段 comparer + BCL | 新增当前生成 comparer、一次闭合和历史 lookup 解释；既有 DTO/Delta/Upgrade 可复用 | 创建时显式取一次 comparer；比较语义与领域 Equals 可不同 | **推荐**；支持边界随持久字段演进，更适合泛型组合和已有领域模型 |
| C：固定语义薄包装/自建等效类型 | 仍需 B 的比较核心；另做 CLR nominal/绑定/创建/接口外观 | 更难漏传 comparer；改字段类型或创建习惯，有 API 适配成本 | 漏传错误成为实测主要问题时重访；暂不重写哈希表 |
| D：任意用户 comparer 或恢复后调用 Default | 要保留任意执行语义、依赖和历史行为；多一恢复阶段也不能解一般容器依赖环 | 最大表面兼容性，最弱防误用 | 不纳入这次有限支持 |

A 是真实可行方案，不以“无法实现”排除。选择 B 的理由是：
增加一套固定 comparer 的工作，换取 Transient、用户 Equals 和持久索引行为的独立演进；
以后增加非持久字段，不会无意破坏原字典的可保存性。这是维护性与领域适用性的取舍。

### 延后但保留路径

- **ValueTuple**：未来作为框架内建复合值，按位置递归本文叶规则。需要明确 TypeExpr/历史 exact 描述、ItemN/Rest 布局和支持 arity；
  泛型/Nullable/引用叶的比较可复用，不直接转发 ValueTuple 默认 Equals。元素名称不应成为键身份；尚未选择具体格式。
- **record struct**：先解决生成器对位置参数、自动属性/隐式 backing field、FieldId 与 history 的纳入方式。
  一旦可序列化，比较复用同一持久成员模型；不依赖其合成 Equals，也不因 record 关键字自动放行。
- **只按部分持久字段、string IgnoreCase、逐字段策略**：确有需求再设计策略身份与演化。
  目前推荐把索引维度放入专用的小 Key，非键信息放到 Value；不能用第二套字段标记草率掩盖信息丢失/历史变化问题。
- **SortedDictionary / OrderedDictionary**：分别有排序规则/顺序状态，不能由当前相等 comparer 自动得到兼容承诺。

## 9. 建议施工顺序与验收

以下是采纳方案后的施工建议，不是本轮已执行的工作。

| 阶段 | 可独立观察的结果 |
|---|---|
| G0：最小生成/闭合见证 | 普通非泛型 Dictionary 消费者、`Key<T>` 均经已有 Family 登记取得 comparer；查询不依赖 Capture/反射；跨 snapshot 来源校验成立 |
| G1：当前比较与政策接入 | 字段规则、泛型闭合、Nullable/null、readonly/private、Transient/抛异常 Equals、identity 环、错误 comparer 诊断 |
| G2：历史查重与字典接线 | 新策略标签、递归 exact 解释、全 source/current lookup 验证；原键寻址 Delta 原语不变 |
| G3：历史演化与真实包 | 旧 Key CLR 删除、泛型键升级、碰撞拒绝、一次 Normalize、强制 Base→NoChange→Delta、连续提交后冷重开 |

验收不要只用两个小整数证明整个闭包。至少包含：

1. `CellKey(int,int)`、`ItemKey<string,Mode>`、嵌套 struct/Nullable、private readonly；同值新键查找成功。
2. Key 自定义 Equals/GetHashCode 抛异常，Transient 不同；显式框架 comparer 仍工作，Default/custom 实例 Capture 拒绝。
3. float/Half/double 的 ±0、多个 NaN 位；null string、Empty、同内容不同实例、孤立 UTF-16 代理项。
4. 同 string 内容换实例后 Remove+Add，实际 Key 引用保存；同值查询不会分配 ID，也不需要打开 Store。
5. 非 string 引用叶、Key→World/Dictionary 环；目标内容修改不改变查找结果，目标 child-only Delta 不使字典产生伪变化。
6. Nullable absent、空字典、空 struct、合法 phantom 参数；缺失/错能力在实际键操作前拒绝；零字节 Key/Value 的 count>1 在 entries 分配前拒绝。
7. 对同一组已捕获 key，交叉核对领域 comparer 与历史 lookup 的两两等价结果；分别检查两条路径各自的 Equals⇒同 hash，不要求两种 hash 数值相同。
8. 升级后不同 canonical body 的 lookup 冲突（包括不可达字典），完整失败且不交付 World；原 DTO/已发布基线不污染。
9. 真实 PackageReference 两代 Key（含泛型与嵌套子版本），旧 CLR 删除后 exact 读取及显式规则工作；旧 accepted history 文件/hash 保留。

实施时按 repository 指南运行根 build、相关/完整 tests，并用真实包证明生成与历史交付。
本轮是文档研究，只做源代码对照、独立审阅与文档链接检查，不把上述验收写成已通过。

## 10. 审阅结论与仍由用户决定的边界

三路独立分析分别从领域需求、最小架构、恢复语义提出方案，主线程对照源码并交叉质询。
主要收敛：A 的严格 Default 子集可行；B 更利于普通领域类型继续演进；冷侧无需再生成一套历史领域 comparer；
其他引用的 identity 叶纳入首片没有新增恢复阶段；comparer 来源检查不能仅靠 schema 相等。

没有发现需要立即砍掉 struct/generic struct、string 或 Nullable 组件的技术障碍。
具体工厂/普通与泛型生成接线及 lookup 临时表示尚需 G0/G2 的代码见证，不宣称已验证性能或零改造成本。
本轮只修改 6 份 Markdown；源代码对照及两路完整主稿复审完成，450 个本地链接、38 个锚点检查通过。

建议用户采纳的核心选择只有两个：

1. **全部持久字段构成键，显式框架 comparer 可以独立于类型自己的 Equals。** 若必须保留 Default 构造且类型满足严格资格，可选更窄的 A；若必须兼容领域自定义相等，则须另定业务比较合同，A 也不能覆盖。
2. **先接受构造时显式传 comparer，继续保留 BCL Dictionary。** 若无参/默认构造的防误用更重要，采用 C 的固定语义容器外观，但比较核心仍沿用 B。

ValueTuple/record struct 继续保留明确后继，不为本片承诺其外观；按字段选比较器、大小写模式、引用内容比较均可在真实需求下单独取舍。

## 11. 证据入口

- 本库：[DB-054](../../design-branches/0054-dictionary-content-object-slice/README.md)、[DictionaryObjectBinding](../../../src/DurableGraph/Runtime/Containers/DictionaryObjectBinding.cs)、
  [StateModelRegistry](../../../src/DurableGraph.Persistence/StateModelRegistry.cs)、[Dictionary snapshot](../../../src/DurableGraph.Persistence/StateModelSnapshot.Dictionaries.cs)、
  [生成形状检查](../../../src/DurableGraph.Generator/DurableSchemaGenerator.Ancestry.cs)、[持久成员枚举](../../../src/DurableGraph.Generator/DurableSchemaGenerator.cs)。
- BCL Dictionary 允许选择 comparer，默认使用类型默认相等，要求键的 hash 依据在存入期间稳定；这支持“显式规则”与“普通 Default”必须分开的判断。
  [Microsoft Dictionary 文档](https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.dictionary-2?view=net-10.0)。
- record struct 合成相等涉及实例字段及其默认 comparer，且允许用户实现；其外观不证明与 Durable 持久字段集合相同。
  [C# 规范 §16.5.3.3](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/language-specification/structs#16533-equality-members)。
- ValueTuple 默认逐分量比较；这与本文固定的持久字段/身份规则不能无条件等同。
  [ValueTuple.Equals 文档](https://learn.microsoft.com/en-us/dotnet/api/system.valuetuple-2.equals?view=net-10.0)。
- 浮点 Equals 的 NaN/零行为与原始位比较不同，查找 Hash 需配套。
  [Double.Equals 文档](https://learn.microsoft.com/en-us/dotnet/api/system.double.equals?view=net-10.0)。
