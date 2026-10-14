# DB-026：同 Revision Frame 的 raw Base 对象内容存取

> 历史迁移（2026-10-14）：原路径 `docs/design-branches/0026-raw-base-object-content-slice.md`；来源修订 `8ef9fca`，正文保留原时期语义。
> 适用范围：wire v2、指定 Revision 的 local Base 重开读取分片；不是完整 Save、发布或领域图 Restore。 今日事实从 [PROJECT-STATE](../../../src/PROJECT-STATE.md) 续接。

> 状态：Chosen / 已实现并通过验收 — 2026-09-06。
>
> 规划审查基线：`171581e`；实施起点 `c8e331d`。规划轮仅核验源码，实施证据见 §8。
> 用户已采纳方案并明确授权实施；移除 DeltaObjectIds 时须在代码保留后续 Delta 工作的 TODO。
> 初始规划与取舍保留如下，具体施工合同与证据在 §8 维护。

## 1. 下一片回答什么

**把已有 SG DTO/string codec 产生的真实字节放进 StateRevision Frame，关闭重开后，
能否从调用方给定的 exact Revision 和 ObjectId 取回正确的完整 Base 内容？**

这是目标设计 P2 的内容存储子片：提供文件内容往返，不声称完成领域对象图恢复或持久提交。
[DB-025](../0025-string-object-decoding-slice/0025-string-object-decoding-slice.md) 已闭合 string 字节解码及引用槽校验；
继续开发更多字段类型已不是连接存储的必要前置。
[DB-016](../0016-next-product-object-content-slice/README.md) 原先也推荐真实 codec 后接 raw 内容存取。

| 候选 | 当前收益和新增问题 | 本轮排序 |
|---|---|---|
| raw Base 内容存取 | 首次连接真实内容、membership、文件轮转和重开；需要升级现有 Frame 模型 | 已选并实施 |
| current DTO → 领域 Restore | 补齐领域实例往返；需要选择构造、Transient、base-private 填充及失败暴露合同 | 独立后续候选 |
| 自定义 struct | 增加实用值布局；需要 exact inline Schema/history 与 nested DTO | 保留 DB-024 TODO |
| Durable 互引/循环 Capture | 开始真正递归图捕获；需要 nominal 引用约束、concrete binding 和队列扩展 | 独立后续候选 |

Restore 技术上可做：当前 Generator 的 DG0011 仍拒绝 readonly durable 字段，旧 boxed
`AppendMaterialization` 也已有无构造分配路径。但旧实现不自动决定新 DTO Restore 的语义；
本轮推荐存储接线，依据是当前收益与依赖，而不是声称 Restore 被语言能力阻塞。

## 2. 实施前已核验的接缝（`171581e`）

- [StateRevision](../../../src/DurableGraph.Storage/StateRevision.cs) 只持有 membership。
  `BaseObjectIds`/`DeltaObjectIds` 声明的是 **同 Frame** 的对象版本，目前均无内容。
- [StateRevisionStore](../../../src/DurableGraph.Storage/StateRevisionStore.cs) 已有真实 RBF
  append/read；`ReadLiveObjectHeads` 将 local ID 映射到 containing Revision Frame。
  它明确只返回 shallow 声明，不核验对象内容是否存在。
- [WireWriter](../../../src/DurableGraph.Storage/StateRevisionWireWriter.cs) 和
  [WireReader](../../../src/DurableGraph.Storage/StateRevisionWireReader.cs) 目前是 provisional v1，
  DGSR tag，membership 后即结束，TailMeta 必须为空。
- [Materializer](../../../src/DurableGraph.Storage/LiveObjectHeadMapMaterializer.cs)
  通过 `LocalObjectIds` 合并 Base/Delta 声明，对二者执行相同的 head 更新。
- [真实文件测试](../../../tests/DurableGraph.Storage.Tests/StateRevisionStoreTests.cs)
  已提供关闭重开、跨 Segment、错误 tag/TailMeta、编码失败及 writer lease 释放的支架。
- [DB-025 typed 消费者](../../../experiments/PackageConsumerProbe/Consumer/Domain.StringDecoding.cs)
  已能由冻结候选产生独立 owner/string bytes，再验证 exact Schema 与引用关系；仍无持久类型头。

