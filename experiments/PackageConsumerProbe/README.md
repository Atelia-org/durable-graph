# PackageConsumerProbe

实际 PackageReference 消费是源码测试之外的交付验证。活动门禁各保范围，运行参数、包来源、
history 与冷进程证据由对应入口维护；当前能力见 [PROJECT-STATE](../../src/PROJECT-STATE.md)。

## 活动门禁

| 入口 | 验证的问题 |
|---|---|
| [Run-Probe](Run-Probe.ps1)、[Consumer](Consumer/Consumer.csproj) | 单 runtime 包的生成、Schema/history、DTO/body 与 Capture |
| [Run-ReadmeQuickStartProbe](Run-ReadmeQuickStartProbe.ps1) | 候选目录搬迁、完整配置与 README 原文命令，保留 Upgrade/Fork/Verify 回归 |
| [Run-RepositoryApiProbe](Run-RepositoryApiProbe.ps1) | DB-095 明确编辑入口的普通包编译正例、七项旧 API 精确编译负例及恢复合法源码后的冷保存 |
| [ExplicitStorageClassificationConsumer](ExplicitStorageClassificationConsumer/README.md)、[runner](Run-ExplicitStorageClassificationProbe.ps1) | DG0024 精确负例、两条实际生成路线、修正后 Publish/Verify 与冷进程恢复 |
| [TagConsumer](TagConsumer/README.md)、[runner](Run-TagProbe.ps1) | 不可变名字、冷进程定位与历史分叉 |
| [ImmutableLeafConsumer](ImmutableLeafConsumer/README.md)、[runner](Run-ImmutableLeafProbe.ps1) | 当前生成的不可变叶资格与 sibling SG |
| [EventHistoryRecoveryConsumer](EventHistoryRecoveryConsumer/README.md)、[runner](Run-EventHistoryRecoveryProbe.ps1) | 应用自身的事件快照与处理进度协议 |
| [MetaBootstrapSchemaConsumer](MetaBootstrapSchemaConsumer/README.md)、[runner](Run-MetaBootstrapSchemaProbe.ps1) | 员工自举、无业务注册的 meta 冷读、数组与字典真实 Delta |
| [SplitRepositoryConsumer](SplitRepositoryConsumer/README.md)、[runner](Run-SplitRepositoryProbe.ps1) | 公开两根 Repository、accepted history、Upgrade、Pair、Fork 与冷续写 |
| [PrivateContainerConsumer](PrivateContainerConsumer/README.md)、[runner](Run-PrivateContainerProbe.ps1) | baseline private、Nullable/List 正常升级、完整 ToShared |
| [DevelopmentSchemaConsumer](DevelopmentSchemaConsumer/README.md)、[runner](Run-DevelopmentSchemaProbe.ps1) | 用户 baseline、开发 fork、定稿及 Off/Publish/Verify |

包准备使用 [PackageProbeSupport](PackageProbeSupport.ps1)，消费独立 DG 四包版本 G 与准确存储六包版本 S；
完整 feed、任务配置和候选来源见[依赖指南](../../docs/storage-dependency.md)。
不以 source ProjectReference 或 feed 中存在 nupkg 代替实际消费。运行输出放在忽略目录。

DB-095 的公开调用门禁接受完整候选 feed 与独立 DG 版本；`WorkDirectory` 可省略，由 runner 创建仓外新目录。
它使用全新包缓存及正常 PackageReference/Generator/Build，不配置 friend 或手工 analyzer。
每个旧调用单独断言诊断代码与源码位置；负例结束后恢复合法源码并重新 Verify build，再执行 Shared/Private 冷恢复保存。

```powershell
./experiments/PackageConsumerProbe/Run-RepositoryApiProbe.ps1 -PackageSource <feed> -Version <G> -WorkDirectory <fresh-outside-directory>
```

## 对外交付与原文接入门禁

