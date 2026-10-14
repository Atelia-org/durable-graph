# DB-046 统一闭合 Schema 目录

> 文档整理：2026-10-14；来源为 Git `8ef9fca` 中的 `docs/design-branches/0046-unified-schema-catalog-slice.md`，正文保留当片合同与历史验收含义。
> 阅读范围：G0–G3 统一目录/单批次登记及包证据见 §5；SCB1 与 SchemaStore 目录已由 DB-087 meta 自举替换，原闭合版本政策不能据 ID 合并而扩大。
> 当前能力与施工焦点见 [PROJECT-STATE](../../../src/PROJECT-STATE.md)；旧稿中的“下一步”不自动成为当前工作单。

状态：**Implemented / G0–G3 已通过验收**，2026-09-09。用户批准在 DB-045 的整数表示边界之后继续简化目录内部。
当前进展入口：[PROJECT-STATE](../../../src/PROJECT-STATE.md)；前序：[DB-045](../0045-persisted-representation-id-slice/README.md)。

## 1. 问题与施工边界

当前 class 的持久解释经过 RepresentationId → SchemaKey → DurableSchema；完整 Schema 没有重复保存，
但 SchemaBatch 与 RepresentationBatch 有两套登记、恢复和安装流程。目标是让 class Schema 的记录本身
成为 Base 所引用的表示记录，inline Schema 和数组记录也进入同一目录，exact 依赖统一按整数寻址。

最小成功见证：混合 class、inline struct、数组的一次登记只追加/flush 一帧，冷重开得到同一完整布局和 ID；
相同 Schema 再请求对象表示无需额外登记。引用目标可独立升版，旧领域 struct 删除后保留的 reader 仍能读历史 DTO。

本片保持 Runtime DurableSchema/ObjectLayout 视图、SG/DTO/body、领域类型身份、版本传播、Upgrade 与 .dgschema history。
不持久化开放模板、CLR Type 或委托，不改 StateRevision/Delta/发布格式，不增加程序集、迁移器、BCL、GC 或联合 Store 视图。
项目未投入使用，没有旧仓库兼容义务；SchemaStore 直接拒绝旧 SGB1/RPB1 日志。Base v4 的字节结构不变。

## 2. 一个目录，保留两类依赖语义

目录中的记录为：reference Schema、inline Schema、array；string 为隐式内建记录。
class 的 Schema 记录即它的对象表示，不另存 alias。基类与 inline 字段/数组元素引用 exact Schema 记录 ID。
普通引用槽仍只保存无版本 nominal TypeExpr，目标版本由目标自己的 Base 决定，不展开其 exact 定义。

例子：`Box<Point>{T Value;}` 内嵌 Point exact Schema；`Box<Node>` 只保存 Node 引用约束；
`ArrayHolder<Point>{T[] Items;}` 不绑定 Point 的版本，由数组记录保存其 inline 元素布局。
`Phantom<int>` 和 `Phantom<string>` 即使 DTO 相同也不可合并；纯 nominal 参数可以没有对应 exact Schema/provider。

节点使用同一仓库内 UInt32 编号空间：0 无效（仅 baseId 字段的 0 表示没有祖先），1 固定 string，
动态记录从 2 连续、单调分配。inline 编号仅是元数据引用，不赋予 ObjectId、ObjectVersion 或独立 Upgrade。
公开 RepresentationId 继续作为对象表示入口；指向 inline 节点时 GetRepresentation/ResolveReader/Base 解码明确拒绝。
不新建并行 ID 分配器；UInt32 耗尽只拒绝缺失节点，已有节点的幂等登记仍可成功。

SchemaKey 的 `(closed nominal TypeExpr, definitionVersion)` 保留为派生查询/冲突索引，不单独编码。
同 key 必须具有完全相同的 kind、字段、base/inline 闭包，不能分配新 ID 绕过冲突。
完整结构相等性和 Upgrade requirement set 不改：尚未登记的候选/中间布局不能以仓库 ID 比较替代。

## 3. 统一记录格式

专用 schemas.rbf 只接受新 tag `0x31424353`（小端 ASCII `SCB1`）。批次版本 1：