## 3. 已选模型与关键取舍

### 3.1 一个完整 Revision，local records 是唯一内容事实

整体推进现有 `StateRevision`：local Base records 输入为 ObjectId 与完整 opaque bytes，
构造后同时拥有 membership 和内容。local IDs 由这些 records 的 keys 派生，不能另收一份
独立 Base ID 清单再要求调用方同步维护。
这里的“完整”仅指每个 local 声明都有 body，不表示已验证 external targets、Schema 或全图引用。

所有 local records 本片均为 Base。允许零长度 body（例如无 durable 字段的 DTO）；
**显式提供空内容与根本没有提供内容是两回事**，不能用自动补空 body 兼容旧调用。
ID 仍须非零、唯一，local/external/removed 的集合关系沿用现有 map 语义。

不建立另一个长期可缺内容的 `StateRevision` 加内容 envelope 双通道，也不改成一对象一个 Frame。
复用已有 containing-frame head 语义；首次读取可解码整个 Frame 并查找 local record，
暂不做按对象偏移随机读取、缓存或跨 Frame 分块。

### 3.2 两种 Delta 必须分清；已收回无内容的占位接口

| 概念 | 本片处理 |
|---|---|
| ObjectHeadMap Base | 保留：local records 加 external heads 构成完整 live map |
| ObjectHeadMap Delta | 保留：相对 parent 更新 local heads、应用 removes |
| ObjectVersion Base | 新增实际内容：完整 bytes，不依赖旧对象内容重建 |
| ObjectVersion Delta | 本片不实现；已移除仅有 `DeltaObjectIds` 的占位输入，代码保留 TODO |

**这是对已有原型 API/测试的有意调整，不是无行为影响的加法。** 当前 wire/store tests
确实写过非空 `DeltaObjectIds`，但只验证 membership，没有保存或重建 Delta 内容。
这些更新场景已迁为真实 Base 内容，保留原来的 map、删除、继承、checkpoint 与地址断言。
StateStore 的已选 Base/Delta policy 不依赖这些构造接口，保持原语义与测试。

替代方案是保留 metadata-only 模型，并让新的内容入口拒绝 Delta 声明；这样可少改部分测试，
却留下“可构造但不能作为完整 Revision 保存”的两层状态。
另一方案是同时存 opaque Delta bytes，但还需明确 prior 定位及重建合同，扩大了本片。
主代理与两名独立设计审查者推荐整体迁移；未来真实 Delta/prior 链出现时再扩展 local record，
不为未来补恒空属性、兼容 overload 或空记录。

### 3.3 存储不知道 CLR 类型；不提前做 TypeCodec

Storage 原样保存上层交付的完整 opaque payload，不区分 DomainClass/string，也不理解 Schema。
未来 TypeCodec/SchemaRef 可成为上层 payload 的一部分；本片不预留虚构类型号，
不把 `CapturedObjectKind`、Schema TypeTag 或 `DurableSchema.GetHashCode()` 当作持久类型标识。

typed 集成见证由测试代码显式持有 roots 与 ID → kind/exact Schema 配对，解码端只从文件读 body。
这些外部 fixture metadata 不是产品持久格式；成功只能证明“已知 codec 的真实内容往返”，
不能宣称任意进程仅凭文件即可自描述加载领域图。

### 3.4 所有权先用复制闭合

构造 BaseObjectRecord 时复制输入 bytes，Revision 冻结不可变 records 的外层集合；
调用方之后修改输入数组不影响待写内容。
调用方在复制期间保持输入稳定，构造不承诺对并发修改取得原子快照。
read 必须在 `RbfPooledFrame` 释放前取得自有副本，不把 pool 内存泄漏到返回值。
读取入口返回调用方拥有的副本，或提供不暴露 backing array 的复制方法；签名在实施时收敛。
仅把 byte[] 包成 `ReadOnlyMemory<byte>` 或只读字典不构成不可变保证。
新 records/派生 ID 集合也应避免 `Array.AsReadOnly` 经 `ICollection.SyncRoot` 暴露 backing array；
复用当前 CapturedGraph 的封装经验，确保修改任何公开取得的对象都不能破坏记录与 ID 的一致性。