[候选导出入口](../../eng/Prepare-DurableGraphCandidate.ps1)在一个新目录内集中交付包、完整相对源配置、
固定 G 的 QuickStart 和运行说明；准备命令与闭包边界见[候选交付合同](../../docs/storage-dependency.md#candidate-delivery)。
项目、源码、配置和最短消费命令只从[根 README](../../README.md#最短接入路径)提取，
不在 Probe 中另维护一份示例。当前示例闭包为十个 Atelia 包和原始 K4os 包，资格限于已安装 SDK 10.0.201 的附带示例。

对已经生成的目录运行：

```powershell
./experiments/PackageConsumerProbe/Run-ReadmeQuickStartProbe.ps1 -CandidateDirectory <candidate>
```

可用 `-WorkDirectory <fresh-outside-directory>` 指定新的仓外工作目录。
runner 将交付目录搬迁，在故意错误的祖先 NuGet 配置、全新缓存下直接调用 dotnet，
不经 `Invoke-DotNet` 的 restore 改写；检查旧命令准确 `NU1100`、新命令的本地包解析与两进程 HP99→98，
并继续运行既有 V2/browse/policy/Fork/Verify 断言。结果与覆盖边界记在[候选交付验收](../../docs/storage-dependency.md#candidate-delivery-validation)。

其他领域 runner 继续使用 `-PackageSource <ten-package-feed> -Version <G>` 和已有任务配置，
可以联网取得第三方依赖；不要求所有领域 Probe 改用便携目录，也不把它们的 helper 行为作为原文命令的验收证据。

<a id="model-fixtures"></a>
## 模型与 history 编译资产

依赖已删除 raw Store/typed reader API 的 19 个旧 runner 和执行入口已退出。
以下项目只保留有效的领域声明、跨库组合、历史形状与 Upgrade，为 Library。
编译只证明这些资产可由生成器消费，不授已退出故事当前包运行资格。
需要新增运行门禁时选择具体故事接当前 Repository，不要求每次施工编译所有资产。

| 资产 | 入口 |
|---|---|
| 标量 DTO、历史 World 与引用图 | [StateStore](StateStoreConsumer/README.md) |
| 删除旧 CLR 声明后的 history 能力 | [HistoryCapability](HistoryCapabilityConsumer/README.md) |
| 引用图与共享模型 | [EventHistory](EventHistoryConsumer/README.md) |
| Guid/decimal/TimeSpan | [BclScalar](BclScalarConsumer/README.md) |
| DateOnly/TimeOnly/DateTimeOffset | [TemporalScalar](TemporalScalarConsumer/README.md) |
| inline struct | [InlineStruct](InlineStructConsumer/README.md) |
| 泛型闭合与 owner Upgrade | [Generic](GenericConsumer/README.md) |
| 可组合值规则 | [ValueUpgrade](ValueUpgradeConsumer/README.md) |
| 数组、List、Nullable | [Array](ArrayConsumer/README.md)、[List](ListConsumer/README.md)、[Nullable](NullableConsumer/README.md) |
| enum | [Enum](EnumConsumer/README.md) |
| 字典与复合 Key | [Dictionary](DictionaryConsumer/README.md)、[CompositeDictionary](CompositeDictionaryConsumer/README.md) |
| record 值与引用对象 | [Record](RecordConsumer/README.md)、[RecordClass](RecordClassConsumer/README.md) |
| 跨程序集 nominal、固定 inline 与继承 | [CrossAssembly](CrossAssemblyConsumer/README.md)、[InlineLibrary](InlineLibraryConsumer/README.md)、[InheritanceLibrary](InheritanceLibraryConsumer/README.md) |

默认先编 V1；更高阶段需要同一独立 history 目录中的已接受前版，各入口说明参数和顺序。
跨库先打包上游模型库，再由下级 PackageReference 消费，不复制他库 history。
指定 `DurableGraphPackageVersion`、任务 NuGet.Config 及独立 history/obj/bin 路径，避免混用旧产物。
旧 runner、输出和原故事只在 `6dbaf0e` 保留；删除范围与证据见
[DB-091](../../docs/design-branches/0091-retired-material-cleanup/README.md)。