```text
byte version=1
UInt32 count (>0)
repeat count:
    UInt32 id
    byte kind                  // 1 reference Schema, 2 inline Schema, 3 array
    kind-specific content

Schema content:
    nominal TypeExpr           // 原闭合前缀语法
    UInt32 definitionVersion   // 1..Int32.MaxValue
    UInt32 baseId              // 0=无基类，否则早于本行的 reference Schema ID
    UInt32 fieldCount
    repeat fieldCount:
        UInt32 fieldId         // 正 Int32，严格递增
        slot

Array content:
    UInt32 codecVersion=1
    byte constructor           // SZ / rank 2 / rank 3 / rank 4：4..7
    slot

slot:
    byte TypeTag               // 1..14 builtin，15 reference，16 inline
    tag15: nominal TypeExpr
    tag16: UInt32 inlineId     // 早于本行的 inline Schema ID
```

UInt32 使用现有 canonical 变长编码。string 使用既有 slot tag4，不额外写 string 节点。
仅 exact base/inline 边要求前驱节点；普通引用名义可自环/互环，不要求目标版本已登记。
动态 ID 连续且不得复用；依赖先于使用者。读取整批成功、完全消费、全部校验通过之后才安装候选目录。
先前帧的依赖和本帧前面行的依赖同等有效；未知、未来、自引用、错 kind 的 exact ID 拒绝。
保留 exact DAG depth≤256、TypeExpr 的深度/节点/arity 限额，以及跨登记顺序的名义 kind/arity 一致性检查。
不得重复登记同 Schema key 或同数组布局到不同 ID，包括内容相同的别名行。

新格式单批次受一帧 RBF payload 上限约束。旧两帧各自上限不保留为批次容量承诺；超限在首次追加前拒绝。
不为本片引入跨帧事务或自动分片。这个改变不影响 State 数据帧的现有限额。

## 4. 登记与恢复合同

保留 SchemaStore 的 Register/RegisterBatch/RegisterRepresentations、GetRequired/TryGet、GetRepresentation/ResolveReader。
Count 仍统计用户 Schema（含 inline），不统计 string/array。Register 返回已有 canonical Schema 实例。
Register(Schema) 同时登记其 exact 依赖及该 Schema 节点；以后请求 class 的表示 ID 不再写新记录。
RegisterRepresentations 返回顺序仍对应输入；依赖导致节点编号不一定与输入序号相邻。

一次操作：冻结输入 → 收集并校验全部 exact 依赖及名义声明 → 分配候选节点/派生索引 → 准备完整单帧 bytes →
检查文件尾未变 → Append + DurableFlush → 安装候选目录并返回。
枚举/晚期 getter、冲突、格式/深度/ID/容量错误均不得部分写入；重入和外部尾变化防护保持。
任何追加/flush 不确定均 faulted，重开前不能继续使用；不在 catch 中偷偷恢复候选。
恢复整份文件时严格校验 framing/CRC/meta/tag/tombstone/尾部，不跳过坏记录或截断；可写非空重开先 flush。
只有描述持久化；reader 从本次冻结代码目录按完整布局绑定，SchemaStore 不保存跨 snapshot 的 CLR reader 缓存。

## 5. 分工与验收

| 闸门 | 工作与负责人 | 可观察结果 |
|---|---|---|
| G0 目录/格式 | production agent，主线程冻结接缝并审查 | 单一节点目录和批次；删除旧四个 codec；依赖 ID 与同 key 冲突正确 |
| G1 测试迁移 | codec/schema tests 与 store/integration tests 分派不同文件 | 独立 golden、全部 slot/rank、坏依赖/重复ID、历史 exact 与故障窗口覆盖 |
| G2 产品集成 | 主线程 | Base inline-ID 拒绝；无两批次安装残留；现有保存/冷读/Upgrade 路径正常 |
| G3 独立审查/整体验证 | reviewer 与主线程 | 根构建、完整 tests、真实 Array/Generic/ValueUpgrade/StateStore 包回归；无阻断发现 |

必要见证还包括：共享 DAG 不按树重复展开；空数组也验证 exact；reference kind/arity 两种登记顺序；
同 key 异形不因已分配 ID 而漏检；无旧领域值类型的历史 reader；phantom 名义区别；未知 ID/inline ID
在业务 callback 前拒绝；单次 Append/Flush 故障后重开、只读不写、幂等登记、重排登记保持旧编号。
既有测试中只属于旧 wire 的成功路径改为拒绝；领域历史和格式校验测试迁移到新路径，不能直接删除有效保护。

### 5.1 实现与回归定位

