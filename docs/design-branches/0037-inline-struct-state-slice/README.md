# DB-037：inline struct 的 exact Schema、嵌套状态与增量保存

> 文档整理：2026-10-14；来源为 Git `8ef9fca` 中的 `docs/design-branches/0037-inline-struct-state-slice.md`，正文保留当片合同与历史验收含义。
> 阅读范围：非泛型 inline struct 的 G0–G4 证据见 §6；不把当片未含泛型/数组及格式版本视为今日限制，组合能力由 DB-038/043 等后继扩展。
> 当前能力与施工焦点见 [PROJECT-STATE](../../../src/PROJECT-STATE.md)；旧稿中的“下一步”不自动成为当前工作单。

> 状态：Chosen / Implemented — 2026-09-07。G0–G4 已实施并验收；实际合同与证据见 §6。
> 核验源码基线：`8d84f3b`（DB-036 后的 Probe 归档）；当前能力从
> [PROJECT-STATE](../../../src/PROJECT-STATE.md)进入，长期约束见[目标设计](../../DurableGraph-target-design-v0.md)。

## 1. 问题与选择

在已有同实例 GraphSession Commit、exact DTO 冷读及单对象升级基础上，能否支持自定义复合值，
同时保持嵌套布局、引用身份、Schema 历史和融合 Delta，而不增加另一套对象生命周期？

推荐先做 **非泛型 inline struct 的完整纵向分片**，包含已有标量、嵌套 struct、string 与 durable class 引用。
最小可观察成功标准：World 的 struct 字段中保存共享/循环引用，经历连续提交、冷重开、嵌套布局升版及再次提交；
struct 无独立对象行，历史按旧布局读，新模型按显式 Upgrade 恢复，升级后 owner 强制 Base。

主代理与独立设计审阅对候选比较后收敛如下：

| 候选 | 本轮收益与新增机制 | 排期建议 |
|---|---|---|
| inline struct | 直接补全值组合；复用现有引用与会话，仅新增 exact inline 依赖和递归静态操作 | 本轮 |
| 有限数组对象 | 增加对象 identity、shape、冻结内容、分配及元素绑定；已有 ref 循环只是其中一部分 | struct 后优先重新评估，届时同时考察标量、引用和复合值元素 |
| 自定义泛型 | 涉及定义/实参身份、领域参数与 DTO 表示参数分离、历史闭合及缓存 | 单独设计，不与首个复合值分片合并 |
| branch/Reset、恢复增强、性能 | 正常单 head 会话已形成消费者闭环；这些仍有独立问题和重访条件 | 按路线图真实需求推进 |

只做标量 struct 可缩小首批代码，但容易推迟发现递归引用遍历和恢复的关键缺口；
因此本片分闸门施工，终点包含现有两种引用槽，不以“只会保存坐标”宣称复合值闭合。

## 2. 现有接缝与范围

下表保留开工前的接缝定位；完成后的实现与验收见 §6，现状导航见 PROJECT-STATE。

| 当前实现 | 本片所需增量 |
|---|---|
| [DurableTypeAttribute](../../../src/DurableGraph/DurableTypeAttribute.cs) 只允许 class；[SG](../../../src/DurableGraph.Generator/DurableSchemaGenerator.cs) 拒绝 struct 字段 | 增加显式标记的 struct 类型路径，与 class 的 DurableBase 继承规则区分 |
| [DurableFieldInfo](../../../src/DurableGraph/Schema/DurableFieldInfo.cs) 只有 tag 与 nominal target；[DurableSchema](../../../src/DurableGraph/Schema/DurableSchema.cs) 只有 exact BaseSchema | 增加最小 Schema kind、exact inline 字段依赖及完整依赖校验 |
| [Ancestry](../../../src/DurableGraph.Generator/DurableSchemaGenerator.Ancestry.cs)、[Build history](../../../src/DurableGraph.Build/SchemaHistoryTool.cs)、[SchemaStore](../../../src/DurableGraph.Persistence/SchemaStore.cs) 只解析 exact 祖先链 | 扩为 base + inline 依赖 DAG，nominal 边不进入此闭包 |
| [GeneratedState](../../../src/DurableGraph.Generator/DurableSchemaGenerator.GeneratedState.cs) 原语/ID DTO、Base/Delta、refs visitor | 嵌套 DTO 及递归静态 body/Capture/VisitReferences |
| [StateModel 生成](../../../src/DurableGraph.Generator/DurableSchemaGenerator.StateModel.cs) class Normalize/Allocate/Hydrate | owner Upgrade 保持；增加当前值的 ref 恢复 helper |
| [StateReaderBinding](../../../src/DurableGraph/Runtime/Binding/StateReaderBinding.cs) 与准备管线要求 unmanaged DTO | 嵌套 DTO 的叶子仍为标量/UInt32 ID，保持该约束，不把领域引用复制进去 |

