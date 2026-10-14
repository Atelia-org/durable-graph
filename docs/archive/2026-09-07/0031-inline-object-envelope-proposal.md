# 已被替代的 DB-031 内联方案快照

> 归档日期：2026-09-07；原文来自提交 `5fdc371`。
> 以下 Proposed 指当时提案状态，现已被替代，不能作为实施输入。
> 原因：重复 Delta 类型声明没有必要；临时内联绕过了已有真实需求的持久 Schema 注册与冲突检查。
> 后继：[DB-031 修订讨论稿](../../design-branches/0031-persisted-object-type-envelope-slice/README.md)。

---

# DB-031：持久对象类型头与 exact Schema 校验

> 状态：Proposed — 本轮只规划，等待用户采纳后实施。
> 日期：2026-09-06；核对源码基线 `6439ee5`，规划开始时工作区干净。
> DB-030 已完成；本文不改变已实现行为，不构成产品修改授权。

## 1. 问题与最小成功判据

[DB-030](../../design-branches/0030-captured-object-preparation-slice/README.md) 已统一从异构 frozen DTO 图准备内容，
[DB-029](../../design-branches/0029-prepared-object-revision-planning-slice/README.md) 接通策略与可追加 Revision。
但真实冷读仍依赖测试的 `metadata[(FrameAddress, ObjectId)]`，其中保存 kind、exact Schema
及 codec 标记。盘上字节即使能用某个 reader 解码，也不能据此知道它声明的类型和版本。

本片回答：能否把对象的类型解释信息与每条对象内容一起持久保存，冷重开后先检查完整
重建链的类型，再调用现有静态 body reader？

最小成功判据：真实异构 Capture → Prepare → policy → Append → 冷重开见证删除上述
per-record 元数据字典；仅从对象记录恢复 string/durable kind 和完整 exact Schema，
并在调用任何 body decoder 前拒绝最后一条 Delta 的错误 Schema。
继续覆盖继承、共享 string、Base/Delta/主动 Base/NoChange 和按目标 Revision 校验引用。

仍由 fixture 提供查询的 Revision/ObjectId、roots 与编译期 reader。reader 是可执行类型知识，
不能从 Schema 描述自动制造；roots 尚未持久保存。本片是自描述对象记录，不是完整自描述图
或通用 Load，也不建立 WorkingTree 的已提交基线。

## 2. 候选与讨论结果

| 候选 | 收益与代价 | 推荐 |
|---|---|---|
| WorkingTree 完整 Commit | 直接收敛使用入口，但需发布点、屏障、故障裁决；冷加载还缺 roots、类型解释、Restore 和身份导入 | 后继消费者；不能以 Append + Accept 代替 Commit |
| 单独 canonical Schema codec | 小而清楚，但只解决 descriptor 往返，仍保留现有冷读的外带元数据 | 合入本片，获得立即消费者 |
| 对象类型头 + canonical exact Schema | 直接补上每条持久对象记录的解释信息，不改变领域类型范围或 raw Storage 职责 | 本轮推荐 |
| 同时做 SchemaStore、roots、自动 reader 分派 | 可进一步缩小完整 Load 缺口，但额外引入寻址、追加顺序、Revision 元数据与运行时绑定 | 分片处理 |
| struct、一般引用、领域 Restore | 各有独立价值，但不消除当前持久类型事实缺口 | 保留穿插机会 |

两位 subagent 独立评估后交叉商议。初始分歧是先单独 canonical descriptor，还是带上
对象 envelope；最终认为后者能立即替换真实 fixture 的外带信息，范围仍可控制。
主代理核对了消费者、Prepare 和计量接缝，采用这一推荐。

### 2.1 内联描述还是 SchemaStore 引用

推荐首版在 durable 对象的每条 Base/Delta 中内联完整 exact descriptor，包含整条祖先链。
string 使用短类型编码，不带自定义 Schema。

- 内联无需增加 Schema 地址、独立追加顺序或缺失 Schema 引用处理，容易独立验证。
- 代价是真实元数据重复；小对象上尤其明显，可能使 Base 更常被选择，或增加 Delta 链的 H。
  Frame 大小上限并不能消除这部分开销。
- 每条 Delta 也带完整头，保留逐记录核对 exact Schema 的简单合同。仅 Base 带头的候选可省空间，
  但需要另定 Delta 继承类型的解释规则，本片不采用。
- 这是首版对象 envelope 的明确取舍；长期仍面向 SchemaStore exact 引用。后续需要时改版 envelope，
  再决定地址或内容引用，不为原型预建双格式兼容、双写或迁移框架。