- [SchemaCatalogEntry](../../../src/DurableGraph.Persistence/SchemaCatalogEntry.cs) 与
  [SchemaCatalogWireCodec](../../../src/DurableGraph.Persistence/SchemaCatalogWireCodec.cs) 是唯一节点格式，
  读写共享连续 ID、唯一性、名义声明及 exact 依赖校验。四个旧 codec 已删除。
- [SchemaStore](../../../src/DurableGraph.Persistence/SchemaStore.cs) 用同一 Prepare/CommitRegistration 处理两种公开登记入口；
  `_nodes` 是登记事实，Schema 查询和对象表示反向索引由同一 Install 建立。
- [格式测试](../../../tests/DurableGraph.Persistence.Tests/SchemaCatalogWireCodecTests.cs)、
  [重放测试](../../../tests/DurableGraph.Persistence.Tests/SchemaCatalogReplayTests.cs)、
  [登记/故障测试](../../../tests/DurableGraph.Persistence.Tests/RepresentationStoreTests.cs) 保留独立 golden、坏字节与顺序/故障见证。
- [数组格式测试](../../../tests/DurableGraph.Persistence.Tests/ArrayWireFormatTests.cs) 证明 256 层 inline Schema 作为元素时不额外计数组外壳；
  [对象读取测试](../../../tests/DurableGraph.Persistence.Tests/TypedObjectVersionReaderTests.cs) 与
  [Repository 集成测试](../../../tests/DurableGraph.Persistence.Tests/RepresentationIntegrationTests.cs) 证明 inline ID 不能冒充对象 Base，且在 body callback 前拒绝。

### 5.2 验证证据

2026-09-09 主线程集中验证：

- 根 solution build：0 warnings / 0 errors，日志 `experiments/PackageConsumerProbe/obj/db046-build.log`。
- StateStore 定向整项目：458 passed；完整 solution：1352 passed（Runtime 636、StateStore 458、Serialization 103、Storage 155），无失败/跳过。
  日志 `obj/db046-statestore-tests.log`、`obj/db046-full-tests.log`（相对 PackageConsumerProbe）。
- 独立审查无阻断问题；收紧一处原有泛型表达式截断断言后，重新根构建并补跑 24 项 ArrayWireFormatTests 全部通过，
  日志 `obj/db046-final-focused-tests.log`。产品代码没有因此改动。
- Array 真实包通过：四 ranks、generic/jagged/cycles、historical exact、元素 Upgrade、独立表示升版与 Delta 继承。
  日志 `obj/db046-array-package.log`；产物 `obj/array-20260909051543-26560-0e1f2ec2`。
- Generic 真实包全部四阶段通过，包括缺失 closed Upgrade 拒绝及删除旧领域 inline 类型后保留历史读取。
  日志 `obj/db046-generic-package.log`；产物 `obj/generic-20260909051809-38120-80e7bd85`。
- ValueUpgrade 真实包全部四阶段通过，包括缺值规则拒绝、开放 owner/Pair、作用域工具及删除旧领域 inline 类型后的 Upgrade。
  日志 `obj/db046-value-package.log`；产物 `obj/value-upgrade-20260909051922-20696-ae7f7fbd`。
  Generic/ValueUpgrade 复用 Array 打出的隔离 feed，包版本 `0.0.0-array-e2e.20260909051543.26560`。
- StateStore 真实包通过完整保存/冷重开、readonly/无参构造器豁免、升级强制 Base、正常 Delta、
  同实例 GraphSession 连续提交、共享/循环图与 unreachable Remove。日志 `obj/db046-statestore-package.log`；
  产物 `obj/state-store-run-20260909052144-39432-15b8d35e`。
- 文档检查：9 份修改文档、423 个本地文件链接、43 个本地 Markdown 锚点通过；git diff --check 通过。
  退役源码的历史文档链接改为后继入口，保留原分片的历史格式与验收含义。

## 6. 后续边界

开放模板＋必要 exact 实参是否能替代“展开 Schema 再反向 Match”的机制另做研究；它需要 SG/history 提供
无法从闭合 Schema 反推的模板材料，也须保留手工 Schema/reader 消费方式。
是否允许同一 Box 模板版本配不同 Point exact 版本是版本政策，不由新节点编号自动放宽。
下一片不因为换成目录 ID 就要求所有 nominal 类型存在 exact Schema 或领域 CLR 类型。