推荐支持：同一编译中的顶层、非泛型、非 record 的 `partial struct`，包括 `readonly partial struct`。
**用户直接在 struct 声明上添加 `[DurableType(schemaId, version)]`，与 class 一样显式纳入 SG 管线。**
它有自己的 SchemaId、版本及 Schema history；不需要先被某个 class 使用才取得 Durable 类型资格。
字段继续明确标注 DurableField 或 Transient，支持 private/readonly 持久字段。struct 不继承 DurableBase。

以下是已支持的用户代码形状：

```csharp
[DurableType("Position", 1)]
public partial struct Position {
    [DurableField(1)] public int X;
    [DurableField(2)] public int Y;
}

[DurableType("Character", 1)]
public partial class Character : DurableBase {
    [DurableField(1)] public Position Location;
}
```

本文“嵌套值/嵌套布局”指 `Character.Location` 的值及其 DTO/body 内含 Position 的状态，
不是把 `struct Position` 的 C# 类型声明写进 `class Character`。本片反而限定顶层声明，
CLR nested type 尚不在范围内。后文“历史 DTO 宿主”讨论的是 SG 生成辅助类型的位置，
也不改变用户直接标记 struct 的入口。

不包含：ref struct、record struct、开放或自定义闭合泛型、enum/nullable/decimal/native int 扩充，
完整数组对象、BCL 容器、boxed value identity、跨程序集模型、一般 TypeCodec、独立 struct ObjectId/Model，
自动递归 Upgrade、新发布/回收/并发合同。已有 class 支持范围保持，拒绝不支持类型，不自动序列化其字段。

## 3. 推荐设计

### 3.1 Schema：exact 值依赖与 nominal 引用分开

建议增加最小 `SchemaKind`：`ReferenceObject` / `InlineValue`（名称可在 G0 调整）。
kind 参与相等性和持久定义，同一 SchemaId 的不同版本不得跨 kind；类型性质改变采用新 SchemaId。
InlineValue 不允许 BaseSchema，class 的 BaseSchema 必须为 ReferenceObject。
family kind 一致性在 SG 全部 current/history、Build 合并材料、SchemaStore 注册及重放中跨版本检查，
不能只依靠相同 `(SchemaId, Version)` 的 equality。

新增 `InlineValue` 字段 tag，单独携带 exact struct Schema；不借用 `TargetSchemaId` 表达版本。
逻辑模型持有完整 immutable exact Schema，history/SchemaStore 记录该定义的 key，并验证可解析闭包。
字段参数必须互斥：普通标量无 target、DurableReference 只有 nominal family、InlineValue 只有 exact inline target。
InlineValue 不能作为根/对象记录、对象 reader/model 或 Base 类型头目标；nominal 引用解析出的对象须为 ReferenceObject。
不因检查 nominal 引用而要求注册其目标闭包，保留自环/互环初始化能力。

```text
Derived@2 --exact base--> Owner@3 --exact inline--> Links@2 --exact inline--> Position@2
                                                   |
                                                   +--nominal ref--> Owner（不绑版本）
```

Position 升版要求含它的 Links、Owner 和受影响 Derived 显式升版。SG/history 检查冲突，
不自动改版本号或生成业务 Upgrade。共享 exact 子节点合法；所有 exact 边构成有界 DAG，
missing、冲突、环、错误 kind 和过深路径在生成/注册/持久重放各入口拒绝。
`Owner → inline Links → nominal Owner` 合法，不是 exact 环。
复用现有最大深度防护并扩展到混合路径；先校验闭包，再做可能递归的结构比较，避免坏输入造成栈溢出。
共享 exact 依赖去重展开，遇到同 key 的另一定义仍检查冲突；避免 DAG 被反复按树展开，
最大深度限制不能代替这项检查。不为此引入跨会话缓存框架。

