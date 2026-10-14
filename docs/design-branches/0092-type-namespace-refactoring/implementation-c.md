# DB-092 C：固定员工命名收尾

> 源码基线 `49ac090`（A／B 已限定验收）。C 源码与根测试已验收，生成材料与真实旧／新包存档资格待验。
> 问题：固定员工 CLR 名与 namespace 收尾后，是否保持 V1 布局、编号和旧存档的准确内容／来源？

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
根构建与相关 tests 通过；真实旧 G 写入，新 G 冷开／续写／再冷开，覆盖 shared/private/meta 与目录，完整旧帧地址／内容保持。
同一 runtime 的 HistoryVersion 变化不替代旧／新包验证。

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

## 生成与存档资格

旧 G `0.0.0-public-history-e2e.20261014082432.4704` 的十包、源码、provenance 已冻结；生产输入与基线匹配。
新 G 将使用已提交的 C 生产源码与同一准确 S `0.1.3-dev.20261014.g758cebf`。
Family DTO/body 与持久 manifest/history 要求逐字节保持；当前 CLR glue 只允许明确 FQN 映射。
生成器按 CLR QualifiedName 排序，员工 projection 块从 P0C/P0D/P0F/P0H/P0T 重排为 P0D/P0F/P0H/P0C/P0T，
按 schema 对应区段比较；不排序任意代码行，也不改变生成器机制。
真实旧／新包和完整旧帧结果待执行后以终态证据替换本段。
