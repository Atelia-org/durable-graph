# DB-092 C：固定员工命名收尾

> 源码基线 `49ac090`（A／B 已限定验收）；源码提交 `c1a992d`。C 的源码、根测试、生成材料与当前新包故事均已限定验收。
> 问题：固定员工 CLR 名与 namespace 收尾后，是否保持 V1 布局、编号和当前保存／恢复的准确内容／来源？

## 范围与验收

采用 [方案 §3.2／§8](README.md)的七个映射：

| 基线名称 | 目标名称 |
|---|---|
| P0CatalogRecord | SchemaCatalogRecord |
| P0DescriptorRecord | LayoutDescriptorRecord |
| P0FieldRecord | LayoutSlotRecord |
| P0TypeExprRecord | TypeExprRecord |
| P0HeadLocator | ObjectHeadLocator |
| P0EmployeeDefinitions | BootstrapDefinitions |
| P0FixedBindings | BootstrapBindings |

两个员工／固定绑定文件进入既有 `Atelia.DurableGraph.Persistence`，收完旧阶段 namespace。
文件路径、程序集、访问级别、IVT 与公共入口保持。
SchemaId `P0C/P0D/P0F/P0T/P0H`、V1、FieldId、字段类型、固定表编号、Family 宿主与 typed ABI5 保持。

最小可观察验收：批准映射之外无算法、协议或校验变化；V1 layout、固定表、Family DTO/body 与 history 对比通过；
根构建与相关 tests 通过；当前新 G 从空库保存、冷开／续写／再冷开，覆盖 shared/private/meta 与目录。
用户明确当前没有旧数据兼容需求，实验存档可重置后重生成；本片移除旧包迁移必经验证，不新增兼容壳或常驻迁移工具。

此片不推进 D 公开名称或 Generator 入口、E 程序集与文件组织，不扩张 bootstrap 发现能力或引入兼容壳。

## 源码与根验证

127 份 C# 中仅 113 处七型标识改名、两份 namespace 和对应 import／alias 适配。
执行审计与从 Git `49ac090` 直接取原文的独立复核均通过；独立核 341,828 个基线 token，
批准映射之外无可执行差异；全部 literal／数值／raw fixture 原样保持。
仅两处必要说明改准：员工为当前默认 meta DTO 输入、有限 bootstrap 表不查询持久 catalog。
Generator／Build／Shared、项目配置和既有 accepted history 未编辑；旧历史测试类名及文件路径保留。

| 项目 | 实际结果 |
|---|---|
| 固定表与目录 | 八项顺序保持：string、Catalog、Descriptor、Slot、List<Descriptor>、List<Slot>、TypeExpr、List<TypeExpr>；P0H 不在 root 固定表，仍为三个 readonly UInt32 的 inline 地址值 |
| 根构建 | Release、准确 package mode，零警告／错误 |
| 根测试 | 2690/2690，零失败／跳过：Persistence 893、Serialization 133、Storage 36、Runtime/SG 1628 |
| 公开 API | 四程序集导出类型与 public/protected 成员／签名／约束／默认值／常量共 1359 行，逐字节保持；不使用改名逆映射 |
| 两个源码 host | MetaBootstrapCrashProbe、ListDeltaReplayProbe 在准确 pin 的 source mode 和隔离 ArtifactsPath 各编译零警告／错误；未重跑 Kill 或性能实验 |

公开 API 清单 SHA256：`ED342428E1E1E068C051EA3DAC8661C2314D5E4F35803C0A4F7AF7024FABE9A5`。
源码审计与根输出工件在忽略目录 `.artifacts/db092-c/`，不新增常驻验证工具。

```powershell
dotnet build DurableGraph.slnx -c Release --no-restore -m:1 -nr:false -p:UseStorageSources=false
dotnet test DurableGraph.slnx -c Release --no-build --no-restore -m:1 -nr:false -p:UseStorageSources=false
```

## 生成与当前包资格