格式建议：保留现有 type tag 1–15 的含义，新增 inline tag；Schema history/manifest 与 SchemaBatch
显式升格式版本，新增 kind 与 exact operand。新读取器严格识别旧 class-only 格式，将其 kind 解释为 ReferenceObject；
已接受 `.dgschema` 不重写，旧记录缺失的 exact 依赖不能由 current candidate 偷补。
所有新增写入使用新格式，包括 class-only 的新 Schema；同一 SchemaStore 严格重放旧/新帧，
不按内容临时猜选写入版本。等价注册/已有 exact history 继续幂等而不重写。
不建设通用格式迁移器。新格式的具体字节/文本语法在 G1 以独立 golden 冻结，
旧格式兼容仅覆盖当前已接受格式，不恢复已拒绝的 `.dgsnapshot` 等格式。
StateRevision wire v3、Base 对象头及发布日志不因 inline 值而增加版本字段或新对象 kind。

### 3.2 DTO、Capture 与领域恢复

owner DTO 的复合字段具有 exact 版本的嵌套 readonly DTO 类型，不持有领域 struct 或可变领域引用。
叶子为已有标量或 UInt32 ObjectId，递归保持 unmanaged。struct 自身不登记对象、不拥有保存策略或提交基线。

Capture 静态调用 struct helper，递归传递同一个 CaptureContext；其中的 string/durable 引用仍调用
现有登记入口。所有 refs-only 操作递归访问嵌套 DTO，继续用完整 stored/current 目录校验、求可达与恢复。
`World → struct → World`、多个 struct 值引用同一 child、同值不同实例的非空 string 都遵守既有身份合同。
struct 字段值变了是 owner 的变化；child 内容变而引用 ID 不变，不产生 owner 的伪 Delta。

当前 struct 的恢复 helper 使用 `ref TDomain` 槽位；已知字段调用静态 helper，未来数组元素可复用。
由 `default(TDomain)` 临时值开始，不执行用户 struct 构造器/字段初始化；递归填充持久字段后写回目标槽。
readonly 字段使用声明类型准确匹配的 UnsafeAccessor；只在恢复未交付值时使用。
Transient 恢复为默认值，业务仍在完整 World 交付后自行重建。
纯 DTO body 用 `in DTO` / typed 返回值即可，必要的 ref 读取桥接通过整体 DTO 赋值实现，
不为对称签名引入通用 visitor 或把 readonly DTO 改成逐字段可写。

### 3.3 历史 inline DTO 不依赖当前领域 struct 宿主

当前 DTO 嵌在 `Domain.__DurableState.Vn`。直接照搬会产生缺口：owner V2 删除某字段及该 struct CLR 声明后，
owner V1 的 reader 失去嵌套值类型宿主，即使所有 `.dgschema` 仍在。

推荐由 SG 根据 owner 历史所需的 exact inline 闭包生成编译内共享的 DTO/body helper，
其身份由 SchemaId/Version 确定，不依赖当前领域 CLR 名称或存在性。只生成实际需要的闭包，
不建立运行时 retired-value 注册框架。当前 struct 的 Capture/ref 恢复桥接到同一表示。
多个 owner 使用同一 exact inline Schema 时共享表示，避免各自生成不相容的 DTO 类型。

G0 要验证可供用户 Upgrade 使用的可读入口：优先给嵌套 DTO 提供可访问的强类型构造器，
让 `next = new V2(..., new(...));` 通过 target-typed new 构造历史嵌套值，不必书写内部编码名称。
历史输入由 owner DTO 的字段类型自然暴露；如确有可读性缺口，再增加 retained owner 侧的小型工厂。
不能把唯一构造入口放在当前 struct 上：删除该 CLR 声明后，仍保留的 owner V1→V2 升级函数也必须能编译。
具体 helper 名称及访问级别在 G0 编译验证、G2/G4 真 SG/包验证，不能只解决 reader 而丢失中间版本的 Upgrade。
这条仅针对布局可从历史闭包生成的值 helper：已删除引用对象族仍适用 DB-036 的可执行能力保留规则。

### 3.4 Base 与融合 Delta

