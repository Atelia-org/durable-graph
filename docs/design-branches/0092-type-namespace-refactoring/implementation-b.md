# DB-092 B：默认内核与执行环境命名

> 基线：A 完成提交 `d2600c8`；B 源码提交 `1563d77`。源码、根测试与同一新 G 的三个包门禁均已限定验收。
> 问题：去掉阶段／旧 backend 名称后，是否仍保同一持久化 owner、准确来源、保存／恢复语义与公开接口？

## 采用范围

按 [方案 §3／§4](README.md)采用 36 个内部类型映射：30 个普通 P0 类型进入既有 `Atelia.DurableGraph.Persistence`，
另外六个内部类型明确执行环境、视图来源、归一化图状态、可编辑恢复、对象写规划和外观操作阶段。
14 份普通内核文件改变 namespace；文件路径、程序集、PackageId、ProjectReference、IVT 和访问级别保持。

配套成员按声明语义处理：

- 执行能力环境用 `CreateEnvironment`，仍保无参 reader `Snapshot()` 的字典复制，以及 catalog／decoded 的 `Snapshot`。
- `StoredGraphRevision` 的 head map 统一为 `ObjectHeads`，移除同值 `Heads` alias；DTO 与归一化目录的 `Objects` 不变。
- 来源入口用 `ReadStoredGraph`、`FromCapturedCandidate`、`PrepareGraphInstallation`、`RequireStoredSource`、`ValidateStoredSource`。
- façade、分支保存会话和 owner 的内部字段／方法收回 backend 标签；`OperationStageObserver` 与 `SaveStageObserver` 保原层次和 hook 位置。
- 仅改七处已核白名单反射字符串：六个 configuration 字段名和 ImmutableLeaf 的一个环境方法名。

固定员工五型、`P0EmployeeDefinitions`、`P0FixedBindings` 仍保旧 CLR 名／namespace；员工声明文件完整字节未变。
FixedBindings 只适配所引用环境类型／方法。SchemaId、V1、FieldId、固定表序号、Family 宿主、ABI5、magic、tag 与枚举数值保持。
没有推进 C 的员工迁移、D 的 public／Generator 入口变化或 E 的程序集／文件组织。

必要说明改正：默认核心不再写作实验 witness；薄 façade 转发保存，GraphResources 仍是唯一 pending／backing／fault owner；
ObjectWritePlanner 只提供对象写计划，producer 负责完整 source membership／Remove。此片没有算法、格式、默认值或校验调整。

## 源码与运行验收

| 项目 | 实际结果 |
|---|---|
| 源码差异 | 193 份 C#；36 type、92 个唯一标识映射和七类歧义成员定位。Roslyn 辅助定位 629 处引用，人工复核具体所属类型 |
| 独立审计 | 从 `d2600c8` 独立取源码，核 423,397 个基线 token；批准映射、namespace/import、同值 alias 删除之外无额外可执行差异。仅七个反射 literal 白名单变化，wire／exception 文案及所有常量保持 |
| owner／来源 | 复核 façade borrowed/owned 与 Dispose、唯一 pending owner、view/source 校验、完整 head/H 与 Event/State 安装边界，无阻断 |
| 根构建 | Release、准确 package mode，零警告／错误；最后 fixture 注释校正后根增量构建亦零警告／错误 |
| 测试 | 全套 2690 项通过：Persistence 893、Serialization 133、Storage 36、Runtime/SG 1628；零失败／跳过。最后仅恢复 raw C# fixture 的原注释，重新构建后对应冷重开测试另通过 1/1 |
| 公开 API | 四个生产程序集的 public/protected 类型、签名、约束、默认值与常量清单共 1359 行，新旧逐字节相同，不使用逆映射 |
| 活动源码 host | MetaBootstrapCrashProbe 与 ListDeltaReplayProbe 使用准确 pin 的显式 source mode、独立 ArtifactsPath，各自编译零警告／错误；没有覆盖根 package-mode assets，也没有重跑 Kill／性能矩阵 |