准确 G：`0.0.0-db092-employee-e2e.20261014085837.22868`；生产源码 `c1a992d3651653eb986e75bf7397579963f5cd18`。
准确 S：`0.1.3-dev.20261014.g758cebf`，源码 `758cebf8ba82788ba91435d34fd484dff32095fd`。
一次受控 pack 生成十包 feed；188 个生产输入在 pack 前后、当前工作树和提交中的字节匹配。

| 有限验证 | 实际结果 |
|---|---|
| 员工 SG | 两侧实际包 SG 编译真实员工声明，四个 hints 和 diagnostics 相同；零错误／警告，五条 hidden unused-usings 相同 |
| 持久生成材料 | 五个 Family 的十个 V1 DTO/body 区段原始字节相同；export／candidate／reference 三份材料相同；实际 Build Publish／Verify 的五份 canonical history 文件名与字节相同 |
| 当前 CLR glue | 只接受七个 CLR 名与 namespace 映射；projection 块按 CLR QualifiedName 从 P0C/P0D/P0F/P0H/P0T 重排为 P0D/P0F/P0H/P0C/P0T，逐 schema 对应区段比较；Family／登记顺序保持，不排序任意代码行 |
| 实际包固定布局／body | 八个编号与完整 V1 layouts、30 对典型 Base/Delta 样例相同；P0H 的三个 UInt32 inline 槽、四个数字地址往返和目录 Base/Delta 保持 |
| 当前 Split 门禁 | 同一新 G 从空库运行五个独立进程；shared/private/meta／目录、Upgrade／Fork／Pair、准确 S、冷开与续写通过，原 CS0122 反例／外部派生正例保持 |
| 当前 Meta 门禁 | 同一新 G 从空库运行四个独立进程；有限员工 Schema 闭包、List／Array／Dictionary、九对象真实 Delta 与冷开通过 |

独立工作包 audit 与主线程复核均通过：十包字节及实际 runtime／SG／Build 来源准确；Split 50 个实际加载图像匹配包，
三份 consumer assets 是准确 G／S 的 PackageReference closure、无 project 型依赖，Split 3／Meta 4 份 accepted history 保持。
准确新包的 1359 行公开 API 清单仍与基线逐字节相同。

工件与固定 SHA256：

- 员工生成：`.artifacts/db092-c/employee-equivalence/result.json`，`77F079741C7AB70AE427675443A12B5785B1634BE19081ACD60A13719FE30EAA`。
- 当前包：`.artifacts/db092-c/new-run-20261014085837-22868/current-qualification-result.json`，`8D00CA253EBBF0DF8FFEA3E8FE58F9BC57CC41698E0B6C629659705D258B3472`。
- 原受控 pack proof：同目录 `pack-provenance.json`，`1DF1C09AF4913B21919C8629C6BA0C1DC9CA0576EC4A7FAD0F227A9D9981C84B`。
- 主线程复核：`.artifacts/db092-c/root-pack-acceptance.json`、`root-employee-acceptance.json`、`root-current-acceptance.json`。

原 Split 门禁使用源证明 schema 适配；原 proof、188 个输入与提交字节、四个 DG 包／DLL 字段映射均有独立 evidence，
未修改或放宽门禁。适配中的时间仅表示观察已完成 pack 的时刻，不是精确 pack 退出时间。

## 维护与资格边界

按[当前原型的数据兼容约束](../../DurableGraph-target-design-v0.md#原型迭代与数据兼容)，实验存档可 reset 后重生成，不维护旧包兼容门槛。
基线包只用于本次改名生成校验；已完成的旧包探索工件留在忽略目录，专用迁移草稿未编译或执行，不授旧存档兼容。
所有新增工具均为本次忽略工件，不成为常驻产品、测试或兼容框架。
body 样例有限；数字地址往返只证明 codec 表示。公开 API 清单不穷举 custom attributes／nullable annotations。
此片未重授 Kill／OS crash／断电／性能资格，也未推进 D 公开名称与 Generator 入口或 E 组织调整。