Base：按既有 base-first/声明段 FieldId 顺序，在复合字段处静态调用 nested Base body。
嵌套值不重复写 SchemaKey、ObjectId 或长度；owner exact Schema 已决定递归解释。

Delta 推荐保持嵌套：每个 inline 字段占 owner 位图的一位；子 PrepareDelta 递归返回 HasChanges/owned bytes，
父层只依据该结果置位，并在置位处直接写子 Delta bytes。叶子只比较一次，浮点按位、引用按 ID；
不用领域 Equals，不另外做“相等性 → 估算 → 重编码”三遍。

| 方案 | 取舍 |
|---|---|
| struct 变化则替换完整嵌套 Base | Apply 简单，但须另做比较或丢弃已准备的 Delta，重复扫描/编码 |
| 嵌套融合 Delta（推荐） | 直接组合现有 PreparedDelta，增加子缓冲及递归 Apply；临时内存可测量后优化 |
| 将全部叶子展平进 owner 位图 | 可少一些中间缓冲，但另建全局 leaf 槽映射，削弱值 body 组合与独立演化 |

精确 Schema 决定子 Delta 的读取范围，不额外保存子长度。Apply 必须验证每层 padding bits、
截断和叶子的 redundant change；尤其 **owner 复合位为 1、子 Delta 却无任何变化时必须拒绝**。
当前顶层 Apply 允许全零位图，不可直接把它当作 nested nonempty 校验。
推荐内部 Apply helper 输出是否发生变化，或使用 requireChanges 参数；不在 Apply 后重新比较整个嵌套值。
顶层全消费仍由原读取边界负责，子 reader 不对剩余 sibling bytes 调用 EnsureFullyConsumed。
零字段 struct 没有 Base 内容且永不触发 owner changed bit。

策略仍消费 owner 对象完整 PreparedBase/Delta 的实际大小及原 envelope；struct 不参与独立 B/D/H 排名。
允许少量 TODO 记录递归临时缓冲、复制的性能机会，本轮不做 pooling、指纹或 Frame cache。

### 3.5 升级属于保存对象

stored owner DTO 先按 exact 旧 inline 布局完整应用 Delta，再交给 owner 的相邻版本 Upgrade。
用户显式构造新嵌套 DTO，可以调用普通强类型 helper；框架不先自动升级 struct、再升级 owner。
因此没有独立 struct Normalize 注册或新的升级调度顺序，Upgrade 不会读取其他对象或分配 ObjectId。

升级后的完整 source DTO 仍全部验证，包括 struct 深处已不可达的坏引用。
之后按 current 引用图分配领域对象、递归恢复值与引用；失败不交付半成品 World。
owner 的 exact Schema 变化沿用现有 RequiresRewrite/强制 Base；发布成功后清除重写义务，
下一次同版 Commit 可 NoChange 或 Delta。嵌套引用删边仍由完整 source − candidate 形成 Remove。

## 4. 施工顺序与验收闸门

每个闸门先读当前代码，不把下表的方法示意当成必须照抄的 API。G0–G4 构成本片完整终点；
若 G0 证据否定关键形状，修订方案后再扩大施工，不悄悄删去历史或 readonly 验收。

| 闸门 | 工作与最小验收 | 主要落点 |
|---|---|---|
| G0 生成形状验证 | 用最小生成代码等价物或受控 emitter fixture 编译两层嵌套、含引用、readonly struct/字段；ref helper 操作字段与已有元素槽；验证 default 恢复不跑构造器。后一次编译删除 struct 宿主，保留嵌套 DTO 及 owner 升级链，验证强类型构造入口仍有效 | test-only 编译机制见证；先冻结 helper/表示映射，不等待完整 history parser，不新建程序集 |
| G1 Schema/history | 最小 kind 与 inline exact descriptor；base+inline DAG、同 key/同 family kind 冲突与缺依赖拒绝；SG/Build/store 规则一致。独立新格式 golden、旧格式读取、文件不改写、批次失败无部分登记 | Runtime Schema、SG history、Build、SchemaStore/SchemaBatch |
| G2 静态值操作 | recursive Capture/Base/融合 Delta/Apply/refs/ref 恢复；padding、冗余子 Delta、截断、尾随拒绝；NaN payload、正负零、零字段、两层嵌套。生成代码断言已知槽没有 Type 查表/ValueSlotCodec 分派 | GeneratedState/StateModel、生成测试 |
| G3 图与会话 | World → struct → child/World 循环，shared/nonintern string；连续 Commit 原实例、NoChange、局部 Delta、冷重开。Capture 后领域变化不改候选，失败不推进基线，删边 Remove 不影响旧 Revision | 真实生成图与 GraphRepository 集成 tests |
| G4 历史与包交付 | V1 Base+Delta → V2 嵌套 Schema 变更及 owner/派生显式升版 → Upgrade/强制 Base → 后续 NoChange/Delta；V3 删字段及 struct CLR，既能 exact 读旧 owner，也保留 V1→V2→V3 函数并 Load 旧 Revision。缺历史/缺 owner Upgrade/坏嵌套引用均拒绝 | PackageConsumerProbe、根 solution 与回归 |

