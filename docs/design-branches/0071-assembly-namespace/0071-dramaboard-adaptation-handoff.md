# DramaBoard 试用 DurableGraph DB-071：适配交接

> 2026-09-12。DB-071 与 DramaBoard 试用均已通过，用户已批准上游提交。本文随迁移提交收录；后续正式 pin 应采用该真实提交。
> 本文提供已验证的接入材料；下游操作范围和提交安排以用户请求及当地真实指令为准。

## 1. 使用本地已验证的新包

```text
PackageSource = E:/repos/Atelia-org/durable-graph/obj/db071-new/feed
Version       = 0.0.0-db071.20260912.1
```

该 feed 包含四个 DG 包和五个 Atelia 依赖包；此次试用一起消费这个闭包。
初次试用时 DurableGraph HEAD 为 `c7ee495`，迁移尚未提交；该旧 HEAD 不包含新组织。
试用时 DramaBoard 的 `Prepare-DurableGraph.ps1` 仍取 `1c6083c`，不能用它准备上述新包。
试用已按命令行覆盖来源/版本完成，反馈见[下游记录](../../../../drama-board/docs/feedback/durablegraph/004-db071-namespace-adaptation.md)。
下一步由下游把准备脚本、默认版本、CI checkout 和来源说明统一固定到收录本迁移的真实提交，并验证默认构建路径。
本地试用 feed 可继续用于复现；它不是远程已发布包。

## 2. 适配映射

以下 PackageReference 和 namespace 同步改名，主包 `Atelia.DurableGraph` 保持：

| 旧名称 | 新名称 |
|---|---|
| `Atelia.DurableGraph.StateStore` | `Atelia.DurableGraph.Persistence` |
| `Atelia.DurableGraph.StateStore.Storage` | `Atelia.DurableGraph.Storage` |
| `Atelia.DurableGraph.StateStore.Serialization` | `Atelia.DurableGraph.Serialization` |

全限定名替换先处理两个长前缀，再处理短前缀。
`EventHistoryRepository/Session`、`StateModelRegistry`、`SchemaStore` 等仍在持久化包，使用 `.Persistence`。

主程序集内还做了 namespace 分组：

- 根 `Atelia.DurableGraph`：领域属性、`IDurableObject`、`ObjectId`、`UpgradeContext`、三个登记接口以及用户配置/异常类型。
- `Atelia.DurableGraph.Schema`：`DurableSchema`、`DurableFieldInfo`、`TypeExpr`、`TypeTag`、布局描述等。
- `Atelia.DurableGraph.Runtime`：`StateModelBinding`、其他 binding、Capture、状态操作与容器 reader 等。

这两个子 namespace 仍属于主程序集，无须额外包。完整分类见[工单 §2](0071-assembly-namespace-implementation-work-order.md#2-确定的组织映射)。
`Atelia.DurableGraph.Generated`、Family/DTO 名、应用登记 facade 的合同保持。
保留业务 SchemaId、版本、字段 ID 和 accepted `.dgschema`；纯组织适配不需要业务升版、重写 history 或改变领域模型。
模型库和宿主一起重编译，不依赖旧 DLL 的直接二进制兼容。

## 3. DramaBoard 已验证的五处改动

隔离验收使用 DramaBoard `30cafe0ed075281bf424e19d71ae00d845cfeac9`。实际差异只有下表五文件；
下游应先记录当前 Git 状态并核对源码是否已前进，再应用对应修改。

| 文件（相对 DramaBoard 根） | 修改 |
|---|---|
| `src/FirstBoard/FirstBoard.csproj` | 直接包引用 `.StateStore` → `.Persistence` |
| `src/FirstBoard/Persistence/FirstBoardOccurrenceHistory.cs` | using 改为 `.Persistence` |
| `tests/FirstBoard.Persistence.Process/Metrics.cs` | using 改为 `.Persistence` 和 `.Storage` |
| `tests/FirstBoard.Persistence.Tests/ContactFloorPersistenceTests.cs` | using 改为 `.Persistence` |
| `tests/FirstBoard.Persistence.Tests/FirstBoardPersistenceTests.cs` | using 改为 `.Persistence`，增加 `.Runtime` 以使用 `StateModelBinding` |

可直接查看本机精确 diff：
`E:/repos/Atelia-org/durable-graph/obj/db071-downstream/migration.diff`。
Kernel/Spatial/FirstBoard 的业务逻辑、Generated 登记 facade 和 64 份 accepted history 未改。
这些结果来自隔离副本，正式 DramaBoard 工作树未由 DurableGraph Agent 修改。

## 4. 在 DramaBoard 根验证与试用

```powershell
$feed = 'E:/repos/Atelia-org/durable-graph/obj/db071-new/feed'
$version = '0.0.0-db071.20260912.1'
$properties = @(
    "-p:DurableGraphPackageSource=$feed",
    "-p:DurableGraphPackageVersion=$version",
    '-p:DurableGraphSchemaHistoryMode=Verify'
)
dotnet build DramaBoard.slnx -v:q @properties
if ($LASTEXITCODE -ne 0) { throw 'DramaBoard build failed' }
dotnet test tests/FirstBoard.Persistence.Tests/FirstBoard.Persistence.Tests.csproj --no-build --no-restore -v:q @properties
if ($LASTEXITCODE -ne 0) { throw 'Persistence regression failed' }
```

后续 `dotnet run` 等会重新构建的命令也传入相同属性，或执行上述构建的输出，避免重新用回旧默认 pin。
检查实际还原版本/输出 DLL 来源，以及 accepted history 文件集和字节保持，再开展业务场景试用。
反馈带上包版本、操作步骤、错误/诊断及是否仅在冷重开或 pending 恢复时出现。

已有验证：新旧隔离副本均 solution build 成功，各 28 项持久化测试通过（含 10 个冷进程案例），
每侧另跑独立 create/open；64 份 history 不变，九个 DLL 与真实包资产 hash、宿主解析路径核对通过。
框架另通过 2,668 项产品测试、八组包 probes，以及旧包写入→新包冷开/续写→另进程冷读的组织兼容见证。
完整证据见[DB-071 验收记录](0071-assembly-namespace-validation.md)。