这项持久表示选择尚待用户采纳。不能把“内联最省实施步骤”说成已证明具有最好运行性能。

## 3. 当前源码接缝

- [CaptureSession.Prepare](../../../src/DurableGraph/Runtime/Capture/CaptureSession.cs) 集中取得 current kind/Schema
  与裸 PreparedBase/PreparedDelta，适合在对象边界统一包头。
- [DurableSchema](../../../src/DurableGraph/Schema/DurableSchema.cs) 已定义完整结构相等、exact BaseSchema
  及声明段内字段排序；[InMemorySchemaStore](../../../src/DurableGraph/InMemorySchemaStore.cs)
  已检查同 key 不同 shape 和祖先冲突。
- [ObjectRevisionPlanner](../../../src/DurableGraph.Persistence/ObjectRevisionPlanner.cs) 按输入 payload
  长度调用 [ObjectVersionPayloadSize](../../../src/DurableGraph.Storage/ObjectVersionPayloadSize.cs)；
  [Storage 链读取](../../../src/DurableGraph.Storage/StateRevisionStore.cs) 的 H 来自实编码长度。
  因而先包头、后规划即可把新开销纳入现有 B/D/H，不另改策略公式。
- [统一保存集成](../../../tests/DurableGraph.Tests/PreparedRevisionGeneratorTests.cs) 与
  [持久 Delta 见证](../../../tests/DurableGraph.Tests/PersistedDeltaChainGeneratorTests.cs) 仍手持
  `DeltaFixtureDescriptor`、Schema 和 roots，是本片应实际修改的消费位置。

## 4. 推荐数据流与模块边界

```text
SG / string 预制 codec：冻结 DTO → 裸 PreparedBase / PreparedDelta
                                  ↓
Runtime CaptureSession.Prepare：kind + exact Schema + 裸内容 → 带类型头的 owned 内容
                                  ↓
既有 prepared rows → DB-029 B/D/H / policy → Storage opaque body
                                  ↓
冷读：raw ObjectVersionChain → 全链类型头预检 → 静态 Read / ApplyDelta
                                  ↓
完整 DTO 的 string 引用验证（目标 Revision）
```

实现放在 DurableGraph runtime，复用其已有 Serialization 依赖；不新增程序集，Runtime 不引用
StateStore/Storage，Storage 不引用 DurableSchema。底层 StateRevision wire v3 继续原样保存
opaque body；本片新增的是 body 内部的对象格式，并非声称“没有持久格式变化”。

### 4.1 裸 body 与带头内容

SG 各版 `Write/Read/PrepareBase/PrepareDelta/ApplyDelta` 保持裸 body 合同，字段操作仍静态绑定。
`StringPayloadCodec.PrepareBase` 同样保持裸 string body。

`CaptureSession.Prepare` 在这些结果外包头，再放入 `PreparedCapturedObject.BaseContent/DeltaContent`。
这些属性实施后表示完整 typed object payload；必须同步 XML、PROJECT、测试和包消费说明，
不能继续把它们当作直接传给 `ReadVn` 的裸 DTO bytes。

保留 PreparedBase/PreparedDelta 的 owned bytes 模型，不为本片增加一套池或缓存。
包装 Delta 必须原样保留原 `HasChanges`；无变化的零位图即使加头后非空，也仍是 NoChange。
全部 live Base 仍提前准备；每次 Prepare 中，每份 Base/Delta 结果各包一层头，规划和落盘复用最终 bytes。
重复 Prepare 可重新生成等价内容，已有候选/异常/重入合同不变。

自定义 preparation callback 同样必须生成与声明 Schema 和本格式匹配的裸 body。
类型头是数据的声明及一致性校验材料，不能证明任意自定义 callback 的正确性或纯度。

### 4.2 首版对象 envelope

建议形状如下，具体类型名与方法签名可在实施合同中收敛：

```text
envelopeFormatVersion : byte = 1
objectTypeCode       : byte = 1 (String) | 2 (Durable)
if Durable:
    schemaByteCount   : canonical VarUInt32
    schemaBytes      : 完整 canonical Schema descriptor
rawBody              : 外层对象 body 边界内的剩余 bytes
```

不重复保存 ObjectId、Base/Delta kind、prior 或 outer body length，它们已有 Storage envelope。
外层表示方式与内部对象类型是两种信息，不能混为一个 kind。
String 不允许携带 Schema；Null 只用引用 ID 0，没有对象记录。

