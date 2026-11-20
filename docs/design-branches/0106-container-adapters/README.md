# DB-106 · 内建容器的持久化适配边界

**主线已采用方案 B 的第一步。** Runtime 的业务准备行说明本次比较是否要求写完整 Base，Persistence 直接消费这个结论。List 和 Dictionary 继续各自负责元素投影与编解码。新增容器何时值得抽取装配组件、何时需要公开家族登记，分别由下文的消费场景触发。[主线验收](#主线验收) · [容器保存与恢复](../0102-container-payload-planning/README.md) · [第三方节点协议](../0105-registered-nodes/mainline.md)

## 哪些部分已经共用

| 边界 | 今天的责任与依据 |
|---|---|
| 当前对象 | `ObjectBinding` 负责从 CLR 对象捕获冻结状态、归一化、访问出边、分配与回填。List 与 Dictionary 各自闭合元素或键值的泛型操作，逐元素仍静态调用。两者和声明节点已能互相嵌套，并保留共享与环。[对象边界](../../../src/DurableGraph/Runtime/Binding/ObjectBinding.cs) · [List 投影](../../../src/DurableGraph/Runtime/Containers/ListObjectBinding.cs) · [独立包读写样本](../../../experiments/PackageConsumerProbe/RegisteredNodes/README.md) |
| 历史对象 | `ObjectReaderBinding` 按准确源布局读冻结状态并访问旧引用。完整选中目录先检查坏边，随后才能升级、分配可达实例并回填。即使新模型已删除那条边，旧目录中不可达的坏行仍要拒绝。[准确历史读取](../../../src/DurableGraph.Persistence/StoredGraphBodies.cs) · [退役引用回归](../../../tests/DurableGraph.Persistence.Tests/RegisteredNodeHistoricalReferenceTests.cs) |
| 保存 | 类型准备自身正文。DG 掌握对象身份、物理前驱、图级预算、发布与安装。CaptureSession 串行，只允许一个未决候选，先校验整批再调用正文操作。公开 `Prepare` 交付每行 Base。独立 Event 自有身份空间和完整 Base，业务 State 可根据前驱延迟编码。[捕获与重试](../../../src/DurableGraph/Runtime/Capture/CaptureSession.cs) · [业务工作区](../../../src/DurableGraph.Persistence/BusinessWorkspace.cs) · [保存与安装](../../../src/DurableGraph.Persistence/BusinessPersistenceAdapter.cs) |
| 现有消费者 | 业务模型直接持有 List 和 Dictionary。DG 的 Schema 目录自身也用 List 表示描述符和槽。源生成器在编译期识别 BCL 类型，运行时由冻结的模型环境闭合绑定。因此内建类型需要支持无额外登记的冷开。[普通包的字典模型](../../../experiments/PackageConsumerProbe/DictionaryConsumer/Model.cs) · [引导模型](../../../src/DurableGraph.Persistence/BootstrapRecords.cs) · [生成器](../../../src/DurableGraph.Generator/DurableGraphGenerator.TemplateHistory.cs) |

## 候选与裁决

| 选择 | 收益 | 成本与判断 |
|---|---|---|
| A　保留写规划器的类型判断 | 实验基线 `e1a4e42d` 已支持注册叶和声明节点。 | DB-104 在规划器加入类型判断，DB-105 随注册基类调整又修改了它。 |
| B　收拢内部保存边界 | Runtime 解释业务对象的比较结果，Persistence 直接据此分类。 | **首片已合入。** 以后调整注册绑定的类型层次，写规划器仍可沿用同一判断。 |
| C　为每个内建家族设独立装配组件 | 有机会集中按 CLR 类型创建当前绑定、按持久布局创建历史 reader 的重复接线。 | **延后。** Array、List、Dictionary 已共享递归保护和依赖收集等操作。若一次真实变更暴露几个工厂重复修同一条规则，再抽该步骤。 |
| D　公开泛型家族 `PersistenceAdapter` | 独立包可以接入无法加 DG 属性的 CLR 类型，模型字段保留其原有类型和引用身份。 | **延后。** 公开协议需贯通生成器、类型表达式、Schema 与历史 reader 的归属、同名登记冲突及冷开。一次运行时注册只覆盖其中一段。 |

现有的 `BusinessPersistenceAdapter` 服务于整个业务工作区，为一次捕获取得 Schema 和物理前驱，规划写入并参与基线安装。候选 D 面向单个类型家族的作者，两者的调用方和寿命各异。[工作区适配器](../../../src/DurableGraph.Persistence/BusinessPersistenceAdapter.cs)

## 首片如何判定业务对象的变化

DB-104 在 `06c57dd4` 中让 `ObjectWritePlanner` 识别 `LeafObjectBinding`。DB-105 在 `ffa19b45` 又把同一行改成识别 `RegisteredObjectBinding`。新加入的声明节点始终返回比较结果，规划器却必须知道注册基类改了。这是两层共享类型知识已经产生的维护成本。[写规划器](../../../src/DurableGraph.Persistence/ObjectWritePlanner.cs) · [声明节点比较](../../../src/DurableGraph/Runtime/Binding/DeclaredNode.cs) · [注册叶的公开合同](../../../src/DurableGraph.Serialization/DurableLeafCodec.cs)

| 准备时实际发生什么 | 业务写规划收到的含义 |
|---|---|
| 没有前驱 | 新对象写 Base。 |
| 旧 string 的引用身份已验证，直接跳过比较 | 已知未变。预算仍可选中它写规范 Base，以缩短重建链。 |
| 同一个叶 codec 针对这对前后值返回 `null` | 这次必须写 Base。把它当作未变会丢编辑，现有回归用值 `99` 验证写入并重开。 |
| 比较返回 `PreparedDeltaBody` | 按 `HasChanges` 区分未变和有 Delta 候选。公开准备结果须保留原正文，未变的 DTO 也可能带一个字节的零掩码。 |
| 普通模型的必需比较回调错误地返回 `null` | 准备失败，允许修正后用同一候选重试。没有可交给规划器的行。 |

这些含义出自实际的[准备行生产入口](../../../src/DurableGraph/Runtime/Capture/CaptureSession.cs)、[叶保存回归](../../../tests/DurableGraph.Persistence.Tests/RegisteredLeafLifecycleTests.cs)和[公开准备回归](../../../tests/DurableGraph.Tests/CapturedGraphPreparationTests.cs)。已加载旧布局的强制重写另由持久层判定，优先于逻辑上的“未变”。例如升级后先保存独立 Event，下一次 State 仍须为已升级对象写当前版 Base。[Event 与升级重写回归](../../../tests/DurableGraph.Persistence.Tests/EventHistoryRepositoryTests.cs)

主线在 `PreparedCapturedObject` 上以内部计算属性 `ComparisonRequiresBase` 表达这一结论，复用该行已有的前驱、Delta 正文和对象种类。规划器先核对物理前驱，识别新对象，随后合并源布局的强制重写要求与本次比较的结论。[准备行](../../../src/DurableGraph/Runtime/Capture/PreparedCapturedGraph.cs) · [业务写规划器](../../../src/DurableGraph.Persistence/ObjectWritePlanner.cs)

另一种可行表示是在允许缺省 Delta 的比较调用处记录一位“本次返回了 null”。两种写法在今天能成功产生的每一行上等价。首选计算属性，因为它消除了已经观察到的跨程序集依赖，同时无需给每行增加字段。若将来第二种业务对象也能证明未变并跳过比较，或增加了赋予 null 另一含义的业务行生产入口，就改由生产点显式记录。另设一套三态枚举或给 string 制造假的 Delta 会重复 `Previous`、`HasChanges` 已表达的事实，也增加公开结果的转换。

**这个推论只适用于业务捕获。** 分支和书签的管理目录可凭“本次只添加条目”直接判定既有 `byte[]` 未变，并对旧字典根使用必定返回结果的 `PrepareDelta`。它直接生成 `PreparedObject`，绕过上述业务准备行。把判断提升成所有对象的规则会误判这里的数组，把底层比较接口普遍改为可空又会削弱字典调用方的静态保证。[管理目录写入](../../../src/DurableGraph.Persistence/ManagementDirectoryWriter.cs) · [必需比较合同](../../../src/DurableGraph/Runtime/Capture/StatePreparationBinding.cs)

## 主线验收

主线直接采用实验中的两个产品提交。`97d6189d` 将比较结论移至准备行，`c19a8d2a` 为其 getter 添加内联提示。实验中第一版的调用点多做了一次前驱检查。加上提示后，在 .NET 10.0.5 x64、关闭分层编译的八对进程中，分类耗时差的中位数为每行 −0.63 ns，基线和最终候选均为每行 64 B。计时范围是对已准备行进行分类。

主线按 Storage 源码 pin `3f780ad9` 重新构建根 Release，零警告、零错误。全量 3,455 项测试通过，零跳过。

主线产物与合入前的 `e1a4e42d` 各保存八代，再关闭仓库、只读打开每一代。对照覆盖同一 codec 在同一次保存中为一个叶写 Base、为另一个叶写 Delta，也覆盖换用内容相同的新字符串实例。两侧各从同一份关闭后的空仓副本开始，每侧产生 11 条 Base、3 条 Delta。含完整正文、准确前驱、对象目录和冷开值的回执逐字节相同。[叶生命周期回归](../../../tests/DurableGraph.Persistence.Tests/RegisteredLeafLifecycleTests.cs) · [写规划回归](../../../tests/DurableGraph.Persistence.Tests/ObjectRevisionPlannerTests.cs)

实验报告、原始样本和独立复现程序保存在主线历史的 `3d47103b`。在任意工作树中都可以用以下命令读取报告和复现说明。

```powershell
git show 3d47103b:docs/design-branches/0106-container-adapters/experiment.md
git show 3d47103b:experiments/BusinessSaveClassificationProbe/README.md
```

## 暂缓抽象的三处边界

**Base 计长。** 业务 State 中，已注册叶可以先报估计值。已有测试的叶估计 263 字节，实际正文只有 1 字节；选择 Delta 时，Base 编码次数为零。部分 List 按实际绑定的元素操作精确计长，自定义操作即使同为 Int32 也可能要立即编码。规划使用候选成本，安装的累计重建字节 H 取自实写记录。把比较、估长和编码合成一个统一回调会改变调用时机。若要单独改这一层，再按同一策略做配对运行，比较编码次数、实际记录及前驱、H、两代冷开、线程分配和耗时。[延迟 Base](../../../src/DurableGraph.Persistence/PreparedTypedBase.cs) · [实写记账](../../../src/DurableGraph.Persistence/NormalizedGraphState.cs) · [DB-102 验收](../0102-container-payload-planning/README.md)

**字典校验。** 两个不同 ID 分别指向 `"Gate"` 和 `"gate"`，即使引用、类型和持久键字节都合法，按 OrdinalIgnoreCase 查找仍会碰撞。不可达旧字典也要拒绝这种存档。反过来，不可达的 Application 字典要核对其全部引用，却不应触发应用的 comparer resolver；可达字典才在分配时解析，回填用 `TryAdd` 检查当前相等性。捕获、准确历史目录、归一化目录检查的是不同阶段的数据。保留字典专用规则，第二个家族也要查完整目录时再提取共同入口。[按记录策略检查键](../../../src/DurableGraph/Runtime/Containers/DictionaryStateReader.cs) · [不可达键碰撞](../../../tests/DurableGraph.Persistence.Tests/DictionaryRepositoryTests.cs) · [不可达应用字典](../../../tests/DurableGraph.Persistence.Tests/CompositeDictionaryRepositoryTests.cs)

**家族装配与 Queue。** 当前绑定按 CLR 类型闭合，历史 reader 按准确布局闭合，即使旧领域 CLR 类型已经删除也能读取；缓存命中仍需复核所选 Schema。List 保有有序区间补丁和读链中的数组复用，Dictionary 按 canonical key 操作、分别投影键和值。这些差异在现有类型内各有消费者。[List 工厂](../../../src/DurableGraph.Persistence/ModelExecutionEnvironment.Lists.cs) · [Dictionary 工厂](../../../src/DurableGraph.Persistence/ModelExecutionEnvironment.Dictionaries.cs) · [绑定缓存回归](../../../tests/DurableGraph.Persistence.Tests/GenericBindingCatalogTests.cs)

在 `durable-graph-mud@66e12a7`，桌面窗口的 `MainWindow._transcript` 用 `Queue<string>` 裁剪显示记录。入档的 `World.Log` 用上限 40 条的 List，按旧到新追加并截头。若将来业务模型的两个字段必须保有同一个现成的 `Queue<T>`，持久化两个独立包装就会破坏别名。届时可先为该队列做一次内部接入，让它与 List 并存在同一张图，出队、入队后再保存，分别冷开两代，核对 CLR 类型、别名与 FIFO。只有独立包还需要自行登记这类家族时，再以普通包的生成、升级和冷开来定公共 Adapter。