G0 编译并执行等价生成代码，不以字符串快照代替机制验证；它只证明代码形状和 ref 恢复机制，
不证明 SG/history 已实现。G1 形成 exact 输入后，
G2 必须由真实 SG 重做这些见证，G4 再验证真实历史包链路，不留下第二套生产实现。
历史包验收同时证明：未递增 owner 或任一 exact 中间依赖版本时构建拒绝；nominal 目标单独升版不要求 owner 升版。
readonly/ref 恢复使用本片 struct 的独立及真实 SG 见证，现有 class 的 UnsafeAccessor 测试不充当其完成证据。

建议委派方式：

- 主代理掌握 kind/descriptor、helper 签名、历史能力和 Delta 格式裁决，先完成 G0 接缝收敛。
- 接缝冻结后，一位 agent 独占 Build history；一位独占 SchemaStore wire/注册测试；
  SG history 与 body 共享模型改动由同一 owner 协调，避免并行修改同一生成器文件。
- 下游编译稳定后，另一位 agent 编写真实包历史见证；独立 reviewer 检查 exact/nominal、nested Delta canonicality、
  历史宿主消失和失败基线四类不变量。主代理检视共享树 diff，集中串行 build/test/package 验收。

最终验证：`dotnet build DurableGraph.slnx`、完整 solution tests、适用的既有 PackageConsumer 回归及新增 inline 历史包见证；
不因只改嵌套值而重复运行无变化的所有 crash probe。格式与历史检查失败不能改成跳过或静默兼容。
下述账本区分本片已执行的验收与尚未完成项，不把 DB-036 的结果算作本片证据。

## 5. 结束与后续

完成后，PROJECT-STATE 更新支持范围与证据入口；长期采纳的 inline Upgrade、kind 和历史 helper 边界进入目标设计；
路线图删除本片已完成项，保留有限数组、泛型与跨程序集的剩余问题。本记录保存实际施工与验证结果。

用户本次要求为下一步泛型留准备：后续以泛型定义/实参身份、领域与表示参数闭合作为评估入口；
当前静态值 helper 与 exact Schema 闭包可复用，但不能仅凭 DTO CLR 类型推导领域类型。
有限数组仍可单独安排，届时决定 shape、rank、元素闭合与冻结内容；本片不自动进入后继实施。

## 6. 施工账本

用户于 2026-09-07 授权完整实施，并要求为后续泛型保留领域类型/状态表示分离。
本轮仍不实现泛型闭合、数组对象、独立值身份或第二套升级调度。
开工前仅有上一轮明确 struct 标注入口的文档修改，纳入本记录；源码基线 build 为零警告/错误，tests 916/916。

本轮已冻结的协作接缝：SchemaKind ReferenceObject=1 / InlineValue=2，TypeTag.InlineValue=16；
DurableFieldInfo.InlineSchema 持有完整 exact 布局，Schema 构造支持 kind。
history/manifest v2 在 version 后写 kind 行，inline field 写 exact key；SchemaBatch v2 在 row key 后写 kind byte。
新写只用 v2，旧 v1 只读；exact DAG 路径上界 256。跨模块具体格式由 golden 与负例验收。

