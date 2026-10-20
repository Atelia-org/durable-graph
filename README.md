# DurableGraph

> Branch/Commit/WorkingCopy、统一 Schema Catalog、组提交、目录树和历史分页已实现。最终源码/普通包验收与主线合入状态见[0096 交付记录](docs/design-branches/0096-history-and-head-model/final-delivery-acceptance.md)。
> 当前存储依赖是固定来源的本地候选；可用[源码联调](docs/storage-dependency.md#显式源码联调)或[完整候选 feed](docs/storage-dependency.md#candidate-delivery)。旧实验存档不自动迁移。

[MIT License](LICENSE)

**带 Schema 版本历史的 C# 对象图增量序列化库。**

直接修改普通领域对象，然后 Commit；不需要代理、setter hook 或手工 MarkDirty。
DurableGraph 捕获独立的版本化状态 DTO；State 比较上次保存基线，以 Base/Delta 保存变化，Event 独立保存完整 Base 图；
加载时按落盘的 exact Schema 读取、执行显式 Upgrade，再恢复共享引用和循环引用。

目前是快速演进的 **.NET 10 原型**，API 和格式尚未冻结。本文面向首次接入的应用开发者和 Coding Agent，
只介绍当前可用入口；详细能力边界见 [产品工作集](src/PROJECT-STATE.md)。

公开入口是非泛型 `Repository` / `WorkingCopy`：状态型应用直接连续提交 State，无需先提交 Event。
需要记录业务事件时，可以另外提交 Event 快照；历史允许任意 E/S 排列。
每条业务分支对应一个 HeadStore Head；普通提交共同发布该分支与所需的新 Schema 根。分支目录另行管理，显示标签可重复。恢复读取已保存的结果，不重新执行历史业务处理器。

## 最短接入路径

先取得一个候选目录：其中已附 `QuickStart/`、`feed/`、完整 `NuGet.Config` 和本批版本/来源说明，
可整体复制到自己的目录。生产者的准备命令见文末[从源码打包](#从源码打包)。
使用已验证的 **.NET SDK 10.0.201**；当前候选不假定已在 nuget.org 公开。

交付目录附带这个 `net10.0` 示例所需的四个 DG 包 G、六个 Storage 包 S 和原始第三方依赖；
目前共十一包。已有适用 SDK/运行时后，示例可仅从本地源还原。
这不包含 SDK 安装、额外 RID/self-contained 发布、任意应用或本仓测试依赖的离线闭包。

生成的 `QuickStart/QuickStart.csproj` 来自下面的唯一模板，准备入口会在其中写入一次本批 G 的
`DurableGraphPackageVersion` 属性。后续接到自己的应用时，同样固定实际版本并直接引用 Runtime 和 Persistence：

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

交付项目已固定版本，无需在每条命令重复传入；正式接入也可用项目属性或统一包版本文件固定版本。
`Atelia.DurableGraph` 包同时携带 Runtime、Source Generator 和 history 构建集成；Persistence 包提供持久化工作副本外观。
`BranchId` 选择继续编辑的分支，`CommitId` 定位一次准确保存，`WorkingCopy` 是分支工作副本。
这些类型与 `Repository` 位于根命名空间 `Atelia.DurableGraph`，实现仍由 Persistence 包提供。
声明模型的每个项目都应**直接引用 Runtime 包**。无需手工添加 Analyzer、AdditionalFiles 或 Import，
也不要仅以 Runtime 项目的 ProjectReference 代替完整包接入。

候选目录根的 `NuGet.Config` 使用相对 `feed` 路径，内容如下；完整配置与包一起交付，
不需要查找生产者的内部 pack 配置或修改用户级源。已有项目应采用匹配的配置，额外应用依赖另行配置：

```xml
<configuration>
  <packageSources>
    <clear />
    <add key="candidate" value="feed" />
  </packageSources>
  <packageSourceMapping>
    <clear />
    <packageSource key="candidate"><package pattern="*" /></packageSource>
  </packageSourceMapping>
  <fallbackPackageFolders><clear /></fallbackPackageFolders>
</configuration>
```

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
```

领域 class/struct 使用顶层 `partial` 声明。class 实现 `IDurableObject`（可从自己的领域基类继承该接口）；每个参与持久化的声明显式标记
`DurableType`。实例字段用 `DurableField` 或 `Transient` 明确分类；FieldId 是该声明内的稳定正整数，
基类和派生类可以分别有自己的字段 1。方法不参与序列化。普通 class/struct 的自动属性不能通过
`[field: DurableField(...)]` 进入持久模型；这类标注报 DG0024，请改用显式字段（可另加包装属性）。
真实属性 backing 或普通 field-like event 可用 `[field: Transient]` 明确排除；没有真实字段目标的标注也会报错。
未标注的普通隐式 storage 保持排除，record 仍要求其已有的完整存储分类；属性访问器不参与保存恢复。

### 2. 保存、重开、继续修改

保存为 `Program.cs`，替换控制台模板的内容。**每次业务保存只调用 `CommitState()`，无需定义 Event 类型：**

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
// 这个示例只有一个根分支；应用通常保存并复用所选 BranchId。
BranchInfo[] branches = repository.ListBranches();
BranchId mainId = branches.Length == 0
    ? repository.CreateRootBranchFromState("main", NewWorld()).Id // 已保存初始 S0。
    : branches.Single(branch => branch.ForkedFromCommitId is null).Id;
using var session = repository.OpenWorkingCopy(mainId);

World world = (World)session.State!;
world.RebuildTransient(); // Load 不调用构造器/字段初始化器，也不自动执行此方法。
if (!ReferenceEquals(world.Hero, world.Characters[0]) ||
    !ReferenceEquals(world.Hero.Partner!.Partner, world.Hero)) {
    throw new InvalidOperationException("Object identity was not preserved.");
}

world.Hero.Hp -= 1;
var commit = session.CommitState();
Console.WriteLine($"{world.Find("Alice").Name}: Hp={world.Hero.Hp}; CommitKind={commit.Kind}");

static World NewWorld() {
    var alice = new Character { Name = "Alice", Hp = 100 };
    var bob = new Character { Name = "Bob", Hp = 80 };
    alice.Partner = bob;
    bob.Partner = alice;
    return new World { Hero = alice, Characters = [alice, bob] };
}
```

在候选目录根运行以下 PowerShell 命令；数据库保存在该目录的 `quickstart-world/`，请放在源码版本控制之外：

```powershell
dotnet restore QuickStart/QuickStart.csproj --configfile ./NuGet.Config
dotnet run --project QuickStart/QuickStart.csproj --no-restore -- ./quickstart-world
dotnet run --project QuickStart/QuickStart.csproj --no-restore -- ./quickstart-world
```

从新目录开始，第一次输出 `Hp=99`，第二个进程重开后输出 `Hp=98`；业务历史为 `S0(100) → S1(99) → S2(98)`，没有 Event。
`CreateRootBranchFromState` 保存初始 S0，返回 `BranchInfo`；它不占用工作副本。`OpenWorkingCopy(mainId)` 恢复新的领域实例，
因此后续修改应使用 `session.State`，不能继续修改传给创建方法的原始对象并期待它自动保存。
成功提交 State 保留当前工作副本的领域实例及比较基线。`BaseCommitId` 指向当前准确提交，
`StateSourceCommitId` 指向所恢复的最近 State；打开不物化 Event 或自动重放。
Dispose **不自动保存，也不撤销领域修改**。`CommitState(nextState)` 允许替换为不同实际类型的根，成功后安装传入实例。
重新运行会再扣一次 HP，是一次新业务操作；保存异常后不能把它当透明重试。
发布失败的 outcome/fault 与重开边界见[事件快照与失败恢复](#事件快照与失败恢复)，纯 State 应用同样适用。

省略策略参数即可使用默认 `(5, 5)`：对象级读取放大动机阈值为 5，可选 Base 软预算为 5%。
无需自己估算尺寸、挑 Base/Delta 或调用 DTO 的二进制 body；有需要再按下文[调整保存策略](#调整保存策略)。

### 3. 独立浏览与分支

完成上面的两次运行后，可以浏览每次保存的 State，再从旧 State 分叉。
以下片段保留原程序的 `using`、参数检查和 `path`/`models` 登记，替换从 `using var repository` 起的其余内容（含 `NewWorld`）。
不要与尚未释放的 writer 一起执行。

```csharp
using (var history = Repository.OpenReadOnlyExisting(path, models)) {
    BranchId mainId = history.ListBranches().Single(branch => branch.ForkedFromCommitId is null).Id;
    foreach (var commit in history.ReadFrames(mainId)) { // 从旧到新；此例只有 State。
        var saved = (World)history.LoadState(commit.Id);
        Console.WriteLine($"History: Hp={saved.Hero.Hp}"); // 100、99、98。
    }
}
```

`ReadFrames` 返回所选分支从旧到新的完整 State/Event 描述，有全量成本；它本身不恢复领域对象。
混合历史需先按 `commit.Kind` 选择 `LoadState(commit.Id)` 或 `LoadEvent(commit.Id)`。这两个入口要求准确角色；
需要“截至此处最近的 State”时用 `LoadLatestSavedState(commitId)`，其 `StateCommitId` 和 `State` 可以为 null。
每次加载需要相应的 reader/Upgrade；修改独立加载的对象不会写盘。`OpenReadOnlyExisting` 用于零写入浏览。

`ForkBranch(selectedCommitId, displayLabel)` 创建可重开的分支，返回 `BranchInfo`；再 `OpenWorkingCopy` 继续编辑。
下面选择 S1（HP99），保存新分支的 `BranchId`，关闭后直接用它重开：

```csharp
BranchId mainId, alternateId;
using (var branches = Repository.OpenExisting(path, models)) {
    mainId = branches.ListBranches().Single(branch => branch.ForkedFromCommitId is null).Id;
    CommitId selected = branches.ReadFrames(mainId)[1].Id; // S1，不是最新提交。
    alternateId = branches.ForkBranch(selected, "alternate").Id;
    using var alternate = branches.OpenWorkingCopy(alternateId);
    var world = (World)alternate.State!;
    world.Hero.Hp -= 10;
    alternate.CommitState(); // alternate: 99 -> 89，main 仍为 98。
}
using (var reopened = Repository.OpenExisting(path, models)) {
    using var alternate = reopened.OpenWorkingCopy(alternateId);
    var world = (World)alternate.State!;
    world.Hero.Hp -= 1;
    alternate.CommitState(); // 冷开后继续：89 -> 88。
    var original = (World)reopened.LoadState(reopened.ReadCurrentCommit(mainId).Commit.Id);
    Console.WriteLine($"Branches: main={original.Hero.Hp}, alternate={world.Hero.Hp}"); // 98、88。
}
```

每个分支至多一个活动工作副本；不同分支可同时保留，仓库操作仍须串行。Dispose 释放占用后可再次 `OpenWorkingCopy`。
标签只用于显示，可以重复；`ListBranches` 只列正式业务分支，应用选择、打开和持久化导航均用 ID。
当前分支只追加；从旧位置续写应 fork，没有移动当前分支或 rewind 入口。

Fork正常返回后，新分支已经创建；随后OpenWorkingCopy失败不会撤销它。应用应保留新BranchId，仅重试打开，
并在新副本打开成功前保留旧副本；旧副本也只在owner健康时可继续使用。完整切换例子见[接入配方](experiments/PackageConsumerProbe/ApplicationRecipesConsumer/README.md#2-分叉后再切换分别处理结果)。

fork 不新增业务提交：新分支的当前 `CommitId` 可以仍属于父分支，首次提交才取得新分支自己的提交 ID。
因此使用 `WorkingCopy.BranchId` 继续原来的世界线，使用 `CommitId` 查看固定存档；不要由当前 Commit 的归属推断写入目标。
打开从 Event fork 的工作副本会恢复最近 State；纯 Event 前缀得到 `State == null`。消息处理和外部效果去重由应用负责。

`BranchId` 和 `CommitId` 是可跨重开保存的值，分别有 4 字节和 12 字节 codec。例如编码后存入应用自己的导航配置：

```csharp
byte[] encodedBranch = new byte[BranchId.EncodedSize];
if (!alternateId.TryWrite(encodedBranch) || !BranchId.TryRead(encodedBranch, out BranchId restoredId)) {
    throw new InvalidDataException("Invalid branch ID.");
}
// 下次打开同一个 repository 后，使用 OpenWorkingCopy(restoredId)。
```

`CommitId` 使用相同的 `EncodedSize` / `TryWrite` / `TryRead` 形状。应用同时保存所选仓库路径或其他定位信息；
ID 不是全局身份，解码成功也不证明所选库中存在该位置，跨库误用检出只是开发期概率诊断。
这些 ID 暂不作为可直接标注的 Durable 模型字段。

默认System.Text.Json不会替这些ID调用字节codec，会得到 `{}`。网页DTO或导航配置可使用
[版本化DTO＋Hex示例](experiments/PackageConsumerProbe/ApplicationRecipesConsumer/README.md#1-保存上次使用的分支)，显式保存LastUsedBranch，不按无序列表末项推断“上次使用”。
同一指南提供[版本化Summary](experiments/PackageConsumerProbe/ApplicationRecipesConsumer/README.md#3-从state投影版本化摘要)：摘要与提交共同发布，但其业务一致性由应用保证，须从投影开始到整个提交调用结束保持图稳定。

不可变 tag 适合命名固定存档：`repo.CreateTag("before-experiment", selectedCommitId)`，
重开后 `ResolveTag` 返回同一 `CommitId`，可用于 Load、ReadCheckpoint 或 ForkBranch。
tag 可指向 State 或纯 Event 前缀；后续提交不改变它，同名 tag 创建一律拒绝。
tag 名称与分支显示标签无关；两者允许有效 Unicode，长度为 1–1024 个 UTF-16 code units，tag 按 Ordinal 比较。
只读打开和空模型配置可以解析 tag；读取领域图仍需要对应模型。创建/解析 tag 不推进业务分支。
包消费者源码见 [TagConsumer](experiments/PackageConsumerProbe/TagConsumer/README.md)；当前候选包资格仍以本页顶部说明为准。

历史可为 `S0 → S1 → S2`、`S0 → E1 → E2 → S1 → S2` 或 `E0 → E1 → S0`。
业务前后关系由 HeadStore 的继承历史表达；State/Event 图不另存业务 Parent。
State 的增量来源是该提交之前最近的 State。每个 Event 独立捕获完整可达图，使用局部对象 ID、完整 Base 和自有目录，没有内容来源；Event 不读取或推进 State 保存基线。
`GetPreviousState(eventCommitId)` 寻找严格祖先中的最近 State；纯 Event 前缀返回 null。

`ReadPair` 是实验性只读快照 API：按输入顺序返回 First/Second，两边成功后才交付。
默认返回两个 `IDurableObject`，保留各自实际类型；通过 `ReadCommitInfo(commitId).Kind` 判断 State/Event，通过模式匹配使用具体领域类型。
两个输入无需相邻，也不要求一份 State、一份 Event；调用方用显式转换或模式匹配取得领域类型。
State-State 配对在本次操作内复用相同 ObjectVersion 的解码结果，并可共享完整引用闭包都一致的领域实例。
只要任一输入是 Event，两边就分别恢复，保留各自图内的共享和环，不跨图共享可变实例；同一 Event 与自身配对也如此。
**两份结果及其可达对象都必须按只读快照使用，包括会影响观察结果的 Transient 写入**。
不要分别给可能共享的 Actor 写入不同的 `OwnerWorld`、查询上下文或视图专属缓存；这些信息应由各自的图外
`WorldView` / 索引持有。即使两个视图引用同一个 Actor，各自的查询仍使用各自的世界上下文。
不能只用全局 Actor→context 表代替视图：共享的 Actor 会命中同一个 key。
不要依赖跨图 `ReferenceEquals` 判断业务身份或版本，也不要假定两图可隔离编辑。
需要原位初始化每份历史图的 Transient 时，使用 `ReadCheckpoint` 或分别调用 `LoadState` / `LoadEvent`，不需要开启 writer；
持久成员仍按历史快照使用，这些读取不安装保存基线。需要继续修改并提交时才使用 `OpenWorkingCopy`。
图外视图与 State-State 共享边界见[ReadPair 合同](docs/design-branches/0066-readpair-comparison-and-transient-contract-slice/README.md)及[产品回归](tests/DurableGraph.Persistence.Tests/SharedGraphReaderTests.cs)；Event 隔离边界见[独立 Event 合同](docs/design-branches/0097-independent-event-graphs/README.md)。
共享候选判定使用持久状态比较，不准备对象 Base/Delta payload；常规读取仍可能为 Dictionary key 唯一性校验进行规范编码。
OpenWorkingCopy 只恢复最近 State；独立读取的 Event 与工作副本不共享可变实例。热路径由用户保持 Event 内容只读；持久 DTO 冻结不会冻结原 CLR 对象。

多开局目录可用 `repo.ListRootBranches()` 取得根，再用 `repo.ListBranchSubtree(branchId)` 取得包含起点的平面子树。它们只返回正式业务 Branch 的标签和固定来源，不加载领域对象，也不要求登记业务模型；空 fork 可立即列出。结果无排序保证，应用自行排序和画树。首版会读取并验证整个已登记森林，不能将子树查询理解为只访问该子树的磁盘文件。

长历史可从准确提交向祖先分页，不用先读取整条历史：

```csharp
HistoryStart start = HistoryStart.AtOrBefore(repo.ReadCurrentCommit(branchId).Commit.Id);
CommitHistoryPage page = repo.ReadHistoryPage(start, new HistoryPageLimits(MaxItems: 50, MaxWorkSteps: 10_000));
// 展示 page.Items；下一次使用 page.Continuation（null 表示已到根）。
```

页面包含 State/Event 描述，不恢复业务对象。即使应用筛选后没有显示项，也使用页面给出的 Continuation；满页不保证后面还有数据，末尾可以有一个空完成页。工作预算耗尽或取消会使整次调用失败，不返回部分页；MaxWorkSteps 计量 HeadStore 历史工作，不是全部 I/O 或耗时上限。完整公开调用见[普通包示例](experiments/PackageConsumerProbe/GroupCommitConsumer/README.md)。

## 工作副本与统一 Schema Catalog

`CreateNew` / `OpenExisting` / `OpenReadOnlyExisting` 在打开时冻结 `models` 的代码配置；修改 registry builder 只影响之后打开的仓库。
Open 负责资源读写；`CreateRootBranchFromState/Event` 和 `ForkBranch` 发布分支，`OpenWorkingCopy(branchId)` 取得可提交的工作副本。所有 `CommitState` / `CommitEvent` 共用仓库的 Schema Catalog；不同类型及正常版本可以共存，同 SchemaKey 的已接受完整定义不可改形。
`IsReadOnly` 表示底层资源是否只读。一个可写 Repository 可同时保有不同分支的工作副本并串行操作；业务分支不创建独立的 Schema 接纳路线。
读取按所选图的准确 Schema 视图闭合执行能力。工作副本固定恢复时的执行环境：有 State 取 State 所 pin 的 Schema，无 State 取打开工作副本时的共享 Catalog；普通模型与 Family 按需闭合。State 提交可以推进持久 Schema 视图和保存基线，仍保留工作副本的执行环境；Event 不安装 State。V2 程序签出 V1 数据时已执行显式 Upgrade，得到 V2 内存对象，可以连续增量保存，无需每次提交后重新签出。
独立 `LoadState` / `LoadEvent` 按被读图自己的准确 Schema 视图恢复。Dispose 后重新 OpenWorkingCopy 取得新的领域实例；有 State 时仍选择最近 State 的视图，不追到最新 shared 或 Event 的目录，也不重载代码配置。

新增模型、reader、Upgrade 或策略应在 Open 前登记。运行中修改 registry 后，需要关闭全部工作副本和 Repository，再用新配置 Open，使用 BranchId 重新打开工作副本或按 CommitId/tag 加载历史。未提交修改先由应用明确保存当前配置支持的内容，或自行保留／重建；Dispose 不保存、不撤销修改，已有 CLR 引用不会跟随新工作副本。

对于相同持久输入和 ObjectId 等明确调用输入，在同一代码配置、准确源／目标布局及显式依赖成立时，Normalize/Upgrade 和恢复的持久结果应稳定；不能因无关 Schema 是否登记、memo 命中或调用次数而改变业务值。准确目录仍用于布局解析和验证，缺失／冲突或缺少 reader 能力可以使操作拒绝。配置冻结保留委托／比较器引用，不复制它们捕获的外部状态；调用者负责行为稳定。
`StateBindingContext.TryGetSchema` 是供核心准确解析／验证使用的 protected 实现接缝；普通 factory 通过 `BindSchema`／`ResolveReader` 等公开服务闭合能力。外部派生 context 可以 override 并在自身方法内调用；访问范围收窄只减少默认查询能力，不能验证用户闭包的行为。长期约束见[模型执行环境与 factory 行为](docs/DurableGraph-target-design-v0.md#模型执行环境与-factory-行为)。
Capture 必须提供 canonical current DTO，同布局保存／恢复正确往返持久字段和引用关系；历史值变化由显式 Upgrade 定义。exact current durable DTO 不调用历史 Normalize 委托。reader、引用遍历、比较器与 binding factory 不修改输入 DTO；Allocate/Hydrate 只创建和填充本次恢复图，业务初始化和视图专属 Transient 放在完整恢复交付之后。上述行为合同不证明任意自定义 reader 都能读回：Commit 成功不等于已执行 reader body 的全部验证。

新增 Schema 与业务提交共同原子发布；无新增定义时复用当前 Catalog。历史读取仍使用所选图和对象 batch 的准确 Schema，不能用最新目录替换历史解释。普通 Upgrade 保留存活对象身份、共享和循环，改型布局保存为新 Base；Event 不推进 State 基线。

未定稿的同版本异形模型使用关闭后的完整 Repository 副本隔离，流程见[开发尚未定稿的 Schema](#开发尚未定稿的-schema)。
当前只有统一 Catalog，没有 Private Schema 或 ToShared 路线。分支目录/业务图格式为 `P0R4` / `P0G2`；
读写和只读打开都不兼容旧布局，实验数据可重建，需要旧数据时先用对应版本另行导出。
Schema 简化背景见[删除切片](docs/design-branches/0096-history-and-head-model/private-removal.md)，目录和业务图编码见[B2 施工记录](docs/design-branches/0096-history-and-head-model/branch-cutover-plan.md)，`P0R4` 的 Event 语义边界见[DB-097](docs/design-branches/0097-independent-event-graphs/README.md)。

## 多分支共同保存

同一 Repository 中，已打开的多个 WorkingCopy 可以共同保存。例如世界减少一把剑、角色增加一把剑后，把两份 State 一同提交：

```csharp
// worldWork 和 playerWork 属于同一 repo，且各自已有 State。
var saved = repo.CommitBranches([
    worldWork.CreateStateCommitRequest(),
    playerWork.CreateStateCommitRequest()
]);
CommitId worldCommit = saved[worldWork.BranchId].Id;
CommitId playerCommit = saved[playerWork.BranchId].Id;
```

请求只保留根引用，执行 `CommitBranches` 时才捕获对象；整个调用期间保持全部参与图稳定。摘要在创建请求时复制。每条分支只能出现一次，正常返回表示全部业务提交和必要 Schema 已共同发布。成功后旧请求因 Base 改变而过期。

也可用 `CreateEventCommitRequest(domainEvent)` 参与混组：Event 仅推进历史位置，不保存该工作副本的 State 编辑。失败检查 `RepositoryPublicationException.Outcome`、`Members` 与 `IsFaulted`；发布失败不回滚领域修改，也不替应用检查物品守恒。完整公开调用和冷重开见[普通包示例](experiments/PackageConsumerProbe/GroupCommitConsumer/README.md)。

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

在 CreateRootBranchFromState、CommitState 或 CreateStateCommitRequest 的 `parameters` 参数传入配置即可。
Event 始终保存完整 Base，不接受该增量策略参数。
例如，在第 2 节的输出语句之前插入下面代码，再保存一次状态：

```csharp
var savePolicy = new ReadAmplificationBaseBudgetParameters(
    ReadAmplificationThreshold: 11,
    BaseBudgetPercent: 5);
world.Hero.Hp -= 1;
session.CommitState(parameters: savePolicy);
```

需要偏向冷读时将 `11` 改为 `3`。**覆盖只对该次调用生效**，包括根分支创建；后续省略参数或传 `null`
都会重新使用库默认值，不继承上次覆盖。应用若要固定自己的策略，应共享一个参数值并在每次保存时显式传入。
当前默认可能随原型演进调整；它选择了模型中约 25% 的额外 Base 摊销开销，尚非实测最优值。
公式、选择依据和适用边界见 [DB-070](docs/design-branches/0070-read-amplification-default/README.md)，
也可打开[交互函数图](docs/research/read-amplification-tradeoff/index.html)比较。

## 事件快照与失败恢复

**按需记录 Event。** 如果只关心可浏览、可分叉的状态历史，上面的 State-only 流程已经足够。
只有业务需要保留“发生了什么”时，再添加事件类型。将下面内容保存为 `Events.cs`，重新构建后再打开仓库：

```csharp
using Atelia.DurableGraph;

namespace QuickStart;

[DurableType("DamageEvent", 1)]
public partial class DamageEvent : IDurableObject {
    [DurableField(1)] public int Amount;
}
```

下面按第 3 节的方式替换程序，仍沿用 `path`、`models` 和顶部 `using`；生成的 Register 会一并登记新增的事件类型。
此例先记录事件，再由应用修改 State；**这是两次独立提交，不是一次事务**，库也不会自动应用或重放 Event。
中断可能留下 `… → E` 而没有后续 State；需要恢复时使用下述处理进度协议。

```csharp
using (var events = Repository.OpenExisting(path, models)) {
    BranchId mainId = events.ListBranches().Single(branch => branch.ForkedFromCommitId is null).Id;
    using var session = events.OpenWorkingCopy(mainId);
    var world = (World)session.State!;
    var damage = new DamageEvent { Amount = 1 };
    session.CommitEvent(damage);
    world.Hero.Hp -= damage.Amount;
    session.CommitState();
}
using var history = Repository.OpenReadOnlyExisting(path, models);
BranchId rootId = history.ListBranches().Single(branch => branch.ForkedFromCommitId is null).Id;
CommitId end = history.ReadCurrentCommit(rootId).Commit.Id;
foreach (var eventFrame in history.EnumerateEvents(end, HistoryOrder.OldestFirst)) {
    var damage = (DamageEvent)history.LoadEvent(eventFrame.Id);
    Console.WriteLine(damage.Amount);
}
var lastEvent = history.EnumerateEvents(end).First(); // 上面刚保存过事件。
var checkpoint = (EventCheckpoint)history.ReadCheckpoint(lastEvent.Id);
var occurrence = (DamageEvent)checkpoint.Event;
var before = (World)checkpoint.PreviousState!; // 此例从 State 开始。
Console.WriteLine($"Event: amount={occurrence.Amount}; beforeHp={before.Hero.Hp}");
```

`EnumerateEvents(end)` 沿固定的逻辑祖先链从新到旧返回 CommitInfo，允许提前停止，跨过 State 时不恢复其领域图。
后续分支提交不改变所选结尾；循环内可以调用 `LoadEvent(eventFrame.Id)`。`HistoryOrder.OldestFirst` 先准备范围内的轻量描述，再从旧到新输出。
可选 `afterExclusive` 排除下界及其更早历史；同点得到空范围，非祖先下界在交付首项前拒绝。这个校验可能需要完整范围的元数据回溯。

`ReadCheckpoint` 一次恢复当前图与最近严格祖先中另一角色的图，最多两份，全部成功才返回。
`EventCheckpoint` 提供 `Event` / `PreviousState`；`StateCheckpoint` 提供 `State` / `PreviousEvent`，前驱分别以 `PreviousStateCommit` / `PreviousEventCommit` 提供 CommitInfo。
不存在相应祖先时，前驱根和描述同时为 null，包括首 State 之前的 Event。
这些图彼此独立，也与其他读取和工作副本的可变对象隔离；重复 getter 保留同一实例及你的本地修改。
`PreviousEvent` 仅表示历史中的邻近事件，不能证明它已经处理或是本次 State 的唯一原因。消息处理进度由应用 State 表达。
这些历史恢复结果不安装保存基线，修改它们不会自动写盘。只需要事件时仍使用 `LoadEvent`，避免额外恢复前置 State。

`CreateRootBranchFromEvent("main", firstEvent)` 直接发布首个 Event，返回 BranchInfo；
随后 `OpenWorkingCopy(branch.Id)` 的 `State` 为 null。
继续 `CommitEvent` 不改变这一状态，直到 `CommitState(nonNullRoot)` 发布首个 State。
无 State 时，无参 `CommitState()` 会在捕获或追加前拒绝。已有 State 的读取失败会传播，不会转换成 null。

Event 捕获可直接引用当前 State 中的角色；提交会独立保存当时的值和图内引用，不消费 State 的身份或差分基线。
热处理仍使用原 CLR 对象，提交不会冻结它们。事件若需要在热处理期间保留角色“当时的 HP 和观察”，
应保存所需的只读业务快照，通过业务 ActorId 找到当前角色进行修改。
可以从可变 `List<string>` 复制为快照私有的 `string[]`，共享不可变 string；readonly 字段本身不会冻结数组或可变元素。
领域采用不可变对象替换时，也可以让事件保留旧对象，不必额外维护一套快照类型。

[可运行的快照与恢复示例](experiments/PackageConsumerProbe/EventHistoryRecoveryConsumer/README.md) 展示
`E1.TargetSnapshot.Hp == 10`、`S1` 中角色 HP 为 7，热处理、冷 OpenWorkingCopy 和独立浏览都保持事件观察值。
恢复由应用持久化的事件标识和处理进度决定；库不提供 PendingEvent，也不赋予 E/S 邻接关系“已处理”的含义。

例如，为事件保存单调业务序号，并在 State 中保存最后成功应用的序号，处理结果与进度一起 CommitState。
失败后先结束当前尝试，关闭工作副本/Repository，再 OpenExisting、`OpenWorkingCopy` 并按应用协议恢复：

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
dotnet clean QuickStart/QuickStart.csproj
dotnet build QuickStart/QuickStart.csproj --no-restore -p:DurableGraphSchemaHistoryMode=Verify
```

初次 Publish 后再 Clean/Verify，可同时验证 history 确实能作为下一次编译的输入。
共享同一 history 目录的构建应串行进行，尤其是首次发布或新增 Schema/版本时；
等待 Publish 构建成功结束后，可使用独立输出目录并行 Verify，期间不要再发布新历史。
Build 工具会协调同目录的 Publish/Verify 操作，防止重叠发布写入冲突历史；这不保证整个构建并发成功，
因为生成器读取历史发生在发布协调之前。遇到发布重叠导致的暂时缺失历史错误，应等待另一构建完成后串行重试。
构建属性及诊断说明见 [Build 工具](src/DurableGraph.Build/README.md)。

升级生成器包后，若报错提示 candidate／reference manifest 不是恰好一份，先检查报错中的
`CompilerGeneratedFilesOutputPath`。确认其中 DurableGraph 生成器的输出子目录属于该项目后，
删除这些派生输出，再以 `dotnet build -t:Rebuild` 强制重新编译。`dotnet clean` 可能留下旧生成目录；
保留 `DurableGraphSchemaHistoryDirectory` 内的 `.dgschema`，无需删除 Schema history 或仓库数据。

### 开发尚未定稿的 Schema

默认构建模式为 `Publish`，会冻结本次候选；实验性下一版本在定稿前可显式使用
`-p:DurableGraphSchemaHistoryMode=Off`。Off 保留已发布 `.dgschema` 作为生成器输入和校验依据，
只停止候选写入／Verify；已经接纳的同版本定义仍不可改形。草稿输入应只含此前已发布的 history，
定稿时再显式 `Publish`，随后 Clean／Rebuild 并 `Verify`。无需删除此前已发布的 history。

复现用户场景时，关闭 baseline 的所有工作副本、Repository 和底层资源，再复制整个仓库目录。
每个草稿使用自己的数据副本、模型代码、编译 history 与输出目录；用 `OpenWorkingCopy` 和普通
`CommitState` / `CommitEvent` 开发。重要模型调整后从干净 baseline 重建试验副本；需要继承
上次已保存草稿再改型时，应递增 Schema 版本，并保留相邻模板与 Upgrade。

只交付一个定稿新版本时，另取干净 baseline 副本，执行 baseline→定稿的 Upgrade 和必要重放，
再接纳 compiler history 与运行时 Catalog。没有试验帧合库或自动推广草稿数据的入口。
副本保留原仓库身份与已有坐标，应始终通过各自路径使用；不能凭相同 ID 判断分化后的数据相同。
设计、可执行见证和限度见 [DB-096 统一 Catalog](docs/design-branches/0096-history-and-head-model/shared-schema-only.md)。

## 当前能放进模型的内容

领域引用对象统一实现 `IDurableObject`；旧 `DurableBase` 已移除。升级旧模型时，将直接基类改成接口，
将泛型约束改为 `where T : class, IDurableObject`，并一起重编译模型库和宿主。
已有领域继承链保留，各祖先仍须参与 Durable Schema；删除原空基类本身不要求 Schema 升版。

record class 的 positional/自动属性使用 `[field: DurableField(id)]` 分类真实 backing field；
派生 positional 参数若复用基类属性，只在实际声明存储的基类标记。C# 的 `with` 和相等性保持原行为，
不自动深复制引用成员或按内容比较容器。框架按引用身份保存，两个等值但不同实例的 record 不会合并。
泛型 record 继承与历史形状可参考[保留模型资产](experiments/PackageConsumerProbe/RecordClassConsumer/README.md)。

| 内容 | 当前边界 |
|---|---|
| 标量 | bool、byte/sbyte、short/ushort、int/uint、long/ulong、char、Half/float/double、Guid、decimal、TimeSpan、DateOnly、TimeOnly、DateTimeOffset |
| 领域类型 | 显式 Durable class、record class、struct、record struct、enum；支持泛型、继承、private/readonly 字段及跨程序集组合 |
| 值组合 | inline struct 嵌套布局、Nullable；record 自动属性的存储使用 `[field: DurableField(...)]` / `[field: Transient]` |
| 引用与数组 | string、已登记的实际派生实例、共享/循环；零下界 `T[]` 和 rank 2–4 多维数组、交错数组 |
| 容器 | exact BCL `List<T>` 和实验性 `Dictionary<TKey,TValue>`；按内容保存。复合 Key 与当前 comparer 的边界见[字典模型资产](experiments/PackageConsumerProbe/CompositeDictionaryConsumer/README.md) |
| 尚不支持 | ValueTuple、DateTime、任意外部基类、CLR 嵌套类型/ref struct、任意 object/interface 字段、boxed value 身份、数组协变、非零下界及非 SZ rank-1 数组、其他未适配 BCL 容器 |

支持的类型可以在字段、泛型参数和容器元素中组合。仍需遵守各类型限制；例如 Dictionary 根 Nullable Key 不支持，
容器子类/接口字段不自动当作 BCL 内容对象。string 保留非空实例的引用身份，空串统一为 `string.Empty`。

恢复不执行领域类/struct 的构造器、实例字段初始化器、属性 getter/setter；不要求无参构造器。
Transient 索引/缓存由应用在恢复交付完整图后重建：`OpenWorkingCopy` 或独立 `LoadState` / `LoadEvent` 的结果
可以做应用侧初始化；`ReadPair` 的视图专属状态必须放在图外，不能原位修改可能共享的节点。
Transient 只表示不参与持久化，并不表示修改没有可见影响。自定义字典 comparer 使用当前业务代码，
不得依赖尚未重建的 Transient 或尚未完成填充的引用目标内容。

## 多模型库与运行约束

每个模型库保有自己的 history，并以公开 facade 包住该库 internal 的 `Generated.DurableDefinitions.Register`。
宿主在 CreateNew/OpenExisting/OpenReadOnlyExisting 前登记所需模型库；不依赖程序集自动扫描，也不把别人的 `.dgschema` 复制到本库。
跨库接法见 [跨库继承模型资产](experiments/PackageConsumerProbe/InheritanceLibraryConsumer/README.md)。

当前外层 API 是**一个 Repository、每个分支至多一个活动 `WorkingCopy` 工作副本、单 writer**；每份已保存的 Event/State 图选一个非空 durable 根。
State/Event 无需共同的领域基类，均实现 `IDurableObject`；同一 CLR 类型也可承担任一角色。
[DB-078-A](docs/design-branches/0078-editable-checkpoint-fork-slice/0078-editable-checkpoint-fork-slice.md#11-078-a非泛型公共基础与自由历史) 已交付自由 E/S 历史和跨类型 State 替换。
[DB-083](docs/design-branches/0083-checkpoint-api-user-stories/0083-repository-checkpoint-api-user-stories.md) 的独立 Checkpoint、固定历史枚举、多分支同时签出、带显示标签的 fork 与不可变 tag 已接入（当前签名以本文为准）。
同步 Commit 期间宿主须停止对领域图的并发修改；DTO 冻结不提供任意并发读写下的快照隔离。
正常打开由 FrameStore/HeadStore 恢复不完整尾部，只读打开不写不修；不承诺完整 OS crash/power-loss 保证。仓库使用共享 `data/` 与发布 `versions/`，不另建 repository.lock。
当前运行时只支持新建的新存储格式库；模型程序集需要重编译，模型的 accepted history 与显式 Upgrade 继续保留。

保存失败时不要一律重试：`RepositoryPublicationException.Outcome` 区分 NotPublished / Unknown / Published，
同时检查仓库与工作副本的 `IsFaulted`。Unknown/Published 不可透明重试；faulted 实例须 Dispose 后重开，
按 `ReadCurrentCommit(branchId)` 判断结果；tag 创建则按原名字 ResolveTag 检查绑定。错误与现有领域修改不会自动回滚。应用通过 Repository 保存；必要 Schema 由真实图捕获进入共同候选，不需要独立调用 SchemaStore 登记或操作发布日志。

## 从源码打包

当前包组织为 `Atelia.DurableGraph`、`.Persistence`、`.Storage` 和 `.Serialization`。
从旧 StateStore 系列升级时，同步修改 PackageReference/using 并重编译模型库与宿主。
领域属性、`IDurableObject`、`ObjectId`、登记接口和 Generated/Family 名保持；高级布局类型使用
`Atelia.DurableGraph.Schema`，绑定与状态操作使用 `Atelia.DurableGraph.Runtime`，两者仍在主程序集内。
仅组织迁移不需要提高业务 Schema 版本，保留 accepted history；不提供旧 DLL 的直接二进制兼容。

底层使用独立 [atelia-storage](https://github.com/Atelia-org/atelia-storage) 的六包 RBF3 栈：
Primitives、Data、Rbf、Binary、FrameStore、HeadStore。
[StorageDependency.props](eng/StorageDependency.props) 统一固定版本 S 与准确来源；当前 pin 是未公开的本地候选。
显式 source mode 用于源码 build/test，包交付使用完整候选目录；不自动发现兄弟仓或回退旧存储包。
源码入口、上游 Pack 和任务 NuGet.Config 见 [存储依赖指南](docs/storage-dependency.md)。

先按指南从准确、干净的上游 commit 准备六包并设置 `$storageFeed`，再从 DurableGraph 根执行：
`$version` 只控制 DG 版本 G，必须与 S 不同；每批不同 DG 内容使用新版本。

```powershell
$ErrorActionPreference = "Stop"
$version = "0.0.0-local.$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss'))"
$candidate = Join-Path (Get-Location) ".artifacts/candidates/$version"
./eng/Prepare-DurableGraphCandidate.ps1 -Version $version -OutputDirectory $candidate -StoragePackageSource $storageFeed
```

准备过程可联网，复用既有准确 S 验证和 G 打包，补齐附带示例的原始第三方包。
最终 `$candidate` 内含匹配版本的项目、相对路径配置、包和 `packages.sha256`；将整个目录交给消费者，
进入该目录执行前面的三条命令即可。输出目录必须尚不存在；不同内容使用新 G，不覆盖既有版本。
生成项目和命令均来自本文，消费者无需了解内部 pack 工作目录。

准备后用原文门禁验收（需要 PowerShell 7.3 或更新版本）：

```powershell
./experiments/PackageConsumerProbe/Run-ReadmeQuickStartProbe.ps1 -CandidateDirectory $candidate
```

该门禁搬迁候选到仓外，以新缓存和明确配置执行原文，并保留原有升级/分叉/history 验证。
[交付边界与证据](docs/storage-dependency.md#candidate-delivery)说明本地示例闭包与联网十包 feed 的区别。
[领域包实验](experiments/PackageConsumerProbe/README.md)继续按各自输入和故事验收；
模型 history/reader/Upgrade 仍需保留，当前存储实验库不要求旧格式数据迁移。

`dotnet pack -p:UseStorageSources=true` 会被拒绝。打 DG 包先按指南用任务配置 package restore，
再以 `--no-restore -p:UseStorageSources=false -p:PackageVersion=<G>` pack；存储版本由统一 S pin 提供。
显式 `StoragePackageVersion` 可统一覆盖六包，必须保存该开发包 manifest 来源；没有旧 SegmentStore/Journal 分别覆盖属性。
不覆盖已有候选，不通过清空用户缓存切换同版本内容，也不把 source build 当作 package 消费证据。

## 接下来查什么

| 需求 | 入口 |
|---|---|
| 包内容与交付边界 | [Runtime 包说明](src/DurableGraph/PACKAGE.md) |
| 跨库引用、固定值、继承 | [模型库](experiments/PackageConsumerProbe/CrossAssemblyConsumer/README.md)、[inline 库](experiments/PackageConsumerProbe/InlineLibraryConsumer/README.md)、[继承库](experiments/PackageConsumerProbe/InheritanceLibraryConsumer/README.md) |
| 泛型/嵌套值/容器迁移 | [泛型三代](experiments/PackageConsumerProbe/GenericConsumer/README.md)、[值升级](experiments/PackageConsumerProbe/ValueUpgradeConsumer/README.md)、[数组](experiments/PackageConsumerProbe/ArrayConsumer/README.md)、[List](experiments/PackageConsumerProbe/ListConsumer/README.md)、[复合字典](experiments/PackageConsumerProbe/CompositeDictionaryConsumer/README.md) |
| 实际能力、术语、未实现事项 | [PROJECT-STATE](src/PROJECT-STATE.md)、[术语表](docs/DurableGraph-glossary.md)、[路线图](docs/DurableGraph-research-roadmap.md) |
| 修改 DurableGraph 本身 | 先读 [AGENTS.md](AGENTS.md) 与产品工作集；根 `dotnet build DurableGraph.slnx` / `dotnet test DurableGraph.slnx`，包边界变动另跑相关包实验 |

下游试用时，优先按自己的领域需求建立一个小的真实 World。记录必须改写的模型、重复登记/升级代码、
需要翻内部源码才能完成的步骤及难以理解的诊断；反馈附最小复现、包版本和保留的 history。
遇到缺失功能先记录，不要仅为让示例通过而改掉原来的建模需求。