验收同时覆盖修改输入、修改返回数组、释放/重用 pooled frame 后再次读取。
这片接受复制及 whole-frame 解码成本；先不引入 lease/callback/零复制持久视图。

## 4. provisional wire 与读取路径

已保留 DGSR Frame tag、无 TailMeta、当前相对地址编码；wire version 升至 2，仅读取新版，
旧 membership-only v1 明确拒绝。不增加旧格式迁移器或双格式兼容路径。

概念布局：

```text
Version / ObjectHeadMapKind / optional exact Parent
LocalBaseCount
  ascending ObjectId / BodyLength / opaque BodyBytes
MapBase: ExternalHeads
MapDelta: RemovedIds
```

这是本片已实现的 provisional framing，不是长期兼容承诺。golden 已固定具体编码，并覆盖
count、canonical length、剩余 buffer 边界、重复/乱序 ID、集合冲突、截断和尾随数据。
沿用已有 collection 限制；body length 必须在复制前验证可表示性与剩余长度，
总长由 RBF 以 long 记录，并在提交前检查单 Frame 上限。
单 Revision 仍须适配一个 RBF Frame；实施时已核验 substrate 上限与超限路径，见 §8。
不拿 Segment soft threshold 当单 Frame 容量上限，不在本片做分块或大对象协议。

读取入口为 `StateRevisionStore.ReadObjectBase(revisionHead, objectId)`：

1. 从指定 Revision materialize live map；ID 不 live 则拒绝，不找“最近的旧值”。
2. 得到 containing Revision address 后，读取并验证该 Frame。
3. 要求该 Frame **本地**实际含对应 Base record；缺失即拒绝，不沿它的 parent 兜底。
4. 返回独立内容副本；typed 外层按 exact Schema 解码并校验引用。

`ReadLiveObjectHeads` 可继续作为 shallow 入口；不能因新增内容 API 就称它验证了所有 external targets。
raw 读取验证的是所请求对象的定位与内容边界，不证明全图 reachability、引用完整或业务不变量。

## 5. 最小验收场景

1. R1 写 A、B 两个真实 Base body；R2 用 ObjectHeadMap Delta 更新 A、继承 B。
   同一 ObjectId 从 R1/R2 分别读到旧/新 A，B 的 head 和内容保持。
2. 后续 Revision 删除 B；新 revision 读取 B 失败，旧 revision 仍可读。
   ObjectHeadMap Base checkpoint 通过 external heads 指回真实旧 local records。
   再以同一数字 ID 写入新的 Base：旧视图读旧内容，新视图读新内容；不把删除变成永久墓碑，
   也不要求本片实现 ID 回收分配器。
3. 强制跨 Segment、关闭并只读重开后，从测试保存的 exact addresses 重复上述断言。
   不能扫描最大地址推断 head，也不能靠进程内 body cache 假装重开成功。
4. 构造 external head 指向合法 Revision 但该 Frame 本地不含目标 ID：shallow map 可形成，
   内容读取必须拒绝；不能从该 Frame 的 inherited map 继续找而掩盖错误 locator。
5. 目录/length golden 与 malformed tests；空 body、ID 边界、输入顺序规范化、错误 tag/TailMeta/版本、
   截断/尾随、输入和返回数据所有权均有正反例。
6. 保留编码失败不结束 partial Frame、lease 可复用的证据；轮转后失败可留下 header-only Segment，
   不能把该可接受结果误写成整个目录完全不变。没有由此扩大到 power-loss/crash 保证。
7. 用真实 Generator 执行 `Capture/Seal → DTO Write + string Write → 文件 → 重开 → ReadVn +
   StringReadTable + ValidateStringReferences`。覆盖 Seal 后领域变化、共享/相等不同非空串、
   null/Empty 规则；不手写 DTO 替代这条集成见证。

## 6. 实施工作包与停止边界

