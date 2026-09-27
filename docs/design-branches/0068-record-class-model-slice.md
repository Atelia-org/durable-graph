# DB-068：record class 领域模型与统一引用资格

> 状态：**Implemented / G0–G3 已验收**。
> 已采纳修订：以 `IDurableObject` 完全替代并移除 `DurableBase`；不保留兼容壳。
> 日期：2026-09-12；调查基线：`8d89a35`（DB-067）。
> 来源：[DramaBoard 真实模型接入反馈 003](../../../drama-board/docs/feedback/durablegraph/003-real-model-integration.md)，消费者固定包来自 `f68388f`。
> 当前能力以 [PROJECT-STATE](../../src/PROJECT-STATE.md) 和源码为准；实施与执行证据见 §10。

## 1. 问题、证据与最小交付

让消费者保留 C# 普通/positional `record class` 的构造、`with`、值相等和继承，
通过现有 Schema、版本化 DTO 与对象图管线保存恢复，减少手写领域样板代码。

最小成功标准：真实 PackageReference 应用使用一个普通 class World 和一条 positional record 事实继承链，
分别覆盖 E1 后退出→Resume 完成 S1，以及 S1 后重开 Pending 为空，并独立读取事件；值相等但引用不同的两个 record 不被合并，
共享引用仍保持共享；无需手写等价 DTO、Equals 或复制函数来绕过库的声明限制。
泛型、跨程序集继承与历史读取必须进入验收，不能只交付能生成简单 record 的语法补丁。

### 反馈裁决

| 反馈 | 本轮判断与处理 |
|---|---|
| 保存 API 已满足完整世界和独立事件 | 接受为下游报告的实际集成结果；本轮核对了 adapter、测试代码和固定包来源，没有重新执行下游测试或校验包产物 |
| record class 改普通 class 产生大量手工语义 | 采纳为下一片需求；直接支持 record class，并附可运行使用范式 |
| readonly List 不代表内容不可变 | 成立；范式明确防御复制和只读外观，框架不自动深复制事件闭包 |
| 单次 Debug 的耗时和分配 | 保留观察，不足以选择缓存、可变领域模型或新存储子系统；后续由较长轨迹和可重复测量触发 |
| local object records 全是 Base | 符合当前纯 fold 替换实例的身份模型。新实例产生新 ID/Base，未变实例可沿用旧 head，map Remove/Delta 仍为增量；不据此引入跨实例内容配对 |

已核对的需求出处：

- [FirstBoardDomain](../../../drama-board/src/FirstBoard/FirstBoardDomain.cs)：`BoardActor.With*`、
  `FirstBoardGameState.With*`、`FirstBoardWorld.With`；`BoardEventPayload` 和 `FirstBoardFact` 手写多态相等。
- [GraphSpatialFact](../../../drama-board/src/Spatial/Facts/GraphSpatialFact.cs)：抽象事实基类和各派生事实手写字段、构造及相等/hash。
- [消费 adapter](../../../drama-board/src/FirstBoard/Persistence/FirstBoardOccurrenceHistory.cs)、
  [行为测试](../../../drama-board/tests/FirstBoard.Persistence.Tests/FirstBoardPersistenceTests.cs)、
  [冷进程测试](../../../drama-board/tests/FirstBoard.Persistence.Tests/ColdProcessTests.cs)、
  [固定包来源](../../../drama-board/docs/worksets/durablegraph-package-source.md)。本轮源码枚举得到 64 个 `.dgschema` 文件。

注意：下游某些手写相等已包含集合内容比较；恢复 C# record 语法并不能自动替代这种额外业务语义。

## 2. 结构阻碍与候选选择

