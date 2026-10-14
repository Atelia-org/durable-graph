# DurableGraph

[MIT License](LICENSE)

**带 Schema 版本历史的 C# 对象图增量序列化库。**

直接修改普通领域对象，然后 Commit；不需要代理、setter hook 或手工 MarkDirty。
DurableGraph 捕获独立的版本化状态 DTO，比较上次提交状态，以 Base/Delta 保存变化；
加载时按落盘的 exact Schema 读取、执行显式 Upgrade，再恢复共享引用和循环引用。

目前是快速演进的 **.NET 10 原型**，API 和格式尚未冻结。本文面向首次接入的应用开发者和 Coding Agent，
只介绍当前可用入口；详细能力边界见 [产品工作集](src/PROJECT-STATE.md)。

公开入口是非泛型 `Repository` / `BranchCheckout`：分别提交 Event 快照和 State，可从 Event 开始，也可连续提交同一角色。
VersionStore 共同发布共享 Schema 根与命名分支目录；恢复读取已保存的结果，不重新执行历史业务处理器。

## 最短接入路径

下面的应用接入示例需要 .NET 10 SDK 和包含当前 DurableGraph 包 G 的完整本地 NuGet feed，准备方式见文末
[从源码打包](#从源码打包)。当前底层存储包 S 是准确来源的本地候选；这里不假定存储候选或 DurableGraph 包已公开发布。

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
    ? Repository.OpenExisting(path, models, schemaWriteMode: SchemaWriteMode.Shared)
    : Repository.CreateNew(path, models, schemaWriteMode: SchemaWriteMode.Shared);
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
dotnet restore QuickStart/QuickStart.csproj --source $feed --source https://api.nuget.org/v3/index.json -p:DurableGraphPackageVersion=$version
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

`CreateNew` / `OpenExisting` / `OpenReadOnlyExisting` 在打开时冻结 `models` 的代码配置；修改 registry builder 只影响之后打开的仓库。
普通工作副本的默认保存还须显式选择 `schemaWriteMode: SchemaWriteMode.Shared`；RW Open 省略时可以读取、签出及发布 ref/tag，默认业务提交提前拒绝。下述 ToShared 入口另为单次提交显式选择归属。
`IsReadOnly == false` 只表示底座可写，不表示已选择 Schema 写入归属。选择仅属于本次打开，冷开后需再次明确选择。
读取按所选图的准确 Schema 视图闭合执行能力。checkout 固定创建／恢复时的执行环境：有 State 取 State 所 pin 的 Schema，普通 checkout 无 State 取创建时 shared；普通模型与 Family 按需闭合。State 提交可以推进持久 Schema 视图和保存基线，仍保留工作副本的执行环境；Event 不安装 State。V2 程序签出 V1 数据时已执行显式 Upgrade，得到 V2 内存对象，可以连续增量保存，无需每次提交后重新签出。
独立 `ReadState` / `ReadEvent` 按被读图自己的准确 Schema 视图恢复。Dispose 后重新 Checkout 取得新的领域实例；有 State 时仍选择最近 State 的视图，不追到最新 shared 或 Event 的目录，也不重载代码配置。

新增模型、reader、Upgrade 或策略应在 Open 前登记。运行中修改 registry 后，需要关闭全部工作副本和 Repository，再用新配置 Open、重新解析 head/tag 并签出。未提交修改先由应用明确保存当前配置支持的内容，或自行保留／重建；Dispose 不保存、不撤销修改，旧地址和 CLR 引用不会跟随新签出。

对于相同持久输入和 ObjectId 等明确调用输入，在同一代码配置、准确源／目标布局及显式依赖成立时，Normalize/Upgrade 和恢复的持久结果应稳定；不能因无关 Schema 是否登记、memo 命中或调用次数而改变业务值。准确目录仍用于布局解析和验证，缺失／冲突或缺少 reader 能力可以使操作拒绝。配置冻结保留委托／比较器引用，不复制它们捕获的外部状态；调用者负责行为稳定。
Capture 必须提供 canonical current DTO，同布局保存／恢复正确往返持久字段和引用关系；历史值变化由显式 Upgrade 定义。exact current durable DTO 不调用历史 Normalize 委托。reader、引用遍历、比较器与 binding factory 不修改输入 DTO；Allocate/Hydrate 只创建和填充本次恢复图，业务初始化和视图专属 Transient 放在完整恢复交付之后。上述行为合同不证明任意自定义 reader 都能读回：Commit 成功不等于已执行 reader body 的全部验证。

开发阶段先用 `CreateBranch(name, baselineCheckpoint)` 从固定用户快照建立分支，再用 `CheckoutPrivate(name)` 签出。在匹配历史 reader／Upgrade 的模型环境中，旧业务图恢复后保留存活对象身份、共享与循环，正常升版的布局保存为新 Base。私有保存只推进分支目录，不改 shared 根。两个操作分开：创建 ref 成功后，签出失败保留该名字及 baseline 指向；修正模型环境后可以重试。
私有冷签出有 State 取 State.S 执行环境，纯 Event 前缀取 Head.S；meta 写来源总取当前 Head.S，不借 latest shared 补缺。已有私有定义不可改写；同 key 内容不同的未定稿版本应在不同 baseline fork 试验，需要继承已接受草稿的数据再改型则递增版本。普通 Shared 保存要求整个所选目录已被 shared 同形接受，否则 Capture 前拒绝。
开发用 `DurableGraphSchemaHistoryMode=Off` 保已发布 history 输入而不冻结新候选；它不能忽略已有同版冲突。最终只交付一个版本时，从原 baseline 建立干净 final fork，执行定稿 Upgrade／必要重放，再接纳完整目录。具体流程和资格边界见 [DB-088](docs/design-branches/0088-development-schema-workflow.md)。

显式 `CommitStateToShared()`／`CommitStateToShared(nextState)`／`CommitEventToShared(domainEvent)` 可把当前 Head.S 的完整目录、待改定义和本次捕获布局合入最新 shared，包括业务不可达的声明。同形复用、新项加入、同 key 异形拒绝；新增 meta 与业务提交共同原子发布。该入口可用于没有 Open 写模式的可写仓库，只为本次提交选择 Shared，不改变工作副本的后续默认归属或执行环境。Event 仍不推进 State 基线；无 rewrite 且完整布局相容的旧对象可保留原 head，不因 descriptor 编号不同强制重写。具体范围与源码／包资格见 [DB-087](docs/design-branches/0087-meta-bootstrap-refactoring-route.md#private-plan)。 DB-087 的有限自举停点保其历史资格；DB-088 删除特例后的 Development、Split 与 PrivateContainer 已通过新 G 普通包故事，当前来源与限定验收见 [DB-088 §7](docs/design-branches/0088-development-schema-workflow.md#7-实施结果与限定验收)；旧包结果仍只解释各自历史版本。

### 3. 独立浏览与分支

关闭 writer 后，可以只加载某个 Event；不需要先构造 World，也不必注册完全不在该 Event 图中的模型。
每次操作仍需要所选图的完整 reader/Upgrade 能力。

```csharp
using var history = Repository.OpenReadOnlyExisting(path, models);
var end = history.GetHead("main"); // 固定本次查询的结尾。
foreach (var eventFrame in history.EnumerateEvents(end, HistoryOrder.OldestFirst)) {
    var damage = (DamageEvent)history.ReadEvent(eventFrame);
    Console.WriteLine(damage.Amount);
}
var lastEvent = history.EnumerateEvents(end).First(); // 默认从新到旧；此例已有事件。
var checkpoint = (EventCheckpoint)history.ReadCheckpoint(lastEvent);
var occurrence = (DamageEvent)checkpoint.Event;
var before = (World)checkpoint.PreviousState!; // 此例从 State 开始。
before.Hero.Hp = 100; // 仅修改这份独立恢复图，不修改 occurrence 或持久历史。
```

`EnumerateEvents(end)` 沿固定的逻辑祖先链从新到旧返回事件地址，允许提前停止，跨过 State 时不恢复其领域图。
后续分支推进或移动不改变所选结尾；循环内可以调用 `ReadEvent`。`HistoryOrder.OldestFirst` 先准备范围内的轻量地址，再从旧到新输出。
可选 `afterExclusive` 排除下界及其更早历史；同点得到空范围，非祖先下界在交付首项前拒绝。这个校验可能需要完整范围的元数据回溯。
`ReadFrames` 仍返回从旧到新的**完整元数据链**，用于检查所有 E/S 位置；它有全量成本。两种查询都排除其他分支独有记录和未连入所选链的 orphan。
打开只读取必要分支目录／shared 两根；所选业务图 pin 完整 Schema 视图，读取时检查内容与来源关系，不执行全历史或 orphan 启动审计。
需要零写入浏览时使用示例中的 `OpenReadOnlyExisting`。

`ReadCheckpoint` 一次恢复当前图与最近严格祖先中另一角色的图，最多两份，全部成功才返回。
`EventCheckpoint` 提供 `Event` / `PreviousState`；`StateCheckpoint` 提供 `State` / `PreviousEvent`，前驱另有对应地址。
不存在相应祖先时，前驱根和地址同时为 null，包括首 State 之前的 Event。
这些图彼此独立，也与其他读取和工作副本的可变对象隔离；重复 getter 保留同一实例及你的本地修改。
`PreviousEvent` 仅表示历史中的邻近事件，不能证明它已经处理或是本次 State 的唯一原因。消息处理进度由应用 State 表达。
这些历史恢复结果不安装保存基线，修改它们不会自动写盘。只需要事件时仍使用 `ReadEvent`，避免额外恢复前置 State。

`ReadPair` 是实验性只读快照 API：按输入顺序返回 First/Second，两边成功后才交付。
默认返回两个 `IDurableObject`，保留各自实际类型；通过输入 frame 的 `Kind` 判断 State/Event，通过模式匹配使用具体领域类型。
两个输入无需相邻，也不要求一份 State、一份 Event；调用方用显式转换或模式匹配取得领域类型。
它在本次操作内复用相同 ObjectVersion 的解码结果，并可共享完整引用闭包都一致的领域实例。
**两份结果及其可达对象都必须按只读快照使用，包括会影响观察结果的 Transient 写入**。
不要分别给可能共享的 Actor 写入不同的 `OwnerWorld`、查询上下文或视图专属缓存；这些信息应由各自的图外
`WorldView` / 索引持有。即使两个视图引用同一个 Actor，各自的查询仍使用各自的世界上下文。
不能只用全局 Actor→context 表代替视图：共享的 Actor 会命中同一个 key。
不要依赖跨图 `ReferenceEquals` 判断业务身份或版本，也不要假定两图可隔离编辑。
需要原位初始化每份历史图的 Transient 时，使用 `ReadCheckpoint` 或分别调用 `ReadState` / `ReadEvent`，不需要开启 writer；
持久成员仍按历史快照使用，这些读取不安装保存基线。需要继续修改并提交时才使用 `Checkout`。
可执行的[图外视图示例](experiments/PackageConsumerProbe/EventHistoryConsumer/README.md#per-view-transient-context)
展示了两份世界各建索引、共享候选 Actor 不携带视图上下文的用法。
共享候选判定使用持久状态比较，不准备对象 Base/Delta payload；常规读取仍可能为 Dictionary key 唯一性校验进行规范编码。
Checkout 只恢复最近 State；独立读取的 Event 与工作副本不共享可变实例。热路径由用户保持 Event 内容只读；持久 DTO 冻结不会冻结原 CLR 对象。

可写仓库支持 `Fork(name, selectedFrame)`：从已提交位置创建持久分支，并返回独立、可继续保存的工作副本。
源工作副本可以保持打开；它的未提交字段不进入子分支。关闭上面的浏览仓库后，下面的代码接续示例数据库；
它的 `main` Head 是已保存的 State，分支名 `left` / `right` 须尚未存在，`models` 沿用打开前注册的固定配置：

```csharp
using (var branches = Repository.OpenExisting(path, models, schemaWriteMode: SchemaWriteMode.Shared)) {
    using var source = branches.Checkout("main");
    var selected = source.Head;
    using var left = branches.Fork("left", selected);
    using var right = branches.Fork("right", selected);
    var leftWorld = (World)left.State!;
    var rightWorld = (World)right.State!;
    leftWorld.Hero.Hp -= 1;
    rightWorld.Hero.Hp -= 2;
    left.CommitState();
    right.CommitState(); // 每个分支独立保存；调用仍按顺序执行。
}
using (var reopened = Repository.OpenReadOnlyExisting(path, models)) {
    var original = (World)reopened.ReadState(reopened.GetHead("main"));
    var leftSaved = (World)reopened.ReadState(reopened.GetHead("left"));
    var rightSaved = (World)reopened.ReadState(reopened.GetHead("right"));
    Console.WriteLine($"Forks: source={original.Hero.Hp}, left={leftSaved.Hero.Hp}, right={rightSaved.Hero.Hp}");
}
```

每个 branch 至多一个活动工作副本；不同 branch 可同时保留，所有仓库操作仍须串行。
显式 Dispose 释放该 branch 后可再次 `Checkout`；`MoveBranch(name, expectedHead, targetFrame)` 只要求目标 branch 没有活动工作副本。
ref-only `CreateBranch("fork", selectedFrame)` 只建立持久引用，不恢复领域图或占用工作副本。
Fork 自身只发布新 ref，不新增历史检查点；Event 地址仍只恢复最近 State，纯 Event 前缀则返回 `State == null`。
消息处理和外部效果去重由应用负责；两个孩子从同一 Event 开始不表示库已经领取或完成该事件。
handle 从 `GetHead`、`ReadFrames` 或提交结果取得，只能用于签发它的这一次打开实例；不要跨库或跨重开复用。
位置统一使用 `CheckpointAddress`；它只用于本次打开，不是可序列化的外部地址。
跨重开保存位置可使用不可变 tag：`repo.CreateTag("before-experiment", selectedFrame)`；
关闭重开后用 `reopened.ResolveTag("before-experiment")` 取得当前打开的地址，再 ReadCheckpoint 或 Fork。
tag 可指向 State 或尚无 State 的 Event；分支提交或移动不改变它，同名 tag 创建一律拒绝。
tag 与 branch 名称空间独立，名称使用 1–128 个 ASCII 字符，匹配 `[a-z0-9][a-z0-9._-]*`，不能以 `.` 或 `.lock` 结尾。
只读打开和空模型配置可以解析 tag；读取领域图仍需要对应模型。创建/解析均不推进分支或物化领域图。
真实包和跨进程示例见 [TagConsumer](experiments/PackageConsumerProbe/TagConsumer/README.md)。
历史可为 `S0 → E1 → E2 → S1 → S2` 或 `E0 → E1 → S0`。
业务 Parent 始终是前一 Head；图 Revision Parent 是提交前最近 State，没有 State 时为 null。
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
这些比例不包含共享 frame、map、Schema 与发布元数据等开销，也不计有限历史与离散选择的偏差；
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
运行时每个业务图 pin 的 meta catalog 记录实际保存的完整布局；构建期 history 与运行时 catalog 职责不同。读取历史可以在内存升级，第一次真实 E/S 保存才共同接受所需新 meta。

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

- 同一份已接受 history 中同一个 Schema ID/版本的字段布局必须一致；不能修改已有 history 来伪装成从未变过。
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

### 开发尚未定稿的 Schema

默认构建模式为 `Publish`，会冻结本次候选；实验性下一版本在定稿前可显式使用
`-p:DurableGraphSchemaHistoryMode=Off`。Off 保留已发布 `.dgschema` 作为生成器输入和校验依据，
只停止候选写入／Verify；已经接纳的同版本定义仍不可改形。草稿输入应只含此前已发布的 history，
定稿时再显式 `Publish`，随后 Clean／Rebuild 并 `Verify`。无需删除此前已发布的 history。

复现用户场景时，可固定一个旧版 checkpoint，以 `CreateBranch(name, checkpoint)` 建分支，再
`CheckoutPrivate(name)` 按当前模型 Upgrade 原图并保留仍存活对象身份。重要模型调整后从同一
baseline 建立新的 dev fork；需要继承上次已保存草稿数据再改模型时，应递增 Schema 版本，
并在该 dev 独立的 history 中保留相邻模板与 Upgrade。两个公开操作分开完成，签出失败不会
撤销已经发布的新分支。

完整 ToShared 会导入全部声明；只交付一个定稿新版本时，从固定 baseline 建立干净的 final fork，
执行 baseline→定稿的 Upgrade 和必要重放，再接纳 compiler history 与运行时 Shared。
实际开发故事与特例删减边界见 [DB-088](docs/design-branches/0088-development-schema-workflow.md)。

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

当前外层 API 是**一个 Repository、每个分支至多一个活动 `BranchCheckout` 工作副本、单 writer**；每份已保存的 Event/State 图选一个非空 durable 根。
State/Event 无需共同的领域基类，均实现 `IDurableObject`；同一 CLR 类型也可承担任一角色。
[DB-078-A](docs/design-branches/0078-editable-checkpoint-fork-slice.md#11-078-a非泛型公共基础与自由历史) 已交付自由 E/S 历史和跨类型 State 替换。
[DB-083](docs/design-branches/0083-repository-checkpoint-api-user-stories.md) 的独立 Checkpoint、固定历史枚举、多分支同时签出、named Fork 与不可变 tag 已接入。
同步 Commit 期间宿主须停止对领域图的并发修改；DTO 冻结不提供任意并发读写下的快照隔离。
正常打开由 FrameStore/VersionStore 恢复不完整尾部，只读打开不写不修；不承诺完整 OS crash/power-loss 保证。仓库使用共享 `data/` 与发布 `versions/`，不另建 repository.lock。
当前运行时只支持新建的新存储格式库；模型程序集需要重编译，模型的 accepted history 与显式 Upgrade 继续保留。

保存失败时不要一律重试：`GraphCommitException.Outcome` 区分 NotPublished / Unknown / Published，
同时检查仓库与工作副本的 `IsFaulted`。Unknown/Published 不可透明重试；faulted 实例须 Dispose 后重开，
按持久 head 判断结果；tag 创建则按原名字 ResolveTag 检查绑定。错误与现有领域修改不会自动回滚。应用通过 Repository 保存；必要 Schema 由真实图捕获进入共同候选，不需要独立调用 SchemaStore 登记或操作发布日志。

## 从源码打包

当前包组织为 `Atelia.DurableGraph`、`.Persistence`、`.Storage` 和 `.Serialization`。
从旧 StateStore 系列升级时，同步修改 PackageReference/using 并重编译模型库与宿主。
领域属性、`IDurableObject`、`ObjectId`、登记接口和 Generated/Family 名保持；高级布局类型使用
`Atelia.DurableGraph.Schema`，绑定与状态操作使用 `Atelia.DurableGraph.Runtime`，两者仍在主程序集内。
仅组织迁移不需要提高业务 Schema 版本，保留 accepted history；不提供旧 DLL 的直接二进制兼容。

底层使用独立 [atelia-storage](https://github.com/Atelia-org/atelia-storage) 的六包 RBF3 栈：
Primitives、Data、Rbf、Binary、FrameStore、VersionStore。
[StorageDependency.props](eng/StorageDependency.props) 统一固定版本 S 与准确来源；当前 pin 是未公开的本地候选。
显式 source mode 用于源码 build/test，包交付使用候选完整 feed；不自动发现兄弟仓或回退旧存储包。
源码入口、上游 Pack 和任务 NuGet.Config 见 [存储依赖指南](docs/storage-dependency.md)。

先按指南从准确、干净的上游 commit 准备六包并设置 `$storageFeed`，再从 DurableGraph 根执行：
`$version` 只控制 DG 版本 G，必须与 S 不同；每批不同 DG 内容使用新版本。

```powershell
$ErrorActionPreference = "Stop"
$version = "0.0.0-local.$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss'))"
$feed = Join-Path (Get-Location) ".artifacts/nuget/$version"
. ./experiments/PackageConsumerProbe/PackageProbeSupport.ps1
Prepare-DurableGraphProbeFeed -RepositoryRoot (Get-Location).Path -OutputDirectory $feed -Version $version -StoragePackageSource $storageFeed
```

得到含六个存储包和四个 DG 包的完整十包 feed，将 `$feed` 和 `$version` 用于前面的 restore/run。
[真实包实验](experiments/PackageConsumerProbe/README.md) 使用独立 feed、两代模型和新库历史校验；
只验收新建存储库，模型 history/reader/Upgrade 仍需保留，旧存储数据无需迁移。

`dotnet pack -p:UseStorageSources=true` 会被拒绝。打 DG 包先按指南用任务配置 package restore，
再以 `--no-restore -p:UseStorageSources=false -p:PackageVersion=<G>` pack；存储版本由统一 S pin 提供。
显式 `StoragePackageVersion` 可统一覆盖六包，必须保存该开发包 manifest 来源；没有旧 SegmentStore/Journal 分别覆盖属性。
不覆盖已有候选，不通过清空用户缓存切换同版本内容，也不把 source build 当作 package 消费证据。

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
