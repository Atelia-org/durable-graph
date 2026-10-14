# DB-031：持久 Schema 注册与 Base 类型引用

> 文档整理：2026-10-14；来源为 Git `8ef9fca` 中的 `docs/design-branches/0031-persisted-object-type-envelope-slice.md`，正文保留当片合同与历史验收含义。
> 阅读范围：当片 Schema 注册、Base 引用和冷读桥接证据见 §8；旧 SchemaStore/Base 格式与严格坏尾合同已由 DB-045/046、DB-086/087 后继替换。
> 当前能力与施工焦点见 [PROJECT-STATE](../../../src/PROJECT-STATE.md)；旧稿中的“下一步”不自动成为当前工作单。

> 状态：Chosen / Implemented — 2026-09-07 用户已授权按讨论结果实施，施工合同见 §8。
> §1–7 保留设计讨论的理由与边界；接口、恢复和本片实施事实以 §8 为准。
> 提案修订时的产品基线为 `6439ee5`（DB-030）；上一版提案提交为 `5fdc371`。

## 1. 本次修正与目标

推荐直接建设产品级持久 SchemaStore，并让对象 Base 引用其中的 exact Schema。
Delta 不重复声明类型：整个 Base→Delta 内容链只能使用 Base 所确定的同一 Schema。

上一版“每条 Base/Delta 内联完整 Schema”不再推荐。它局部减少了寻址工作，但引入重复类型
声明、较大 payload 和一次预期会废弃的格式；持久 Schema 注册已有真实消费者，不应再绕开。
[原提案及当时理由](../../archive/2026-09-07/0031-inline-object-envelope-proposal.md) 已归档。

本片要补的是：代码声明的 Schema 经持久注册后，同一个 `(SchemaId, Version)` 永远指向同一个
完整定义；State Base 通过 exact 引用找到它，冷重开不依赖 writer 留下的描述字典。

最小可观察结果：真实文件注册 Schema 及祖先、写入引用它的对象 Base 和同版 Delta，关闭后
重开 SchemaStore/StateStore，解析 stored exact Schema 并用匹配的历史 reader 重建 DTO；
再次提供同 key 不同定义时，在任何新注册或对象追加前拒绝。

## 2. 已有事实与可复用机制

- 当时的 `InMemorySchemaStore`（已退役）已具备 exact key、完整
  Schema 相等、祖先闭包预检、等价注册幂等和冲突拒绝。缺的是持久记录、恢复与确认边界。
- 当时的 `InMemoryStateStore`（已退役）已表达“先注册 Schema，
  状态保存其 key，读取解析 exact Schema”的职责关系；其 string slot 和 boxed 字段字典属于旧实验，
  不适合作为新对象图产品 API 的骨架。
- [StoredGraphNormalizationProbe](../../../tests/DurableGraph.Tests/StoredGraphNormalizationProbe.cs)
  把历史 Snapshot 升级为 current，并设 `RequiresRewrite`；
  [GraphDeltaProbe](../../../tests/DurableGraph.Tests/GraphDeltaProbe.cs) 对该标记产生完整 Upsert，
  即使值没变也不能省略，变得不可达则只移除。
- [GraphDeltaProbeTests](../../../tests/DurableGraph.Tests/GraphDeltaProbeTests.cs) 与
  [图恢复见证](../../../tests/DurableGraph.Tests/NormalizedGraphMaterializationProbeTests.cs) 覆盖
  重写义务、失败保留、恢复后保存、共享和循环。这是内存 logical graph 见证，不是物理 Delta 链升级
  或持久发布故障已经完成的证据。