`DurableBase` 当前是空 abstract class。C# record class 只能继承 record class 或 object，
不能继承这个普通 class。因此仅删除 SG 的 `IsRecord` 拒绝条件不可行。
语言依据：[C# record reference](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/builtin-types/record)。

| 方案 | 收益与成本 | 推荐 |
|---|---|---|
| 共同 `IDurableObject` 标记；移除 `DurableBase` | class/record 共用泛型和非泛型入口；统一资格与祖先终点，不保留冗余基类 | **用户已采纳此修订** |
| 共同 marker，但保留 `DurableBase` 便利壳 | 可少改现有声明；没有独占能力，却长期保留两种入口认知和一个特殊祖先终点 | 原提案；按用户反馈取代，不保留兼容壳 |
| 再加 `DurableRecordBase` | record 仍受人为祖先约束；两种根最终仍需要共同接口或 object | 不增加第二个框架基类 |
| 全部改 `where T : class` / object | 已有 object 内核可用，最终登记仍可校验；但公共入口失去领域对象意图，误传只能更晚发现 | 不作为公开外观；内核继续用 object |
| 仅文档/不可变 class 范式 | 能指导快照所有权，无法消除 `with`、positional 和继承 equality 的改写 | 作为交付附件，不替代功能 |
| 独立发布前置 marker 重构，再单独做 record | 有利于内部拆包，但第一批本身没有消费者收益 | 同一分片分门实施，不额外创造无功能的发布阶段 |

共同 marker 不是序列化能力本身：生成声明仍需 DurableType/字段分类；执行仍需显式绑定、
完整 Schema 和 exact CLR 检查。手工绑定沿现有显式登记合同，不变成程序集扫描或自动 POCO 序列化。

移除空基类不会显著增加本片的机制复杂度：原方案已要求迁移 Runtime/StateStore 的约束、擦除入口和 SG 准入；
额外工作主要是现有模型声明、测试中的源码字符串、示例与活动文档的成批迁移。
收益是只剩一种资格合同，SG 不再保留框架基类终点特例。删除类型后，普通源码中的漏改能由编译器发现；
反射名称、SG metadata-name 字符串和动态编译测试仍须搜索和实际执行，不能仅凭根 build 宣称改全。

## 3. 建议的领域/API 形状

以下为本片的领域/API 形状：

```csharp
public interface IDurableObject { }

[DurableType("Fact", 1)]
public abstract partial record Fact(
    [field: DurableField(1)] string Actor) : IDurableObject;

[DurableType("Damage", 1)]
public sealed partial record Damage(
    string Actor,
    [field: DurableField(1)] int Amount) : Fact(Actor);

[DurableType("World", 1)]
public partial class World : IDurableObject {
    [DurableField(1)] public Fact? LastFact;
}
```

`Damage.Actor` 复用基类属性，没有自己的持久字段；FieldId 按声明 Schema 分段，
`Fact` 和 `Damage` 各自的 FieldId=1 合法。属性实际声明在哪一层，就在哪一层保存一次。
`Damage` 的 Actor 参数不重复标 `[field: DurableField]`，不能把没有实际字段的参数误收为数据。

- `IDurableObject` 是用户定义引用对象的资格标记，无成员，不进 DTO/Schema；不覆盖内建 string/数组/容器。
- 删除 `DurableBase` 类型，不保留 obsolete 壳、类型别名或替代框架基类。声明根直接继承 object 并实现 marker；
  用户可构建自己的已标记领域基类体系，派生类型从领域祖先继承 marker，不要求逐层重复声明接口。
  普通 class 的字段发现政策保持，不随本片开放一般自动属性序列化。
- 顶层 public/internal partial record class，支持 positional/body、abstract/sealed、开放泛型；
  跨程序集依赖沿 DB-059–061 的 public 导出/显式登记规则。派生类沿 C# 规则只能继承 record。
- 任何真正的领域中间祖先仍须有 DurableType 和完整 history；引用类型祖先链最终止于 object，object 不进祖先 Schema。
  不能借 marker 跳过一个有未知状态的未标记基类；任意第三方基类的保存恢复不在本片目标内。
- 字段/元素复用现有槽闭包。record 是 ReferenceObject，占 ObjectId；不是 inline record struct。
  不开放 object/接口通配持久字段、boxed struct、未登记派生类型或 string/容器产品根。
- 所有强类型领域引用 API 统一 `where T : class, IDurableObject`。
  `PendingEvent`、`CommitDomainEvent` 的擦除边界及非泛型 `ReadPair` 两个返回值使用 `IDurableObject`。
  读入口可请求这个共同接口，实际 root 必须仍是已登记的具体 StateModelBinding；
  Create/Resume 的 State exact 类型和根替换规则保持。
- 实现接口的 struct 不得因装箱混入图。Runtime 的 ReferenceObject 定义必须检查实际 CLR class、marker 和既有 kind/arity；
  不把接口本身登记为对象模型。marker 不表达任意接口槽的 nominal Schema。
- 现有直接 `: DurableBase` 模型改为 `: IDurableObject`，已有领域继承链保留；泛型约束从
  `where T : DurableBase` 改为 `where T : class, IDurableObject`，不能遗漏引用类型约束。
  PendingEvent/ReadPair 等显式变量类型、委托及手工绑定同步修改并重编译。
  这是有意的源码/二进制 API 破坏，不提供旧 API 兼容，也不新增两套发布/恢复外观。
  仓库内活动模型、测试、消费者、生成模板和包内 XML 必须迁移；下游升级包时按迁移说明调整自己的代码。

## 4. 生成与恢复规则

复用 [DB-056](0056-record-struct-state-slice.md) 的实际字段分类与 Family 路径，
复用 [DB-061](0061-cross-assembly-inheritance-slice.md) 的声明层 Capture/Hydrate 与 base projection。

1. SG 的声明形状分开 `record class`、`record struct`、普通 class/struct，不以 IsRecord 单独决定生成壳。
2. 显式字段沿原分类；positional/自动属性/C# 14 field-backed 属性通过 `[field: DurableField]` 或 `[field: Transient]`
   分类真实 backing storage。无存储的计算属性和 EqualityContract 不入 Schema。
3. 继承 positional 参数、属性隐藏/覆盖、属性替换等只按 Roslyn 的实际声明字段建模。
   对没有命中实际字段的持久属性标记、未分类 backing、未知隐式存储、字段式事件和 helper 冲突明确诊断。
   真有基/派生两份独立存储则各自分类，不能凭同名合并。
4. backing accessor 使用 MetadataName，但**引用类型 receiver 是 `value`，值类型才是 `ref value`**。
   当前 GenericProjection 的 backing 读取无条件 `ref Unsafe.AsRef(in value)`，必须修正这条 struct 专用假设。
5. 冷恢复仍 RuntimeHelpers 分配未初始化实例，然后按 base→derived 填充实际字段和连接引用。
   不执行主构造器、copy constructor、初始化表达式、属性 setter/init 或任何业务校验方法。
6. SG 只增加静态/类型级支持，不给 record 添加参与 C# equality 的隐藏实例状态；
   不生成自己的业务 Equals/GetHashCode/clone/with 替代品。
7. current 形状选择 Family，历史 DTO/reader/Upgrade 不依赖旧 record CLR 壳。
   ReferenceObject 的精确父链、inline 传播、相邻升级及 reader 保留合同保持。

## 5. 身份、相等与快照所有权

以下三件事分别验收：

- **领域相等**：遵循用户/C# 当前 Equals。编译器合成相等可能包括 Transient 存储，
  对数组/List 不会自动变成内容相等；不把业务相等当持久状态相等。
- **引用身份**：Capture/恢复的实例字典继续 ReferenceEqualityComparer。两个 `new Damage("A", 3)`
  即使相等也获得不同 ID；同一个实例被多处引用只捕获一次。`with` 创建的新实例不能继承原 ObjectId。
  只读 ReadPair 的共享仍受既有 head、完整状态 proof 和引用闭包约束，不因 IsRecord 就认定安全。
- **冻结状态**：DTO 仍按全部持久字段/引用 ID 比较和生成 Delta，忽略 Transient；业务 Equals 为 true 也可能有持久变化。
  业务 Equals 抛错或循环，不应被普通对象 Capture、Delta 或图身份查重调用。

`with` 通常做浅复制；用户自定义 copy constructor 仍是用户代码。readonly/init 和只读集合接口不保证深不可变。
范式应同时展示一个纯值/字符串 positional fact（保留自动相等），以及含 private List 的事件快照
（输入复制、对外只读视图、必要时自行提供集合内容相等）。不宣称所有手写业务相等都能删掉。
record 也可以有 mutable 字段；支持声明不等于提供冻结 hot PendingEvent 的能力。
本片不扩张 Dictionary 的非 string 引用 key 支持范围。

## 6. Schema、格式与源码迁移

record/class 外观、marker、backing field 名、C# equality/copy 方法均不是持久布局。
相同定义 ID、版本、祖先、FieldId 和槽应生成相同 Schema/history；不新增 TypeTag、SchemaKind、
RepresentationId 类别、wire 版本或 record 专用 codec。

删除旧空基类本身不改变 Schema：它原本就没有祖先 Schema 或持久字段。
先验证普通 class 从 `DurableBase` 改为 `IDurableObject` 后持久布局/history 不变，再验证 record 外观转换。
源码清理覆盖活动 `src/tests/experiments` 的编译输入（包括动态源码、脚本内嵌样例、metadata-name 字符串），
根 README/PACKAGE/XML 和当前术语/能力文档；已归档设计和明确的旧包输入见证保留历史含义，不机械改写全部旧文档。
旧版产物仅作为隔离的读取/迁移见证输入，不要求它们使用新包重新编译而保留旧基类。

需要真实见证：V1 普通 class 的手写字段 → 同版 record 的对应 backing 字段，在整个继承链合法转换且
持久布局相同的情况下，读旧数据并继续保存，不虚构业务 Upgrade。涉及继承的模型库和宿主一起重编译，
不要求旧 CLR 普通 class 基库与新 record 派生库二进制混用。
源码 DTO helper 可能由普通生成路径切到 Family，内部类型别名需随之迁移；这与持久格式不变不同。
再用真实字段变化的 V2 证明仍需显式升版、历史 reader 和 Upgrade；基类变化漏升派生必须拒绝。
如果当前导出合同确实缺少必要的执行信息，实施前先给出最小失败例再修订此提案，不能静默增加格式。

## 7. 施工门与可委派范围

| 门 | 工作 | 最小出口 |
|---|---|---|
| G0 共同资格 | 新建 marker、删除 DurableBase；统一 object 祖先终点，迁移 Runtime/StateStore、SG 普通 class 准入及现有声明/测试输入 | 迁移后的普通模型回归通过；新包不再导出 DurableBase；class+marker 可显式绑定；boxed/interface/错误根/未登记模型拒绝，exact/发布/故障检查保留 |
| G1 SG 声明与投影 | record class 接纳、actual backing 分类、receiver 修正、同库继承/泛型，外部 nominal/export 准入 | positional record 真正 Capture/Hydrate；构造器/getter/init 零调用；继承字段一次且仅一次；DB-056 struct 回归保持 |
| G2 图与历史 | shared/distinct identity、完整状态比较、容器组合、两代升级与跨程序集 record 基类 | 真实冷恢复、记录等值不合并、历史 Schema 不变量、explicit Upgrade/漏升诊断可观察 |
| G3 消费者交付 | 新独立 PackageReference record 示例、旧恢复 helper/README/XML 的共同类型迁移 | E/S 热冷、Pending、只登记事实读取、with 后保存、两代包/history Publish/Verify 均执行；更新当前能力及剩余路线 |

G0→G1 依赖顺序由主代理控制；删除基类与 SG 普通 class 准入必须协同，不能期待尚未迁移的生成器先通过 G0。
签名冻结后，可分别委派 Runtime/外观、SG、历史与跨库测试、真实包示例；
同一文件有唯一编辑者。独立审阅重点为 marker 伪造/放宽准入、引用身份、隐式存储丢失与旧 history。
Windows .NET 构建和测试由主代理串行执行；不能让包测试/生成测试并行争用 DLL。

关键源码入口：

- [IDurableObject](../../src/DurableGraph/IDurableObject.cs)、[StateModelBinding](../../src/DurableGraph/Runtime/Binding/StateModelBinding.cs)、
  [StateDefinitionBinding](../../src/DurableGraph/Runtime/Binding/StateDefinitionBinding.cs)、[StateBaseProjection](../../src/DurableGraph/Runtime/Binding/StateBaseProjection.cs)。
- [CaptureContext](../../src/DurableGraph/Runtime/Capture/CaptureContext.cs)、[ObjectReadTable](../../src/DurableGraph/Runtime/Capture/ObjectReadTable.cs)、
  [BuiltinStateValues](../../src/DurableGraph/Runtime/State/BuiltinStateValues.cs)、[StateModelSnapshot](../../src/DurableGraph.Persistence/StateModelSnapshot.cs)。
- [Ancestry](../../src/DurableGraph.Generator/DurableSchemaGenerator.Ancestry.cs)、[CrossAssembly](../../src/DurableGraph.Generator/DurableSchemaGenerator.CrossAssembly.cs)、
  [Records](../../src/DurableGraph.Generator/DurableSchemaGenerator.Records.cs)、[GenericProjection](../../src/DurableGraph.Generator/DurableSchemaGenerator.GenericProjection.cs)、
  [GeneratedState](../../src/DurableGraph.Generator/DurableSchemaGenerator.GeneratedState.cs)。祖先终点须统一，不能只改诊断入口。
- [EventHistoryRepository](../../src/DurableGraph.Persistence/Repository.cs)、[EventHistorySession](../../src/DurableGraph.Persistence/BranchCheckout.cs)、
  [GraphReader](../../src/DurableGraph.Persistence/GraphReader.cs)、[WorldWorkspace](../../src/DurableGraph.Persistence/WorldWorkspace.cs)。
- [RecordStructGeneratorTests](../../tests/DurableGraph.Tests/RecordStructGeneratorTests.cs)、
  [RecordStateProjectionTests](../../tests/DurableGraph.Tests/RecordStateProjectionTests.cs)、
  [PackageConsumerProbe](../../experiments/PackageConsumerProbe/README.md)。旧 record-class 拒绝测试应按新的合法/非法边界改写，不能整组删除。

## 8. 验收矩阵与范围封顶

除各门出口外，必须覆盖：

- 非 positional/positional、abstract/sealed、generic record；普通 class 与 record 相互引用；
  现有 Nullable/inline/generic 槽和数组/List/Dictionary value 中至少各有组合见证。
- 基类复用 positional property、派生独有字段、private/init/readonly backing、C# 14 field-backed；
  错目标 attribute、未分类实际 storage、未标记中间基类、未知 concrete subtype 和 counterfeit marker/attribute 拒绝。
- 同库及真实跨库 generic base；基类和派生各自 history/export/注册，base 改动漏升派生诊断。
- 自环/互环不触发业务 Equals；两个等值不同 record 的身份与共享引用均正确；同实例 mutable record 的持久字段变化产生真实 Delta，
  而 `with` 替换产生新实例身份，不以业务 key 自动接续 ID。
- 用户 Equals 忽略某持久字段、或 Transient 导致业务不等时，DTO 比较仍只按完整持久状态；
  ReadPair 的 proof/闭包和冷 Resume 的可变图隔离保留。
- 真包 S0/E1/S1 与独立进程重开；Pending 完成后第二次恢复不重放；event-only 登记不依赖 State 根模型；
  公共非泛型返回可 pattern match record 与普通 class。
- 同布局 class→record 的历史复用，以及 V1→V2 字段升级/强制 Base/后续 Delta；Publish/Verify，已有普通模型包消费者保持可重编译运行。
- 删除旧基类后的普通 class 自建继承链、直接 marker 根和继承取得 marker 的派生均通过；
  泛型约束保留 class，新包不含 DurableBase，活动可执行输入不依赖旧名。
  根 build、动态生成测试和真包构建共同发现漏改；历史说明与隔离旧版输入不计为残留依赖。

产品变更后运行 `dotnet build DurableGraph.slnx`、Runtime/Generator、StateStore、相关 Build/history tests 与真实包消费者；
按变更影响选择 Storage 回归，不把设计时只读调查写成已通过产品验收。
文档的签名/样例必须以实际消费包验证，不能靠 ProjectReference 代替 NuGet 边界。

不做：自动深复制/深不可变集合、业务 comparer 合成或历史保存、引用 record Dictionary key 扩张、
跨实例业务 ID 合并、持久 hash、ValueTuple、通配 object/interface 字段、任意 POCO、普通 class 一般属性支持、
程序集重排、新缓存/新 diff/新发布器、任意外部基类适配。下游可继续固定旧包；升级新包需迁移接口，
但不要求同时把普通 class 改成 record。

## 9. 设计审阅状态

本轮分别核对了下游源码/包钉扎和上游生成/运行边界，并由独立设计审阅比较 marker、双基类与 object 外观。
共同意见：用 marker 消除 CLR class 专属假设合理；仍须完成整条生成、继承和包消费验证。
随后用户确认主动移除旧基类，利用原型试用期统一 API；修订迁移范围、祖先终点和验收后授权实施。
下游单次性能样本没有升级为性能结论；本片执行结果集中记录于下节。

## 10. 实施与验收记录

基线 `4dcb926`，根构建 0 警告/错误。此次 Runtime/StateStore 统一接口，删除旧基类，
SG 在 source/metadata/nominal 参数入口核对真实 attribute/marker symbol；祖先终点统一为 object。
没有修改 Storage/Schema wire 编码或增加 history 版本。活动模型、动态测试源码及包示例一并迁移。

| 门 | 代码与验证入口 | 状态 |
|---|---|---|
| G0 接口与准入 | [IDurableObject](../../src/DurableGraph/IDurableObject.cs)、[Runtime 准入回归](../../tests/DurableGraph.Persistence.Tests/DurableObjectAdmissionTests.cs)、[SG 合同回归](../../tests/DurableGraph.Tests/DurableObjectContractGeneratorTests.cs) | 已实现；StateStore 721 项通过，新包实际不导出 DurableBase |
| G1 声明与投影 | [record 声明矩阵](../../tests/DurableGraph.Tests/RecordClassGeneratorTests.cs)、[图/投影回归](../../tests/DurableGraph.Tests/RecordClassGraphTests.cs) | 已实现；构造/accessor 绕过、继承存储、身份与业务比较隔离已验证 |
| G2 完整历史 | [history 回归](../../tests/DurableGraph.Tests/RecordClassHistoryTests.cs)、[真包跨库三代见证](../../experiments/PackageConsumerProbe/RecordClassConsumer/README.md) | 已通过同版转换、基类升版约束、两/三参隐式 Upgrade 与显式泛型 Upgrade |
| G3 包交付 | [RecordClass runner](../../experiments/PackageConsumerProbe/Run-RecordClassProbe.ps1)、[真实旧包迁移](../../experiments/PackageConsumerProbe/Run-DurableBaseMigrationProbe.ps1) | 三代新包、旧包迁移、既有 EventHistory/恢复消费者、README 原文执行均通过 |

实现中补齐了 `GenericUpgrades` 的另一处生成壳：隐式 owner Upgrade 适配器对 record 也须发出
`partial record class`。既有方法名仍为 `UpgradeStateV1ToV2`，没有新增第二套隐式命名。
测试调整包括旧准入诊断提前为 DG0001、普通 class history fixture 显式开启 Family、
以及为 Delta 见证保留足够不变内容，使实际 Delta 小于 Base；没有通过改变策略绕过尺寸比较。

新真包运行目录：`experiments/PackageConsumerProbe/obj/record-class-20260911192824-71544-8d3bce14`。
九包版本 `0.0.0-record-class-e2e.20260911192824-71544-8d3bce14`；三代模型分别 pack 独立 ref/lib，
每代 Publish/Clean/Verify 并核对完整历史字节。E1 后恢复、S1 后无重放、event-only、非泛型 pair、
with 新身份及 v2 Base→Delta 冷读均通过。模型包的 SDK NU5131 与既有跨库见证相同，
实际 PackageReference 的 ref/lib 资产已验证；不承诺 packages.config。

最终源码回归：Runtime/Generator/Build-history 1,571 项、StateStore 721 项通过（共 2,292，零失败/跳过）。
TRX 分别为两个测试项目 `TestResults/db068-runtime-final.trx` 和 `TestResults/db068-statestore.trx`；
早期失败记录保留在忽略目录，用于追溯 fixture 修正，不混作最终结果。
产品 Storage/Serialization/Build 源码未修改；实际旧 Base、新 Delta、目录与发布的贯通由真包和 StateStore 回归验证。

复现入口（根目录串行执行；除首项自 pack 外，其余使用上述新 feed/version）：

```powershell
dotnet build DurableGraph.slnx -v:q
dotnet test tests/DurableGraph.Tests/DurableGraph.Tests.csproj --no-restore -v:q
dotnet test tests/DurableGraph.StateStore.Tests/DurableGraph.StateStore.Tests.csproj --no-restore -v:q
./experiments/PackageConsumerProbe/Run-RecordClassProbe.ps1
./experiments/PackageConsumerProbe/Run-DurableBaseMigrationProbe.ps1 -LegacyPackageSource <old-feed> -LegacyVersion <old-version> -PackageSource <feed> -Version <version>
./experiments/PackageConsumerProbe/Run-EventHistoryRecoveryProbe.ps1 -PackageSource <feed> -Version <version>
./experiments/PackageConsumerProbe/Run-ReadmeQuickStartProbe.ps1 -PackageSource <feed> -Version <version>
./experiments/PackageConsumerProbe/Run-EventHistoryProbe.ps1 -PackageSource <feed> -Version <version>
```

旧输入使用 DramaBoard 固定 `f68388f88ba09354e9fa90420dc2cf22b146b6cf` 包，
版本 `0.0.0-dramaboard.20260912.f68388f.1`；运行前逐一核对其来源 manifest 的九包 SHA256。
两代分进程、独立编译/缓存：旧 DurableBase 写 S0/E1，新 marker 读 Pending 并写 S1 Delta，
最后只读验证旧 S0/E1 和新 S1；唯一 World v1 history 完整字节保持。
该见证运行目录 `obj/durable-base-migration-20260911193549-58968-b742c640`（相对 PackageConsumerProbe）。
旧 Store 尚未实现 IDisposable，脚本只为旧输入发出普通局部变量，新输入仍按 using 释放缓存；未增加产品兼容壳。

既有消费者也从同一 fresh feed、各自隔离缓存运行：

- `obj/event-recovery-20260911193641-44912-aac0ce72`：包内/恢复后 XML、hot/cold、零次重放、事件快照和只读文件保持。
- `obj/readme-20260911193752-64784-127056c3`：直接提取根 README 项目/模型/程序/升级/浏览代码，V1 两次保存、V2 Upgrade、Clean/Verify。
- `obj/event-history-20260911193905-70440-06d6505e`：既有两代事件/状态模型、event-only、ReadPair、Pending、Base→Delta、根替换及只读共享。

日志集中在忽略目录 `artifacts/db068-validation/`。独立审阅未发现阻断，
接口资格、引用身份、实际字段归属、历史/格式和旧消费路径均已完成本片验收。
最终根构建 0 警告/错误；10 份受影响 Markdown 的 652 个本地链接、55 个锚点检查通过，
三个相关 PowerShell 脚本语法检查通过。旧执行输入中 128 个文件的差异严格等于符号/泛型约束迁移，
另外四个既有测试文件仅调整 marker 防伪与合法/非法 record 的诊断边界；新增语义测试另经独立审阅。