| 包 | 负责范围 | 依赖与分派建议 |
|---|---|---|
| A | 完整 Revision 值模型、provisional wire、ownership 和对应单测 | 主代理先定接缝，交一名 agent 成片修改 |
| B | exact-head 内容读取、真实文件/轮转/reopen 与错误 locator 见证 | A 接缝确定后交另一 agent；避免并改模型/wire |
| C | SG DTO/string 到真实 Storage 的 typed 集成见证 | 接缝确定后并行准备；仅测试项目按需加依赖，不倒置产品依赖 |
| D | 独立审查、根 build/tests、文档及提交 | 主代理组织；build/pack 不争用输出目录 |

验收命令至少为根 `dotnet build DurableGraph.slnx`、受影响测试与全套产品测试。
若修改单包依赖/生成输出/下游接线，还需运行真实 PackageConsumerProbe；不为这片强行把
Storage 打包进现有单 runtime 包。实施前重新读当前 source/status，不以历史 536/536 当新验证。

本片不生成 Delta、估算器、comparer，不调用 policy 决定实际保存；不能填假 D/H 或将 Update 报成 Insert。
没有 ObjectVersion prior chain、Base 历史 lineage、SchemaStore、TypeCodec、领域 Restore/升级、
CaptureSession 导入/ID 回收、Save coordinator、持久 roots/manifest、head 发布或 crash recovery。
append 仍只返回 candidate address，调用方持有 authority；不因文件往返自动 Accept 捕获候选。

完成后依据真实 payload 消费者重新排期 raw Delta/prior 链与比较估算，再接 fixed policy；
领域 Restore、Durable 互引和 struct 可穿插，本文不把它们全部变成 Delta 的前置条件。

## 7. 规划证据与重访条件

本轮三路只读工作：独立比较候选、限定 Storage 源码事实调查、反方审查整体迁移。
主要分歧是是否保留无 body 的 ObjectVersion Delta 占位；选择依据与替代方案见 §3.2。
规划轮没有运行新的 executable witness，当时新增能力保持 Open；后续实施记录见 §8。

如果出现实际需要读取 v1 文件的用户数据、现有外部调用者依赖 metadata-only API，或本片必须
同时解决自描述重开，则重访迁移/范围决定；目前未发现这样的产品消费者。

## 8. 实施合同与验收账本

实施起点为 `c8e331d`，工作区干净。用户已采纳整体迁移、暂收回 Delta ID 占位，并要求代码 TODO。
本片沿用 Storage → Serialization/RBF 依赖，SG/runtime/policy 不增加产品依赖或改变生成合同。
改动前根 build 零警告/错误，全套基线测试 536/536 通过，无跳过。

首波接缝：

- 新 `BaseObjectRecord(uint objectId, ReadOnlySpan<byte> body)` 拷贝输入，sealed 不可变；
  `ObjectId` 和 `ReadOnlySpan<byte> Body` 只读公开。Revision 持有不可变 records 并冻结外层列表。
- `StateRevision.CreateBase(parentRevisionAddress, baseObjects, externalObjectHeads)` 与
  `CreateDelta(parentRevisionAddress, baseObjects, removedObjectIds)`；`baseObjects` 为
  `IEnumerable<BaseObjectRecord>`。`BaseObjects` 按 ID 排序，`BaseObjectIds` 是派生只读视图。
- 删除旧 ID-only 构造入口和 `DeltaObjectIds`；`StateRevision` 中留 TODO(DB-026)，记录
  实际 Delta payload、exact prior 定位、重建链验证与 ID 新占用者从 Base 开始的后续工作。
- wire v2 采用 §4 次序，old v1 拒绝。非法 public 模型参数用 ArgumentException 家族；
  非法 wire 用 InvalidDataException/截断 EndOfStreamException，沿用已有 reader 错误边界。
- `StateRevisionStore.ReadObjectBase(FrameAddress revisionHead, uint objectId)` 返回独立 byte[]；
  ID 0 参数拒绝，非 live 或 locator 对应 Frame 无 local record 时 InvalidDataException。
  不增加隐式 head、parent 内容兜底、policy 调用或 candidate Accept。