格式版本 1 同时约定现有标量/string Base 与 DB-027 Delta 的 body 编码规则；Schema 版本不代替
编码格式版本。将来改变 body 解释规则须明确改版，不能只维持相同 Schema 就静默切换 codec。
不新增程序集名、CLR Type 名、history 路径或自定义 codec 字符串作为持久类型身份。

底层 raw Storage 接口仍可保存其他 opaque bytes。typed 读取是显式选择的合同，不能以试读成功
来自动探测类型，也不在失败后回退为旧裸 body。既有 raw tests 继续测试 raw 层。

### 4.3 Canonical Schema descriptor

推荐单一二进制编码器从 DurableSchema 生成 bytes，读回同一种逻辑 descriptor。
保持当前 typed Schema；不要求 SG 同时维护第二份 blob 常量或改写 history 格式。

```text
schemaFormatVersion : byte = 1
declarationCount    : canonical VarUInt32，至少 1
按最远祖先到最终类型依次写每个声明段：
    schemaId        : 现有 BinaryPayloadWriter.WriteString 的规范内容编码
    schemaVersion   : canonical VarUInt32，1..Int32.MaxValue
    fieldCount      : canonical VarUInt32
    各声明字段，按 FieldId 严格升序：
        fieldId     : canonical VarUInt32，1..Int32.MaxValue
        fieldType   : byte，使用显式固定映射
```

每段的直接 base 是前一段；第一段无 base，最后一段是被描述类型。无需另写重复 base 指针。
空字段段有效；整条链不能重复 SchemaId。FieldId 只在本声明段内唯一。

fieldType 首版固定表（不直接依赖 enum 强转或枚举未来增长）：

| 代码 | 类型 | 代码 | 类型 |
|---|---|---|---|
| 1 | Boolean | 8 | UInt16 |
| 2 | Int32 | 9 | UInt32 |
| 3 | Int64 | 10 | UInt64 |
| 4 | String 引用槽 | 11 | Char |
| 5 | Byte | 12 | Half |
| 6 | SByte | 13 | Single |
| 7 | Int16 | 14 | Double |

这恰好覆盖当前 schema 模型，不是一般 TypeCodec；未来 nominal 引用、inline struct、数组或泛型
必须另定表达。本片不预留假实现 tag，不据此声称可表示任意 CLR Type。

SchemaId 保持 ordinal、区分大小写；不修剪、不做 Unicode normalization，复用现有 string
codec 以保留合法 CLR UTF-16 内容。它是描述中的值，不进入对象身份表。
同逻辑 descriptor 必须唯一编码；相同 key 的不同字段或祖先不能具有同一份 canonical bytes。
本片用完整 descriptor 比较，不引入 SchemaHash、GetHashCode 持久键或内容寻址。

### 4.4 严格解析与 exact 校验

解析器先验证格式与边界，之后才交付结果；不产生 registry 或会话副作用。
须拒绝未知格式版本、未知类型码、非最短 VarInt、非法长度、截断、descriptor 尾随数据、
零/溢出的版本及 FieldId、重复或乱序字段、非法 SchemaId 和重复祖先身份。
不能先交给 DurableSchema 自动排序，再悄悄接受非规范 wire。
声明数/字段数须受输入剩余字节约束，checked 转换，不能按未经检查的 count 巨额分配；
采用迭代处理祖先链，避免直接按 wire 层次递归解析。

当前 DurableSchema 的构造会扫描祖先，Equals 也递归比较 base；因此只限制输入 bytes 不足以
约束深链成本。首版建议最多支持 256 个声明段（含最终类型），读写两端在构造/编码前拒绝超限，
测试覆盖边界及超限。这是实现支持上限，不改变 count 的 wire 编码；本片不顺带重构 Schema 模型。
descriptor 长度不得越过传入对象 payload 边界，各段/总字段数按剩余字节检查及累计校验。

“合法 descriptor”与“存在可执行 reader”分开：格式已支持的 descriptor 可以声明任意正 Schema
版本，但只有与所选历史 reader 的完整 Schema 匹配才可执行。未知业务版本不回退到 latest。
不同 envelope 格式版本则在解析阶段直接拒绝。

## 5. 冷读与全链预检

Runtime 提供窄的解析及 exact 检查操作：输入一条已定边界的 payload，可取得 kind、可选
完整 Schema 和独立拥有的 raw body；durable 可核对 expected exact Schema，string 可核对类型。
具体 API 不需要接收 Storage 地址、委托注册表或构造领域实例。

冷读集成按以下顺序消费：

1. Storage 从指定 Revision/ObjectId 读取 object-first 原始链，继续负责 prior/Parent/地址校验。
2. 先解析全链 envelope，再检查各记录与所选 reader 的 kind、完整 Schema 一致；Delta 不能跨 Schema。
   string 只接受单条 Base。不得边解析一条头边解码一条 body，导致晚到错误前已执行 reader。