公开 API 清单 SHA256：`ED342428E1E1E068C051EA3DAC8661C2314D5E4F35803C0A4F7AF7024FABE9A5`。
token／源码审计工件见忽略目录 `.artifacts/db092-b/`；包与 API 工件见其 `qualification/`。
这些工件辅助可追溯验收，不新增常驻工具或永久测试。

主线程构建／测试：

```powershell
dotnet build DurableGraph.slnx -c Release --no-restore -m:1 -nr:false -p:UseStorageSources=false
dotnet test DurableGraph.slnx -c Release --no-build --no-restore -m:1 -nr:false -p:UseStorageSources=false
```

## 新包限定验收

准确 G：`0.0.0-public-history-e2e.20261014082432.4704`，生产源码 `1563d777cd7fe0fa59d9f797754cc60ee8cbac26`。
准确 S：`0.1.3-dev.20261014.g758cebf`，源码 `758cebf8ba82788ba91435d34fd484dff32095fd`。
受控 pack 的 production inputs 与提交匹配、无未提交生产输入，完整十包 feed 供三条现行门禁共同消费。

| 门禁 | 实际通过的边界 |
|---|---|
| SplitRepository | 普通非 IVT public consumer；准确 CS0122 反例与外部派生正例；V1／V2 Publish、Verify；seed、upgrade、rewrite、continue、verify 五个独立 PID，保存、准确来源、增量续写与全关冷开 |
| MetaBootstrapSchema | 改名后的内部内核消费、未改名固定员工与 ABI5；seed、read、continue、verify 四个独立 PID，descriptor／Dictionary／业务 Delta 与冷开 |
| ImmutableLeaf | 当前实际包的 Family 结构资格、Fork leaf 共享／mutable 隔离／冷保存；Sibling 保守反例；Family 与 Sibling 另以包内 SG／Build 执行 Verify，accepted history 字节保持 |

独立 audit 及主线程复核均通过：十包 nupkg 的 hash 保持，包内 runtime DLL 与 cache／consumer 输出字节一致；五份 consumer assets
为准确 G／S 的 PackageReference closure，无 project 型依赖；实际 SG／Build／props／targets 来自包。
主线程另核 Split 的 50 个实际 loaded assembly 图像及五个进程 evidence，accepted history 为 Split 3、Meta 4、Family 2、Sibling 1 份。
准确新包的 1359 行公开 API 清单与 A 后基线逐字节相同，SHA256 仍为上文值，不依靠名称逆映射。

终态工件目录：`.artifacts/db092-b/qualification/run-20261014T082801030Z-35668/`。
资格结果 `qualification-result.json` SHA256：`8D828DF0ADCB1BC759748BC60145BF2D34EF121FE3A19868ACEB30F027274351`。
主线程复核记录为同目录 `root-acceptance.json`；受控 pack provenance SHA256：`8C5AFE8863D64F3D4A22A2F85B827D5DAE475889A84AFC245692F9B095DA461D`。

首轮控制器在原子覆盖结果文件时遇到 Windows AccessDenied，尚未启动产品 consumer。保留其失败证据，
随后在新目录复用同一 G／feed／provenance，三个控制器均正常退出；未重 pack 或修改门禁。
隔离探针复现读取句柄阻止覆盖，未确认首轮具体持有者；尝试摘要保存在同目录 `attempts.json`。

## 交付边界

本结果限定于当前 G 的现行消费故事，不承诺旧／新包二进制兼容或 C 员工迁移。
公开 API 清单覆盖导出类型、public/protected 成员、签名／约束／默认值／常量，未穷举 custom attributes 或 nullable annotations。
Immutable 的来源证据为包／cache／输出字节及隔离启动，不具备 Split 的进程内 LoadedAssemblies 遥测。
默认开关保持，已退出 runner 未恢复；source 构建与包消费资格分开。
源模式 crash host 与 List replay 的直接内部调用已取得编译资格；此片不自动重授 ProcessCrashOnly、性能或断电资格。