- 用户提到的 [SnapshotUpgradeShapeProbe（归档）](../../../experiments/ARCHIVE.md#snapshot-upgrade-shape "原路径：experiments/SnapshotUpgradeShapeProbe/README.md")
  主要是升级函数 `in/out` 等 C# 语言形状见证。
- 产品 [PreparedObject.BaseOnlyUpdate](../../../src/DurableGraph.Persistence/PreparedObject.cs) 与
  [ObjectRevisionPlanner](../../../src/DurableGraph.Persistence/ObjectRevisionPlanner.cs) 已接受
  “旧对象无合法 Delta，必须 Base”；无需 D/H，不消耗可选 Base 预算。
  新 DTO 的升级、加载基线导入及重写义务传递尚未接通，当前 CaptureSession 跨 Schema 仍拒绝。

## 3. Base 定义整条内容链

推荐将以下规则定义为产品合同，而非依靠每条 Delta 再做一份类型声明：

```text
Base(V1) → Delta(V1) → Delta(V1)
           按 V1 完整重建
                    ↓
               完整 V1 DTO
                    ↓ Upgrade
         current V2 DTO + RequiresRewrite
                    ↓ 下一次显式保存，若仍 live
                 Base(V2) → 后续 Delta(V2)
```

关键顺序是先应用完旧版 Delta，再升级完整旧 DTO，不能升级 Base 后用新版 reader 应用旧 Delta。
“内存统一 current DTO”指对外交付的可编辑基线；历史解码和升级过程仍会暂时存在旧 DTO。

- 合法 Delta 必须延续 prior 内容链的 exact Schema；布局变化从新 Base 开始。
- current DTO 与 normalized baseline 相等，也不能取消旧 Schema 带来的重写义务。
- 义务属于加载/工作会话元数据，不进入领域字段或持久 DTO。新基线同时保存与持久 prior 的关系，
  不能把 normalized DTO 误称为旧 bytes 的逐字投影。
- 对仍 live 的对象映射到 BaseOnlyUpdate；不再 live 的对象移除，不因标记而保活。
- 只有确认发布 exact candidate 后才清除义务；确定未发布失败保留，发布不确定时先 reconcile。
  读取和 Upgrade 本身不回写。
- string 的 Base 使用内建类型标记；不可变内容没有对象 Delta。ID 复用的新占用者同样从 Base 开始。

不在 Delta 存 Schema 后，无法通过“Delta 自己声明的版本”检测伪造的跨版 body；它本就没有该
独立声明。正确性来自受控 producer 的同版规则、Base exact reader 及 raw prior 校验。
重复类型头也不能证明任意 callback 确实按声明 Schema 编码。
上一版“最后一条 Delta 错 Schema 头、全部 reader 零调用”的验收因此撤回，改测跨版 producer
不能生成 Delta、Base exact 匹配、旧链先还原后升级和强制 Base 的状态律。

新 DTO Upgrade/工作会话不是 SchemaStore 的前置实现要求：首片可以先用现有同版 Capture
消费者验证类型引用，后续加载分片再把上图自动串起来；不得以 fixture 的人工 BaseOnly 分类
宣称完整自动升级保存已实现。

## 4. SchemaStore 的产品职责

### 4.1 注册和冲突检查

推荐提供批量注册操作，输入本次使用的 Schema 与完整 exact 祖先依赖闭包：

1. 对已恢复的注册表及整个输入批次预检；同 key 相同定义幂等，不同字段、类型或 exact base 拒绝。
2. 全部检查通过后才追加缺失定义。批次内自己的冲突也不能变成部分注册。
3. 满足约定的注册确认边界后，安装内存 key→记录/descriptor 索引。
4. 冷重开从持久注册事实重建索引，并重新检查重复 key 一致性和依赖完整性。
5. reader 绑定时再核对 stored 完整 descriptor 与所选历史 reader；这不能代替写入时的注册冲突检查。

即使本次所有对象 NoChange，也不能跳过代码声明与已注册 Schema 的一致性检查。
正常处理冲突的方式是修正版本及需要的升级函数，或由开发者明确重建开发数据；库不覆盖旧定义，
不自动推断字段迁移，也不自动删除历史来迁就新代码。

SchemaStore 保存的是定义事实，不负责制造升级函数。新增版本的注册与是否有可执行升级路径
是两项检查；打开旧数据进行编辑前必须找到需要的合法路径，否则明确失败。

### 4.2 注册是否随 State 保存回滚

用户已采纳 MVP 采用 repository-wide、单 writer、单调积累的不可变注册表；以下限于该阶段：

- 已完成的 Schema 注册可以在后续 State 保存失败时保留，继续占用该 key。
- branch reset 不撤销 Schema 定义，同 key 一致性也不只针对某个 branch 的可达历史。
- Schema 的注册确认与 State 业务 head 发布不同；注册定义不会使任何对象成为 live。
- State 发布前必须保证引用的 Schema 已满足规定的持久化屏障；不要求两类记录回滚为同一个事务。

MVP 不增加版本化 catalog head；这不否定联合 State/Schema/Artifact Commit/Ref 下的整体回滚
和分叉。未来将重新定义 Schema 视图的可见性，不能把上述阶段性全局注册方式变成永久限制。
使用无用户类型的 StateStore 保存 Schema 集合的候选、自举及冲突作用域问题，集中在
[路线图](../../DurableGraph-research-roadmap.md#41-schemastore-复用-statestore-与联合版本视图)；不扩大本片施工范围。

首片应以现有 RBF framing 为底座，定义完整 Schema 注册批次的追加与恢复边界。
不能把“任何物理文件/残留 bytes”当作注册：仅合法格式、完整且符合恢复规则的批次有效；
未完成/torn 尾部按底层合同处理，已确认内容损坏不能随意跳过。
Append/flush 结果不明确时暂停注册，重开裁决；恢复可能确认一个尚未来得及向调用方返回成功的批次。
完整批次的格式、所用 durability barrier、扫描范围及故障模型必须在施工前具体核对底层 API，
不能用 EndAppend 返回成功代替这些保证。

### 4.3 身份、引用与规范表示

推荐先以逻辑 `SchemaKey = (SchemaId, Version)` 作为 Base 的 exact Schema 引用；
从恢复后的注册索引定位其规范定义，物理地址可留作 SchemaStore 内部实现。
这直接承接现有 exact key 语义，不是省略完整定义：定义及 exact 祖先必须真实持久保存和校验。

| 引用候选 | 取舍 |
|---|---|
| 逻辑 SchemaKey | 不绑定 Frame/文件重写，复用现有冲突索引；代价是 Base 重复 SchemaId，以及冷打开需建立索引 |
| 物理 Schema 记录地址 | 引用可能更紧凑，可直接寻址；对象 wire 与 Schema 文件保留/重定位耦合，批次内还需记录定位规则 |
| 仓库内数字 Schema 编号 | 紧凑但增加编号分配、映射及恢复状态；尚无消费者证据要求现在引入 |

逻辑 key 是当前推荐，引用形式与批次格式尚未冻结。所有 key 均在所打开 Repository 的 SchemaStore
中解析，不能拿另一个 Store 的相同裸 key 冒充来源。

Schema 规范编码须独立于反射次序、CLR 名称、程序集版本和 GetHashCode；使用固定版本及字段
类型映射，保留声明段 FieldId、exact base 依赖和严格解析。可以保存各段定义并以 exact base key
表达依赖，避免在每个对象或每条 Schema 定义重复完整祖先内容。
未知编码版本/tag、非规范字段顺序、截断、非法长度、依赖缺失/循环及深度超限均明确拒绝。
规范表示的详细 wire 表需随真实注册记录设计一起冻结，不机械套用已归档的内联链格式。

## 5. 与当前保存路径和分层衔接

DurableSchema 及 SG 的强类型 Schema/body 知识仍由 Runtime 持有。
产品 SchemaStore 继承 InMemorySchemaStore 的不变式；产品 State 保存路径使用 DB-030/029 的
冻结对象图、prepared 内容与 Revision，不把旧 InMemoryStateStore 的 slot/boxed API 扩成第二套产品。
旧实验是否迁移或保留为见证，按实际消费者裁决，不为名称兼容冻结结构。

推荐由已有 StateStore 上层协调注册、Base 引用封装与 planner；必要时让该程序集引用 Runtime。
Runtime 不反向依赖 Storage，也不让 SG 知道磁盘地址；无需为本片新增程序集。

```text
CaptureSession.Prepare：继续产生 raw Base/Delta + current Schema
                         ↓
StateStore 保存准备：完整 Schema 批次预检/注册
                    → 仅 Base 包类型/SchemaKey 头
                    → 既有 planner 与 Append
                         ↓
cold：SchemaStore 恢复索引 + StateStore raw chain
      → 解析 Base 类型/SchemaKey，取得 exact descriptor
      → 匹配历史 reader，应用同版整链
```

上图是本片建议的新产品接缝，尚未实现；不沿用上一版“Runtime Prepare 内为每份 payload 包头”。
B 含 Base 类型引用；D 不含重复 Schema 信息；H 计一次 Base 头及实际 Delta 内容。
Schema 定义记录属于共享元数据写入，不重复计入每个对象 B/D/H；整体提交字节应另行统计。
现有百分比策略仍不是全提交硬峰值上限。

## 6. 下一施工分片的建议边界

建议 DB-031 分两步整合，先冻结真实注册记录及恢复合同，再接首个 Base 引用消费者：

1. 持久 SchemaStore：规范定义、批量注册/冲突预检、索引、真实关闭重开及故障测试。
2. Base 类型/SchemaKey 引用：接统一 prepared 内容与现有规划器，移除冷读 fixture 的外带类型定义；
   Delta 使用 Base exact reader，保持 existing policy 与 raw prior 校验。

验收应包括：

- 等价注册不重复写、输入批次冲突零新写入；同 key 不同字段/祖先在重开后仍拒绝。
- 真实 RBF 注册确认和恢复边界，完整批次可恢复，不完整尾部与已确认损坏有明确不同处理。
- 在 State 追加前或期间注入确定失败，已确认 Schema 仍可重开，DTO 基线不安装；本片不调用业务发布。
  不确定注册结果不能按 key 空闲重试，也不把发布后基线安装失败归入确定未发布失败。
- generated 历史 Schema（含旧祖先）经持久化后仍 exact 匹配，不借当前 Schema 猜旧布局。
- Base+Delta 冷重建只从 Base 获取类型及 Schema 定义，缺失 Schema、错误定义或 reader 不匹配明确失败。
- 真实异构/string Base→Delta→主动 Base→NoChange，计量反映只在 Base 存类型的实际字节。
- SchemaStore 与对象追加失败都不安装已提交 DTO 基线；不据此声称完整 Commit 已落地。

完整新 DTO Upgrade、RequiresRewrite/加载身份导入、领域 Restore 与 WorkingTree 发布作为后继
分片；本片先确定其同版链约束。roots、一般引用、struct、数组/BCL、GC 和性能设施仍不混入。
具体 public API、RBF 恢复步骤、程序集变更和测试闸门在用户认可方向后补成施工合同。

## 7. 本轮讨论核对

本节记录进入实施前的文档修订轮，实施结果见 §8。

主代理核对了现有 InMemory 两类 Store、逻辑图 normalization/save 见证、产品 BaseOnlyUpdate
与 planner；两位 subagent 再次评估并交叉讨论，撤回逐 Delta 重复头与临时对象内联 Schema 的推荐。
也讨论了 catalog head 与单调注册表；选择后者作为修订建议，保留其确认/恢复边界待施工细化。
本轮仅文档修订，没有运行或修改产品代码，不把旧测试结果算作本片的新验证。
修订稿经过独立复审；五份 Markdown 的 123 个本地链接及引用锚点有效，UTF-8/LF 与 Git diff 检查通过。

## 8. 实施合同与账本

实施起点 `34606ca`，工作区干净。主代理按 spec-driven-implementation 分派子任务，集中串行运行
dotnet。基线 build 0 警告/错误，全套 tests 685/685，无跳过；这不是本片最终验证结果。

### 8.1 本片冻结的接缝

- 既有 StateStore 项目增加 Runtime 引用；Runtime/SG/Storage 不增加反向依赖，不新增程序集。
  复用既有 SchemaNotFoundException（公开其构造器）和 SchemaConflictException。
- public `SchemaKey(string schemaId, int version)`：ordinal key，非空白 ID、正 Int32 version。
  wire key 复用 canonical WriteString + VarUInt32 version，不使用进程 hash。
- public `SchemaStore(IRbfFile file, bool readOnly = false)` 借用一份专用 Schema RBF 日志，
  构造时严格恢复；文件由调用方创建/打开及释放，SchemaStore 使用期间须独占其读写。
  `RegisterBatch(IEnumerable<DurableSchema>)`、`Register(schema)`、`GetRequired(key)` / `(id,version)`、
  `Count`、`IsFaulted`；readonly 禁注册。一个 Schema 文件先不轮转，与 State 的 SegmentStore 分开。
- public `BaseObjectPayloadCodec.EncodeString(rawBase)` / `EncodeDurable(schema,rawBase)` 产生 owned
  PreparedBase；`Decode(payload)` 返回 owned `BaseObjectPayload` 的 Kind、SchemaKey? 和 raw Body。
  编码不自行注册，保存协调器保证先注册。只有 Base 有头；Delta 和 Runtime Capture/Prepare 继续 raw。
- public `TypedObjectVersionReader.ReadDurable<TState>(chain,schemas,expectedSchema,readBase,applyDelta)`
  核对 Base 完整定义后，调用 typed delegates 并逐 body 检查全消费；ReadString(chain) 不查 SchemaStore，
  只接受单 Base，保留现有 string 编码与 Empty 规则。不生成 CLR codec registry、Upgrade 或领域实例。
- internal `CapturedRevisionPlanner.PrepareRevision(store,schemas,parent,input,parameters)` 检查
  Previous/Parent 存在性、完整 prior ID 集合、每个 survivor 的 Base kind/完整 Schema，再批量注册全部
  current Schema，包 Base 并调用原 planner。NoChange 也检查，跨 Schema 拒绝，迁移不自动回退 Base。
  仍由上层保证 DTO 内容与 Parent 对应；本片不制造持久 baseline 认证或执行 Accept/State 发布。

### 8.2 Schema 注册格式与恢复

采用一批次一完整 RBF 帧，tag `0x31424753`（little-endian `SGB1`），无 TailMeta；payload：

```text
formatVersion : byte 1
definitionCount : canonical VarUInt32，至少 1
按 SchemaId ordinal、Version 升序的各声明：
    key : canonical string + positive VarUInt32 version
    hasBase : byte 0 | 1
    optional baseKey
    fieldCount : canonical VarUInt32
    各字段 : ascending positive VarUInt32 FieldId + byte fieldType
```

fieldType v1 显式固定为 TypeTag 现有 1..14 的对应类型，不因 enum 增长自动支持新 wire tag。
依赖允许指向已注册定义或本批次声明；解析完整批次后验证闭包、循环和最大 256 段继承深度。
字段严格递增，长度/count 受输入边界约束，未知版本、tag、重复定义、尾随 bytes 等拒绝。
完整输入闭包预检并预建替换索引、编码/容量检查后，才 Append → DurableFlush → 安装索引。
输入为空或全部等价时不追加、不 flush；同 key 不同 shape 在任何写入前失败。

专用日志严格使用 ScanForward(showTombstone:true)，检查 TerminationError，并逐帧 ReadPooledFrame
验证 payload CRC。未知 tag、TailMeta、tombstone 或非法批次均拒绝；本路径不使用 builder 取消帧。
不使用 SegmentStore 默认的自动尾部恢复，也不调用跳过坏帧的 recovery scanner。

**故障边界选择：不自动修复坏尾。** 没有额外持久确认水位时，无法可靠区分尚未完成的追加与
已确认的末帧后来损坏；因此 torn/损坏日志打开失败且保持原样，不能承诺自动丢尾又绝不丢已确认帧。
完整但追加/flush 返回未知的批次可在重新打开后确认。进入追加后的异常使实例 faulted，重开前拒绝
继续读写注册状态；预检错误不使实例失效。文件新建目录项/power-loss 保证不超出底层 FlushToDisk
合同，不据本片宣称整个 Repository crash recovery。自动尾修复不是此片验收项。

可写打开恢复了非空注册表后，还须完成一次 DurableFlush 才返回实例；这为此前结果未知的完整
批次重新建立确认屏障，不能仅因 OS cache 可读而将其用于后续 State 保存。只读打开不 flush、
不提供新持久确认且禁止 Register。正常幂等注册依旧零追加/flush。

Base body 格式：`byte version=1 + byte kind(1=String,2=Durable) + optional SchemaKey + raw body`。
该版本约定当前 Base/Delta body 解释；Schema 版本不代替 codec 格式版本。Storage v3 不变。
B 含 Base 头，D 无类型头，H 从原记录实编码计算；共享 Schema 注册帧不摊入对象 B/D/H。

### 8.3 责任与验收

| 合同 | 负责人 / 路径 | 验证 | 状态 |
|---|---|---|---|
| key、canonical 批次、持久注册、严格恢复及故障状态 | SchemaStore 子任务 / StateStore | SchemaStore / batch tests | 已验证 |
| Base 头、owned bytes、exact typed 读取 | Base codec 子任务 / StateStore | Base payload / typed reader tests | 已验证 |
| Schema 预检与 raw planner 桥接 | 主代理 / CapturedRevisionPlanner | 集成反例及保存链 | 已验证 |
| 真实 SG 历史与异构冷读，删除外带类型字典 | integration 子任务 / DurableGraph.Tests | PreparedRevision / PersistedDeltaChain | 已验证 |
| 实际 StateStore + Runtime 包消费 | package 子任务 / PackageConsumerProbe | 独立 feed、真实 RBF 冷重开 | 已验证 |
| 独立审查、集中验证、文档收尾 | 主代理 + reviewer | 根 build/tests、原/新 package probes、diff/链接 | 已验证 |

满足 §6 收窄后的验收与上述故障合同即停止；新 DTO Upgrade、工作会话/联合 Commit、roots、
SchemaStore 自托管、Dictionary/内建复合类型以及 ID/物理回收只保留后续方向。

### 8.4 实施结果与验证

实际入口：

- [SchemaStore](../../../src/DurableGraph.Persistence/SchemaStore.cs)、[SchemaKey](../../../src/DurableGraph.Persistence/SchemaKey.cs)、
  [SchemaBatchWireCodec（其后由 DB-046 替换）](../0046-unified-schema-catalog-slice/README.md) 实现借用文件、批次预检/规范编码、
  严格恢复、可写重开确认及 faulted 状态；tail 检查拒绝过期 facade 或回调期间的外部追加。
- `BaseObjectPayloadCodec`（后继为 [BaseObjectBodyCodec](../../../src/DurableGraph.Persistence/BaseObjectBodyCodec.cs)）与
  [TypedObjectVersionReader](../../../src/DurableGraph.Persistence/TypedObjectVersionReader.cs) 实现 Base-only 类型头、
  owned raw body、callback 前完整 Schema 匹配，以及每段 body 全消费；string 无 Schema 查询且拒绝 Delta。
- [CapturedRevisionPlanner](../../../src/DurableGraph.Persistence/CapturedRevisionPlanner.cs) 统一完成保存适配，
  同版/Parent/完整 prior membership 校验先于任何注册写入。Schema 注册持久成功不等于 State 发布。
- [真实 SG 保存集成](../../../tests/DurableGraph.Tests/PreparedRevisionGeneratorTests.cs) 保留五轮异构根和
  string 共享，删除 per-record 类型字典；独立 H golden 从旧 70 更新为 97（68 raw + 27 type header + 2 envelope），
  实际 Delta 字节不加头，策略主动 Base 重置 H。
- [持久历史链](../../../tests/DurableGraph.Tests/PersistedDeltaChainGeneratorTests.cs) 验证旧祖先 CLR 定义删除
  后仍由持久 exact Schema 匹配旧版静态 reader；缺失 Schema、同 key 不同祖先与错误版本在 body callback 前拒绝。
- [保存失败集成](../../../tests/DurableGraph.Tests/GeneratedSchemaPersistenceTests.cs) 覆盖全批冲突零追加、
  NoChange 的错误 Parent/type 拒绝及有效重试；合法 Schema 注册后 State readonly Append 确定失败，
  Schema 仍能冷重开，候选未安装。

2026-09-07 主代理集中执行（subagents 未并行构建）：

- `dotnet build DurableGraph.slnx --verbosity quiet`：最终 0 警告、0 错误。
- StateStore 新机制 focused tests：76/76（SchemaStore、SchemaBatchWireCodec、BaseObjectPayload、TypedObjectVersionReader）。
- DurableGraph 持久生成 focused tests：5/5；其余 5 个 Parent 拒绝 theory cases 随完整 suite 执行。
- `dotnet test DurableGraph.slnx --no-build --verbosity quiet`：768/768，无跳过；DurableGraph 381、
  StateStore 129、Storage 155、Serialization 103，比基线净增加 83 项。
- `./experiments/PackageConsumerProbe/Run-StateStoreProbe.ps1`：通过。独立本地 feed 中 8 个依赖包，
  显式 Runtime + StateStore PackageReference，无手工 Analyzer/ProjectReference；2 份 generated history，
  六项输出标记全部通过。产物 `experiments/PackageConsumerProbe/obj/state-store-run-20260906164520-41228-9d7c068f`。
- `./experiments/PackageConsumerProbe/Run-Probe.ps1`：原单 Runtime 包回归通过，history count 7；
  产物 `experiments/PackageConsumerProbe/obj/run-20260906164627-4776`。
- 独立 reviewer 检查最终产品、测试和新包消费者，无未解决阻塞项；主代理复查实际 diff 与执行结果。
  27 个修改文件为 UTF-8/LF；6 份 Markdown 的 144 个本地文件链接及引用锚点有效，Git diff 检查通过。

产品变更仅为 StateStore 新机制及依赖、Runtime 异常构造器可见性；Storage wire、SG raw body、
策略算法和上游源码未变。故障验证是进程内文件及 close/reopen 的定向注入，不是断电模拟。
严格坏尾拒绝、显式 reader/roots、未实现新 DTO Upgrade/Restore/WorkingTree 等边界均按合同保留。