3. 全部预检通过后，解码最早 Base，按序 ApplyDelta，并要求每段 raw body 全消费。
4. 完整 DTO 的引用再按目标 Revision 验证；owner 沿用旧记录也不能改用旧视图的 string 表。

全链迭代可留在现有 integration coordinator，因为 Runtime 不依赖 Storage。
类型头解析与 exact 检查必须是产品操作；测试不能仍手写一份 header 解释器。
此处消除外带 per-record 类型信息，不把 fixture 显式选择 reader 包装成已经实现的自动类型分派。

## 6. 计量与可观察验收

类型头属于对象自身 payload，完整计入 B、D、H。DB-029 的 Delta 文件距离仍按现有上界计量，
不借本片改 policy、预算含义或 prior 遍历方式。
集成测试应按含头后的实际成本重新选取对象规模与参数，使真实 Delta 和主动 Base 两种路径
都仍被覆盖；不能硬保留旧 H 数字，或在估算中排除头以制造旧决策。

| 验收 | 可观察证据 |
|---|---|
| 规范表示 | 独立手写 golden，所有现有字段类型、稀疏编号、空段、继承链、ordinal/UTF-16 SchemaId；不只 writer-reader 自洽 |
| 拒绝非规范数据 | 未知版本/tag、重复/乱序、超长/截断/尾随；解析失败无对外部分结果或状态修改 |
| exact 匹配 | 同 SchemaId/version 但不同字段/祖先，错误 kind、未知业务版本、跨版 Delta 在 body 调用前失败 |
| 真实冷读 | 保存后丢弃 writer-side 描述字典，仅凭盘上头核对类型；Base→Delta→主动 Base→NoChange、new/remove、string 共享及目标视图校验 |
| 历史生成 | 当前编译仍可按积累的旧 DTO Schema 读取；祖先旧 CLR 定义删除后，不借新 base 假装旧 exact descriptor |
| 全链先检后读 | 最后一条 Delta 的错误头导致 Base/Delta reader 调用计数均为零；类型合法但 body 非法仍由 body reader 拒绝 |
| 计量 | 持久 envelope golden 与实际链 H 相符；B/D 均包含头，原 HasChanges=false 经包装仍产生 NoChange |
| 候选与所有权 | Seal 后领域 mutation 不影响包头内容；重复 Prepare 等价、包装异常不安装基线；输入/输出 bytes 隔离 |
| 实际包消费 | 单 Runtime PackageReference 调用 SG AddRoot + Prepare，使用产品操作拆头并检查实际 raw goldens |

## 7. 实施拆分、停止条件与后续

用户采纳后，建议主代理先冻结 envelope/schema wire 表及小型 runtime API，再安排：

1. runtime 子任务：canonical descriptor、对象 envelope、严格读取/匹配及独立 goldens。
2. Capture/包消费子任务：在 Prepare 包头、保留候选与 HasChanges 合同、更新 XML 与真实 PackageConsumer。
3. 集成子任务：去掉真实冷读的外带类型字典，核对全链预检和新 B/D/H；不修改策略算法。
4. 独立审查：规范化唯一性、晚到错误、payload 层次、历史 Schema、计量与范围。
   主代理整合后集中串行 build/tests/package，避免共享 obj 争用。

实施验证运行根 solution build、相关及整合 tests、PackageConsumerProbe。
DB-030 的 685/685 与包结果是历史基线，本轮规划没有运行代码验证。

上述判据满足即停止。本片不实现 roots 持久化、SchemaStore 寻址、通用 TypeCodec/registry、
DTO 升级/领域 Restore、WorkingTree Commit/发布、身份导入/数字回收、struct/一般引用/数组/BCL，
也不加入性能框架。余项只在[路线图](../../DurableGraph-research-roadmap.md)维护。

最接近的后续选择是 roots/类型读取分派，或在具备必要元数据后推进受控工作会话；
这不是自动进入下一分片的承诺。

## 8. 本轮规划核对

主代理核对当前 Runtime、Schema、Storage、规划器及真实生成集成源码；两位 subagent 交叉讨论
选片后分别审阅本文。修订了深祖先链上限与重复 Prepare 的表述，无剩余阻塞意见。
本轮仅修改本提案、PROJECT、路线图与设计索引；长期目标保持原样，未改产品代码或运行 build/tests。
四份 Markdown 为 UTF-8/LF，112 个本地文件链接及引用锚点检查通过，Git diff 检查通过。