| 要求 | 代码/负责人 | 状态与验证 |
|---|---|---|
| immutable records、派生 IDs、TODO、v2 framing | BaseObjectRecord（后由 DB-028 的 [ObjectVersionRecord](../../../src/DurableGraph.Storage/ObjectVersionRecord.cs) 取代）、[StateRevision](../../../src/DurableGraph.Storage/StateRevision.cs)、wire Reader/Writer；model/wire agent | 已实现；模型/record/wire 测试已通过 |
| exact-head raw 读取、membership 迁移、真实文件失败/重开 | [StateRevisionStore](../../../src/DurableGraph.Storage/StateRevisionStore.cs)、Store/Materializer tests；storage agent | 已实现；Storage 95/95，包括 body 缓冲后失败的普通/轮转两种用例 |
| SG DTO/string → 实际 Storage → typed decode | [RawBaseStorageGeneratorTests](../../../tests/DurableGraph.Tests/RawBaseStorageGeneratorTests.cs)；integration agent | 已通过独立聚焦测试；仅测试项目加 Storage 引用 |
| substrate 上限核验、集成审查、build/tests、文档/提交 | 主代理 + 只读事实/独立审查 agent | 根 build 零警告/错误，全套 559/559；独立最终代码审查无阻断 |

原型无兼容读取、无对象 Delta/prior 链、无 TypeCodec/SchemaStore/领域 Restore/Save/发布。
主代理已实际检查 diff、运行集成验证；独立审查提出的 late-body failure 见证已补齐并通过。

### 容量与失败边界

当前相邻 Atelia 源码的 `RbfFile.MaxPayloadAndMetaLength` 为 **268,435,428 bytes**，
由 `SizedPtr.MaxLength`（268,435,452）减去 24-byte Frame 固定开销得到。
本片没有 TailMeta，因此上限约束的是整个 v2 Revision payload，包含目录与 bodies，不是单个 body 的独立额度。
`RbfFileImpl.CommitFromBuilder`（相邻 Rbf/Internal/RbfFileImpl.cs）使用 long writer 长度，
在超过上限时返回 RbfArgumentError，之后才将合规长度转为 int；Storage 沿用 EndAppend/Unwrap 与 using 释放。
writer 不另做第二套完整 wire 长度算法，可能先缓冲再拒绝；单条 length 由既有 codec 约束为 int，
有限 collection 数量乘以单条上限不可能溢出 long。这是当前资源成本，不宣称超大输入零分配拒绝。

正常编码失败发生在最终提交前时，builder Dispose 放弃缓冲内容，lease 可再次取得。
新增 100 KiB body 后置坏 external locator 的测试确认此路径；轮转后可留 header-only Segment。
真实底层 I/O 异常后不保证物理截断已写 prefix，本片没有扩大到故障发布、crash/power-loss 恢复合同，
也没有为该路径修改上游 RBF。

### 最终验证

```powershell
dotnet build DurableGraph.slnx --verbosity quiet
dotnet test tests/DurableGraph.StateStore.Storage.Tests/DurableGraph.StateStore.Storage.Tests.csproj --no-build --verbosity quiet
dotnet test tests/DurableGraph.Tests/DurableGraph.Tests.csproj --no-build --verbosity quiet --filter FullyQualifiedName~RawBaseStorage
dotnet test DurableGraph.slnx --no-build --verbosity quiet
git diff --check
```

根最终 build 为 0 warnings / 0 errors。初次 Storage 聚焦 93/93、真实 SG 集成 1/1 通过；
补入两条 body 缓冲后失败用例并重建后，全套 **559/559**，零跳过：
Storage 95、DurableGraph 330、Serialization 94、StateStore 40。
真实 SG 见证写入两个正常 Revision 与两条损坏引用候选；清空捕获返回的 body 数组、关闭并
只读重开文件，再按 exact Revision 读取与解码。重复 root、独立 string 身份、空串与 null 均保留所选语义。

本片未修改 Generator/runtime/policy、NuGet 打包或现有单包消费接线；仅测试程序集增加 Storage 引用，
因此没有再次运行 PackageConsumerProbe，也没有把 Storage 加入 runtime 包。
所有持久化证据仅针对上述显式 metadata fixture 和正常关闭重开，不扩称自描述加载或持久发布。
