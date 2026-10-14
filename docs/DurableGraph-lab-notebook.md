# DurableGraph 实验与验证证据入口

> 本页只索引证据和新增的简短实验结果。当前能力见[产品工作集](../src/PROJECT-STATE.md)，
> 后续事项见[路线图](DurableGraph-research-roadmap.md)；不从旧日志中的 Next 恢复排期。

## 按问题找证据

| 问题 | 证据入口与适用范围 |
|---|---|
| Schema/history、包交付与 legacy 读取升级 | [归档实验簿](archive/2026-09-06/DurableGraph-lab-notebook.md) EXP-001–010；包回归运行入口在 [PackageConsumer](../experiments/PackageConsumerProbe/README.md) |
| sharing/cycles、logical diff、升级断边、两阶段物化 | 归档实验簿 EXP-011–014；[DB-006](design-branches/0006-flat-graph-delta-prototype/README.md)。这些是 fixture/probe 证据，不是产品通用图 API |
| 无无参构造器、private readonly 与占位对象填充 | [2026-09-07 机制验证](#2026-09-07无构造器分配与-readonly-实例字段写入)；局部 CLR 能力，不是产品 Restore |
| DTO Capture、string 身份与读取 | [DB-022](design-branches/0022-versioned-state-dto-capture/README.md)、[DB-024](design-branches/0024-reference-capture-and-reusable-object-ids/README.md)、[DB-025](design-branches/0025-string-object-decoding-slice/0025-string-object-decoding-slice.md)的验收部分 |
| raw Base、wire v2 与 exact Revision 文件重开 | [DB-026 §8](design-branches/0026-raw-base-object-content-slice/README.md#8-实施合同与验收账本) |
| 同版 DTO 融合 Delta 准备、历史/引用校验与包交付 | [DB-027 §6](design-branches/0027-generated-same-schema-delta-body-slice/README.md#6-本轮实施账本)；body codec，不含持久 prior 链 |
| 持久 raw Delta、exact prior 与原始重建成本 H | [DB-028 §6](design-branches/0028-persisted-object-delta-chain-slice/README.md#6-实施账本)；object-first 冷重开，typed 解释元数据仍由 fixture 提供 |
| 已准备内容到真实表示选择与可追加 Revision | [DB-029 §9](design-branches/0029-prepared-object-revision-planning-slice/README.md#9-实施合同与账本)；PrepareBase、完整 prepared rows、策略落盘/冷重开，typed 元数据与发布仍在边界之外 |
| 地址/策略选择史及可重跑技术储备 | [DB 索引](design-branches/README.md)、[Probe 导航](../experiments/README.md)；TwoLeg 与合成 payload 结果不构成产品恢复或性能保证 |

## 历史卷

截至 `54a33df` 的原实验簿已[完整归档](archive/2026-09-06/DurableGraph-lab-notebook.md)：
EXP-001–014 保存机制实验，后半部按日期记录后续存储与 codec 分片。
[归档目录](archive/README.md)同时保留当时目标稿和路线。
上述快照保留旧语境，仅修复史实或引用错误，不再同步产品进度。

## 新结果

### 2026-09-07：无构造器分配与 readonly 实例字段写入

**问题：** 无需无参构造器、private readonly 字段是否会妨碍“先分配全部对象，再填充/连接引用”？

**已有源码依据：** [NormalizedGraphMaterializationProbe](../tests/DurableGraph.Tests/NormalizedGraphMaterializationProbe.cs)
的 AllocateUninitializedPlaceholder 使用 RuntimeHelpers.GetUninitializedObject；Materialize 先建立
全部占位对象映射，再 HydrateForMaterialization。这是逻辑图 fixture 见证，不是新 DTO 产品 Restore。

**本次观察：** 讨论中在仓库外临时 net10.0 控制台项目执行
`dotnet run --project Probe.csproj --configuration Release --verbosity quiet`，输出运行时为 .NET 10.0.5，退出码 0。
输入 Node 只有 `Node(string)` 构造器，其基类只有 `Base(int)`；两类均含 private readonly int
及非零字段初始化值，Node 另含 private readonly Node 引用。先分配两个未初始化 Node，再写字段。

| 操作 | 实测结果 |
|---|---|
| RuntimeHelpers.GetUninitializedObject 分配两个 Node | Node 构造器计数 0；派生及基类字段值均为 0，非零初始化表达式未运行 |
| 匿名 DynamicMethod，restrictedSkipVisibility=false，发出 stfld | private readonly `_value` 写为 37 |
| 匿名 DynamicMethod，restrictedSkipVisibility=true | 同样写入成功 |
| 关联 Node owner 的 DynamicMethod，skipVisibility=false | 同样写入成功 |
| UnsafeAccessor 写派生/基类 private readonly int | 值分别为 41、42 |
| UnsafeAccessor 连接两个 readonly Node 引用 | `ReferenceEquals(a, a.Next.Next)` 为 True，构造器计数仍为 0 |

关键访问器形状如下，实际字段绑定使用其声明类型；调用方在未交付实例上通过返回 ref 赋值：

```csharp
[UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_baseValue")]
static extern ref int BaseValue(Base instance);

[UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_next")]
static extern ref Node? Next(Node instance);
// Next(a) = b; Next(b) = a;
```

**官方技术依据：** [GetUninitializedObject](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.compilerservices.runtimehelpers.getuninitializedobject?view=net-10.0)
提供未初始化实例；[UnsafeAccessor](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.compilerservices.unsafeaccessorattribute?view=net-10.0)
由运行时实现 extern static 访问器，字段访问返回 ref，只查指定声明类型，不沿继承链查找。
readonly 写入、基类字段及循环的结果来自上述实测，不能仅由 API 简介推导。

**边界：** 这是 .NET 10.0.5 下单程序集、非泛型领域类和实例字段的机制见证，未验证 AOT、其他
运行时、跨程序集或所有类型组合；不包含 static readonly。DynamicMethod 的三个模式在本机
均成功，不据此将 restrictedSkipVisibility=false 写入私有字段当作跨运行时承诺。
实验不是从持久 DTO 自动生成 Hydrate，也未验证完整 Restore 的失败隔离；不得把成功写入局部字段
当作产品支持已经完成。本轮文档保存此前实际观察，未运行产品 build/tests。

**采纳位置：** [目标设计](DurableGraph-target-design-v0.md#恢复transient-与宿主边界)记录支持范围及
GetUninitializedObject + SG Hydrate/UnsafeAccessor 方向；[路线图](DurableGraph-research-roadmap.md#2-已采纳方向中的未完成能力)
维护后续施工，不以生成 DTO 构造器逐个普通 new 替代两阶段恢复。

### 2026-09-06：文档工作集治理

将当前事实、长期目标和未完成工作分别集中到三个核心文件；旧长稿归档，
设计记录与 Probe 按任务导航。产品代码与可执行实验保持原样。
独立审查保留了混合文档中的未完事项，并隔离了旧 Probe 的 no-ID-reuse 假设。
本次为文档变更，验证范围是语义对照、相对链接、归档保真与 Git diff，不重复产品测试。

后续若已有施工记录保存结果，本页只增加必要的证据入口；没有独立记录的小实验可在此写
“问题、观察、边界、来源”四项，结束后将影响当前工作的结论更新到其唯一维护位置。