| 闸门 | 落点 | 已执行证据 |
|---|---|---|
| G0 | [代码形状见证](../../../tests/DurableGraph.Tests/InlineStructShapeWitnessTests.cs) | 独立 test-only project 编译执行 2/2；精确 struct ref、private readonly、default 恢复、字段/元素及删除领域宿主后的完整升级链 |
| G1 Runtime | [Schema 合同测试](../../../tests/DurableGraph.Tests/InlineSchemaContractTests.cs) | exact 值依赖相等性、kind/参数互斥、200 层共享 DAG 比较/哈希、inline 无对象身份入口 |
| G1 Build | [history 工具测试](../../../tests/DurableGraph.Tests/InlineSchemaHistoryToolTests.cs) | v2 golden、旧 v1 原字节保留、闭包/深度/冲突/kind、历史缺口不由 current 补齐、批次失败无写入 |
| G1 Store | [持久 Schema 测试](../../../tests/DurableGraph.Persistence.Tests/InlineSchemaStoreTests.cs) | 混合 v1/v2 重开、base+inline DAG、原子预检、对象头/typed reader/发布 head 指向 inline 的拒绝 |
| G1/G2 SG | [生成器测试](../../../tests/DurableGraph.Tests/InlineStructGeneratorTests.cs)、[body 测试](../../../tests/DurableGraph.Tests/InlineStructBodyTests.cs) | standalone 标注、支持形状、历史宿主消失、深度/升版、静态 nested Delta golden、浮点按位、坏位图/空子变化/截断/尾随拒绝、真实 SG ref 恢复 |
| G3 | [真实生成图测试](../../../tests/DurableGraph.Tests/InlineStructGraphTests.cs) | 同实例连续 Commit、nested owner Delta、child-only、共享/循环/string 身份、Remove、失败基线、候选隔离、stored 深层缺失/错误 kind ID 拒绝 |
| G4 | [真实 PackageReference 消费者](../../../experiments/PackageConsumerProbe/InlineStructConsumer)、[运行脚本](../../../experiments/PackageConsumerProbe/Run-InlineStructProbe.ps1) | 四阶段全部通过；history 5→10→11→13，旧 hash 不变；精确值/owner/派生升版强制 Base；nominal child 独升；删除 Point/Links CLR 后 exact read 和完整 owner Upgrade 链均成功 |
| 独立审阅 | Runtime/Build/Store 与 SG metadata/body 分开审查 | 未留 P1/P2；主代理检视集成 diff，保留 class 原 Apply 两参数形状；新增 kind 范围和 v2 manifest 的旧断言按真实新合同更新 |

最终生成形状：class 的 `__DurableState.Vn` 保持原入口；inline DTO/body 放在编译内共享
`Atelia.DurableGraph.Generated.__Inline_<SchemaId UTF8 hex>_Vn` 中，表示不含领域 CLR 名称。
当前 struct 的 `__DurableState.Capture(in TDomain[, context])` 与
`Hydrate(ref TDomain, in TState, ObjectReadTable)` 桥接该表示；历史 Upgrade 用可访问构造器及 target-typed new。
嵌套 Apply 的 requireChanges 只存在于 inline helper，不给所有 class 增加重载。
所有已知成员仍静态绑定；BinaryPayloadWriter.WriteSpan 公开为不带长度前缀的既编码 body 拼接操作。

本轮明确保留的限制：DTO 仍 unmanaged；无一般泛型、数组对象、独立值 Normalize 或 boxed identity；
最大布局路径 256，Schema/State/发布日志各自的其他容量与故障模型不变。
递归 PrepareDelta 的临时缓冲与复制留有性能 TODO，尚未引入 pool/cache。

包证据（2026-09-07，产物位于忽略的 obj）：

- 新 inline 四阶段：`experiments/PackageConsumerProbe/obj/inline-struct-20260907144544-30792-7af35e26`。
- Runtime 既有回归：`experiments/PackageConsumerProbe/obj/run-20260907145057-15568`。
- StateStore 既有回归：`experiments/PackageConsumerProbe/obj/state-store-run-20260907145201-26392-483e44b6`。
- 历史引用族迁移壳回归：`experiments/PackageConsumerProbe/obj/history-capability-20260907145318-25536-7eb074be`。

最终 root solution build：零警告、零错误；完整 tests **994/994**、零跳过（TRX 前缀 `db037-final`）：
Runtime/SG 450、StateStore 286、Storage 155、Serialization 103。所有实际运行的包见证通过。
未改变 Storage wire v3 或发布格式；未修改上游 Atelia 项目。
