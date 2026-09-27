# DurableGraph

[MIT License](LICENSE)

**带 Schema 版本历史的 C# 对象图增量序列化库。**

直接修改普通领域对象，然后 Commit；不需要代理、setter hook 或手工 MarkDirty。
DurableGraph 捕获独立的版本化状态 DTO，比较上次提交状态，以 Base/Delta 保存变化；
加载时按落盘的 exact Schema 读取、执行显式 Upgrade，再恢复共享引用和循环引用。

目前是快速演进的 **.NET 10 原型**，API 和格式尚未冻结。本文面向首次接入的应用开发者和 Coding Agent，
只介绍当前可用入口；详细能力边界见 [产品工作集](src/PROJECT-STATE.md)。

公开入口是非泛型 `Repository` / `BranchCheckout`：分别提交 Event 快照和 State，可从 Event 开始，也可连续提交同一角色。
Journal 的命名 branch ref 是唯一发布点；恢复读取已保存的结果，不重新执行历史业务处理器。

## 最短接入路径

下面的应用接入示例需要 .NET 10 SDK 和包含当前 DurableGraph 包 G 的完整本地 NuGet feed，准备方式见文末
[从源码打包](#从源码打包)。底层存储包 S 从 nuget.org 下载；这里不假定 DurableGraph 自身的包已公开发布。

创建一个独立控制台项目，例如 `QuickStart/QuickStart.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <DurableGraphGenerateDefinitions>true</DurableGraphGenerateDefinitions>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Atelia.DurableGraph" Version="$(DurableGraphPackageVersion)" />
    <PackageReference Include="Atelia.DurableGraph.Persistence" Version="$(DurableGraphPackageVersion)" />
  </ItemGroup>
</Project>
```

示例通过命令行传入 `DurableGraphPackageVersion`；正式接入时可将实际版本固定在项目属性或统一包版本文件中。
`Atelia.DurableGraph` 包同时携带 Runtime、Source Generator 和 history 构建集成；Persistence 包提供持久化工作副本外观。
本文说明文字遵循[项目术语表](docs/DurableGraph-glossary.md)，称 `BranchCheckout` 为“分支工作副本”，签出操作为 `Checkout`。
`Repository` 与 `BranchCheckout` 位于根命名空间 `Atelia.DurableGraph`，实现仍由 Persistence 包提供。
声明模型的每个项目都应**直接引用 Runtime 包**。无需手工添加 Analyzer、AdditionalFiles 或 Import，
也不要仅以 Runtime 项目的 ProjectReference 代替完整包接入。

### 1. 定义领域模型

保存为 `Models.cs`。Schema ID 在模型集合中稳定且唯一；此处短名称便于展示，真实应用可采用自己的前缀。

```csharp
using Atelia.DurableGraph;

namespace QuickStart;

[DurableType("Character", 1)]
public partial class Character : IDurableObject {
    [DurableField(1)] public string Name = "";
    [DurableField(2)] public int Hp;
    [DurableField(3)] public Character? Partner;
}

[DurableType("World", 1)]
public partial class World : IDurableObject {
    [DurableField(1)] public Character Hero = null!;
    [DurableField(2)] public List<Character> Characters = [];
    [Transient] private Dictionary<string, Character> _byName = new();

    public void RebuildTransient() {
        _byName = Characters.ToDictionary(character => character.Name, StringComparer.Ordinal);
    }

    public Character Find(string name) => _byName[name];
}

[DurableType("DamageEvent", 1)]
public partial class DamageEvent : IDurableObject {
    [DurableField(1)] public int Amount;
}
```

领域 class/struct 使用顶层 `partial` 声明。class 实现 `IDurableObject`（可从自己的领域基类继承该接口）；每个参与持久化的声明显式标记
`DurableType`。实例字段用 `DurableField` 或 `Transient` 明确分类；FieldId 是该声明内的稳定正整数，
基类和派生类可以分别有自己的字段 1。方法不参与序列化，普通 class 自动属性目前不等价于受支持字段。

### 2. 保存、重开、继续修改

保存为 `Program.cs`，替换控制台模板的内容：

```csharp
using Atelia.DurableGraph;
using Atelia.DurableGraph.Persistence;
using QuickStart;

if (args.Length != 1) {
    throw new ArgumentException("Provide one repository directory path.");
}

string path = Path.GetFullPath(args[0]);
var models = new StateModelRegistry();
Atelia.DurableGraph.Generated.DurableDefinitions.Register(models);

using var repository = Directory.Exists(path)
    ? Repository.OpenExisting(path, models)
    : Repository.CreateNew(path, models);
using var session = repository.ListBranches().Contains("main")
    ? repository.Checkout("main")
    : repository.CreateBranch("main", NewWorld()); // 已保存初始 S0。

World world = (World)session.State!;
world.RebuildTransient(); // Load 不调用构造器/字段初始化器，也不自动执行此方法。
if (!ReferenceEquals(world.Hero, world.Characters[0]) ||
    !ReferenceEquals(world.Hero.Partner!.Partner, world.Hero)) {
    throw new InvalidOperationException("Object identity was not preserved.");
}

var damage = new DamageEvent { Amount = 1 };
session.CommitEvent(damage);
world.Hero.Hp -= damage.Amount;
var frame = session.CommitState();
Console.WriteLine($"{world.Find("Alice").Name}: Hp={world.Hero.Hp}; Revision={frame.RevisionAddress}");

static World NewWorld() {
    var alice = new Character { Name = "Alice", Hp = 100 };
    var bob = new Character { Name = "Bob", Hp = 80 };
    alice.Partner = bob;
    bob.Partner = alice;
    return new World { Hero = alice, Characters = [alice, bob] };
}
```

在 PowerShell 中设置 `$feed` 为本地 feed 绝对路径、`$version` 为其中的匹配版本，然后运行：

```powershell
# 在包含 QuickStart/ 的目录运行；数据库路径保持相同，并放在源码版本控制之外。
$data = Join-Path (Get-Location) "quickstart-world"
dotnet restore QuickStart/QuickStart.csproj --source $feed -p:DurableGraphPackageVersion=$version
dotnet run --project QuickStart/QuickStart.csproj --no-restore -p:DurableGraphPackageVersion=$version -- $data
dotnet run --project QuickStart/QuickStart.csproj --no-restore -p:DurableGraphPackageVersion=$version -- $data
```

第一次输出 `Hp=99`，第二个进程重开后输出 `Hp=98`。后续修改继续使用同一个工作副本的 `State`；
成功提交 State 保留领域实例及比较基线。`Checkout` 保持分支精确 Head，只恢复最近的 State，不物化 Event 或自动重放。
Dispose **不自动保存，也不撤销领域修改**。`CommitState(nextState)` 允许替换为不同实际类型的根，成功后安装传入实例。
这段最短程序每次运行都创建一条新事件，未实现中断恢复；不要把重新运行当作透明重试。
需要恢复的应用须持久化自己的处理进度，见[事件快照与失败恢复](#事件快照与失败恢复)。

`CreateBranchFromEvent("main", firstEvent)` 可直接发布首个 Event 并返回 `State == null` 的工作副本；
继续 `CommitEvent` 不改变这一状态，直到 `CommitState(nonNullRoot)` 发布首个 State。
无 State 时，无参 `CommitState()` 会在捕获或追加前拒绝。已有 State 的读取失败会传播，不会转换成 null。

省略策略参数即可使用默认 `(5, 5)`：对象级读取放大动机阈值为 5，可选 Base 软预算为 5%。
无需自己估算尺寸、挑 Base/Delta 或调用 DTO 的二进制 body；有需要再按下文[调整保存策略](#调整保存策略)。

`CreateNew` / `OpenExisting` / `OpenReadOnlyExisting` 在打开时冻结 `models` 的配置，后续操作共用这一模型环境。
修改 registry builder 只影响之后打开的仓库；同一仓库不能逐操作切换模型目录。普通模型与 Family 按需闭合，成功结果可复用。
历史 Normalize/Upgrade 必须由固定输入与环境决定结果且不修改输入；Capture 必须提供 canonical current DTO。
exact current durable DTO 不调用历史 Normalize 委托。reader、引用遍历、比较器与 binding factory 的结果必须稳定，
不能让调用次数决定业务结果；Allocate/Hydrate 只创建和填充本次恢复图，业务初始化放在恢复交付之后。

### 3. 独立浏览与分支

关闭 writer 后，可以只加载某个 Event；不需要先构造 World，也不必注册完全不在该 Event 图中的模型。
每次操作仍需要所选图的完整 reader/Upgrade 能力。

```csharp
using var history = Repository.OpenReadOnlyExisting(path, models);
var events = history.ReadEvents("main");
foreach (var eventFrame in events) {
    var damage = (DamageEvent)history.ReadEvent(eventFrame);
    Console.WriteLine(damage.Amount);
}
var lastEvent = events.Last(); // 此示例已经保存过事件。
var before = history.GetPreviousState(lastEvent)
    ?? throw new InvalidOperationException("This example starts with a State.");
var pair = history.ReadPair(before, lastEvent);
```

`ReadFrames` / `ReadEvents` 按本次取得的 branch head 返回**从旧到新的完整逻辑链**，排除未连入该链的 orphan 和其他分支独有记录。
返回值已完整物化；`Reverse().Take(n)` 不会减少底层枚举量。枚举只取得 frame，不恢复领域图；ReadEvent 等操作才恢复对象。
严格打开仓库仍校验全历史及相关元数据，包括 orphan；不要把只读一个 Event 等同于跳过这些检查。
需要零写入浏览时使用示例中的 `OpenReadOnlyExisting`；可写打开下的枚举可能保存 Journal 派生缓存。

`ReadPair` 是实验性只读快照 API：按输入顺序返回 First/Second，两边成功后才交付。
默认返回两个 `IDurableObject`，保留各自实际类型；通过输入 frame 的 `Kind` 判断 State/Event，通过模式匹配使用具体领域类型。
两个输入无需相邻，也不要求一份 State、一份 Event；调用方用显式转换或模式匹配取得领域类型。
它在本次操作内复用相同 ObjectVersion 的解码结果，并可共享完整引用闭包都一致的领域实例。
**两份结果及其可达对象都必须按只读快照使用，包括会影响观察结果的 Transient 写入**。
不要分别给可能共享的 Actor 写入不同的 `OwnerWorld`、查询上下文或视图专属缓存；这些信息应由各自的图外
`WorldView` / 索引持有。即使两个视图引用同一个 Actor，各自的查询仍使用各自的世界上下文。
不能只用全局 Actor→context 表代替视图：共享的 Actor 会命中同一个 key。
不要依赖跨图 `ReferenceEquals` 判断业务身份或版本，也不要假定两图可隔离编辑。
需要原位初始化每份历史图的 Transient 时，分别调用 `ReadState` / `ReadEvent` 即可，不需要开启 writer；
持久成员仍按历史快照使用，这些读取不安装保存基线。需要继续修改并提交时才使用 `Checkout`。
可执行的[图外视图示例](experiments/PackageConsumerProbe/EventHistoryConsumer/README.md#per-view-transient-context)
展示了两份世界各建索引、共享候选 Actor 不携带视图上下文的用法。
共享候选判定使用持久状态比较，不准备对象 Base/Delta payload；常规读取仍可能为 Dictionary key 唯一性校验进行规范编码。
Checkout 只恢复最近 State；独立读取的 Event 与工作副本不共享可变实例。热路径由用户保持 Event 内容只读；持久 DTO 冻结不会冻结原 CLR 对象。

可写仓库在**没有活动工作副本**时支持 `CreateBranch("fork", selectedFrame)` 和
`MoveBranch("main", expectedHead, targetFrame)`；随后用 `Checkout` 从目标分支签出工作副本。
handle 从 `GetHead`、`ReadFrames` 或提交结果取得，只能用于签发它的这一次打开实例；不要跨库或跨重开复用。
位置统一使用 `CheckpointAddress`；它只用于本次打开，不是可序列化的外部地址。
历史可为 `S0 → E1 → E2 → S1 → S2` 或 `E0 → E1 → S0`。
Journal Parent 始终是前一 Head；图 Revision Parent 是提交前最近 State，没有 State 时为 null。
Event 不成为 State 的增量比较基线。`GetPreviousState` 寻找严格祖先中的最近 State，纯 Event 前缀返回 null。

## 调整保存策略

`ReadAmplificationThreshold` 控制何时产生可选 Base 动机：小值倾向缩短冷恢复的对象链，大值倾向少写重复 Base、节省历史存储。
它不是读取放大的硬上限；获得动机后还要经过 `BaseBudgetPercent` 的软预算筛选。
新增、升级或 Base 不大于 Delta 估算值等必要 Base 不受这项可选预算限制；预算也不限制事务大小或总写入字节。

以下为**稳定小 Delta 的长期模型估算**：单对象 Base 大小 `B` 大致不变，Delta 小而稳定，预算不推迟 Base，
并在周期内均匀取样冷重建。以持续写 Delta 的 payload 为 `1×`，令阈值为 `L`，
则长期写入 payload 约为 `1 + 1/(L−1)` 倍，平均重建 payload 约为 `(L+1)B/2`。

| 调整方向 | 参数 `(L, 预算%)` | 长期写入 payload / 全 Delta | 相对默认的写入变化 | 平均重建 payload | 相对默认的重建量变化 |
|---|---|---:|---:|---:|---:|
| 偏向冷读 | `(3, 5)` | `1.5×` | +20% | `2B` | −33.3% |
| 默认 | `(5, 5)` | `1.25×` | — | `3B` | — |
| 偏向存储 | `(11, 5)` | `1.1×` | −12% | `6B` | +100% |

若取 `L=10`，对应约 `1.111×` 写入与 `5.5B` 平均重建；上表用 `11` 表达额外写入约 10% 的典型选择。
这些比例不包含共享 frame、map、Journal 等开销，也不计有限历史与离散选择的偏差；
**重建 payload 减少 33.3% 不等于读取快 33.3%**，写入比例也不是整个仓库的磁盘空间保证。
当前正常存储为 append-only；调参影响后续写入，不回收既有历史或改变已保存旧版本的重建链。

在 CreateBranch、CreateBranchFromEvent、CommitEvent 或 CommitState 的 `parameters` 参数传入配置即可。
例如，对上面的工作副本，再完成一次偏向存储的事件/状态保存：

```csharp
var savePolicy = new ReadAmplificationBaseBudgetParameters(
    ReadAmplificationThreshold: 11,
    BaseBudgetPercent: 5);
var nextDamage = new DamageEvent { Amount = 1 };
session.CommitEvent(nextDamage, parameters: savePolicy);
world.Hero.Hp -= nextDamage.Amount;
session.CommitState(parameters: savePolicy);
```

需要偏向冷读时将 `11` 改为 `3`。**覆盖只对该次调用生效**，包括 CreateBranch；后续省略参数或传 `null`
都会重新使用库默认值，不继承上次覆盖。应用若要固定自己的策略，应共享一个参数值并在每次保存时显式传入。
当前默认可能随原型演进调整；它选择了模型中约 25% 的额外 Base 摊销开销，尚非实测最优值。
公式、选择依据和适用边界见 [DB-070](docs/design-branches/0070-read-amplification-default.md)，
也可打开[交互函数图](docs/research/read-amplification-tradeoff/index.html)比较。

## 事件快照与失败恢复

事件若需要记录角色“当时的 HP 和观察”，应保存所需的只读业务快照，通过业务 ActorId 找到当前角色进行修改。
可以从可变 `List<string>` 复制为快照私有的 `string[]`，共享不可变 string；readonly 字段本身不会冻结数组或可变元素。
领域采用不可变对象替换时，也可以让事件保留旧对象，不必额外维护一套快照类型。

[可运行的快照与恢复示例](experiments/PackageConsumerProbe/EventHistoryRecoveryConsumer/README.md) 展示
`E1.TargetSnapshot.Hp == 10`、`S1` 中角色 HP 为 7，热处理、冷 Checkout 和独立浏览都保持事件观察值。
恢复由应用持久化的事件标识和处理进度决定；库不提供 PendingEvent，也不赋予 E/S 邻接关系“已处理”的含义。

例如，为事件保存单调业务序号，并在 State 中保存最后成功应用的序号，处理结果与进度一起 CommitState。
失败后先结束当前尝试，关闭工作副本/Repository，再 OpenExisting、`Checkout` 并按应用协议恢复：

- 从所选历史读取业务事件，对照 State 中的处理进度，只应用尚未处理的事件，再保存结果与新进度。
- 若进度已包含目标请求，则不重复应用；State 可能已发布，只是调用方没收到成功返回。
- Event-first 且 State 为 null 时，按应用初始化协议处理，不能把 null 解释为读取失败后的默认值。
- 打开或恢复失败：报告并停止；不自动退回旧 head 或修复文件。

Head 是 Event 或 State 均不能独自证明某个请求是否完成；应用也须区分事件尚未发布与已发布未处理。
外部命令是否重新提交由应用的请求标识和去重策略决定。库不回滚内存修改，也不保证文件之外的业务副作用只执行一次。

## Schema 演化：保留 history，显式写转换

正常本地构建会把当前 Schema 模板发布到模型项目的 `DurableGraphSchemaHistory/*.dgschema`。
**将这些文件与模型源码一起提交 Git**；它们用于重新生成旧版 DTO/reader，不是可清理的 obj 缓存。
Repository 内的 `schemas.rbf` 则记录实际保存时的完整布局；两者职责不同。

首次接入建议显式设置 `DurableGraphGenerateDefinitions=true`，固定使用本文的 Family 登记外观。
生成的代码默认位于项目 `obj` 下，可搜索 `DurableGenericStates.g.cs` 查看 DTO 和登记入口；不要编辑生成文件。

以上 World 示例若增加天数字段：先将 `[DurableType("World", 1)]` 改为版本 2，
再添加 `[DurableField(3)] public int Day;`，并新建 `Upgrades.cs`：

```csharp
using Atelia.DurableGraph;
using WorldStates = Atelia.DurableGraph.Generated.Family_576F726C64;

namespace QuickStart;

public static class WorldUpgrades {
    [DurableUpgrade(typeof(World), 1)] // 相邻边：V1 -> V2。
    public static void Upgrade(in WorldStates.V1 prior, out WorldStates.V2 next,
        UpgradeContext context) {
        next = new(prior.Segment0Field1, prior.Segment0Field2, 1);
    }
}
```

这里 `576F726C64` 是 ID `World` 的 UTF-8 十六进制编码。V1 的两个字段都是引用 DTO 槽 `ObjectId`；
转换保留原引用，给新增 Day 赋业务默认值 1。重新构建并用原数据库路径运行即可加载升级。
升级读取本身不回写；下一次 Commit 为仍存活的升级对象写 Base。

升版规则：

- 同一个 Schema ID/版本的字段布局必须一致；不能修改已有 history 来伪装成从未变过。
- 基类或嵌套值类型布局变化，会要求受影响的 owner/派生类型升版；普通引用目标自身升版不传播到引用方。
- 升级转换的是完整对象 DTO；框架不自动额外执行每层基类的 Upgrade。
- Upgrade 只转换当前这个对象的字段，不读取其他对象或分配新 ObjectId。不能假设缺失转换会使用默认值补齐。
- 支持旧数据的程序需要保留相应模板、reader 和升级能力；详细的泛型/值/容器升级请参考下方真实示例。

CI 可设置 `ContinuousIntegrationBuild=true`，或显式验证而不发布 history：

```powershell
dotnet clean QuickStart/QuickStart.csproj -p:DurableGraphPackageVersion=$version
dotnet build QuickStart/QuickStart.csproj --no-restore -p:DurableGraphPackageVersion=$version -p:DurableGraphSchemaHistoryMode=Verify
```

初次 Publish 后再 Clean/Verify，可同时验证 history 确实能作为下一次编译的输入。
构建属性及诊断说明见 [Build 工具](src/DurableGraph.Build/README.md)。

## 当前能放进模型的内容

领域引用对象统一实现 `IDurableObject`；旧 `DurableBase` 已移除。升级旧模型时，将直接基类改成接口，
将泛型约束改为 `where T : class, IDurableObject`，并一起重编译模型库和宿主。
已有领域继承链保留，各祖先仍须参与 Durable Schema；删除原空基类本身不要求 Schema 升版。

record class 的 positional/自动属性使用 `[field: DurableField(id)]` 分类真实 backing field；
派生 positional 参数若复用基类属性，只在实际声明存储的基类标记。C# 的 `with` 和相等性保持原行为，
不自动深复制引用成员或按内容比较容器。框架按引用身份保存，两个等值但不同实例的 record 不会合并。
可运行的泛型 record 继承、事件恢复与两代升级见[真实包示例](experiments/PackageConsumerProbe/RecordClassConsumer/README.md)。

| 内容 | 当前边界 |
|---|---|
| 标量 | bool、byte/sbyte、short/ushort、int/uint、long/ulong、char、Half/float/double、Guid、decimal、TimeSpan、DateOnly、TimeOnly、DateTimeOffset |
| 领域类型 | 显式 Durable class、record class、struct、record struct、enum；支持泛型、继承、private/readonly 字段及跨程序集组合 |
| 值组合 | inline struct 嵌套布局、Nullable；record 自动属性的存储使用 `[field: DurableField(...)]` / `[field: Transient]` |
| 引用与数组 | string、已登记的实际派生实例、共享/循环；零下界 `T[]` 和 rank 2–4 多维数组、交错数组 |
| 容器 | exact BCL `List<T>` 和实验性 `Dictionary<TKey,TValue>`；按内容保存。复合 Key 与当前 comparer 的边界见[真实字典示例](experiments/PackageConsumerProbe/CompositeDictionaryConsumer/README.md) |
| 尚不支持 | ValueTuple、DateTime、任意外部基类、CLR 嵌套类型/ref struct、任意 object/interface 字段、boxed value 身份、数组协变、非零下界及非 SZ rank-1 数组、其他未适配 BCL 容器 |

支持的类型可以在字段、泛型参数和容器元素中组合。仍需遵守各类型限制；例如 Dictionary 根 Nullable Key 不支持，
容器子类/接口字段不自动当作 BCL 内容对象。string 保留非空实例的引用身份，空串统一为 `string.Empty`。

恢复不执行领域类/struct 的构造器、实例字段初始化器、属性 getter/setter；不要求无参构造器。
Transient 索引/缓存由应用在恢复交付完整图后重建：`Checkout` 或独立 `ReadState` / `ReadEvent` 的结果
可以做应用侧初始化；`ReadPair` 的视图专属状态必须放在图外，不能原位修改可能共享的节点。
Transient 只表示不参与持久化，并不表示修改没有可见影响。自定义字典 comparer 使用当前业务代码，
不得依赖尚未重建的 Transient 或尚未完成填充的引用目标内容。

## 多模型库与运行约束

每个模型库保有自己的 history，并以公开 facade 包住该库 internal 的 `Generated.DurableDefinitions.Register`。
宿主在 CreateNew/OpenExisting/OpenReadOnlyExisting 前登记所需模型库；不依赖程序集自动扫描，也不把别人的 `.dgschema` 复制到本库。
跨库接法见 [跨库继承示例](experiments/PackageConsumerProbe/InheritanceLibraryConsumer/README.md)。

当前外层 API 是**一个 Repository、一个活动 `BranchCheckout` 工作副本、单 writer**；每份已保存的 Event/State 图选一个非空 durable 根。
State/Event 无需共同的领域基类，均实现 `IDurableObject`；同一 CLR 类型也可承担任一角色。
[DB-078-A](docs/design-branches/0078-editable-checkpoint-fork-slice.md#11-078-a非泛型公共基础与自由历史) 已交付自由 E/S 历史和跨类型 State 替换。
[DB-083](docs/design-branches/0083-repository-checkpoint-api-user-stories.md) 的便利 Checkpoint、按需事件枚举、多分支同时签出、一步 Fork 与 tag 仍待后续分片。
同步 Commit 期间宿主须停止对领域图的并发修改；DTO 冻结不提供任意并发读写下的快照隔离。
没有自动坏尾修复或完整 OS crash/power-loss 保证。文件布局为 schemas.rbf、state/、journal/ 和 repository.lock。
旧仓库/工作副本 API 与 publication.rbf 发布器已移除；原型没有旧格式迁移路径。

保存失败时不要一律重试：`GraphCommitException.Outcome` 区分 NotPublished / Unknown / Published，
同时检查仓库与工作副本的 `IsFaulted`。Unknown/Published 不可透明重试；faulted 实例须 Dispose 后重开，
按持久 head 判断结果。错误与现有领域修改不会自动回滚。通常无需直接操作 SchemaStore、Storage 或发布日志。

## 从源码打包

当前包组织为 `Atelia.DurableGraph`、`.Persistence`、`.Storage` 和 `.Serialization`。
从旧 StateStore 系列升级时，同步修改 PackageReference/using 并重编译模型库与宿主。
领域属性、`IDurableObject`、`ObjectId`、登记接口和 Generated/Family 名保持；高级布局类型使用
`Atelia.DurableGraph.Schema`，绑定与状态操作使用 `Atelia.DurableGraph.Runtime`，两者仍在主程序集内。
仅组织迁移不需要提高业务 Schema 版本，保留 accepted history；不提供旧 DLL 的直接二进制兼容。

底层五库存储依赖来自独立的 [atelia-storage](https://github.com/Atelia-org/atelia-storage)，
默认使用 [StorageDependency.props](eng/StorageDependency.props) 固定的 NuGet 版本 S。
正常 `dotnet build/test` 直接从 nuget.org restore，不需要先运行 Prepare 或检出存储源码。
独立包 probes 的 Prepare 仅下载公开五个 nupkg 并返回 feed，不 clone 或重新打包存储库。
源码联调、唯一版本的本地开发包与显式 NuGet 配置见 [存储依赖指南](docs/storage-dependency.md)。

从 DurableGraph 根目录执行以下命令，生成含五个 S 包和四个 DG 包的完整 feed。
`$version` 只控制 DG 的版本 G，必须与 S 不同；每批 DG 产物使用新版本，避免缓存复用同版本旧代码：

```powershell
$ErrorActionPreference = "Stop"
$version = "0.0.0-local.$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss'))"
$feed = Join-Path (Get-Location) ".artifacts/nuget/$version"
. ./experiments/PackageConsumerProbe/PackageProbeSupport.ps1
Prepare-DurableGraphProbeFeed -RepositoryRoot (Get-Location).Path -OutputDirectory $feed -Version $version
```

将 `$feed` 和 `$version` 用于前面的 restore/run。也可直接运行带独立 feed、两代模型和历史校验的
[真实包实验](experiments/PackageConsumerProbe/README.md)，例如 `Run-InheritanceLibraryProbe.ps1`。
源码模式用于 build/test；`dotnet pack -p:UseStorageSources=true` 会被拒绝。联调实验包先由上游 Pack
生成新的存储版本，再用指南中的自定义 NuGet.Config 和 `StoragePackageVersion` 按包模式打 DG。
不能给五库重复套用 DG 的 G，也不能覆盖公开版本或通过清空缓存切换包内容。

## 接下来查什么

| 需求 | 入口 |
|---|---|
| 更详细的生成 API、泛型/组合值升级 | [Runtime 包说明](src/DurableGraph/PACKAGE.md) |
| 跨库引用、固定值、继承 | [模型库](experiments/PackageConsumerProbe/CrossAssemblyConsumer/README.md)、[inline 库](experiments/PackageConsumerProbe/InlineLibraryConsumer/README.md)、[继承库](experiments/PackageConsumerProbe/InheritanceLibraryConsumer/README.md) |
| 泛型/嵌套值/容器迁移 | [泛型三代](experiments/PackageConsumerProbe/GenericConsumer/README.md)、[值升级](experiments/PackageConsumerProbe/ValueUpgradeConsumer/README.md)、[数组](experiments/PackageConsumerProbe/ArrayConsumer/README.md)、[List](experiments/PackageConsumerProbe/ListConsumer/README.md)、[复合字典](experiments/PackageConsumerProbe/CompositeDictionaryConsumer/README.md) |
| 实际能力、术语、未实现事项 | [PROJECT-STATE](src/PROJECT-STATE.md)、[术语表](docs/DurableGraph-glossary.md)、[路线图](docs/DurableGraph-research-roadmap.md) |
| 修改 DurableGraph 本身 | 先读 [AGENTS.md](AGENTS.md) 与产品工作集；根 `dotnet build DurableGraph.slnx` / `dotnet test DurableGraph.slnx`，包边界变动另跑相关包实验 |

下游试用时，优先按自己的领域需求建立一个小的真实 World。记录必须改写的模型、重复登记/升级代码、
需要翻内部源码才能完成的步骤及难以理解的诊断；反馈附最小复现、包版本和保留的 history。
遇到缺失功能先记录，不要仅为让示例通过而改掉原来的建模需求。
