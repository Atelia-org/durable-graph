# 显式存储分类：普通包负例与修正后冷恢复

[DB-093](../../../docs/design-branches/0093-explicit-storage-classification/README.md) 的有界交付见证。
本 consumer 只有普通 `PackageReference`，不引用源码项目，不手工挂 analyzer、Build import 或 friend 身份。
runner 将输入复制到仓库外的新目录，用固定 SDK、完整任务 NuGet.Config 和独立 NuGet cache 构建。
Generator、Build 工具和十份实际加载的 runtime DLL 都与所选 feed 核对 SHA256。

默认与 `DurableGraphGenerateDefinitions=true` 使用两个独立项目/history/database 目录。两组源码都含
H01 自动属性负例和独立、非泛型的合法 `LegalControl`；不混入 record、泛型或 Upgrade 来改变路线。
默认必须实际生成 `DurableStates.g.cs`，显式 Family 必须实际生成 `DurableGenericStates.g.cs`。

每组只走同一有限故事：

1. Invalid / Publish 强制 Rebuild，SARIF 恰有一个 Error `DG0024`，位置准确指向
   `Model.Invalid.cs` 的 `DurableField(1)` attribute。合法控制仍生成；错误类型无自己的 candidate、current 投影或 binding。
   失败前后 accepted history 仍为空，没有可运行的旧 consumer DLL。
2. 将项目输入切换到 `Model.Corrected.cs` 的显式字段和真实 C# 14 Transient backing；强制 Rebuild，Publish 两份 V1 history。
   再强制 Rebuild / Verify，已接受 history 的文件集与内容不变。负例没有发布的少字段 history 不作为正例输入。
3. 在新进程创建新 Repository，用 `CreateRootBranchFromState` 保存 `Value=42`、`Control=7`、`Scratch=777`；Capture 不执行 Transient accessor。
   另一进程只读冷开，由唯一分支的 `BranchId` 读取当前 `CommitId` 后 `LoadState`，前两值保留，Transient backing 为 0；Hydrate 不执行 accessor。只有消费程序自己验证
   `Scratch` 的显式 getter 调用计数为 1。只读进程前后数据库文件集与内容不变。

使用主线程准备的新 G 完整十包 feed；G 独立于 [准确六包 S pin](../../../eng/StorageDependency.props)。
此 runner 只读消费现有 feed，不打包、不发布、不清理历史工件：

```powershell
./experiments/PackageConsumerProbe/Run-ExplicitStorageClassificationProbe.ps1 `
  -PackageSource '<complete-ten-package-feed>' `
  -Version '<new-DG-G>' `
  -DgSourceRevision '<40-character-DG-source-commit>' `
  -WorkDirectory '<fresh-absolute-directory-outside-repositories>'
```

未给 WorkDirectory 时使用新的系统临时目录。`result.json` 记录包版本/来源/字节、两组编译结果、诊断位置、
实际生成路线、accepted history 和两个冷进程的程序集证据；失败保留工件并非零退出。包准备和配置规则复用
[既有 support](../PackageProbeSupport.ps1) 与[依赖指南](../../../docs/storage-dependency.md)。

本见证不重复源码 static/冲突/别名/事件形状矩阵，不验证旧库迁移、压测、故障注入或 DB-094 的 backing 持久化。
新 G 来源和本次实跑证据由 DB-093 的验收记录保留；此入口本身不授予任何已打包版本资格。
