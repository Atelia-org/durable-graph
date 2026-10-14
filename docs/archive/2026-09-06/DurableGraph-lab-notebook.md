> 历史快照：2026-09-06，来源提交 `54a33df`。正文按原稿保存，仅重定位 Markdown 相对链接。
> 文中的当前、下一步和状态均属于当时语境，不作为当前规格或排期。
> 续工从 [产品工作集](../../../src/PROJECT-STATE.md) 开始；本文件冻结，仅修复引用或史实错误。

# DurableGraph 实验簿

> 状态：Living Working Note  
> 用途：记录逐步实验形成的认识，帮助作者与 Coding Agent 跨会话恢复项目上下文。  
> 边界：本文不是需求规格、实现指令或当前行为的权威来源；当前源码、测试和可复现输出优先。

## 1. 记录约定

重要陈述使用以下状态：

- **Observed**：已由当前源码、测试或可复现实验直接观察。
- **Decided**：当前采用的工作决定；后续证据允许推翻。
- **Tentative**：有一定依据，但尚未得到足够验证。
- **Rejected**：已经试验或讨论后放弃，并保留放弃原因。
- **Open**：尚待回答的问题。

只记录会影响后续工作的事实、判断和悬案，不复制聊天过程或大段命令输出。

## 2. 当前基线

记录日期：2026-09-06

- **Observed**：仓库已跑通 boxed-value 内存 Save/Load/upgrade 与隔离探针 R1–R3b；产品已实现
  SG Versioned DTO/body、string 引用 Capture/读取及同 Frame raw Base 文件存取，仍无完整领域图恢复、
  对象 Delta/prior 链或 Save/发布闭环。精确产品边界见 [共享工作集](../../../src/PROJECT-STATE.md)。
- **Observed**：`DurableGraph.slnx` 当前包含原 runtime、Generator、Build tool、CLI 和 Tests，以及三个
  StateStore 产品项目和三个配套测试项目；Build tool 仍是随 NuGet 包部署的私有 snapshot-history
  publisher/verifier，不承载运行时持久化语义。
- **Observed**：核心库将 Generator 作为 Roslyn analyzer 引用；CLI 引用核心库；测试项目引用核心库和 Generator。
- **Observed**：运行时项目和测试项目目标框架为 `net10.0`；Generator 为兼容 Roslyn 加载而目标框架为 `netstandard2.0`。
- **Observed**：`Directory.Build.props` 将程序集名、根命名空间和包名统一加上 `Atelia.` 前缀。
- **Observed**：`docs/DurableGraph-target-design-v0.md` 描述目标设计和 P0-P7 原型设想，但尚不代表实现事实。
- **Decided**：近期不进一步细分程序集，也不为了分类整齐而提前整理命名空间。
- **Decided**：采用自底向上的细粒度实验；想清楚一部分、实现和验证一部分，再逐步拼合全景。
- **Decided**：允许后续依据实验结果重构命名、命名空间、项目边界和暂定 API。

## 3. 当前工作地图

### 已有材料

- Target design：提供问题空间、候选不变量和远期方向。
- 空项目骨架：提供最小构建、测试、Generator 和 CLI 边界。
- Schema 值模型：提供 `TypeTag`、`DurableFieldInfo` 和 `DurableSchema`。
- 内存 SchemaStore：提供 exact version 注册、查询、幂等和冲突语义。
- Schema Generator：从受限的 durable class/field 声明生成静态 `DurableSchema`。
- Boxed State demo：由 generated serializer 驱动 `InMemoryStateStore`，验证 Schema-first Save 与 validate-before-Deserialize。
- Source Generator history feedback probe：隔离证明 `AddSource` 不会自行反馈为后续 `AdditionalFiles`，但显式 post-compile publisher 可以形成下一轮可见的 Snapshot History。
- Snapshot upgrade shape probe：固定 ordinary struct、`in/out`、partial implementation、definite assignment 与 overload 的 C# 语义边界。
- Package-delivered Snapshot History：单一 `Atelia.DurableGraph` 包向直接消费者交付 runtime、Generator、MSBuild 自动接入与私有 Build tool，并由真实 PackageReference probe 验证。
- Generated read-time upgrade：按 stored version 只分派一次，随后以强类型 Snapshot locals 和 required partial 相邻 handler 直达 current domain object；Load 不隐式写回。
- Research roadmap：按 Graph Delta semantic probe → generated graph operations → normalized graph Load → logical delta chain → binary codec → persistent publication 的证据依赖安排后续切片。
- Flat Graph Delta R1：已用 fixture-only latest typed Snapshot baseline、flat ID table、`RequiresRewrite`、whole-object Upsert 与 success-only baseline replacement 完成可执行探针。
- Generated Graph Operations R2：internal、无 `[Generator]` 的 probe generator 已产生强类型 Capture/Snapshot equality/reference visitor，并与 R1 oracle 做差分验证；默认 package analyzer 路径不运行它。
- Stored Graph Normalization R3a：test-only mixed V1/V2 record table 经全表 exact preflight、typed decode/upgrade 与 source-reference gate 归一化成 current-Snapshot baseline。
- CLR Graph Materialization R3b：只对 normalized current root closure allocate-all/hydrate-all，恢复 sharing/cycles 后 root-only exposure；disconnected source rows 不分配。
- Multi-segment StateStore candidate：DB-014 已选择 1-based FileNumber、canonical filename 与
  `BackwardFileDistance` 任意 earlier-file reference；阶段 A `TARGET-DESIGN.md` 只负责 G0-G4 in-memory
  地址、OVD、Base/Delta、Save/Load、策略/评价；阶段 B 已建立 `StateStore -> StateStore.Storage`，由 Storage
  同时引用 RBF substrate 与 BCL-only `StateStore.Serialization` leaf，并完成 live ObjectId membership 的
  semantic/wire/真实文件纵切。StateStore 尚无 ObjectVersion consumer，暂不直接引用 Serialization。
- StateStore TwoLeg 基础/地址/派生文档：相邻 FileScope、1-bit `RelativeFrameTicket`、A/B/C evacuation 与
  `CanPrepareAndRotate` 已被产品路线 supersede，完整保留为 TwoLeg 技术储备。
- Two-leg rotation probe：独立 Arena/Baselines/Tests subsolution 已冻结；保留 runtime OVD、Base/Delta、
  continuous rotation、typed admission/no-fallback、completion certificate 与 evaluator 证据，不再维护活跃策略 roadmap。
- Candidate design branches：在 `docs/design-branches/` 隔离尚未裁决的架构分叉。
- 本实验簿：保存随实验演化的项目认识。

### 后续方向

- **Observed**：读取阶段的闭世界升级管线已经落地；Load 本身不回写，显式 Save 才保存升级后的当前版本对象。
- **Decided**：首轮升级机制只支持唯一相邻版本链 `V1 → V2 → V3`；出现真实跳版或分支消费者前不引入一般图。
- **Decided**：Generator 生成一个线性 read-time coordinator：入口只有一次 version switch，各 case decode 对应 Snapshot 后以 `goto SnapshotVnReady` 进入共享的顺序相邻边；ordinary struct 与 required partial `void UpgradeV1ToV2(in old, out next)` 契约不变。runtime historical binding/upgrade registry 继续暂缓。
- **Tentative**：快速原型的 local real build 自动 append checked-in Snapshot History；CI/design-time 只读，单 writer/单 TargetFramework/串行发布。显式 Accept target 记录为竞争分支。
- **Observed**：上述 local publish/CI verify 快速原型已由包内 `build/*.props/targets` 和 `DurableGraph.Build` 落地；其工作流已实现，但 snapshot 格式和发布模型仍是可替换的原型边界。
- **Observed**：DB-019 已为显式 SchemaOnly 开放同编译领域继承的元数据/history；默认 boxed serializer 仍关闭领域继承。
  声明层 FieldId 与 exact base 传播已选定，继承 payload/private body/升级仍待后续分片。
- **Observed**：EXP-011 已跑通 fixture-only 单类型 Graph Delta 语义探针；逻辑 baseline 是带 RootId 的 flat ID table，每项保存 current Snapshot 与 `RequiresRewrite`，未引入 bytes、持久 head 或正式 DurableId。
- **Observed**：EXP-012 已跑通 isolated generated graph operations；caller-provided `TIdentity : struct` 只是一条 test seam，未引入正式 DurableId、Reference TypeTag 或产品 Generator 支持。
- **Decided**：当前不把 self-reference 半接入 scalar-only Schema History/boxed Serializer；默认 `DurableSchemaGenerator` 继续 DG0007 fail closed，R3a/R3b 均留在 test-only logical graph。
- **Observed**：EXP-013 已跑通 test-only StoredGraphImage normalization；全表 Schema preflight 先于 Decode，V1/V2 均归一化为 current `ProbeSnapshot`，并保留完整 source record table 与 historical rewrite obligation。
- **Decided**：R3a 失败只承诺不修改输入、不返回 partial baseline；decode/upgrade hook 自身的外部 side effect 不可回滚。其 current baseline 随后成为 R3b materialization 输入。
- **Observed**：EXP-014 已跑通 root-only two-pass materialization；sharing/self-cycle/two-node cycle 恢复 `ReferenceEquals`，constructor/initializer 被绕过，transient 保持零值。
- **Decided**：materialized CLR root 是 disposable working graph，不是 baseline/StateMap authority；`RequiresRewrite` 只影响 Save。R4 将从带 disconnected rows/rewrite obligations 的 source StateMap 开始验证 apply 后 exact clean closure。
- **Decided**：historical payload 在读取边界 exact decode 并升级到 current Snapshot；reachable upgraded node 在下一次显式 Save whole-object rewrite，unreachable upgraded node 不被保活。
- **Decided**：normalized baseline 是派生比较投影，不复制 per-entry source Schema/object address；未来 persistent Save 通过 graph-level exact head + authoritative StateMap 与 projection 的同源 bundle 取得 provenance。
- **Decided**：当前持久化优先级转向 DB-014 / `MultiSegmentStateStoreProbe`；文件 rollover 与
  Base/Deltify/OVD membership 解耦，冷 ObjectVersion 不因切文件而强制 relocation。
- **Observed**：MultiSegment 首切片只实现 FileNumber、canonical filename、absolute address、
  RelativeFrameTicket 与 canonical Base128 fail-close；G1 又实现 strong FrameTicket、same-file strictly-earlier、
  append-only in-memory Segment/Store、唯一 provisional envelope estimator 与 tail-triggered rollover：crossing
  append 留在当前 Segment，下一 Save 在 final-origin render 前轮转。typed hard reject 与 FileNumber overflow 均
  不 append/publish；显式 multi-origin witness 保留 relative re-encode 定律；G2 又以 F1-F4 验证 Revision shared Prior、OVD/ObjectVersion
  Base/Delta 与 exact-head current reconstruction。冷 head 可留在 F1，OVD Base lineage-only prior 不进入 current
  closure；G3 又闭合 exact-parent normalization、origin-free whole-candidate plan、OVD Base historical-head
  reencode、shared-prior lineage、append-before-publish orphan 与 cache-install failure。Stage-A session 只从 empty
  Store 起步，不宣称 reopen；G4 最终接入 deterministic workload、pure ReadAmplification+BaseBudget policy 与
  admitted-only W/P/F/R/L evaluator，并产出 all-Delta/all-Base/adaptive raw report。阶段 A G0-G4 已闭合，正式
  reopen 与 GC 仍未实现。
- **Decided**：TwoLeg 全子树保持完整可执行但默认不活跃；相邻两文件 closure、1-bit relative ticket、
  A-debt、Stay/Rotate 与 terminal settlement 不迁移到 DB-014 正常 Save。
- **Decided**：可复用的是 absolute-normalize/relative-reencode、OVD authority、Base/Delta reconstruction、
  current/lineage 分层、whole-candidate gate、no-fallback apply、deterministic workload 与 raw evaluator 思想。
- **Decided**：R4–R7 logical graph 依赖顺序保留；产品整合前与 MultiSegment 持久化探针继续分离。
- **Observed**：SameStateRebase 独立 witness 证明恰一次 rebase 保持 ordinal、增加总 W，并降低 rebase 后累计
  R；OVD required-frame observation 只读取最终 live External targets，且与 object paths 做 full-Frame 去重。
- **Decided**：按阶段 B 文档把已证明语义晋升为正式 StateStore Sub-System；先建立独立
  `Atelia.DurableGraph.StateStore` 与 `Atelia.DurableGraph.StateStore.Storage` 空壳；前者单向引用后者，只有
  Storage 直接引用当前 `RbfSegmentStore` source，正式 reopen 仍待后续切片。
- **Decided**：MVP 固定使用 ReadAmplificationBaseBudgetPolicy；先以全部 post-live 对象的估算 DTO 输入，
  输出稀疏 Base/Delta 写计划，不以前置实现序列化为条件。[DB-015](../../design-branches/0015-statestore-object-representation-policy/README.md)
  已落实为 StateStore 内部纯 selector，参数采用整数倍数与整数百分比；真实 codec、对象内容与提交接入保持后续切片。
- **Open**：Schema runtime representation 与 canonical authority 的候选分叉记录在 `DB-001`，等待 exact codec/persistent format 实验裁决。
- **Open**：哪些类型和 API 最终属于核心程序集，等待真实代码形状出现后再判断。

### 当前自洽边界

截至 EXP-014，公有 demo 仍只具有 EXP-010 的受限、单线程结构自洽边界；EXP-011/012/013/014 是隔离证据，不扩大 package/runtime 保证：

- 成功保存的每条 State record 都记录 exact `(SchemaId, Version)`，且对应 Schema 已先登记在同一个 `InMemoryStateStore.SchemaStore`。
- Schema conflict 和 serialization failure 都不会覆盖 slot 中原有 State。
- Load 从 State key 解析 authoritative exact stored Schema，并在进入 serializer 前拒绝 SchemaId 不同；generated serializer 在 payload decode 前验证其 generated historical exact Schema 的 version 和完整 shape。
- generated serializer 只按稳定 FieldId 处理当前四种 scalar durable field，并忽略 transient field。
- known historical version 的 payload 先 decode 到强类型 Snapshot，再依次升级；只有全部成功后才分配 current domain object。Load 不登记 current Schema 或覆盖旧 State。

当前不能声称：

- **对象始终语义自洽**：Save 不检查输入对象的领域 invariant；Load 绕过构造器，尚无 cross-field validation、RebuildTransient 或 repository-level invariant pass。
- **两个 Store 构成原子精确合集**：失败的 Save 可以在 SchemaStore 留下未被 State 引用的 Schema；当前只保证 State → Schema 引用闭合，不保证 Schema → State，也没有 unified commit。
- **任意输入都安全**：手写 serializer 可以返回任意 object；boxed values 只做浅拷贝，missing/type-mismatched fields 虽会在 handler 和领域对象分配前失败，但尚无最终统一错误模型；额外字段仍被忽略。
- **并发或故障下仍成立**：两个 Store 都不提供线程安全、事务、crash recovery 或 durability。

因此后续可以依赖“known historical State 能被 exact Schema 绑定并只在内存中升级到 current”，但不能把 handler 纯度、对象 invariant、跨 Store atomicity 或持久化安全当作已解决。

## 4. 实验记录

### EXP-001：Durable 类型与字段分类契约

状态：Concluded

日期：2026-08-27

问题：能否用最小公共 API 明确 durable object 的 opt-in、稳定 Schema 身份，以及每个字段是否参与持久化？

本轮明确不回答：

- Generator 如何检查每个实例字段恰好选择 `[DurableField]` 或 `[Transient]`。
- durable object 实例身份、继承图、序列化和落盘格式。

最小实验：

- 引入空的抽象基类 `DurableBase`。
- 引入仅作用于 class 的 `DurableTypeAttribute(string schemaId, int version)`。
- 引入仅作用于 field 的 `DurableFieldAttribute(int fieldId)`。
- 引入仅作用于 field 的 `TransientAttribute`。
- 用契约测试验证 Attribute 目标、参数和显式字段分类示例。

观察：

- **Observed**：`System.Attribute` 已定义 `TypeId` 属性；在 `DurableTypeAttribute` 上暴露同名属性会产生成员隐藏警告和反射歧义。
- **Decided**：Attribute 对外使用 `SchemaId` 属性，避开 BCL 的 `Attribute.TypeId`。
- **Decided**：`SchemaId` 必须为非空白字符串，Schema version 和 FieldId 必须为正整数；精确的 Schema 版本由 `(SchemaId, Version)` 二元组标识。
- **Decided**：Type 与 Field 使用两个 Attribute 类型，避免仅靠 Generator 解释条件必填参数。
- **Decided**：durable 字段与 transient 字段采用显式标记，不以 CLR 字段名称充当长期身份。
- **Observed**：当前版本尚未强制所有实例字段必须二选一；该能力需要后续 Generator diagnostic。

结论：最小元数据契约成立；solution 构建为 0 warning / 0 error，12 个契约测试全部通过。

相关源码/测试：

- `src/DurableGraph/DurableBase.cs`
- `src/DurableGraph/DurableTypeAttribute.cs`
- `src/DurableGraph/DurableFieldAttribute.cs`
- `src/DurableGraph/TransientAttribute.cs`
- `tests/DurableGraph.Tests/DurableMetadataContractTests.cs`

### EXP-002：Wire-format-independent Schema 值模型

状态：Concluded

日期：2026-08-27

问题：能否先定义足够支撑版本化 Schema 冲突实验、但不绑定序列化格式的最小内存模型？

本轮明确不回答：

- Schema 如何编码、落盘和计算跨进程稳定的 `SchemaHash`。
- CLR field type 如何由 Generator 映射为 TypeTag。
- SchemaStore、StateStore、版本升级器和加载流程。
- nullability、collection、durable reference 等复合类型语义。

最小实验：

- `TypeTag` 只枚举 `Boolean`、`Int32`、`Int64` 和 `String`，并保留值为 0 的 `Invalid` sentinel。
- `DurableFieldInfo` 是由正整数 FieldId 与有效 TypeTag 构成的只读 record struct。
- `DurableSchema` 保存 SchemaId、正整数 Version 和不可变字段序列。
- 构造 Schema 时复制输入、按 FieldId 排序，并拒绝无效或重复 FieldId。
- Schema 结构相等比较使用 ordinal SchemaId、Version 和规范排序后的完整字段序列。

观察：

- **Rejected**：没有采用 `record struct DurableSchema(..., DurableFieldInfo[] Fields)`；可变数组别名、数组的引用相等和 struct 默认值会给 Schema 比较制造错误语义。
- **Decided**：`DurableSchema` 是不可变 sealed class，公开 `ImmutableArray<DurableFieldInfo>`。
- **Decided**：字段声明顺序不属于 Schema 语义；Fields 在构造时按 FieldId 升序规范化。
- **Decided**：TypeTag 的整数值属于 Schema 元数据身份，显式赋值并由测试锁定；当前枚举值不代表 serializer 已实现。
- **Observed**：相同 `(SchemaId, Version)` 可以构造出结构不相等的 Schema；后续 SchemaStore 应将该情况判为硬冲突。
- **Observed**：`GetHashCode()` 只满足进程内对象相等契约，不是稳定 `SchemaHash`，不得落盘或跨进程比较。

结论：最小 Schema 值模型成立；solution 构建为 0 warning / 0 error，32 个测试全部通过。

相关源码/测试：

- `src/DurableGraph/TypeTag.cs`
- `src/DurableGraph/DurableFieldInfo.cs`
- `src/DurableGraph/DurableSchema.cs`
- `tests/DurableGraph.Tests/DurableSchemaContractTests.cs`

### EXP-003：内存 SchemaStore 冲突语义

状态：Concluded

日期：2026-08-27

问题：能否在不涉及 wire format 和磁盘 authority 的前提下，验证 exact version Schema 注册与 fail-closed 冲突规则？

本轮明确不回答：

- 持久化 SchemaStore 的编码、事务、发布和恢复。
- 多线程访问与多写者并发。
- Schema upgrade path、StateStore 和 load orchestration。
- Store 接口的长期边界。

最小实验：

- `InMemorySchemaStore.Register(schema)` 首次登记 Schema。
- 相同 `(SchemaId, Version)` 且结构相等时幂等，并返回首次登记的规范实例。
- 相同 key 但结构不等时抛出 `SchemaConflictException`。
- `GetRequired(schemaId, version)` 只做 exact lookup，缺失时抛出 `SchemaNotFoundException`。

观察：

- **Decided**：当前只有一种 Store，不提前抽取 `ISchemaStore`。
- **Observed**：冲突注册不会替换或污染已经登记的 Schema。
- **Observed**：不同 Version 可以在同一 SchemaId 下共存。
- **Decided**：SchemaId 使用 ordinal、case-sensitive 身份语义。
- **Observed**：typed conflict 同时携带 registered 与 conflicting Schema，可供后续诊断使用。
- **Observed**：typed not-found 精确保留缺失的 SchemaId 与 Version，不提供 latest fallback。
- **Decided**：`InMemorySchemaStore` 明确不承诺持久化或线程安全，不能被描述为 authority Store。

结论：内存 SchemaStore 的最小 fail-closed 语义成立；solution 构建为 0 warning / 0 error，44 个测试全部通过。

相关源码/测试：

- `src/DurableGraph/InMemorySchemaStore.cs`
- `src/DurableGraph/SchemaConflictException.cs`
- `src/DurableGraph/SchemaNotFoundException.cs`
- `tests/DurableGraph.Tests/InMemorySchemaStoreTests.cs`

### EXP-004：首个 DurableSchema Source Generator

状态：Concluded

日期：2026-08-27

问题：能否从最小 durable class 声明生成可执行的静态 `DurableSchema`，并在编译期拒绝当前契约内的不明确字段？

本轮明确不回答：

- properties、auto-property backing field、继承字段、嵌套/泛型/record durable type。
- nullability、collection、durable reference 和用户自定义 value type。
- serializer/deserializer、SchemaHash、Store 自动注册和跨类型 Schema key 冲突。
- 历史版本升级路径与 State load orchestration。

最小实验：

- 实现 `DurableSchemaGenerator : IIncrementalGenerator`。
- 支持顶层、非泛型、非 record、`partial` 且直接继承 `DurableBase` 的 class。
- 要求直接声明的实例 field 恰好具有 `[DurableField]` 或 `[Transient]`。
- 把 `bool`、`int`、`long`、`string` 映射为现有 TypeTag。
- 为每个合法类型生成公共静态 `Schema` 属性。
- 用 DG0001-DG0009 覆盖类型形状、Schema metadata、字段分类、FieldId、field type、成员冲突和静态字段误标。

观察：

- **Decided**：Generator 不引用 runtime project，避免 `DurableGraph -> Generator -> DurableGraph` 循环；它按 metadata name 识别契约，并在生成代码中使用全限定 runtime 类型名。
- **Decided**：一次 compilation 生成单一 `DurableSchemas.g.cs`；类型按完全限定名排序，字段按 FieldId 排序，换行统一为 LF。
- **Observed**：Generator-driver 测试能编译输入源码、运行 Generator、emit 动态程序集，并从生成的静态属性读回正确 `DurableSchema`。
- **Observed**：仅比较简单类型名和 namespace 会误认同 namespace 下的嵌套 lookalike；metadata matcher 已收紧为拒绝 containing type，并有 DurableBase/Attribute 回归测试。
- **Observed**：单个非法 durable type 只抑制自身生成，不妨碍同 compilation 中其他合法类型。
- **Decided**：当前直接继承限制使继承字段语义保持关闭；尚未设计前不得静默遍历用户基类。
- **Observed**：auto-property 等合成存储目前被忽略，符合本轮 field-only 非目标，但仍是后续必须显式裁决的边界。
- **Observed**：独立只读审查在修复 metadata lookalike 问题后批准实现，无 blocking 或 medium finding。

结论：首个 Schema Generator walking skeleton 成立；solution 构建为 0 warning / 0 error，58 个测试全部通过。

相关源码/测试：

- `src/DurableGraph.Generator/DurableSchemaGenerator.cs`
- `src/DurableGraph.Generator/AnalyzerReleases.Unshipped.md`
- `tests/DurableGraph.Tests/DurableSchemaGeneratorTests.cs`

### EXP-005：Schema-gated boxed State round-trip

状态：Concluded

日期：2026-08-27

问题：能否用刻意潦草的进程内 State 表示，首次跑通“generated serializer → Save 自动登记 Schema → Load 先校验 Schema → generated deserializer”的闭环？

本轮明确不回答：

- wire format、跨进程/重启持久化、durability、事务和并发。
- DurableId、共享引用、循环图、collections、properties 与继承字段。
- 旧版本自动升级、upgrade registry、RebuildTransient 和对象图 invariant validation。
- malformed/untrusted boxed payload 的完整错误模型。

最小实验：

- `IDurableSerializer<T>` 暴露 exact Schema、FieldId → boxed value 的 Serialize，以及反向 Deserialize。
- `InMemoryStateStore` 用 ordinal string slot 保存 `(SchemaId, Version, boxed fields)`；slot 明确不是 DurableId。
- Save 在调用 serializer 和替换 State 前注册 Schema。
- Load 在调用 deserializer 前校验 stored exact Schema 的 identity、version 和 shape。
- Generator 在 `Schema` 旁生成静态 `Serializer`，支持当前四种 scalar TypeTag。
- generated Deserialize 使用 `RuntimeHelpers.GetUninitializedObject` 创建实例并直接填充 durable fields。

观察：

- **Decided**：不使用 obsolete 的 `FormatterServices.GetUninitializedObject`；.NET 10 路径使用官方替代 `RuntimeHelpers.GetUninitializedObject`。
- **Observed**：只有带参构造的动态测试类型可成功恢复；Load 不再次运行构造器或 field initializer。
- **Observed**：所有 durable fields 由生成代码赋值，transient field 保持零值；尚无 RebuildTransient。
- **Observed**：Schema conflict 在 Serialize 前失败；serialization failure 不覆盖旧 State；Load mismatch 在 Deserialize 前失败。
- **Decided**：当前 boxed fields 是浅拷贝的 `Dictionary<int, object?>`，仅用于同进程流程实验，不构成 serialization 或 persistence 证据。
- **Decided**：当前不支持继承，durable type 必须 sealed，防止派生实例经基类 serializer 静默截断。
- **Decided**：readonly durable field 被 DG0011 拒绝；generated deserializer 必须能直接写入全部 durable fields。
- **Observed**：缺失 FieldId 或 boxed type 错误由 dictionary/cast 自然失败，多余字段被忽略；这些不是最终 malformed-input contract。
- **Open**：当前 Deserialize 在读取全部字段前分配未初始化对象。内存 Store 正常路径不会产生 malformed boxed state，但持久化或不可信输入阶段必须先读入/验证全部 locals，再分配对象，避免带 finalizer 的半初始化对象产生可观察行为。
- **Observed**：独立审查发现并促成 sealed-type 和 enclosing type named `Schema` 两个 fail-closed 修复；复审后无 blocking 或 medium finding。

结论：第一个 Schema-gated State round-trip demo 成立；solution 构建为 0 warning / 0 error，71 个测试全部通过。

相关源码/测试：

- `src/DurableGraph/IDurableSerializer.cs`
- `src/DurableGraph/InMemoryStateStore.cs`
- `src/DurableGraph/StateSchemaMismatchException.cs`
- `src/DurableGraph.Generator/DurableSchemaGenerator.cs`
- `tests/DurableGraph.Tests/InMemoryStateStoreTests.cs`
- `tests/DurableGraph.Tests/DurableSchemaGeneratorTests.cs`

### EXP-006：Source Generator Snapshot History feedback

状态：Concluded

日期：2026-08-27

问题：Source Generator 能否把本轮生成的 Durable shape 自动累积为后续 compilation 的 `AdditionalFiles`，并在不保留旧领域类源码的情况下重建强类型历史 Snapshot？

本轮明确不回答：

- Snapshot History 的正式 canonical 格式和 durable authority。
- 是否允许普通 build 改写受版本控制的源码树。
- 并行 build、多 TargetFramework、原子发布和跨程序集 history ownership。
- version-aware runtime serializer、真实 upgrade handler contract 和 StateStore 集成。

最小实验：

- 在 `experiments/SourceGeneratorHistoryProbe/` 建立与正式 Generator/Runtime 隔离的 Roslyn Generator 和单一 consumer project。
- 同一 consumer project 由 `ProbeVersion` 分别只编译 V1 或 V2 领域源码；V2 源码没有 V1 领域类副本，却直接引用 generated `CharacterSnapshotV1` 与 `CharacterSnapshotV2`。
- Generator 从当前 marked fields 和显式 `*.dgsnapshot` AdditionalFiles 生成 sealed reference Snapshot，并额外产生 comment-only snapshot candidate。
- opt-in `AfterTargets="CoreCompile"` target 只在成功编译后把 candidate 复制到外部 history；下一次独立 project evaluation 才把它作为 AdditionalFile。
- PowerShell runner 覆盖 no-feedback 负对照、V1 publish、clean V2 build、强类型 handler 执行、clean rebuild 和重复发布幂等。

观察：

- **Observed**：关闭 publisher 时，V1 `AddSource` candidate 即使仍物理存在，下一次 V2 build 也不会把它视为 AdditionalFile；V2 因缺少 `CharacterSnapshotV1` 按预期编译失败。
- **Observed**：开启 publisher 后，成功 V1 build 产生一个外部 V1 snapshot；clean V2 build 读取它并同时生成 `CharacterSnapshotV1`/`V2`，强类型 `V1 → V2` 方法成功执行。
- **Observed**：AdditionalFiles feedback 延迟一个 build；同一次 compilation 看不到 post-compile 新发布的文件。
- **Observed**：clean V2 rebuild 可只依赖显式 history 重现，重复发布保持两个 history 文件及其 SHA-256 不变。
- **Observed**：完整 probe 矩阵和现有 solution gate 都能做到 0 warning / 0 error；负对照中的预期编译错误由 runner 显式要求。
- **Rejected**：依靠 Source Generator 自身、compiler cache 或 `obj` 中 `.g.cs` 隐式累积历史。`AddSource` 是当前 compilation 的派生输出，不是下一轮输入 authority。
- **Tentative**：MSBuild hook、自制 build tool 或 CLI 可以承担显式 side effect；本实验只证明技术可行性，没有选定正式发布 owner。
- **Open**：正式 publisher 必须进一步定义同 key 异形冲突、并行与原子发布、CI/IDE/design-time 权限，以及 build 改写工作树是否可接受。

结论：强类型 Snapshot History 可以由“显式历史输入 → 纯 Generator 投影”稳定重建；全自动累积需要 Generator 外部的受控 build side effect，而且只能供下一次 build 使用。

相关材料：

- `experiments/SourceGeneratorHistoryProbe/README.md`
- `experiments/SourceGeneratorHistoryProbe/Run-Probe.ps1`
- `experiments/SourceGeneratorHistoryProbe/Probe.Generator/HistoryProbeGenerator.cs`
- `experiments/SourceGeneratorHistoryProbe/Probe/HistoryProbe.csproj`

### EXP-007：Snapshot struct 与 Upgrade 签名语义

状态：Concluded

日期：2026-08-27

问题：ordinary struct Snapshot 与 `in old/out next` 是否能为强类型相邻升级提供可执行的 C# 契约？`out`、`bool`、return overload 和 stack allocation 的真实边界是什么？

本轮明确不回答：

- struct 相比 class 的真实性能、GC、copy 或 stack pressure。
- production Generator 如何生成 Snapshot/partial declaration/direct pipeline。
- nullability、领域 invariant、malformed payload 和 handler error wrapping。
- durable inheritance flattening 和 jump-version path selection。

最小实验：

- 在 `experiments/SnapshotUpgradeShapeProbe/` 用同一 .NET 10 project 条件编译一组成功/失败源码。
- 成功路径执行 ordinary struct 的直接强类型 `in/out` V1 → V2，并用显式 out target 选择 V1 → V3 overload。
- 失败路径精确锁定 missing target field、false-without-out、missing partial implementation、return-only overload、`out var` ambiguity 和 managed stackalloc diagnostics。

观察：

- **Observed**：逐字段写完 V2 out Snapshot 可 0 warning / 0 error 编译并执行；漏写 Field2 产生 CS0177。
- **Observed**：显式 accessibility 的 partial upgrade declaration 缺 implementation 产生 CS8795。
- **Observed**：`newValue = default` 可以绕过逐字段 definite-assignment，且 string field 得到 null；out 只是结构赋值 tripwire，不是有效性证明。
- **Observed**：bool false path 仍须赋 out，否则同样产生 CS0177；当前没有 expected rejection consumer 支持引入 bool。
- **Observed**：两个只靠返回 V2/V3 区分的 `Upgrade(V1)` 产生 CS0111；out target type 可区分 overload，但 `out var` 产生 CS0121。
- **Observed**：含 string 的 managed Snapshot 作为 `stackalloc` element 产生 CS0208。
- **Decided**：当前原型使用 ordinary mutable struct + version-qualified required partial `void/in/out`；失败通过异常传播。
- **Deferred**：Try/bool 等到首个 expected data rejection；readonly/record struct、class return 和 `ref struct` 保留在 DB-003。
- **Rejected**：把 ordinary struct 描述成“保证栈分配、零复制或整个 State pipeline 无装箱”。

结论：用户提出的 `out` 具有真实的字段级编译期帮助；struct 值语义值得进入下一 vertical slice，但性能与完整性主张必须保持为待验证假设。

相关材料：

- `experiments/SnapshotUpgradeShapeProbe/README.md`
- `docs/design-branches/0003-snapshot-value-shape-and-upgrade-signature.md`
- `docs/design-branches/0004-snapshot-history-authoring-and-publishing.md`
- `docs/design-branches/0005-durable-inheritance-flattening.md`

### EXP-008：Package-delivered Snapshot History workflow

状态：Concluded

日期：2026-08-27

问题：能否把 EXP-006 的外部 feedback 路径固化为下游只需直接 `PackageReference` 即可获得的可复用产物，并让本地构建自动发布、CI 构建只读校验？

本轮明确不回答：

- runtime read-time upgrade orchestration、historical serializer 与自动 Save 回写。
- 最终 canonical Schema/wire format、跨程序集 ownership 和一般版本图。
- multi-targeting、并行 writer 与多个 history 文件的事务发布。
- `buildTransitive` 间接消费者语义、IDE code fix 或显式 Accept UX。

最小实现：

- Generator 严格读取单-block `*.dgsnapshot` AdditionalFiles，要求 current Vn 的 V1...V(n-1) 连续存在，并以 DG0012-DG0016 拒绝 malformed、冲突、缺口、current mismatch 和 generated member collision。
- Generator 产生固定 comment-only candidate manifest，以及当前 durable partial class 内的 private ordinary `__DurableSnapshotVn` structs；历史成员只用稳定 `Field{id}` 与 TypeTag 映射重建。
- 新增独立 `DurableGraph.Build` net10 tool，提供 `publish`/`verify`；它严格解析、全批预检、create-only 发布、同形幂等，并用 SchemaId/content 的 SHA-256 形成不含原始 SchemaId 的文件名。
- `Atelia.DurableGraph` nupkg 显式包含 runtime、`analyzers/dotnet/cs` Generator、`build` props/targets 与 `tools/net10.0` Build tool；直接消费者不写 `Import`、Analyzer reference、AdditionalFiles 或 post-compile script。
- local 默认为 Publish；`ContinuousIntegrationBuild=true` 默认为 Verify；design-time 跳过，`Off` 只作为显式诊断逃生口。

观察：

- **Observed**：真实 local-feed restore 证明 package layout 会自动加载 Generator 与 build assets，项目文件只含一个普通 `PackageReference`。
- **Observed**：空 history 直接构建 V2 以 DG0014 失败且不发布；V1 成功构建发布 V1，随后 clean V2 build 从 V1 history 重建 V1/V2 private structs，并执行强类型 `in/out` 转换。
- **Observed**：V2 成功构建追加 V2；重复 local publish 内容哈希不变。CI Verify 不写 history；移走 current V2 后 CI 失败且不补写。
- **Observed**：候选使用固定 `DurableGraphSnapshotCandidates.g.cs`；没有 durable type 的成功 compilation 仍产生空 manifest，避免旧 manifest 被误当作当前候选，同时不会创建空 history 目录。
- **Decided**：AdditionalFiles 与 publisher 都只读取 history 根目录的 `*.dgsnapshot`；首版不承诺递归目录布局。
- **Decided**：包只放 `build/`，不放 `buildTransitive/`；当前只为直接引用 DurableGraph 的 C# durable project 自动接入。
- **Decided**：candidate manifest 与 checked-in history 使用不同 header；Generator 不接受把 candidate 重命名后冒充已发布 history。
- **Decided**：SchemaId 必须能由 strict UTF-8 无损编码；Generator 以 DG0002 拒绝孤立 UTF-16 surrogate，防止不同 ordinal SchemaId 折叠为同一 Base64 history identity。
- **Decided**：Generator 负责结构语义，Build tool 是 checked-in history 原始字节 canonicality 的最终 gate；例如 CRLF 可能先被 AdditionalText 文本层规范化，但 publisher/verify 仍会拒绝非 LF canonical bytes。
- **Observed**：当前 TypeTag build-time parser 只接受已锁定的 1...4；新增 TypeTag 时必须同步 Generator、publisher 与格式测试。
- **Open**：普通 build 写工作树在团队/IDE 场景中的摩擦、显式 Accept workflow 和 batch atomicity 仍保留在 DB-004。

结论：EXP-006 的技术验证已经成为一个可真实消费的单包 vertical slice；它证明 build-time Snapshot History 的自动累积与只读 CI gate 可行，但尚未实现读取旧 State 或调用 upgrade handler。

相关材料：

- `src/DurableGraph/build/Atelia.DurableGraph.props`
- `src/DurableGraph/build/Atelia.DurableGraph.targets`
- `src/DurableGraph.Build/`
- `tests/DurableGraph.Tests/SnapshotHistoryToolTests.cs`
- `experiments/PackageConsumerProbe/`
- `docs/design-branches/0004-snapshot-history-authoring-and-publishing.md`

### EXP-009：Generated static read-time upgrade coordinators

状态：Concluded

> 初始的每版本独立协调器代码形状已由 EXP-010 线性化；本实验建立的 runtime seam、强类型相邻链与无写回语义继续有效。

日期：2026-08-27

问题：能否让当前 compilation 仅凭 checked-in Snapshot History 和当前领域源码，生成从每个 known stored version 到 current 的强类型升级路径，并使 Load 升级成功后仍不隐式改写 State/Schema Store？

本轮明确不回答：

- wire format、persistent Store、对象图身份、增量保存或 Schema/State unified commit。
- 一般版本图、跳版本 handler、runtime registry、plugin/跨程序集贡献或 path selection。
- handler 纯度、领域 invariant、RebuildTransient 和统一 malformed boxed-payload 错误模型。
- O(N²) generated call-site/IL 在大量版本下的真实性能与体积影响。

最小实现：

- `IDurableSerializer<T>.Schema` 明确为 current/write Schema；`Deserialize` 接收 authoritative `storedSchema` 与 boxed fields。
- `InMemoryStateStore.Load` 只解析 stored exact Schema、拒绝不同 SchemaId，并把同 identity 的历史版本交给 version-aware serializer；它不登记 current Schema，也不写回 State。
- Generator 先建立 history-validated IR，再生成 serializer；同 compilation 两个 current CLR types 复用 SchemaId 时以 DG0017 双方 fail closed。
- 每个 durable type 生成 private ordinary `__DurableSnapshotVn` 与 required partial `UpgradeVnToVnPlus1(in,out)`；缺实现由 CS8795、漏赋目标字段由 CS0177 拒绝。
- nested serializer 生成每版 exact `DurableSchema`、唯一一次 stored-version switch，以及每个 stored version 的独立直达-current静态协调器。
- 每个协调器固定执行 exact shape validation → 起始 Snapshot decode → 相邻强类型 direct calls → 最后 `GetUninitializedObject` 并 hydrate current fields。
- unknown version、identity mismatch、shape conflict 和具体 edge failure 使用 typed exceptions；edge failure 保留原异常为 InnerException。

观察：

- **Observed**：动态 V1 compilation 生成并 Save 的 State 可由独立 V2 compilation 读取；第一次与重复 Load 都调用 V1→V2 handler，且 SchemaStore 没有 V2。
- **Observed**：显式 Save 已升级的 V2 object 后，slot 才推进到 V2；后续 Load 走 current coordinator，不再调用历史 handler。
- **Observed**：V1→V2→V3 handler 严格按顺序执行并传递强类型字段值；generated code 只含一次 `storedSchema.Version switch`。
- **Observed**：historical shape mismatch、缺字段和错误 boxed type 在 handler 与 current domain object 分配前失败；handler 异常被包装为带 SchemaId/from/to 的 `DurableUpgradeException`。
- **Observed**：真实 local nupkg consumer 用手写 V1 serializer 保存 boxed State，再通过 package-delivered `Character.Serializer` 连续 Load、显式 Save 和 current Load，证明 runtime/Generator/build assets 的发布组合也执行同一升级语义。
- **Superseded**：初次独立挑战曾在“每入口单帧直线协调器”和“多层 O(N) suffix helpers”之间选择前者；EXP-010 后来用 goto labels 找到单帧且 O(N) 的第三种形状。
- **Observed**：初始 O(N²) 只涉及所有入口累计生成的 call-sites/IL，一次 Load 的执行边数始终是 O(current-n)；EXP-010 消除了前者。
- **Deferred**：额外 boxed fields 继续忽略；`newValue = default` 仍可绕过 out 的逐字段 tripwire；用户 handler 仍可自行产生外部 side effect。

结论：闭世界唯一相邻链的 read-time upgrade vertical slice 成立；动态性被限制在一次版本入口分派，历史 payload 进入 handler 前已转成强类型 Snapshot，Store authority 只有显式 Save 才改变。

相关材料：

- `src/DurableGraph/IDurableSerializer.cs`
- `src/DurableGraph/InMemoryStateStore.cs`
- `src/DurableGraph/DurableUpgradeException.cs`
- `src/DurableGraph/UnsupportedSchemaVersionException.cs`
- `src/DurableGraph.Generator/DurableSchemaGenerator.cs`
- `tests/DurableGraph.Tests/InMemoryStateStoreTests.cs`
- `tests/DurableGraph.Tests/DurableSchemaGeneratorTests.cs`
- `experiments/PackageConsumerProbe/Run-Probe.ps1`
- `docs/design-branches/0002-read-time-version-upgrade-pipeline.md`

### EXP-010：Goto-label linear upgrade coordinator

状态：Concluded

日期：2026-08-27

问题：能否在不增加 helper frames、不改变任何 public/runtime/handler 语义的前提下，用 generated `goto` labels 共享相邻升级后缀，把所有版本入口累计生成规模从 O(N²) 降为 O(N)？

本轮明确不回答：

- JIT 是否复用不同 Snapshot locals 的 stack slots，或单个较大方法的首次 JIT 成本。
- 大型 Snapshot/长历史链的实测栈帧、机器码体积和吞吐。
- 一般版本图、跳边、循环或分支 path selection。

最小实验与实现：

- 独立 warnings-as-errors probe 预先声明全部 Snapshot locals；每个 switch case 完成 exact-version decode 后跳到 `SnapshotVnReady` label。
- switch 后只生成一份有序 labels；每个非 current label 调用并包装一条相邻 edge，然后自然落入下一 label；current label 只 materialize/hydrate/return 一次。
- 保持 shape-before-decode、decode-before-handler、handler-before-domain-allocation，以及 unknown/identity/shape/edge typed failure 语义不变。
- Generator tests 结构性锁定：一个 switch statement、没有 `DeserializeVn` methods、每个版本字段 decode 一次、每条 edge call 一次、`GetUninitializedObject` 一次。

观察：

- **Observed**：C# definite-assignment 接受 case→label 多入口与 labels 的顺序穿透；V1/V2/V3 和零字段历史 Snapshot 均成功动态编译执行。
- **Observed**：原有 V1 Save→V2 Load/no-writeback、V1→V2→V3 顺序、异常包装、shape/payload gates、CS8795/CS0177、DG0017 与 package E2E 语义保持不变。
- **Decided**：generated 源码和 IL 控制流结构现在随版本/边数量线性增长；一次 Load 仍执行 O(current-stored) 条边。
- **Open**：单方法包含全部 Snapshot locals；虽然其生命周期呈线性，`in/out` 会取地址，JIT 不保证 slot reuse。历史很长或 Snapshot 很大时需用实际 stack/JIT/IL 测量裁决。

结论：goto 在手写领域代码中通常需要克制，但在确定性 generated state-machine-like code 中恰好表达了“多个静态入口共享同一升级后缀”，以更小生成规模保留了单帧、强类型和直线可读性。

相关材料：

- `src/DurableGraph.Generator/DurableSchemaGenerator.cs`
- `tests/DurableGraph.Tests/DurableSchemaGeneratorTests.cs`
- `docs/design-branches/0002-read-time-version-upgrade-pipeline.md`

### EXP-011：Flat Graph Delta semantic probe

状态：Concluded

日期：2026-08-27

问题：能否在不引入产品 DurableId、Generator graph adapter、wire format 或 Store 的前提下，用单类型强类型 fixture 验证 flat latest-Snapshot baseline、identity-aware traversal、whole-object delta 和成功后 clean baseline 的核心状态律？

本轮明确不回答：

- historical payload → current baseline 的真实 Load integration。
- generated `Capture / Equals / VisitReferences`。
- two-pass CLR graph hydrate、正式 DurableId、异构类型和 Schema identity gate。
- binary codec、StateMap/head、persistent delta chain、commit 和 concurrent mutation。
- allocation、stack、throughput 或内存优化。

最小实现：

- 在测试项目内定义 internal `ProbeId`、`ProbeNode`、`ProbeSnapshot`、`BaselineEntry`、`NormalizedBaselineGraph` 和 `GraphDelta`，不修改 runtime public surface。
- `PlanSave` 用显式 stack 和单一 ID→CLR instance table 遍历 current root closure；同 ID/different instance 在 visited skip 前 fail closed。
- 每个节点只读取一次 reference fields，同一 locals 同时形成 Snapshot ID slots 和 child work items。
- missing、`RequiresRewrite` 或 Snapshot unequal 产生完整 Upsert；source baseline IDs 与 current reachable IDs 的差集成为 Unreachable。
- `AcceptForAssertion` 拒绝 Upsert/Unreachable overlap、遗漏 rewrite obligation、missing root、dangling reference 和结果 table 的 disconnected entries；成功时返回全新的 clean baseline。

观察：

- **Observed**：21 个聚焦测试覆盖 no-op、transient、leaf locality、identity replacement、sharing、cycles、duplicate ID、root replacement、rewrite/unreachable 分流、default ID、defensive copy、capture failure/retry、malformed candidate 和状态律。
- **Observed**：`RequiresRewrite` 不必携带 source version 就能满足当前比较与首次显式重写；未来 record provenance 仍属于 authority StateMap，不是本轮结论。
- **Observed**：baseline 可以包含因 normalization 而暂时 disconnected 的 source entries，但 accepted clean baseline 必须恰好等于新 RootId closure；独立审查发现并促成了该 gate。
- **Observed**：当 ProbeId immutable 时，无需同时维护 ID map、reference visited set 和 reachable set；一个 ID map 足以保留全部 R1 语义。
- **Observed**：Map/Set 容器枚举顺序没有被提升为语义；确定性展示由测试显式按 ID 排序。
- **Observed**：solution build 0 warning / 0 error，完整测试从 100 增至 121 且全部通过；三路最终复审无 blocker/medium。

结论：DB-006 R1 semantic probe 成立。它证明的是纯内存逻辑状态转换，不是产品对象图持久化；下一主线入口是 R2 generated graph operations。

相关材料：

- `tests/DurableGraph.Tests/GraphDeltaProbe.cs`
- `tests/DurableGraph.Tests/GraphDeltaProbeTests.cs`
- `docs/design-branches/0006-flat-graph-delta-prototype.md`
- `docs/DurableGraph-research-roadmap.md`

### EXP-012：Generated Graph Operations probe

状态：Concluded

日期：2026-08-27

问题：Source Generator 能否为单个 direct-self-reference durable type 产生 R1 所需的强类型 current Snapshot capture、durable equality 和 reference visitation，同时暂不定义正式 identity、Reference TypeTag、wire format 或产品 graph API？

本轮明确不回答：

- 默认/package `DurableSchemaGenerator` 如何发布 reference Schema/history。
- 正式 `DurableId`、allocator、异构 dispatch、polymorphism 或跨程序集 reference。
- historical graph payload normalization、two-pass hydrate、StateMap、bytes、commit 或 recovery。
- provisional `TIdentity`、delegate 与 generated private shape 的性能或最终 API 适合性。

最小实现：

- 在 Generator 程序集内新增 internal、无 `[Generator]` 的 `DurableGraphOperationsProbeGenerator`；测试用 Roslyn driver 显式运行，默认 analyzer discovery 保持不变。
- 支持四种 scalar 与字段类型恰好等于当前 durable type 的 direct self-reference；Snapshot reference slot 使用 caller-provided `TIdentity?`，CapturedReferences 保存具体 CLR child。
- `CaptureCurrent` 先按 FieldId 把 durable fields 各读入一个 local，再用同一 reference local 同时写 Snapshot ID 和 captured child；`DurableEquals` 比较 scalar/ID；`VisitReferences` 按 field slot 访问且不预先去重 shared child。
- generated-driven test coordinator 继续复用 R1 的 identity-conflict/cycle/reachability 语义，但 expected baseline 和 literal delta 不由被测 capture 生成。

观察：

- **Observed**：shared two-node cycle 的 root/child Snapshot、重复 alias visitation 和 transient omission 与 literal values 及 EXP-011 oracle 一致。
- **Observed**：两个不同 CLR child 具有相同 ID、或只修改 child 内容时，parent Snapshot 保持相等；child ID/null slot 改变时 parent Snapshot 改变。
- **Observed**：no-op、transient-only、child scalar locality 与 same-valued child replacement 的 generated delta 同时匹配 literal Upserts/Unreachable 和 R1 oracle。
- **Observed**：array、base-typed、cross-type reference 以 DG0007 fail closed；reserved helper 以不可配置的 probe-only DG0018 fail closed；失败的单类型 run 不产生空 hint。
- **Observed**：生成文本不受字段声明顺序影响并使用 LF；reference visit 明确按 FieldId。syntax/symbol/semantic gates 证明 generated normal path 没有 `object`/`dynamic` data flow、dictionary、TypeTag 或 Serializer。
- **Observed**：测试首先抓到 validation failure 后残留空 hint，以及文本扫描不足以排除 weak `object` flow 两个问题；修复后两路独立复审均无 blocker/medium。

验证：

- R1+R2 聚焦 `~Probe`：31/31 passed，其中 EXP-012 新增 10 cases。
- Generator 单项目：0 warnings / 0 errors。
- `DurableGraph.slnx`：0 warnings / 0 errors；完整 `DurableGraph.Tests`：131/131 passed。
- `dotnet format --verify-no-changes`：passed；真实 PackageConsumerProbe：passed，确认 packaged analyzer 仍只自动发现产品 Generator。

结论：R2 的 code-generation seam 获得可执行证据，但仍是默认不可发现的隔离探针；它没有改变严格 1...4 Snapshot History 或产品 self-reference 的 DG0007 边界。下一主线入口是 R3a test-only StoredGraphImage → normalized baseline。

相关材料：

- `src/DurableGraph.Generator/DurableGraphOperationsProbeGenerator.cs`
- `tests/DurableGraph.Tests/GeneratedGraphOperationsProbeTests.cs`
- `tests/DurableGraph.Tests/GraphDeltaProbe.cs`
- `docs/design-branches/0006-flat-graph-delta-prototype.md`
- `docs/DurableGraph-research-roadmap.md`

### EXP-013：Stored Graph normalization probe

状态：Concluded

日期：2026-08-27

问题：能否把 mixed exact-version logical records 全有或全无地归一化为 latest/current typed Snapshot baseline，并把 historical rewrite obligation 与完整 source record table 正确交给 R1 Save？

本轮明确不回答：

- R3b CLR placeholder allocation、reference hydrate、sharing/cycle `ReferenceEquals` 或 transient rebuild。
- product DurableId、Reference TypeTag、Generator/Store integration 或 historical wire bytes。
- heterogeneous dispatch、upgrade-created node、StateMap/head、commit、recovery 或 concurrency。
- decode/upgrade handler 外部 side effect 的 rollback。

最小实现：

- `StoredGraphImage` 从 entry sequence defensive copy 出 record table；duplicate/default ID、missing/default root 和 null record 在构造阶段 fail closed，`Records.Keys` 是唯一 `SourceRecordIds`。
- test-only `ProbeStoredSchema` 对 SchemaId/version/sorted FieldId-kind 做内容相等；logical kind 只有 `Int32` 与 `Reference`，不修改产品 TypeTag 1...4。
- 封闭 V1/V2 record variants 分别返回 `ProbeSnapshotV1` 与 current `ProbeSnapshot`；V1 handler 是 `void(in old, out current)`。
- normalization 先按 ID 对全表 preflight identity/version/exact shape/payload variant，确认 required handler；随后逐 record typed decode/upgrade、验证对完整 SourceRecordIds 的 references，全部成功后才返回 `NormalizedBaselineGraph`。

观察：

- **Observed**：mixed/reversed V1/V2 records 得到相同 current baseline 与按 ID trace；value-changing 与 value-preserving historical upgrades 都 `RequiresRewrite=true`，current record 为 false。
- **Observed**：identity、shape、unknown version 与 payload variant mismatch 均在全表任何 Decode/Upgrade 前失败。
- **Observed**：current、upgrade-produced 和显式 nullable-default reference 都 fail closed；坏引用位于 disconnected source record 时仍被检查。
- **Observed**：upgrade 删除旧 edge 后 target entry 仍保留；R1 PlanSave 对 matching current root 强制 Upsert historical root，并把断开的 target 标为 Unreachable。
- **Observed**：late typed Decode failure 与 late Upgrade failure 都不返回 partial baseline，保留 input records/inner exception；同一 image 修复后重试重新 decode 并返回完整 baseline。
- **Observed**：schema/entry input arrays、typed value payload 与 normalized baseline 相互 detached；core map/set 枚举顺序仍不成为语义。
- **Observed**：独立 correctness/test-evidence review 找到并促成 decode-failure retry 与 null-record coverage；最终无 blocker/medium。

验证：

- R3a 聚焦测试：9/9 passed。
- `DurableGraph.slnx`：0 warnings / 0 errors；完整 `DurableGraph.Tests`：140/140 passed。
- `dotnet format --verify-no-changes`：passed。
- 真实 PackageConsumerProbe：passed，确认 test-only R3a 未改变 packaged runtime/Generator/history 行为。

结论：R3a normalization state law 成立，但只是一套 test fixture evidence。其 baseline 随后成为 R3b 输入；StoredGraphImage、probe Schema 与 sorted processing policy 都未提升为 public/durable contract。

相关材料：

- `tests/DurableGraph.Tests/StoredGraphNormalizationProbe.cs`
- `tests/DurableGraph.Tests/StoredGraphNormalizationProbeTests.cs`
- `tests/DurableGraph.Tests/GraphDeltaProbe.cs`
- `docs/design-branches/0006-flat-graph-delta-prototype.md`
- `docs/DurableGraph-research-roadmap.md`

### EXP-014：Two-pass CLR graph materialization probe

状态：Concluded

日期：2026-08-28

问题：能否从 normalized current-Snapshot baseline 只物化 current root closure，先 allocate-all 再 hydrate-all，恢复 sharing/cycles，并在 allocation/hydration failure 时不返回 partial root？

本轮明确不回答：

- product DurableId/allocator、Reference TypeTag、Generator materializer 或 heterogeneous dispatch。
- `RebuildTransient`、graph invariant validation 与它们失败时的最终 exposure policy。
- StateMap/cache/session ownership、bytes、commit、recovery 或并发。
- allocation、stack、throughput 或内存性能。

最小实现：

- 从 baseline RootId 用显式 stack 计算 current reachable ID set；不修改或裁剪 baseline，disconnected source rows 不进入 CLR closure。
- 第一轮以 `RuntimeHelpers.GetUninitializedObject` 为每个 reachable ID 建立唯一 placeholder；第二轮才从完整 map 解析 references 并一次性 hydrate scalar/identity/edges。
- `ProbeNode` 增加 fixture-only one-time hydration seam：只允许 constructor-bypassed、尚未绑定 ID 的 placeholder 调用；正常构造或已绑定节点 fail closed。
- 全部成功后只返回 root；phase hook 只接收 `(Phase, Id)`，不暴露 placeholder/map。可插拔 allocator 仅用于 fault injection，默认路径不变。

观察：

- **Observed**：R3a historical/current/disconnected baseline 经 R3b 恢复 literal scalar、shared alias、two-node cycle 与 leaf；随后 R1 PlanSave 仍由 baseline flag 强制 Upsert historical root，并把 disconnected source row 标为 Unreachable。
- **Observed**：self-cycle、two-node cycle 与 shared references 均以 `ReferenceEquals` 恢复；重复 materialization 产生互不共享的新 CLR graphs，不安装 cache。
- **Observed**：所有 Allocation phase 结束后才出现 Hydration phase；tests 只比较 phase 与 ID sets，不把 HashSet/Dictionary 枚举顺序提升为语义。
- **Observed**：constructor/field initializer 被绕过，transient 与 CaptureHook 保持零值；`RequiresRewrite` 不进入 CLR object；one-time seam 拒绝 rebind 且不修改既有 fields。
- **Observed**：late allocator-boundary failure 与 late hydration failure 均保持 result null、outer phase/id/inner exception 与 baseline 不变；同一 baseline retry 重新执行完整两阶段并返回完整 graph。
- **Observed**：dangling/default baseline 由 `NormalizedBaselineGraph` 在 materializer 入口前拒绝，phase hook 计数保持零；materializer 不重复建立第二套 malformed-input authority。
- **Observed**：独立 review 找到并促成真实 allocator fault seam 与移除 ID sorting；最终无 blocker/medium。

验证：

- R3b 聚焦测试：7/7 passed。
- `DurableGraph.slnx`：0 warnings / 0 errors；完整 `DurableGraph.Tests`：147/147 passed。
- `dotnet format --verify-no-changes`：passed；真实 PackageConsumerProbe：passed。

结论：R3b root-only two-pass materialization state law 成立，但仍是 test fixture evidence。materialized root 是 disposable working graph；下一主线进入 R4 logical StateMap/repeated delta apply，R5–R7 顺序不变。

路线修订：

- R4 初始 StateMap 允许含 read-time upgrade 后相对 current root disconnected 的 source rows 与 rewrite obligations；成功 apply 后必须得到 exact result-root closure 和 clean baseline。
- R4 增加 `Materialize(LoadNormalized(StateMapN)) == expected CLR root closure` 组合闸门。
- 正式 identity one-time binding 与未来 transient/invariant phase 成为产品化重访点；后者加入时 root exposure 必须延后到全部 validation 成功之后。

相关材料：

- `tests/DurableGraph.Tests/NormalizedGraphMaterializationProbe.cs`
- `tests/DurableGraph.Tests/NormalizedGraphMaterializationProbeTests.cs`
- `tests/DurableGraph.Tests/GraphDeltaProbe.cs`
- `docs/design-branches/0006-flat-graph-delta-prototype.md`
- `docs/DurableGraph-research-roadmap.md`

## 5. 实验记录模板

后续实验按需增加条目，不要求为了形式填写无意义内容。

```text
### EXP-NNN：名称

状态：Planned / Running / Concluded / Superseded
日期：YYYY-MM-DD

问题：
本轮明确不回答：
最小实验：
成功/失败判据：
观察：
结论：
遗留问题：
相关源码/测试：
```

## 6. 船长日志

### 2026-09-06：完成 DB-026 raw Base 内容与 exact-head 重开读取

- **Decided/Observed**：用户采纳整体迁移，StateRevision 现在从不可变 BaseObjectRecord 统一取得 IDs/bytes；
  v2 同 Frame 记录按 ID 排序，拒绝旧 membership-only v1。Map Base/Delta、external checkpoint、removes 均保留。
  无内容的 DeltaObjectIds 已移除，StateRevision 的 TODO(DB-026) 记录真实 Delta/prior 链与新 ID 占用者从 Base 开始。
- **Observed**：ReadObjectBase 从 exact Revision 找 live locator，仅接受目标 Frame 本地记录。
  输入/输出/pooled-frame 内容隔离、旧新 Revision、删除后 ID 复用、坏 locator 与损坏 wire 均有见证。
  真实 SG Capture/DTO/string bytes 写入文件并关闭重开；测试的显式 kind/Schema/roots 不是持久类型头。
- **Observed**：根 build 零警告/错误，全套 559/559，零跳过；初始基线 536/536。
  独立最终代码审查无阻断，建议的 100 KiB body 缓冲后编码失败/lease 复用测试已补齐，覆盖轮转。
  详细命令、分工与边界见 [DB-026 §8](../../design-branches/0026-raw-base-object-content-slice/README.md#8-实施合同与验收账本)。
- **Decided**：沿用 RBF 268,435,428-byte payload 上限及 EndAppend 检查，接受缓冲后超限失败，
  不新增第二套 wire 尺寸算法。未修改上游框架，不承诺真实 I/O fault 物理截断或 crash 恢复。
  policy、Generator/runtime 及包接线未改；仅测试项目新增 Storage 依赖，未重跑 package probe。

### 2026-09-06：规划 DB-026 同 Frame raw Base 内容存取

- **Observed**：在 `171581e` 只读核验，SG DTO/string 已有独立 bytes 闭环，Storage 仍是
  membership-only；local heads 指向 containing Revision Frame，不能用独立 blob store 无缝替代。
- **Tentative**：下一片推荐 [DB-026](../../design-branches/0026-raw-base-object-content-slice/README.md)，
  由完整 local Base records 统一产生 IDs 与内容，跨 Revision/Segment 重开后按 exact head 读取。
  保留 ObjectHeadMap Base/Delta；推荐收回仅 ID 的 ObjectVersion Delta 占位并升级 provisional wire，
  不兼容旧 v1。此为当时的显式原型迁移建议，后已由用户采纳并实施，见上条完成记录；对象 Delta/policy 方向保留。
- **Observed**：独立候选比较、Storage 事实调查和反方审查已完成；草稿无阻断。
  核心边界包括输入/pooled bytes 所有权、错误 external locator 拒绝、删除后同 ID 新 Base 的视图隔离。
  本轮仅文档，不复跑历史测试，不新增产品能力。

### 2026-09-06：完成 DB-025 string 内容读取与引用校验，空串明确规范化

- **Decided**：用户选择 Capture/读取两端统一零长度 string 为 string.Empty，不保留其不同实例身份。
  非空仍按引用区分；不采用任何独立空串分配机制。此前非公开及公开 API 的
  [分配见证](../../design-branches/0025-string-object-decoding-slice/0025-empty-string-allocation-witness.md)仅保留为未采用方向的研究证据。
- **Observed**：CaptureString 在候选/parent 身份查找前规范化；StringReadTable 同步解码完整独立 bodies，
  保持非空身份、允许不同空串 ID → Empty、拒绝零/重复条目 ID/损坏内容/尾随字节，失败不暴露半张表。
- **Observed**：SG 为各 Vn 生成独立 ValidateStringReferences，只检查 exact Schema 的 String 槽；
  历史 String→UInt32 与旧 CLR 祖先消失仍有发布历史/真实执行见证。ReadVn 的 ID DTO body 保持不变。
- **Observed**：根 build 零警告/错误，聚焦 70/70，全套 536/536、零跳过；真实单包消费者通过，
  产物 `experiments/PackageConsumerProbe/obj/run-20260906055311-34904`。独立审查无代码/测试阻断，
  并修正旧文档对空串 ID 反向映射的无条件描述。完整验收入口见 [DB-025 §7](../../design-branches/0025-string-object-decoding-slice/0025-string-object-decoding-slice.md)。
- **Observed**：首轮发现测试框架将 MemberData 中孤立 D800 替换为 FFFD；改为 Fact 内部构造，
  保留原 golden 与码元断言后通过，产品编码未因此改变。
- **Deferred**：完整目录与 exact body 配对仍由 typed 消费者组织；领域 Restore、通用图加载、
  导入 CaptureSession、DTO upgrade、ObjectVersion 持久内容/Save 继续独立排期。

### 2026-09-06：规划 string 对象解码与引用槽校验分片

- **Observed**：在 `327a425` 核验现有 Capture/ID DTO 与 string 内容 codec；ReadVn 尚不解析引用，
  string 读写入口为 internal，空内容解码明确返回 string.Empty；本轮未重新运行历史测试。
- **Proposed**：主代理与独立设计子代理推荐 [DB-025](../../design-branches/0025-string-object-decoding-slice/0025-string-object-decoding-slice.md)：
  保留 uint DTO，增加 string-only 解码表与 SG 各版引用槽校验，真实 typed 消费者从 bytes 验证引用保真。
  该表不证明完整异构目录唯一性；不提前引入 resolved DTO、领域 Restore 或通用 registry。
- **Resolved later**：本次仅规划；后续用户选择空串统一 Empty，并完成 DB-025（见上条）。

### 2026-09-06：string 引用 Capture、ID DTO 与内存候选闭环

- **Decided**：用户采纳 SG 根登记适配器与 Runtime 统一会话/候选管理，G0 冻结后完成 DB-024 首片。
- **Observed**：concrete AddRoot 自动配对 exact 类型/Schema/current DTO；string 经引用身份表转 uint，
  当前和历史 DTO/body 均支持 ID 槽，Schema tag 仍为 String；纯标量和基类段级 Capture 保留原合同。
- **Observed**：CaptureSession 单在途、Seal 冻结、accept/discard、失败烧号、存活 ID 延续、退役映射清理与耗尽拒绝有执行见证。
  图不持有领域实例；WeakReference 测试验证旧候选/旧 context 留存时仍可释放退役源对象与回调闭包。
- **Observed**：自审发现 Array.AsReadOnly 可经 ICollection.SyncRoot 暴露可写数组，实际复现后改为私有 IReadOnlyList 包装，
  独立审查复核修复；历史非法 Leaf 的负面测试按类型判断拒绝，保留合法 Base body，并验证 publisher 双端拒绝。
- **Observed**：根 build 0 warnings/errors，聚焦 37/37，全套 503/503、零跳过；单 PackageReference 消费者通过，
  包产物 `experiments/PackageConsumerProbe/obj/run-20260906024101-5228`；独立源码/测试/包审查无剩余阻塞。
- **Deferred**：只闭合内存 Capture；字符串对象内容解码/独立空串分配、领域 Restore、Durable 互引/循环、
  struct/数组/BCL、ID 回收和持久 Save 继续分别排期。下一候选为 string 对象编码与引用恢复，尚未授权实施。

### 2026-09-06：引用 Capture 的 G0 接缝具体化，待用户裁决

- **Observed**：从干净的 `8816f0c` 开工，完整读取工作单与工作集，并与独立只读子代理核对 generator/DTO/包消费者。
  当前基类 Capture 接收派生实例，exact root 检查必须留在新根登记边界。
- **Tentative**：DB-024 §3.1 推荐 SG 生成 internal AddRoot 适配器，Runtime 公开泛型登记、session/context 与封闭候选；
  DTO 保持 internal/unmanaged，条目私下装箱、按值取出。AddRoot 登记根，Seal 才捕获字段。
- **Open**：用户启动 Goal 时要求 G0 尚未裁决的 public API 分支先呈现再继续；当前等待该决定，G1–G3 未实施。
  本轮只有设计/进度文档修改，未重跑 build/tests，不把源码可行性评估当作编译证据。
- **Observed（后续基线复核）**：在 `1c4f2e9` 串行运行根 build（0 warnings/errors）、根 test --no-build
  （471/471，零跳过）和 PackageConsumerProbe，全部通过。包产物目录为 `obj/run-20260906022525-33812`。
  G0 尚未收到用户裁决；这些是原有标量/历史/存储能力的基线证据，不是新引用 Capture 的验收结果。

### 2026-09-06：引用 Capture 首片采用单调 ID，形成实施前交接

- **Decided**：用户认可前轮分析，明确将 ID 回收延期，先使用单调分配；要求先写目标与思路，不启动实施。
- **Draft**：[工作单](../../WORK-ORDER-REFERENCE-CAPTURE.md)收窄到 roots/string → ID DTO/封闭候选及内存 accept/discard。
  [Goal 草稿](../../GOAL-REFERENCE-CAPTURE.md)按 G0–G3 交接，未创建 Goal；G0 根入口与必要公开类型接缝仍待评审。
- **Proposed implementation detail**：失败/discard 可消耗号码但不改变 parent；accept 清理退役绑定，数字不回收。
  只保证当前会话单调，持久 high-water mark、字符串恢复、循环/struct/数组/BCL 均延期。
- 本轮核对当前生成器、测试、包依赖与路径，仅编辑文档；未重跑产品构建/测试，历史 471/471 不当作新验证。

### 2026-09-06：引用 Capture 与可复用 ObjectId 设计讨论

- **Decided**：用户明确 ObjectId 经 StateRevision 解释，允许回收后复用；修订目标文档中“永不复用”的过强约束。
  用户补充 struct 采用嵌套值布局，exact struct 版本变化沿 inline/base 依赖传播并要求 owner 升版。
- **Draft**：[DB-024](../../design-branches/0024-reference-capture-and-reusable-object-ids/README.md)记录 string Capture → ID DTO → 对象列表、
  目标 revision 解析引用、候选隔离、隔成功发布复用与同候选复用的取舍；尚待讨论，不是实现成果。
- **Observed**：只读检查 StateJournal SlabBitmap/SlotPool/SlotHandle，并查看 GcPool 组合合同。
  位图/低号分配可借鉴；24+8 handle、generation 回绕、mark 期间不得分配及 slot 移动不能原样当成持久 ID 语义。
  未移植代码或运行相邻仓库测试；也未增加产品依赖。
- **Deferred**：struct 的 exact 字段类型表达、历史闭包及 nested DTO 已列 TODO，可与引用 Capture 分开排期。
  string 空内容 codec 当前返回 string.Empty，独立空串实例保真要在恢复分片用分配见证裁决。
- 本轮仅保存设计文档和校准工作集；未实现 ID 池、GC、图 Capture/Restore 或 StateStore Save。

### 2026-09-05：13 种标量贯通 Schema、历史与 DTO

- **Decided**：在 DB-022 的 DTO 管线之后，先补齐已存在的标量字节原语消费者；
  string 引用 Capture 的身份生命周期、上下文与对象列表保留为下一候选。
  合同见 [DB-023](../../design-branches/0023-scalar-schema-dto-slice/README.md)。
- **Observed**：TypeTag 保持原有 1–4，追加 5–14；Schema、两端 history 工具、legacy Snapshot、
  当前/历史 DTO 支持 byte/sbyte、short/ushort、uint/ulong、char、Half/float/double。
  新旧标量共 13 种，string DTO body 继续明确拒绝。历史语法未改，旧工具拒绝新 tag。
- **Observed**：生成 body 继续直接静态调用原语。char 使用 UInt16 canonical 编码，允许孤立代理项；
  浮点保持位模式。Half 识别使用实际 BCL 符号，而非类型显示名。
- **Observed**：独立 golden 与真实 publisher→history→再生成验证数值边界、负零/NaN、
  旧 CLR 祖先消失、同版本改型拒绝及 legacy 路径；包消费者验证全部新增方法的公开接线。
- 主代理根 build 零警告/错误，全部产品 tests 471/471，无跳过；PackageConsumerProbe 全流程通过，
  独立只读审查无阻塞，diff 与文档链接检查通过。未实现 string 图 Capture、DTO 比较/升级或 Save。


### 2026-09-05：Versioned DTO 取代直接领域 binary body

- 用户选择捕获后的版本化状态作为保存比较/估算/编码输入。[DB-022](../../design-branches/0022-versioned-state-dto-capture/README.md)
  已实现 readonly V1..Vcurrent DTO、current Capture、Write(in Vn)/ReadVn；DB-021 直接领域 Read/Write 已移除。
- 各版 DTO 静态配对 GetSchema(n)，从已有 accepted history + current 定义再生成；字段按 exact 祖先声明链
  物理展平为 SegmentNFieldId，Schema 仍分段，不增加第二套历史 authority。
- Capture 仅当前 private 字段，基类负责自己的 private 访问；捕获后领域修改不影响 scalar DTO。
  ReadVn 成功才返回完整 DTO，失败可推进 Reader；DTO 升级、领域恢复、图和基线安装未实施。
- 真实 publisher 集成验证旧 CLR 祖先改名/删除和替换祖先链后的历史字节往返；所有历史 DTO 独立于旧 CLR。
  历史曾含 string 的布局明确拒绝，包括当前 Capture 间接依赖的基类历史，等待引用上下文。
- 根 build 0 warnings/errors，全产品测试 456/456（原 17 body case 迁移、新增 8 case），无跳过；
  单一 PackageReference 完整消费流程通过，Capture 后改变领域对象仍编码原 golden `012154`。
  独立审查无阻塞，工作集及设计/包说明已更新。

### 2026-09-05：实际 SG 静态标量 body 闭环

- [DB-021](../../design-branches/0021-generated-primitive-body-slice/README.md)选用已有 SchemaOnly class 字段模型，
  额外 GenerateBinaryBody 整链启用，生成当前 bool/int/long 的 private 成员读写及 base-first 组合。
  显式启用保留 metadata-only 边界；已知成员直接调用 byte primitive，无 Type 查表或 slot 委托。
- 同层按 FieldId 排序、跨层允许重复 ID，body 生成要求自身与所有祖先通过 metadata/history 校验。
  string 明确拒绝，等待统一身份上下文；body 不解释历史 Schema，也不承担对象头、分配或图发布。
- Runtime 首次引用 Serialization 并开放实际所需 Reader/Writer API。PackageConsumerProbe 继续只有
  一个 runtime PackageReference；完整旧流程与新增 private 继承 body 消费均通过，golden `012154`。
- 根构建 0 warnings/errors，全部产品测试 448/448（新增 17），无跳过。
  独立审查无阻塞，损坏/冲突 history 与祖先 string 的补测已通过；读写部分失败边界得到实测。
- 后续可扩充 Schema primitive kinds 或接引用上下文；struct exact 内嵌表达、历史 binary decoder、
  泛型组合与完整图仍需独立分片，BCL 集合继续暂缓。

### 2026-09-05：primitive slot 查表回归测试工具

- **Decided / Observed**：按用户建议把仅供测试消费的 PrimitiveSlotCodecs 移入 Serialization.Tests/TestHelpers，
  保留读写 body 与现有测试调用；产品不再携带该 Type 字典。ValueSlotCodec/ArrayElementCodec 本次保留。
- **Decided**：已知成员类型的 SG body 直接调用字节原语或静态值 body；ref 槽位复用不要求委托包装。
  开放泛型中的 T 如何绑定仍需单独决定，运行时组合见证不构成产品逐成员分派的必要性。
- **Observed**：移动后根构建 0 warnings / 0 errors，Serialization.Tests 94/94；测试用例源码无需修改。

### 2026-09-05：落地 typed slot 与数组元素 binding

- **Decided**：按下一分片实施授权选择 [DB-020](../../design-branches/0020-typed-slot-array-binding-slice/README.md)，
  在现有 Serialization leaf 闭合值槽位/数组元素机制；先不同时引入 string 引用表与 SG 历史 body。
- **Observed**：internal ValueSlotCodec<T> 持 typed ref 读写委托，13 种 primitive 复用字节原语；char 保留 UInt16 code unit。
  已闭合 slot 直接创建 SZ/rank-2 模板，实际数组只在入口精确验证/强转，元素循环直接传 ref。
  每个 binding 保留调用方指定的 body，无 Type-only 全局缓存，无额外反射或逐元素装箱。
- **Observed**：真实字节见证在运行时闭合手写 Cell<T> 工厂：Cell<Cell<int>> 的同一 body 操作 local、字段、
  SZ 和非零下界二维元素，保留未触碰的嵌套成员；另测 Cell<double> NaN 位保持。
  数组 extreme bounds 覆盖 int.MinValue 下界与 int.MaxValue 上界；坏输入保持约定的部分修改边界。
- **Boundary**：只写已有数组的元素，未编码 shape/ID/类型头或分配对象；SG body、泛型 Schema、
  引用上下文与历史 binary decoder 尚未实现。下一候选是让实际生成代码消费此 byte/slot 机制。
- 上一批祖先实现和设计工作集已提交为 `29a4704`；本片的最终构建、测试与审查证据见 DB-020。

### 2026-09-05：完成祖先 Schema/history 产品分片

- 按用户规划并实施下一分片的授权，选择 [DB-019](../../design-branches/0019-schema-ancestry-implementation-slice/README.md)：
  `SchemaOnly=true` 显式生成同编译继承链的 Schema/GetSchema 元数据，不生成 boxed Serializer、payload Snapshot 或 Upgrade。
- DurableSchema 的 Fields 按声明层编号，BaseSchema 携带 immutable exact 祖先；内存 Store 先整链校验再登记。
  Generator/history/publisher 将 exact base 纳入 shape，并要求 accepted history 自身闭合，不能由当前候选补缺。
- SG → 发布 → 历史 → 再生成 → runtime 的三层见证验证：基类升级逐层拒绝未升版的派生类，全部升版后通过；
  旧 Leaf 仍绑定旧 Middle/Base，旧 CLR 基类移出当前源码也不改变旧链。默认 serializer/upgrade 回归保持通过。
- 主代理最终根 build 0 warnings / 0 errors；DurableGraph.Tests 224/224（基线 147/147），无跳过；
  diff check 与文档链接检查通过。独立审查无阻塞项，两个无效祖先过滤反例已补齐。
- 下一候选为基础 typed body 与 runtime 泛型/数组 binding。完整图 codec、继承 payload/升级、跨程序集继承与 BCL 集合未进入本片。
  代码与原先文档修改随后提交为 `29a4704`；产品工作集仍为 src/PROJECT-STATE.md。

### 2026-09-05：祖先 Schema 校验与泛型 codec 按需组合

- **Decided**：用户认可祖先布局属于 exact Schema，基类变化要求派生版本递增；
  同意 nominal 引用声明与 exact 对象头的区分，明确暂缓 BCL 集合支持。
- **Observed**：当前 Schema/equality/history/publisher 都只包含本层 fields；生成器禁止领域继承。
  增强需同时覆盖直接 exact base、历史保存与跨层校验，不只是修改 DurableSchema。
- **Draft**：[DB-018](../../design-branches/0018-generated-graph-codec-shape/0018-generated-graph-codec-shape.md)新增三层版本传播见证及
  SG 开放泛型 body + runtime typed binding 方案；局部 DynamicMethod 用于数组形状仍可选，完整 IL 后端未选定。
- **Observed**：主审复跑内存 C# 见证，.NET 10.0.9 下 runtime 闭合 generic struct array，
  typed delegate 经 ref Reader/ref 元素读入 [17,29]、消费 8 bytes，无 Reflection.Emit。
  仅证明组合机制，没有新增产品实现或修改旧项目；
  [完整复跑代码](../../design-branches/0018-generated-graph-codec-shape/0018-runtime-binding-witness.md)已保存。

### 2026-09-05：统一引用图与生成器形状

- **Decided**：用户明确所有受支持引用对象按引用相等统一编号，包含 string、数组和 BCL 容器；
  string 成员写 ID、字符串对象 body 写内容，保留图内 ReferenceEquals 关系，取代旧字段 inline 值方案。
  struct 值嵌套、class base-first；引用对象头用可组合 TypeCodec 表达实际类型。
- **Draft**：[DB-018](../../design-branches/0018-generated-graph-codec-shape/0018-generated-graph-codec-shape.md)起草 typed
  VisitReferences/Write/Read、引用收集/封闭表、对象边界分派、allocation prefix/填充及类型绑定；
  三路独立审查未发现阻断问题。继承分段及版本传播、泛型执行闭包、boxed 值等仍待定。
- **Observed**：参考 StateJournal TypeCodec 当前只有有限类型的 postfix 解码，不包含新稿的
  arrays/custom SchemaRef，也不自动生成 codec。当前产品仍不具备上述新图能力，本轮仅修订文档。

### 2026-09-05：产品开发工作集迁入 src

- **Decided**：采纳用户指出的上下文归属问题，把产品跨程序集进展集中到
  [src/PROJECT-STATE.md](../../../src/PROJECT-STATE.md)，根 AGENTS.md 提供导航；
  使用 src 共同父目录覆盖 core/Generator/StateStore/Storage/Serialization 及对应测试。
- **Changed**：MultiSegment Probe 的工作集收缩为自身完成状态、边界和证据入口，
  README 转链产品工作集。设计分支保存讨论，笔记保存历史，不再在旧 Probe 重复维护产品 roadmap。

### 2026-09-05：选择 primitive-first codec 并建立设计讨论稿

- **Decided / Updated**：用户接受 codec-first；从支持的 CLR 基础类型开始。最初 string 字段 inline
  的提案随后由 DB-018 的统一引用身份取代，仍不做内容相等的 symbol 合并。
  Durable 相互/循环引用、SG 循环逐元素 ref accessor 的数组、按内容重建的 BCL 容器列为后续能力。
- **Tentative**：[DB-017](../../design-branches/0017-object-codec-design-points/README.md)给出可逐项确认的草稿：
  类型名单、位/字符串语义、Base 布局、accessor、冻结/读失败，以及未来 identity、数组形状和 comparer。
  用户随后澄清 ref 主要用于 struct codec 共用字段/数组元素槽位；K4 改为分离 Write/Read 的 typed
  值 codec，共享遍历降为实现选项。inline 容器与独立 identity 不能从“按内容保存”自动推导。
- **Observed**：现有 byte primitives 已覆盖更多标量，但 Schema 与 history parser 仍只支持四种 tag；
  扩大类型范围必须同步接线。本轮仅更新文档，未实现 codec 或未来容器支持。
- **Observed**：两路只读审查用户提供的 Robird 旧实现，确认字段取地址与数组 ref/out 元素复用
  struct body handler，读写协议分开。吸纳槽位/内容职责划分、外层 shape 与原地填充机制；
  不移植 DynamicMethod、旧 wire 或默认 equality 的实例表。备份有注释草稿和未实现入口，未构建/运行。
- **Tentative**：底层 byref 读入不自动保证槽位回滚；建议由外层未发布 Snapshot/对象/数组
  承担失败隔离，不要求每层 struct 都复制暂存。具体签名和其他设计项继续等待用户逐项反馈。

### 2026-09-05：比较策略之后的对象内容纵切

- **Observed**：策略已提交 `c8a98dc`；重新核对源码，历史 Snapshot 积累、partial Upgrade 声明与
  运行时升级链已进入主项目，业务转换函数体仍手写。现有 serializer 只产出 boxed 字段字典，
  Storage 仍为 membership-only；DurableGraph.Tests 本轮 147/147 通过。
- **Decided**：三路独立分析及交叉质询后，[DB-016](../../design-branches/0016-next-product-object-content-slice/README.md)
  的 codec-first 方向随后获用户接受，类型范围转 DB-017 具体讨论。raw Base-only 存储是可交换次序的
  备选，审查保留 storage-first 偏好；ObjectVersion 内容仍是后续接入目标。
- **Open**：生成代码的 Serialization 原语可见性和 consumer/package 引用是实际接线点；
  首片不预设引用/继承展平、Delta、binary history、SchemaHash 或完整 Save。本轮未新增产品实现。

### 2026-09-05：落地估算驱动的固定对象表示策略

- **Decided**：采用用户选定的 DTO → plan 边界，MVP 不预设可替换策略或 Estimate/Write 序列化接口。
  输入覆盖全部 post-live 对象；Removes、真实 parent 和保存视图由调用方负责。
- **Implemented**：[DB-015](../../design-branches/0015-statestore-object-representation-policy/README.md)经设计审查后，
  用户批准实施；既有 StateStore 内提供纯 Plan、统一 long 估算和唯一只读 Writes。保持严格读阈值、
  完整 Base 计费、联合排序前缀与首候选超预算规则；没有新增程序集或策略替换接口。
- **Decided**：用户将参数简化为整数 X 倍、Y%；用 Int128 计算阈值、比率交叉积和
  `floor(G*Y/100)`。此前为 decimal 舍入边界提出的 BigInteger 路径不再需要；Probe 保持原样。
- **Verified**：独立实现与验收测试分工、只读交叉审查无阻断发现；集成后根 solution build 为
  0 警告/0 错误，StateStore/Storage/Serialization 测试分别 40/73/65 项通过。策略测试覆盖
  完整例子全部 120 种输入排列、整数 floor、零成本首位、long 极值排序及溢出拒绝。
- **Open**：序列化估算生产、NoChange Base 获取、H 的恢复与提交更新、映射 checkpoint 等待真实
  consumer；本轮只实现估算选择算法，没有新增 payload、wire 或持久化行为。

### 2026-09-04：建立共享 StateStore payload serialization leaf

- **Implemented**：把五个 internal primitives 移入 BCL-only
  `Atelia.DurableGraph.StateStore.Serialization` assembly：ref-struct `BinaryPayloadReader/Writer`、
  `CanonicalVarInt`、`StringPayloadCodec` 与 `NullablePayloadHeader`。它只向 Storage 及相关测试开放 internals；
  StateStore 等第一个真实 ObjectVersion consumer 出现后再决定直接引用。
- **Provenance**：实现裁剪自 `atelia` StateJournal `BinaryDiffReader/Writer` 的 Bare 思想；相关源文件最后变更
  commit 为 `1e1f9e3132b0756769bfecf9258ff06cee01c17c`。没有引入 StateJournal 项目依赖，也没有搬入 Symbol、
  Revision、pool、Tagged、ByteString 或 `asKey`。
- **Invariant**：reader 拒绝 ordinary/max-width overlong、overflow 与 truncated VarInt，失败不推进 cursor；string
  仅在 strict UTF-8 严格更短时采用 UTF-8，tie 用 UTF-16LE，并拒绝非最短 alternate encoding。unpaired surrogate
  通过 raw UTF-16LE code units 无损往返，不允许 replacement fallback 静默改值。
- **Integrated**：Storage 改为 `Storage -> Serialization`；`StateRevisionWireReader/Writer` 与
  `FrameAddressWireCodec` 共用 `BinaryPayloadReader/Writer`，删除 Storage 自有 `CanonicalVarUInt`。既有 v1
  golden bytes 不变，composite FrameAddress 失败时外层 cursor 回滚。`StateRevisionStore` 通过 RBF
  `BeginAppend/EndAppend` 直接编码到 `PayloadAndMeta`，删除生产路径的完整 payload staging/copy；编码失败由
  builder Auto-Abort，不提交 partial Frame。writer acquisition 先触发轮转时，可留下并复用 header-only active
  Segment。wire codec 随后删除仅为 same-file chronology 传播的 containing Frame offset；该相对 invariant
  留在持有完整 absolute containing/target addresses 的 live-head traversal。parent chain 每步严格下降同时排除
  cycle，因此 materializer 删除 visited set。
- **Boundary**：这只是尚未接 ObjectVersion 的 internal provisional codec，不是 public API 或正式 wire
  compatibility；ObjectVersion/frame envelope 后续负责具体资源 hard bound。
- **Verified**：专属 Serialization.Tests 的 65 个 focused cases 覆盖独立 reader/writer golden、canonical/malformed
  VarInt、bounded `IBufferWriter`、浮点 bit pattern、string 两种编码、nullable、unpaired surrogate 与失败 cursor
  rollback；Storage integration tests 另覆盖 wire golden、address cursor rollback 与 append 前失败不改 RBF tail。

### 2026-09-04：完成 StateRevision live membership 文件纵切

- **Decided**：Storage 暂用 provisional 1-based `UInt32 ObjectId`；`StateRevision` 以 local Base/Delta IDs 隐式
  发布 containing Frame head，ObjectHeadMap Delta 只保存 Removes 并继承 exact parent，Base 只保存 cold
  External heads 并成为 current membership 早停点。不持久 `InheritSet` 或重复 `BindSelf`。
- **Boundary**：Remove 的正确与完整由上层调用方负责；Storage 不遍历对象图或猜测遗漏，只验证收到的
  Revision 自身 canonical shape。Object representation Base/Delta 与 ObjectHeadMap Base/Delta 保持正交。
- **Invariant**：genesis 不允许 Delta ObjectVersion；required FrameAddress 必须含非空 ticket，Parent 与
  External head 在 graph traversal 时必须严格早于 containing Revision。单 Frame wire decode 不接收 containing
  offset，也不冒充跨文件 ticket provenance validation。wire collection 暂设每集合 1,000,000 项硬上限，并在
  分配前 fail closed；该上限不是正式 compatibility 决策。
- **Implemented**：加入 immutable `StateRevision`、纯 `LiveObjectHeadMapMaterializer`、provisional wire
  reader-writer、wire-only backward distance 转换，以及基于真实 `RbfSegmentStore` 的
  `StateRevisionStore.Append/Read/ReadLiveObjectHeads`。materializer 保留每个 pending Delta 的 containing address，
  从完整 Base 向前应用 Remove 与 local overwrite，返回 immutable、ObjectId 升序的 shallow
  `{ObjectId -> absolute FrameAddress}` map。
- **Verified**：独立 semantic oracle 证明 local self-binding、external preservation、inherit/update/add/remove、
  no-change、repeated overwrite、Remove 后 reappearance、Base early-stop、升序与不可变结果，以及 future
  parent/external fail-close；真实 RBF 测试在写入期及 dispose/reopen 后核验 exact head values，并覆盖 1→2→3
  tail-triggered rollover checkpoint。既有手写 golden bytes、malformed、append/read 与 Auto-Abort 测试保持通过。
- **Boundary**：`FrameAddress` 继续是 RBF `EndAppend` 后产生的唯一物理 identity；不增加
  `StateRevisionBuilder`、self-address 或 addressed snapshot。head map 只是 membership declaration，不读取或验证
  ObjectVersion record；Object payload、lineage、自动 checkpoint、skip、durable publication、GC 与正式 wire
  compatibility 仍未实现。实施边界见
  `experiments/MultiSegmentStateStoreProbe/WORK-ORDER-STATESTORE-LIVE-OBJECT-HEADS.md`。

### 2026-09-03：启动正式 StateStore 阶段 B

- **Decided**：建立独立 `src/DurableGraph.StateStore` 项目；程序集名和根命名空间均为
  `Atelia.DurableGraph.StateStore`；随后建立 `Atelia.DurableGraph.StateStore.Storage`，冻结
  `StateStore -> StateStore.Storage -> RbfSegmentStore` 的单向项目引用链。
- **Observed**：只有 Storage 直接 ProjectReference 当前 sibling `atelia/src/RbfSegmentStore`；地址模型开始使用
  `SizedPtr` 后又显式引用 `Data`，其余 substrate 依赖由现有项目图传递。两个产品程序集均有只引用各自被测
  项目的 xUnit 项目。
- **Observed**：首个 Storage 机制切片随后加入空壳 `StateRevision`、runtime
  `FrameAddress { UInt32 FileNumber, SizedPtr FrameTicket }` 与 `FileScope`；0 distance 表示当前文件，
  future/zero/underflow fail closed，并以 focused tests 覆盖同文件、远距及 UInt32 边界往返。
- **Boundary**：产品内存模型不移植 Probe `RelativeFrameTicket`；`BackwardFileDistance` 只作为后续 wire codec
  瞬时值。当前仍没有 wire bytes、Revision 内容或文件 I/O。

### 2026-09-02：采用 RbfSegmentStore 的 tail-triggered rollover

- **Decided**：`RolloverThresholdBytes` 只在每次取得 writer 前与 existing `TailOffset` 比较；达到或超过时
  checked 切换 Segment，随后在 lease 选定的 final origin render 一次。crossing Revision 留在当前文件，下一
  Save 才轮转。threshold 是 soft trigger，不是严格文件大小上限。
- **Simplified**：删除 candidate-aware prospective sizing、explicit rotate、current/next 双 render、
  `InitialCandidate` 与 placement-derived `RolledOver`。origin-free logical plan、absolute-normalize、final-origin
  relative encoding、RBF hard gates 与 one-Revision/one-Frame 继续保留。
- **Boundary**：每个 Save 单独借还 writer lease且只 append 一个 Revision Frame，因而 overshoot 最多一个合法
  RBF append envelope；2 GiB threshold 在当前约 256 MiB Frame hard bound 下得到小于约 2.25 GiB 的文件。
  threshold 必须大于 header-only tail、4-byte aligned 且不超过最大 Frame start；strict file-size consumer 或
  multi-frame/Extent 出现时重访。
- **Superseded evidence**：commit `556365b0043519b3ff94a98a26818495ff327986` 保留旧
  candidate-crossing/rerender Probe、测试与 raw reports；当前主线更新 golden，不保留双 rollover policy。

### 2026-09-02：把 MultiSegment Probe 与正式 StateStore 产品化拆成两阶段

- **Decided**：`MultiSegmentStateStoreProbe` 在 G4（Workload、policy、evaluator）完成后停止；真实 filesystem/RBF、reopen、durability 与 `src/DurableGraph` 整合不再是 Probe roadmap。
- **Decided**：保留 `TARGET-DESIGN.md` 稳定路径并收敛为阶段 A 专门规范；新增 `STATESTORE-SUBSYSTEM-DESIGN.md`，记录 G4 后的上下层契约、Atelia substrate audit、产品 authority、ProjectReference/NuGet acquisition 与 B0-B4 晋升 gates。
- **Boundary**：阶段 A 只证明 in-memory logical publication、orphan visibility、address/OVD/reconstruction/policy/evaluator；阶段 B 从 Probe 抽取已证明语义重新实现，不直接搬运 experiment assembly、provisional wire 或 benchmark infrastructure。
- **Prepared**：`GOAL-G0-G4.md` 已提供 4,000 字符限制内的可粘贴 `/goal` objective；stop condition 要求逐项闭合 G0-G4、验证、文档和 commits，并把阶段 B 设为硬禁区。

### 2026-09-02：收敛 MultiSegment StateStore 实现主设计

- **Decided**：新增项目级 `TARGET-DESIGN.md`，把 DB-014 的路线裁决展开为唯一 authority、origin-scoped address、OVD/ObjectVersion、Save/Load、rollover、publication、baseline policy、W/P/F/R/L 与 executable gates；其原始 G5/G6 随后被上方两阶段裁决移出 Probe。
- **Superseded**：当时要求 candidate crossing 时按 next origin 重新 relativize、编码和定尺；后续已采用
  `RbfSegmentStore` tail-triggered writer acquisition，正常 Save 只在 final origin render 一次。origin-free plan
  与 representation/file-placement 解耦仍保留。
- **Decided**：保留 shared PriorRevision 与 Base lineage；OVD Base 的 prior 是 lineage-only，不进入 current recovery closure。保留可选 SameStateRebase 作为读放大维护，删除 TwoLeg evacuation/rotation 语义。
- **Boundary**：durable-before-publish 是目标 law；head carrier、atomic publication、file/directory flush、orphan 后 FileNumber allocation 与正式 wire 仍待 filesystem/RBF gate。当前没有把目标能力描述成已实现。
- **Review**：四路独立 thesis、交叉质询与终审已收敛；无架构 blocker。终审补齐 current-required 与 lineage-only dependency、派生 NoChange、首次文件、OVD Delta canonicality、零尺寸 amplification、物理 W/P 与 Empty-vs-missing head 边界。

### 2026-09-02：将 TwoLegRotationProbe 冻结为可执行技术储备

- **Decided**：不再继续 Adaptive、策略竞赛、capacity repair 或 TwoLeg publication；完整保留独立源码、362 个测试、Arena/Baselines/evaluator/workloads 与研究文档，压缩活跃 `PROJECT-STATE`，不做半残式删测试。
- **Superseded**：产品地址和 rollover authority 由 DB-014 接管；旧 StateStore base/addressing/derived 文档的相邻两文件、1-bit ticket、A/B/C evacuation 与 `CanPrepareAndRotate` 只属于冻结 TwoLeg grammar。
- **Preserved**：runtime OVD、absolute-normalize、Base/Delta 与 current/lineage 分层、whole-candidate hard gate、no-fallback apply、typed outcomes、deterministic workload 和 W/P/R/L 测量思想可供 MultiSegment 选择性移植；TwoLeg topology/debt machinery 不移植。
- **Verified**：TwoLeg tests 362/362；TwoLeg subsolution 与 root solution 均 0 warning / 0 error；format verification 与文档 link/diff checks passed。annotated recovery tag 为 `research/two-leg-rotation-probe-tech-reserve-20260902`。

### 2026-09-02：选择多历史 Segment 产品路线并建立地址 Probe

- **Decided**：产品候选放弃 latest reconstruction 仅限相邻两文件的格式约束；文件切换只由 soft target/RBF hard bounds 控制，Base/Deltify 与未来 compaction 独立。TwoLeg 保留为研究路线。
- **Decided**：FileNumber 使用目录内 1-based `UInt32`，canonical filename 直接寻址，不增加集中 catalog 或 StoreId/FileId 文件头；wire 使用无固定 horizon 的 canonical `VarUInt32 BackwardFileDistance`，runtime 立即 absolute-normalize。
- **Implemented**：新增隔离 `MultiSegmentStateStoreProbe` 与独立 tests/slnx；首批 executable vectors 覆盖 same/previous/>65,535/最大距离、十位 `.rbf` 文件名，以及 Base128 overlong/overflow/truncated/required-zero fail-close。
- **Boundary / Next**：`FrameTicketCode` 仍是 `SizedPtr` stand-in，尚无 RBF/OVD/reopen/GC。下一切片只做 in-memory Segment/Frame store 与 soft rollover，不进入 `src/DurableGraph`。

### 2026-09-01：拆分 Adaptive readiness progress 与 Rotate pressure

- **Decided**：大型不可分割冷对象每个单层 epoch 必须支付完整 Base，是 TwoLeg 结构性下界；双层同构 Hot/Cold segments 以 DB-013 记录为 deferred 产品分叉，当前不扩 Arena/wire。
- **Concluded**：Adaptive 后续控制拆为 ReadMotive、ReadinessProgress、ShouldRotate。首个局部候选用 `ReadyMaxE=ceil(G*f)-1` 与 Need-only preparation；A-dependent Update/NoChange 获得不可饿死、fit-and-skip、严格不越 `Q` 的进度席位，达到 Ready 后停止低放大迁移。
- **Rejected for now**：Fill-Q 会把更多 Base 提前写进即将成为 Previous 的 B；banked credit 若能跨 `Q` 只会恢复尖峰，若不能则不比本轮预算席位多提供能力；per-object debt age 在单 epoch 内也没有新增信息。
- **Open / Next**：ready-only target 在 `E==0` 且 foreground 每轮自然 Base 时可退化成一 Revision 一文件。先用 near-ready sawtooth 与 same-age/different-leg-pressure 两个 test-local discriminator，再选择 Should pressure；可恢复的 post-opening Current-leg physical growth 是 leading product signal，但 threshold 尚未裁决。

### 2026-09-01：复合 workload 证实 Adaptive 的 stable A-debt 停滞

- **Question**：修复无动机 progress floor 后，现实感更强的持续活跃对象群中，低放大率冷 A-debt 是否会长期阻塞 Rotate。
- **Implemented**：加入只负责对齐 Save timeline、隔离 ObjectId ownership 并合并 change lanes 的 `WorkloadTraceComposer`；corpus revision 18 保留原 `active-hundred-mixed`，另叠加 20 个 bootstrap-only、合计 1120B 的冷对象 channel，形成十八 workloads × 两 profiles = 36 cases。
- **Observed**：两 profile 在复合 trace 的 64 个 workload Saves 中全部选择 Stay，末端 20 个冷对象仍从 A 重建；`(3,5%)` 的 `W/P/F/R/L=117404/2076/117448/4166576/345484`，`(4,4%)` 为 `115692/2056/115736/4108844/345484`。
- **Concluded / Superseded next step**：stable low-amplification A-debt stall 已从假说变成有限 horizon 可执行反例。最初计划只补 progress；上方后续研究证明 Need-only preparation 还必须与独立 ShouldRotate 语义共同设计，仍不恢复绕过预算的无条件迁移。

### 2026-09-01：修复 Adaptive NoChange progress 越过 Base 预算

- **Problem**：旧 `ReadAmplificationBaseBudgetPolicy` 无条件从 A-dependent NoChange 迁移一个对象；`oversized-cold-nochange-tiny-clock` 用 10000B stable cold object 与反复更新的 1B clock 将其放大，旧实现得到 `Pworkload=10052B`，证明 NoChange 可绕过 `baseBudgetFraction`。
- **Implemented**：修复后 Update 与目标动作支持的 NoChange 只有超过 `readAmplificationLimit` 才形成 Base 动机；动机按放大率降序、ObjectId tie-break 排列并取 `baseBudgetFraction` 预算前缀。动机非空但首项自身超过预算时，允许这个不可拆分首项整体通过；不再存在无动机 NoChange progress floor。两个原 identity ID 的 component version 均由 v1 升至 v2；revision 17 收录该对抗 trace，共十七 workloads、两个 profiles、34 admitted cases，两 profile 的对抗 case 均保持 `Pworkload < 10000B`。
- **Observed**：`active-hundred-mixed` 的 shared `Delta/Base references=67206/165606`、`L=273804` 不变；修复后 `(3,5%)` 的 `W/P/F/R=116424/2128/58460/3501092`，`(4,4%)` 为 `114756/2104/91380/3754140`。下方 revision 16 数字仍是旧实现的历史证据，不可与当前 baseline 混用。
- **Open / Deferred**：没有读放大动机时，stable A debt 可能长期不推进；`E/G` 仍只刻画 Ready-to-Rotate 成本，尚无 Should-Rotate hysteresis。上一轮是否 Update 的热度修正与按 `Base - Delta` 边际成本选择均推迟为独立候选，不混入本次 bug fix。

### 2026-09-01：canonical 写入评价只保留 Wworkload

- **Decided**：report schema 5 / metrics `raw-wpfr/5` / corpus revision 16 从 canonical metrics 删除 closed-horizon total 与 `Wterminal`，只保留 `Wworkload` 作为策略 write comparator；report 共七个整数：Wworkload、两项 references、Pworkload、F、R、L。
- **Decided**：terminal settlement 仍是 admitted outcome 的闭合、容量与 reconstruction 验证步骤；其写入及 `Wworkload + Wterminal` 守恒继续在 evaluator 内部计算，并以 XML doc 标明只作 cutoff-liability/accounting 诊断。
- **Observed**：active-hundred 上 `(4,4%)` 比 `(3,5%)` 少写 `1352B Wworkload` 且 Pworkload 低 28B；`(3,5%)` 的 F/R 分别低 `11004B/322448B`。这是自然写入/峰值与 closed-horizon file guardrail/累计冷读的 Pareto trade；有限 workload 的自然 Rotate 截止相位仍未消失。
- **Supersedes**：下方 schema 4 决定中“closed-horizon W 与 Wterminal 继续作为 canonical report 指标”的部分；其原始数据仍是历史实现事实。

### 2026-09-01：退役 no-migration 与 paced-one-debt profile

- **Decided**：`DeltaReference` 与 `BaseReference` 已承担 strategy-independent 写入参照，不再用两个行为很弱的 profile 充当写入 baseline。
- **Observed**：annotated tag `research/no-migration-paced-baselines-20260901` 保存移除前的完整实现、64-case corpus 与专属诊断；主线 revision 15 只注册两个 Adaptive profiles，十六条 trace 共 32 cases。
- **Decided**：删除 profile bindings、manifest case IDs、正式跑分行与 profile 专属相位/golden tests；保留必要的 test-local 极端动作作为底层机制对照，不把它们重新包装成策略。

### 2026-08-31：将 canonical P 分相并移除终点相位噪声

- **Problem**：schema 3 的 P 取 workload 与一个人工 terminal-settlement Commit 的共同峰值；当 terminal 主导时会遮住自然 Save 峰值。T、final cursor、settlement 明细及两个可由 manifest/protocol 推导的 counts 也把终点相位与内部认证信息带进了可比较报告。
- **Implemented**：corpus revision 14 / metrics `raw-wpfr/4` / report schema 4 将 canonical P 改为 workload-only `PeakWorkloadCommitWriteBytes`；terminal burst 已由唯一 synthetic Commit 的 `Wterminal` 精确表达，旧 closed peak 可按 `max(Pworkload,Wterminal)` 派生。report 的 metrics 仅保留九个整数：`W/Wworkload/Wterminal`、两项 payload references、Pworkload、F、R、L；typed outcome position 仍统一保留 phase 与 completed/total workload counts。T、final cursor、settlement 与冗余 metric counts 保留在 evaluator 内部用于正确性/专项诊断。F 仍是全过程 Current-file high-water capacity guardrail，不是终点快照。
- **Observed**：active-hundred 的 no-migration、paced、Adaptive `(3,5%)`、Adaptive `(4,4%)` 的 `Pworkload` 为 `2044/2128/2176/2148`。paced 被 no-migration 在 W/Pworkload/F/R 四项严格支配。Adaptive `(3,5%)` 相比 `(4,4%)` 将 W/F/R 分别降低 `1940/11004/322448B`，但 Pworkload 高 28B；修正后的关系是窄峰值交换，不是 schema-3 所显示的严格支配。
- **Superseded**：当时决定在 canonical report 保留 closed-horizon W 与 `Wterminal`；2026-09-01 的 schema 5 已将二者降为 evaluator 内部诊断，只保留 Wworkload 作为 write comparator。

### 2026-08-31：拆分 workload/terminal 写入并加入 payload references

- **Implemented**：corpus revision 13 / metrics `raw-wpfr/3` / report schema 3 在保持既有 W/P/F/R/T 核心结果不变的同时，新增精确整数 `Wworkload`、`Wterminal`、workload Delta-reference 与 Base-reference，并强制 `W = Wworkload + Wterminal`。Delta-reference=`Insert Base + Update Delta`，Base-reference=`Insert Base + Update result Base`；Remove、NoChange、bootstrap、terminal settlement 与 rejected Saves 不进入 references。
- **Observed**：active-hundred 的两组 Adaptive references 同为 `67206/165606`；`(3,5%)` 的 W=`118716 workload + 1044 terminal = 119760`，`(4,4%)` 为 `117364 + 4336 = 121700`。后者 workload 少写 1352B、terminal 多写 3292B，净多写 1940B。
- **Boundary / Next**：references 是 synthetic foreground payload totals，不是实际 all-Delta/all-Base 物理 baseline、上下界或 score；report 不输出浮点。下一步先据四整数复审 active-hundred，再决定 candidate；完整 `I/UB/UD/NB` 与策略原因归因暂缓。

### 2026-08-31：将 R 从随机终相位改为 workload-cycle cumulative cold load

- **Problem**：revision 11 的 R 只在强制 terminal settlement 后测一次最终 head；它衡量人工收尾布局的随机相位，不能表达策略在一系列自然 Save 中持续承担的冷读压力。active-hundred 上 `(4,4%)` 的较小终点读量因此给出了误导性印象。
- **Implemented**：revision 12 的 `after-every-workload-save-cold-load/1` 在每个成功 workload outer Save 后以空缓存测一次 authoritative OVD+current-reconstruction full-Frame union；同一 Commit 内多个 Revision 只采样一次，bootstrap/rejection/terminal settlement 不采样。canonical R 为物理读字节累计，L 为对应 post-live Base payload 累计；报告精确整数 `(R,L)`，T 单列 terminal cold-head 诊断，report schema 升至 2、metrics 升至 `raw-wpfr/2`。
- **Observed**：active-hundred 的共享 `N=64,L=273804` 下，no-migration、paced、Adaptive `(3,5%)`、Adaptive `(4,4%)` 的 `R/avg/amplification` 分别为 `3915000/61171.88/14.2985`、`4020812/62825.19/14.6850`、`2896812/45262.69/10.5799`、`3219260/50300.94/11.7575`。当时 schema-3 的 closed-horizon P 让 `(3,5%)` 看似在 W/P/F/R 四项严格支配 `(4,4%)`；上方 revision 14 复审已把该结论修正为 workload-P 的 28B 反向交换。后者较小的 T 仍只是终相位差。
- **Boundary / Next**：R 仍是“每次 Save 后必冷启一次”的命名读取日程，不声称真实 IO 频率；per-Save vector 保留内部，不输出浮点 ratio。下一步回到策略候选设计，用 revision 12 复跑。

### 2026-08-31：将两个过短反例合并为 active-hundred mixed workload

- **Implemented**：corpus revision 11 用 `active-hundred-mixed` 取代 `many-small-nochange-backlog-100` 与 `continuously-updated-previous-debt`，形成十六 traces / 64 admitted cases。新 trace 由既有 fixed-seed generator 生成：100 个持久对象按 Field/List 1:1 权重初始化，后续 64 轮每轮确定性随机 Update 60 个不同对象，无 Create/Remove。
- **Observed**：no-migration/paced 在 64 个 workload Saves 中都没有 Rotate；Adaptive `(3,5%)` 在 25/47 轮转，`(4,4%)` 在 31/61 轮转。raw `W/P/F/R` 分别为 no-migration `116844/5208/111680/5200`、paced `117120/2128/115092/116856`、Adaptive 3/5 `119760/2176/44040/34368`、Adaptive 4/4 `121700/4336/55044/12060`；四者各有一个 direct terminal-settlement Revision。
- **Decided / Next**：这是面向反馈迭代的策略探针，不新增 dedicated unit test，不锁定 trace/manifest/report hash，也不宣称 steady state 或 winner。下一步先回应其 pacing/active-debt 行为；只有具体候选白盒弱点需要时才继续调整 workload。

### 2026-08-31：revision 10 的 provisional round-1 freeze

- **Implemented**：corpus revision 10 以十五条 trace 形成 60-case admitted matrix；新增 matched `insert-burst-three-one/two-two` 与 nested-prefix `debt-zero-before-rotate` horizon diagnostic，原有 trace vectors/hashes 不漂移。
- **Observed**：burst pair 固定 operation multiset、horizon、final versions 与 per-profile cadence/scope，所有 workload Frames 小于 2 KiB；分组会改变 P，并可经 provisional layout 与 Rotate/settlement placement 传播到 W/F。horizon short/long 的 online prefix 完全相同；paced/Adaptive 的 cutoff 跨过首次 natural Rotate，no-migration 是同 scope control。delta 同时包含额外 Save 与 terminal placement，不做因果成本拆分或跨 horizon 排名。
- **Superseded**：round 1 仍不加入 near-limit performance trace，typed capacity 只作为 qualification gate；但“suite 至此冻结”随后被用户显式重开，并收敛为上方 revision 11 的可调 active-hundred workload。

### 2026-08-31：将 Previous-debt granularity 收编为 canonical matched family

- **Implemented**：corpus revision 9 新增 `previous-debt-granularity-single-large/three-small`；两条 ordinary trace 固定 operation multiset、最终 versions 与 horizon，两组 Adaptive 在共同 `G/E=601/300` pivot 上分别保留一个 300B 或三个 100B old-A debt，随后以相同操作 reconverge。
- **Observed**：48 cases 全部 admitted，本 pair 均为四个 workload Commits 加 direct settlement。no-migration exact tie，paced 仅有 4B P layout fallout；两组 Adaptive 在相同 final scope 下形成 W/P/F 与 R 的交换。
- **Boundary / Next**：结果只证明当前 Adaptive one-object progress floor 对 debt granularity/indivisibility 敏感，不是 arrival/service-rate pressure、steady state、starvation 或一般 size preference。其后的 round-1 suite freeze 已由上条记录闭合。

### 2026-08-31：将 transient overlap/serial 收编为 canonical lifecycle family

- **Implemented**：corpus revision 8 新增 `lifecycle-transient-overlap/serial`；两条 ordinary trace 共享 step0 的两个 100B 对象，并以相同 IDs/payloads/horizon 对两个 400B transient 对象执行相同 Create/Remove multiset，最终状态完全相同。
- **Observed**：40 cases 全部 admitted，均为四个 workload Commits 加 direct settlement。no-migration 在 pair 内均为 `Stay/Stay/Stay/Stay` 并终止于 scope `2/3`；paced 与两组 Adaptive 均为 `Stay/Stay/Rotate/Stay` 并终止于 `3/4`。overlap/serial 的 peak transient live-set 分别为 2/1，后三组策略的主差异是 serial 将 F 降低 408B。
- **Boundary / Next**：4B W 差是当前 provisional layout fallout；本 family 只证明当前策略对有限 horizon 内等尺寸 transient overlap 敏感，不是 churn rate/lifetime prediction、GC、steady state、winner/default，也不建议应用串行化。其后的 debt-granularity 切片已由上条记录闭合。

### 2026-08-31：将 ordinary size-skew 置换收编为 canonical matched family

- **Implemented**：corpus revision 7 新增 `size-skew-low-id-small/large`；两条 trace 只交换 20B/100B payload 与 ObjectId 的绑定，形成八 traces x 四 Baselines 的 32-case matrix。
- **Observed**：32 cases 全部 admitted。size-skew 两侧均以一个 workload Commit 加 direct settlement 到 scope `2/3`；no-migration 对置换不变，paced 与两组 Adaptive 在各 trace 内相等，low-id-large 以相同 P 换来更高 W/F/R。
- **Boundary**：ordinary bootstrap 把两个 Base 共置一个 A Frame，两种 workload 迁移都不立即释放它；结果只证明当前 ObjectId-first progress 对尺寸到 ID 绑定及 immediate-vs-terminal placement 敏感，不是旧 singleton-Frame release oracle、winner 或默认策略。其后的 lifecycle 切片已由上条记录闭合。

### 2026-08-31：将 locality/ObjectId permutation 收编为 matched canonical family

- **Implemented**：corpus revision 6 新增 `locality-next-update-low-id/high-id`；两条 trace 共享 bootstrap 与首个 `StrategyStepViewV1`/selection，只在下一次等尺寸 Update 的 ObjectId 上置换，形成六 traces x 四 Baselines 的 24-case matrix。
- **Observed**：全部选择 `Stay/Stay`，以两个 workload Commits 加 direct settlement 到 scope `2/3`。no-migration low/high 同为 `448/256/256/248`；paced 为 `456/256/448/440`、`452/152/340/292`；两组 Adaptive 参数在各 trace 内相等，low/high 为 `452/252/444/288`、`348/152/340/332`。
- **Boundary**：候选首步看不到 future Update；结果只证明 ObjectId-first assignment 对 next-update locality 敏感，不是长期 hot/cold、温度推断、旧 singleton-Frame oracle 复制、winner 或默认策略。其后的 size-skew 切片已由上条记录闭合。

### 2026-08-31：将 debt-share target boundary 收编为第四个 canonical workload

- **Implemented**：corpus revision 5 新增 `previous-debt-share-dilution-boundary`；普通 step0 创建 40B/960B 对象，随后 measured full-rewrite Updates `100/1/100`，形成四 workloads x 四 Baselines 的 16-case matrix。
- **Observed**：16 cases 全部 admitted。Adaptive `(3,5%)` 走 `Stay/Rotate/Stay` 并得到 `2148/1004/1096/1124`；`(4,4%)` 走 `Stay/Stay/Rotate` 并得到 `2192/1012/1132/1088`。两者各执行三个 workload Commits 与一次 direct settlement，最终 scope 同为 `3/4`。
- **Boundary / Next**：`Base==Delta` 使 Adaptive 的读阈值与表示选择惰性，且只有 Adaptive pair 共享预边界物理 source；controls 不是额外的 4%/5% treatment。本结果不选择 winner/default/steady state。下一步尝试把 known-future hot/cold 证据化简成不暴露 future oracle 的 matched locality/ObjectId-permutation workload。

### 2026-08-31：将 hot-chain threshold-band 收编为第三个 canonical workload

- **Implemented**：benchmark corpus revision 4 以普通 step0 Creates 表达原 test-local source，新增 `read-amplification-threshold-band` 并对四个 Baseline 形成 12-case matrix；原 8 case vectors/trace hashes 保持，expanded manifest/report 使用新 canonical hashes。
- **Observed**：新 workload 全部 admitted；no-migration、paced/Adaptive `(4,4%)`、Adaptive `(3,5%)` 形成三个互不支配 unique vectors。两 Adaptive 以 `4B W` 交换 `260B R`，P/F 相等；paced 与 `(4,4%)` 精确相等。
- **Boundary / Next**：末尾 Remove 会解除 maintenance co-residents 对旧 Frames 的 pin，因此这是 hot-chain reset 与 lifecycle/layout 的综合证据；两个 Adaptive 全程 Stay，不能冒充纯 `E/G` Rotate-or-Stay threshold。下一步把已有 test-local target-band 化简成独立 canonical workload。

### 2026-08-31：闭合多策略 Arena 的内部赛道 vertical proof

- **Implemented**：`TwoLegRotationProbe` 已拆为 Arena class library、独立 Baselines 与 Tests 三个项目；Arena 无 Baselines 反向引用。四个原有策略的 whole-run delegate 与 Adaptive 实现均位于 `Atelia.TwoLegRotationProbe.Baselines`。
- **Decided**：不把 candidate 固定成 selector interface；organizer 只绑定 identity 与 whole-run delegate。当前 canonical toolkit 以逐步 `StrategyStepViewV1` 输入和完整 Stay/Rotate action 驱动 Arena-owned planning/apply，产出带 final Store、workload Commit receipts、final checkpoint 与 typed termination 的 Arena-certified product；product 不允许策略声明 metrics。
- **Observed**：裸 final Store 无法唯一恢复 outer-Commit Peak，且缺 final PublishedRevision 与 Current-scope 时间线，故 Store-only 不足以独立计算 P/F/R。任意 hand-built Store/ledger 的离线验真等待真实 candidate consumer，不在本轮开放。
- **Verified**：原 8 cases 的 typed outcomes、W/P/F/R、manifest/report/trace hashes 全部不漂移；Remove-A-debt + Insert 反例仍得到 `parentDebt=true,G=200,E=0`，DebtZero 与 Adaptive 分别选择 Stay/Rotate。下一步先扩充 strategy-neutral workload 因果多样性，再冻结首轮 competition packet。

### 2026-08-31：选择静态多策略 Benchmark Arena 作为下一阶段方向

- **Researched**：Redis incremental rehash、Sprite LFS cost-benefit cleaning、RocksDB/LSM compaction debt、Git delta depth、Kafka cleaner ratio/lag/throttle、SQLite WAL checkpoint 与 ski-rental/knapsack 分别覆盖渐进搬迁、搬迁选择、压力控制、重建链和未知未来决策；整理为 `experiments/TwoLegRotationProbe/PRIOR-ART.md`。
- **Decided**：先闭合在途的 natural parameter-discriminating workload，再以旧 golden 不漂移为 gate 做 payload-only、无状态 Policy V1 seam；随后拆 Arena/Tests/Baselines、形成 workload-only suite，并让多个独立 candidate projects 静态链接参与统一复跑。Arena 继续独占 exact planning/admission/apply/settlement/metrics。
- **Deferred / Boundary**：不建立插件发现、排行榜、标量 score、自动进化或独立 Contracts package；Frame-aware facts、stateful lifecycle 与 strategy-neutral persistent suite hash 都等待命名 consumer。第一轮只报告 typed outcomes 与 raw `W/P/F/R`，第二轮起对受既有结果影响的改进做 post-freeze 新 workload validation。
- **Recorded**：最小依赖图、策略信息边界、corpus 因果轴和并行 Agent 隔离规则记录为 DB-012；公开 synthetic suite 不替代真实 workload/SLO 或唯一产品默认值。

### 2026-08-31：将 Adaptive 原子 profiles 接入 benchmark-v1

- **Decided**：manifest schema 2 以单一 `selectionProfile` 取代 target/decision 双栏；corpus revision 3 注册 no-migration、paced、Adaptive `(3,5%)`、Adaptive `(4,4%)` 四个 exact profiles。Adaptive identity 固定映射 decimal 参数，只读取当前 normalized facts；不解析 ID、不接受任意参数、不引入 plugin/fallback。
- **Observed**：两个 frozen workloads 共 8 cases 全部 admitted，旧四 case raw/scope 与 trace SHA 不变。handwritten 上 no-migration 为 `856/680/680/832`，其余三 profile 同为 `1536/696/804/756`；mixed 上 controls 同为 `368/164/344/352`，两 Adaptive 同为 `440/164/184/280`。每组有两个互不支配 unique vectors，但 `(3,5%)`/`(4,4%)` 都被 workload 遮蔽。
- **Verified / Next**：独立 `H=300,D=50,B=100,G=2500` checkpoint 使两 profile 分别选择 Base/Delta，并逐字段等于 direct policy，锁定 registry 没有错绑参数。下一步把已有 threshold-band trace 接入相同 corpus，先获得 canonical 参数辨别证据，再做不合分的 Pareto reduction。

### 2026-08-31：闭合 common third-epoch continuation discriminator

- **Observed**：fixed-cadence 两侧第二 epoch 终点同为 scope `3/4`、Previous debt `{10,20,30}`、per-object `H/B=1`、Current tail 52B；control 的三个 cold Base 共置一个 file-3 Frame，paced 分散在三个 Frames，source cold-head read 分别为 700/792B。
- **Observed**：两端共同采用 paced-one-debt 跑同构第三 epoch，迁移顺序同为 `10,20,30`；三个 workload Commit 写入均为 `152,260,348B`，direct settlement 均为 52B，第三段 `W/P/F/R` 同为 `812/348/812/792`。三个 workload checkpoint 的 cold-head bytes 仍为 `848,1104,792` 与 `792,792,792`，旧 Bases 全部覆盖后差异消失。
- **Concluded / Next**：coarse endpoint summary 只在此 trace 上对 closed W/P/F/R observationally sufficient，不能推广成一般充分性；v1 final-only R 不等于 intermediate cold-read guardrail，checkpoint 也不是实际或累计 IO。下一步以 coupled named profiles 把 adaptive `(3,5%)`/`(4,4%)` 接入相同 benchmark workloads，再形成不合分的 Pareto evidence。

### 2026-08-31：闭合 Base-fraction target-band 与首轮因果 matrix

- **Observed**：测量前固定 `A-debt=40B`、`B-local=960B`，两次 `Base == Delta` Update 使 read limit、soft budget 与 progress 惰性；`(3,5%)` 以 `[Rotate,Stay]`、`(4,4%)` 以 `[Stay,Rotate]` 各完成一次 workload 轮转，再各 direct settlement 到 scope `3/4`。
- **Observed**：两侧 `W/P/F/R` 为 `1144/1004/1096/1124` 与 `1188/1012/1128/1088`，final Previous debt 为 `{1,100}` 与 `{100}`；final per-object `H/B` 同为 1，所以 R 差异来自 retained full-Frame layout/provenance。这是严格 4% equality / 5% Rotate 边界及 target timing/terminal liability 的 Pareto 证据，不是默认参数或稳态 winner。
- **Decided**：首轮小型 matrix 以 negative control、read-threshold、Base-fraction target 与 typed inadmissibility 四条因果索引闭合；不新增重复 goldens 的聚合 test/type，不把 rejection 数值化，也不增加第五 score。其后的 third-epoch continuation-state 问题已由上方条目闭合。

### 2026-08-31：加入 realized H/B diagnostic 与首个参数辨别器

- **Observed**：test-local diagnostic 从 accepted head 的 OVD/reconstruction authority 冻结全部 live object 的原始 `H/B`，显式区分 finite、`0/0` 与 positive-over-zero infinity；不带候选 Delta、不汇总共享 Frame，也不扩张 canonical evaluator/report。
- **Observed**：原 matched cadence 中 `(3,5%)` 与 `(4,4%)` 均为 `1296/472/796/524`；soft budget 和 progress floor 完全遮蔽参数变化，故将其固定为 parameter-insensitive negative control。
- **Observed**：独立 threshold-band witness 排除 target/progress/budget 混淆后，`(3,5%)` 与 `(4,4%)` 分别得到 `1516/1056/1056/1212`、`1512/1056/1056/1472`，final hot `H/B=15/10`、`40/10`；本 trace 只证明少写 4B/多读 260B 的局部 W/R trade，共同 terminal evacuation 主导相同 P/F。
- **Open**：下一参数辨别器应隔离 `E/G` 落在 `[4%,5%)` 的 target 分叉，再决定 test-local matrix reduction 形状；不提前合成第五 score 或默认参数。

### 2026-08-31：落地 read-amplification + Base-budget policy v0

- **Observed**：以 synthetic payload proxy 实现两参数纯 selector：严格读放大/轮转边界、5% soft Base envelope、`Base<=Delta` 弱支配、NoChange-first one-debt progress、target-specific Stay/Rotate decisions；最终仍由 existing whole-candidate hard gates 接纳。
- **Observed**：独立 exact witness 证明 B-contained hot object 的 `(251+50)/100=3.01` 真由阈值选择 Base；policy-selected oversized Rotate 命中 typed `PayloadAndTailMetaLength`，不 fallback、不改变 Store。
- **Observed**：首个 matched cadence 中 control/paced/adaptive 的 `W/P/F/R` 分别为 `924/476/476/524`、`1248/372/748/524`、`1296/472/796/524`；paced 在本 fixture 局部严格支配 adaptive。Adaptive 将 hot reconstruction payload 从 controls 的末值 300 重置到 100，但两次换腿使 final-only R 均为 524。
- **Boundary / Next**：payload bytes 不是 encoded write 或物理 cold IO；soft budget、NoChange-first progress 是对原始草案的显式 v0 细化。下一步先建立 test-local intermediate per-object amplification diagnostic，再做小范围参数/workload matrix；不改 canonical report 或声明默认 winner。

### 2026-08-31：闭合 fixed-cadence two-epoch terminal-liability witness

- **Observed**：共同的三对象 A Frame 上，no-migration 与 paced-one-debt 都按 `3 workload Stay + 1 direct settlement` 运行两个 epoch；两侧同为 8 Commits、两次 scope advance，最终 scope `3/4`、live state 与 Previous debt `{10,20,30}` 对齐。
- **Observed**：no-migration combined `W/P/F/R=984/664/800/700`，paced 为 `1624/348/812/792`。paced 以 `+640 W` 换得 `-316 P`，同时 horizon F/final-only R 高 12/92 bytes，形成当前 fixture 下的 raw Pareto trade，不选择 winner。
- **Observed**：paced 首个 settlement 后形成的新 Previous debt `{10,20,30}`，在第二 epoch 的三个自然 Save 中又按 `10,20,30` 被实际迁移；terminal liability 因而进入了后续 workload，而不是只被另一个 zero-workload settlement 掩盖。
- **Boundary / Next**：固定 Stay cadence 是实验控制，不是 `DebtZeroThenRotate` 或产品 trigger；相同 final debt membership 也不等于相同 retained physical layout/provenance state、steady state 或 regenerative cycle。上方 common third-epoch discriminator 已继续裁决这种物理历史对 closed vector 与中间 cold-read pressure 的不同影响。

### 2026-08-30：闭合 source-layout/provenance fixed-horizon 2x2

- **Observed**：role-disjoint 六对象 source 在 Shared 单 payload Frame 与 Split cold/changed 两 payload Revisions 下，分别运行 no-migration/paced；四格都执行四个 workload Stay 加两次 direct terminal settlement，以 6 个 Commits 从 scope 1/2 到 3/4。
- **Observed**：Shared/Split 在同一 decision treatment 下最终 raw vectors 完全相同：no-migration 为 `W/P/F/R=1556/1288/1288/1352`，paced 为 `2284/788/956/1352`。但 workload 内 live-object reconstruction Previous payload-Frame bytes 不同：Shared 恒为 1276，Split/no-migration 恒为 1308，Split/paced 为 `1308,1308,656,656`。
- **Concluded / Boundary**：v1 W 排除 bootstrap，R 只读 final head，两次换腿又把原 A 排出最终 closure，因此 raw-vector 相等是 measurement-boundary 的负结果，不证明 layout 无关。source Revision/OVD/ticket/offset 同时变化，不能称 pure packing；后续 fixed-cadence two-epoch witness 已由上方 2026-08-31 日志闭合，general complete-cycle/long-run protocol 仍未建立。

### 2026-08-30：闭合 fixed-two-scope-advances horizon discriminator

- **Observed**：handwritten control 在 v1 scope 2/3 后真实追加一个 zero-workload direct settlement，得到 `commits/W/P/F/R=6/944/680/680/752 @ 3/4`；paced 已以 `5/1536/696/804/756 @ 3/4` 完成相同两次 scope advance。分段组合仅作 `W/commits=sum`、`P/F=max`、`R=final-only`，且全 Store tail growth 独立核对 W。
- **Observed / Boundary**：control/paced final Previous debt 分别为 `{10,20,30}` / `{1004}`。equal scope 排除了不同 final file generation，却没有中性化 Commit placement、布局历史、terminal liability 或 single-A-Frame bias；该 trace 上 control 四项较低不构成一般 winner。
- **Next**：先做 test-local、anchor-normalized shared/split source-layout discriminator；在第二个真实消费者出现前，不抽通用 horizon runner，也不修改 canonical benchmark schema。

### 2026-08-30：形成 benchmark-v1 首组 matched treatment 证据

- **Observed**：corpus revision 2 把 handwritten 与 seed12345 mixed traces 各自扩为 no-migration/paced pair；组内共享同一 frozen trace，且 source、target、protocol 与 horizon 相同。四例均 admitted，manifest/report 与两条 expanded trace 均有 literal SHA-256 goldens。
- **Observed**：mixed pair 没有 eligible A-debt `NoChange`，两侧严格同为 `W/P/F/R=368/164/344/352`。handwritten control 为 `856/680/680/832 @ scope 2/3`，paced 为 `1536/696/804/756 @ scope 3/4`。
- **Concluded / Next**：active pair 混合了 paced migration、workload 内额外轮转与无条件 terminal rotation，不能选择 winner；下一 named witness 先让两侧完成相同次数的 scope advance，再判断 terminal-liability bias。canonical report 同时补上 outcome horizon/phase 与 manifest 的 fail-close 一致性校验。

### 2026-08-30：建立 benchmark-v1 manifest、batch runner 与 canonical raw report

- **Observed**：closed registry 已把 step0 Create-only workload bootstrap 成单一 full-OVD A Frame + metadata-only B anchor，并让 evaluator 只消费后续 steps；target 仅按 source A-debt 决定 Stay/Rotate，decision 仅含 Delta no-migration 或 smallest-ObjectId paced migration，不读取未来 trace、feasibility 或 candidate observation。
- **Observed**：manifest 绑定 source/trace/generator/seed、expanded trace SHA-256、treatments 与实际 evaluator/settlement/accounting/layout/grammar/read identities；canonical UTF-8 report 以 manifest hash 关联，四叶 outcome 中只有 admitted 含 W/P/F/R。伪 protocol 标签、trace hash 漂移和 rejection metrics 泄漏均 fail closed。
- **Observed / Boundary**：handwritten paced 与 seed12345 mixed no-migration smoke cases 分别以 5/3 realized Commits 到达 final scope 3/4 与 2/3，重跑 bytes 相同并有 literal SHA-256 goldens。它们使用不同 trace，不能比较策略优劣；下一步先在同一输入上形成 matched treatments，并保留 single-A-Frame bootstrap 与 terminal-horizon bias。

### 2026-08-30：闭合 evaluator v1 typed outcome 与 terminal settlement

- **Observed**：`EvaluatorV1Session` 在独立 Store fork 上运行；只有完整 workload 加真实 terminal settlement 的 `AdmittedEvaluatorRun` 暴露 W/P/F/R 与 final cursor。selected capacity、有限路径 `RejectedUnproven` 和 incomplete 均为互斥、不可评分结果，stale/corrupt/programmer errors 继续 fail closed。
- **Observed**：`DirectRotateElseAscendingSingleDebt-v1` 先尝试零 Stay 的 maintenance Rotate；容量阻塞后按 ObjectId 升序逐个迁 A-debt。large-A witness 的两次 Stay 与最终 Rotate 被实际 replay 为一个 synthetic Commit，P 因而包含整段 settlement burst，原 source Store 不变。
- **Boundary / Next**：成功只保证 terminal source `A/B` 的旧 A 退出 final `B/C` current reconstruction，不保证新 scope Previous debt 为零。下一切片建立 manifest/batch/raw report，并在自动优化前用完整 epoch/long-run 对照量化 terminal-liability bias。

### 2026-08-30：冻结 evaluator v1 的 W/P/F/R 原始指标

- **Observed**：experiment-only accumulator 以 outer Commit 前后全文件 tail 总和之差计算 W/P，因而 fresh-C 的 4B header 会被计入；同一 Commit 内多个 realized Revision 合并为一个 peak sample，F 从初始与各 checkpoint 的 Current tail 取最大值。
- **Observed**：`FinalHeadColdLoad` 将 OVD materialization chain 与所有 live-object current-reconstruction chains 的 full-Frame 地址去重后求和；空 live graph 仍因 OVD chain 产生非零 R，OVD/object 重叠 Frame 只计一次。
- **Boundary / Next**：指标只接纳实际成功写入，typed rejection 不参与平均且没有 result scope；metric `Complete` 不证明 horizon 闭合。下一步先设计并执行 canonical terminal settlement/typed run outcome，再提取 evaluator runner/report。

### 2026-08-30：闭合 grouped-foreground Frame-envelope capacity witness

- **Observed**：同一 source、同一份 normalized facts 与固定 Stay-B target 下，三个 B-contained Update 全写 Base 的 foreground-only exact candidate 距 `PayloadAndTailMetaLength` 上限不超过 32B；再加入一个 10B A-debt same-state migration 后得到该 exact limit 的 typed rejection。两侧 alternate Rotate-C 均可行。
- **Observed**：rejected selected Stay 不 fallback 且不改变 Store/cursor；foreground-only 分支经 completion/apply 只向 B 追加 exact candidate，证书无需 preparatory Stay，也未隐式执行 final Rotate-C。
- **Corrected / Next**：同一 source/tail 的两个候选拥有相同 Frame start，因此 B-tail relative-start gate 不能解释一侧拒绝；真正的因果是 one-Revision/one-Frame envelope。该结果是 evaluator 的 hard-feasibility 反例，不是 repair、score、file-size 或 rotation-trigger policy；下一阶段先冻结 evaluator v1 的 horizon、settlement 与 raw metric contract。

### 2026-08-30：闭合 natural-update hot/cold opportunity witness

- **Observed**：两个 100 B A-debt objects 各自独占同构 singleton A Frame；同一两步 fixed-Stay trace 先显式迁 future-hot 或 cold，再在两侧强制 future-hot `Base@B` 并移除非空 Save sentinel。实际 append 都是 `[152,148]`，累计 300、峰值 152。
- **Observed**：第二步后 hot-first 仍需 `{cold}`/1 old-A Frame，cold-first 已清空 old-A debt；两侧 completion 都零准备。反事实 terminal-C 中 hot-first 要搬 cold、C append 更大，但换腿后只留 hot/1 个 new-Previous Frame，cold-first 则留下 cold+hot/2 个 B Frames。
- **Concluded / Next**：已知未来 Update + forced-Base control 会改变当前迁移 membership 的价值，但 scope shift 会反转部分压力；这不是在线温度推断、总 IO 或长期 winner。后续 burst witness 已把原先的 B-tail 假说纠正为 single-Frame envelope coupling。

### 2026-08-30：闭合 payload-skew migration Pareto witness

- **Observed**：两个 A-debt objects 分别独占同构 full-OVD A Frames，large=`Id10/1000 B`、small=`Id20/100 B`，再由 metadata-only full anchor 合并；反向 ID 顺序与显式 membership 排除了 ObjectId treatment。同一 pure-Insert、fixed Stay 只选择迁 small 或 large。
- **Observed**：small-first 的 exact maintenance/whole Stay append 更少、B slack 更多；large-first 同样只让一个对象/一个 Frame 退出 A debt，却退出更多 Previous full-Frame closure bytes。两侧都无需 preparatory Stay 即可形成 exact terminal-C。
- **Observed**：small-first 的反事实 C append 更大，但换腿后新 Previous debt 为 `{20,1001}`/101 B/1 Frame；large-first 的 C append 更小，却留下 `{10,1001}`/1001 B/1 Frame。实际运行只 apply Stay，没有创建 C。
- **Concluded / Next**：object count 与 Frame count 不是 byte pressure 的充分统计量；即时写入和 old-A reconstruction closure 形成局部 Pareto 冲突，轮转后压力还会反向重组。这不是物理回收、实际/总 IO、长期 winner 或产品默认。下一切片用已知未来日程的 oracle-style hot/cold 对照，验证 domain Update + forced-Base control 对当前迁移 membership 的 opportunity cost。

### 2026-08-30：闭合 equal-byte migration membership conflict

- **Observed**：合法 source 以共享 `{1,2}` payload、独占 `{3}` payload、metadata-only full-OVD anchor 与 B PublishedRevision 组成；三个 live object 都是 100 B。同一 pure-Insert、固定 Stay 只改变迁移 member：smallest-ObjectId 选 `1`，frame-release-first 按 source reconstruction Frame fanout 选 `3`。
- **Observed**：两侧 maintenance bytes 与完整 Stay layout 相等，结果都剩 2 个 A-debt object/200 B；前者仍需 shared+singleton 两个 A Frames，后者只需 shared，差值等于 singleton stored Frame length。
- **Observed**：两侧 completion certificate 都直接给出 terminal-C，无 preparatory Stay；反事实 C layout 相等，换腿后 Previous debt 分别为 `{1,1001}` 与 `{3,1001}`，均为 101 B/1 Frame。实际运行只 apply Stay，未把 certificate 算作 realized rotation。
- **Concluded / Next**：smallest-ObjectId 不是对 coarse-Frame pressure 中性的 assignment；该结果只证明 equal-write membership 的局部因果差异。下一切片用 payload-skew singleton objects 冻结 maintenance append 与立即释放 Previous full-Frame bytes 的 Pareto 冲突，不预设换算权重或 winner。

### 2026-08-30：闭合 source payload-Frame partition discriminator

- **Observed**：两种 source 都使用 metadata-only full-OVD A anchor 和六个 External bindings；shared accepted chain 为 `shared payload -> anchor -> B published`，split 为 `cold payload -> changed OVD Delta -> anchor -> B published`。split 不从无关 snapshots 拼装 source，OVD materialization 与 lineage validator 均通过。
- **Observed**：相同 trace、四 treatment 与 target 下，逐步 old-A debt ObjectIds/Base payload bytes 完全相同。第三次 Stay 的 shared/split required Previous-Frame bytes 为：Delta+none `1276/1308`、Delta+paced `1276/656`、Base+none `1276/652`、Base+paced `0/0`；exact addresses、count 与 stored FrameLength sum 一致，neutral anchor 不进入 object reconstruction metric。
- **Concluded**：object-debt summary 不是 exact coarse full-Frame reconstruction pressure 的充分统计量。这是 current-reconstruction source provenance/layout 反例，不是总 IO、纯 record-packing 因果、multi-frame Revision 或产品策略输入裁决。
- **Decided / Next**：构造等 payload、每步只迁一个对象的选择冲突，让 ObjectId-first 选择无法释放 Frame 的对象，frame-release-aware treatment 选择能完整释放另一 Frame 的对象；先比较无权重原始事实，不定义 winner。

### 2026-08-30：闭合 changed-write x cold-migration 2x2 因果对照

- **Observed**：六对象共享 A Frame fixture 将 migration-only `{1,2,3}` 与 changed `{10,20,30}` 按 100/200/300 B 等尺寸配对；四条运行共享 `Update 10/20/30 + Create` trace 与 `[Stay, Stay, Stay, Rotate]`，paced 两格固定迁移 `1/2/3`，不存在 Update/migration assignment 重叠或 selector mediation。
- **Observed**：三次 Stay 后，Delta+none 的 old-A debt 保持 1200 B；Delta+paced 与 Base+none 各从 1100/900 降至互不重叠的 600 B；Base+paced 为 1000/600/0 B。组合格每一步退休的 ObjectId 都是两个单轴格退休集合的不相交并集，只能称该 fixture 上的 set-additive retirement。
- **Observed**：六个 Base 共居同一个 1276 B A Frame；三个非组合格第三次 Stay 后仍需该 Frame，只有组合格清掉最后一个依赖后释放。换腿后的 B/C Previous debt 分别为 none、cold 600 B/3 Frames、changed 600 B/3 Frames、all 1200 B/3 Frames；组合格每个 B Frame 共居一个 changed Base 与一个 cold Base。
- **Observed**：realized append vectors 依次为 Delta+none `48/48/48/1292`、Delta+paced `152/256/356/680`、Base+none `144/244/344/680`、Base+paced `248/452/652/72` B；这些只属于当前 provisional v0 grammar，不定义 winner，也不归因成总读取 IO 或一般机制协同。
- **Decided（当时）**：该条提出的 source layout discriminator 已由上方日志闭合；结果证明 object debt 与 exact Frame pressure 非等价，但不要求产品策略采用某一字段组合。

### 2026-08-30：闭合 changed A-debt Base/Delta 因果对照

- **Observed**：同一 `Update 10/20/30 + Create` trace 与固定 `[Stay, Stay, Stay, Rotate]` 日程下，Delta control 的 realized append 为 `48/48/48/668` B；Base treatment 为 `144/244/344/60` B。前者在 final C 以 608 B maintenance domain records 集中 evacuation，后者把完整值写入分散到前三次 foreground Save；本 witness 不定义总分或 winner。
- **Observed**：Base treatment 的旧 A debt 从 600 B 依次降为 500/300/0 B，但 10/20/30 共居一个 652 B A Frame，因此 object-reconstruction Previous-frame bytes 在最后一个 A 依赖消失前不下降。对象级 debt 不是粗粒度 RBF read pressure 的充分代理。
- **Observed**：换腿后，Base treatment 的三个 Base@B 在新 B/C scope 中成为 600 B / 3 Frames / 720 frame bytes Previous debt；Delta control 因 C mandatory evacuation 而得到零 B reconstruction debt。zero-prep counterfactual terminal 同样以更小 append 换取 `{10} -> {10,20} -> {10,20,30}` 新-scope debt。
- **Decided**：第三种 action caller 只促成 test-local、stateless decision selector；target selection 继续正交，未引入 policy interface、Runner、score 或自动 trigger。
- **Decided（当时）**：该条提出的 mixed Update/NoChange 2x2 已由上方日志闭合；结论保持 fixture-level，不外推一般替代/互补关系。

### 2026-08-30：闭合 scope-safe rotation observation reductions

- **Observed**：现有固定日程与 `DebtZeroThenRotate` runs 已归约为 test-local realized step / observed epoch / run 原始量；source/result 各带自己的 FileScope，Rotate 属于并关闭旧 epoch。额外 `[Stay, Rotate, Stay, Rotate]` witness 验证 `A/B -> B/C -> C/D` 的跨 epoch result/source 连续性。
- **Observed**：source debt/Previous Frames 从 normalized source paths 与当步 Store layout 冻结，result 复用 selected candidate 的既有 raw observation；foreground、maintenance、non-domain 与 whole append 在 step、epoch、run 三层守恒，不建立第二 sizing authority。
- **Decided**：Previous Frame bytes 只表示 live-object current reconstruction 的去重完整 Frames，不是累计 IO；next-frame-start slack 只表示 relative start 的可编码余量，不是文件容量。
- **Decided**：certificate final-C 以 `Counterfactual` 独立投影，只记录额外 preparatory Stay 数和 final-C append/result；不计入 realized totals/peaks/rotation，也不聚合互斥未来。当前归约只执行了零 prep，既有 certificate 测试另有两 prep witness。
- **Decided（当时）**：这些值类型继续留在测试内；该条提出的 changed A-debt Base/Delta 切片已由上方 2026-08-30 日志闭合，仍未定义 score、winner 或产品 trigger。

### 2026-08-29：闭合 DebtZeroThenRotate 进展/停滞基线

- **Observed**：同一四步纯 Insert trace 上，target 只从本次 Save 之前的 source A-debt 派生；no-migration 四步均 Stay 并以 `{10,20,30}` deferred debt 结束，paced-one-debt 的 source debt 为 `{10,20,30} -> {20,30} -> {30} -> {}`，第三步清零、第四步才真实轮转。
- **Observed**：paced 换腿后的 Previous debt 重新成为 `{10,20,30,1001,1002,1003}` / 603 B / 3 Frames；这是相对 B/C 新 scope 的正常锯齿，不能用最终 debt 非空否定已经发生的轮转。
- **Observed**：no-migration 的每个 accepted Stay 都有可行 terminal Rotate certificate，但 caller Store 始终只有 A/B；certificate 是反事实 completion witness，不是 realized action 或自动进展。
- **Decided（当时）**：有限证据只称 `CompletedTraceWithDeferredPreviousDebt`，不外推“可永远追加”；该基线不比较成本 winner，也不成为产品默认 trigger。该条提出的 rotation observation reductions 已由上方 2026-08-30 日志闭合。

### 2026-08-29：建立单步策略 seam 与固定日程迁债对照

- **Observed**：无状态 `ExplicitRotationPolicyStepHarness` 只在 caller 选定 target 后组合 selected capacity、Stay completion certificate 与 exact apply；四类 typed outcome 保留 applied Stay/Rotate、capacity rejection 与 `RejectedUnproven`，不 fallback、不执行 certificate 的虚拟 maintenance/final chain。
- **Observed**：同一三步 Insert trace 与固定 `[Stay, Stay, Rotate]` 日程下，no-migration 的 Previous debt 为 `{10,20,30} -> {10,20,30} -> {1001,1002}`，paced-one-debt 为 `{20,30} -> {30} -> {10,20,1001,1002}`。
- **Observed**：paced 将相同的三个 maintenance Base domain records 分散到三步，降低 realized peak 与最终 Rotate append；代价是换腿后 Previous debt/base bytes/frame bytes 更高。迁债早期虽减少 debt bytes，却因剩余对象共用 A Frame 而未减少 Previous-frame IO。
- **Decided（当时）**：固定日程仅是隔离 migration pacing 的实验控制，不裁决产品 rotation trigger；ObjectId 顺序也不代表真实冷热。该日志提出的 `DebtZeroThenRotate` 与后续 rotation observation reductions 均已由较新的日志闭合。

### 2026-08-29：用现有 seams 跑通连续两次换腿

- **Observed**：test-local caller script 依次执行 Stay-B、Rotate-C、Stay-C、Rotate-D，跑通 `A/B -> B/C -> C/D`；每步 physical materialization 等于 normalized PostLive，current reconstruction 保持在结果 FileScope 内。
- **Observed**：两个 accepted Stay 都先取得 exact `CanPrepareAndRotate` certificate，规划不改变 caller Store；live run 只应用 caller 选择的 initial Stay，certificate 的 maintenance/final chain 保持 admission witness 角色。
- **Observed**：同一四步脚本的 Previous debt 为 `{10} -> {20} -> {} -> {20}`，直接展示迁债与换腿后的锯齿，而无需先定义 score。
- **Decided（当时）**：先不新增通用 Runner、transcript 或 observation schema；后续两个迁债 caller 暴露的 admission/apply 重复已提取为上一条日志所述的无状态单步 seam，仍未建立有状态 Runner 或 policy interface。

### 2026-08-29：闭合显式 apply 与保守 CanPrepareAndRotate 证书

- **Observed**：caller-owned volatile cursor 以 `{FileScope, PublishedRevision, Current tail}` 绑定实验 runner 的当前点；强类型 Stay-B / Rotate-C apply 在唯一变异前重验 source facts、reconstruction、candidate state/layout/anchor 与 target tail，失败不产生本次追加，但不声称 durable publication、并发或 crash safety。
- **Observed**：certificate planner 在 exact scratch fork 上实际重放同一 apply；先试零迁移 Rotate-C，容量拒绝后按 ObjectId 升序逐个生成统一 maintenance Stay-B candidate，每个 prefix 再试 Rotate-C，首次成功即冻结 exact chain。
- **Observed**：3 x 140,000,000-byte A debt 反例稳定生成两次单对象 B migration 后的 final C；relative-start 边界则稳定返回带 stage/count/ObjectId/capacity 的 `RejectedUnproven`。证书生成不改 caller Store，成功 chain 可在真实 Store 上重放到 B/C closure。
- **Observed**：apply 8/8、certificate 5/5、当时 TwoLegRotationProbe 259/259，solution build 0 warning / 0 error；独立复审无 blocker/high/medium。其后续 caller-scripted 连续轮转证据见上一条船长日志。

### 2026-08-29：闭合显式 Rotate-C candidate 与 maintenance 统一入口

- **Observed**：`RotateCRevisionPlanner` 只消费与 Stay-B 相同的 normalized facts；A-dependent PostLive 强制写 Base@C，B-contained Update 才接受显式 Base/Delta，B-contained NoChange 可选 same-state Base，否则 External 到精确旧 B head。
- **Observed**：候选写 full OVD Base，entries 恰等 PostLive，Remove 完全省略，source PublishedRevision@B 只作为 shared lineage anchor；mixed fixture 以“B-local head 早于 PublishedRevision”和“head@B/Base@A”两个 discriminator 证明 exact-head encoding 与 B/C current reconstruction closure。
- **Observed**：独立 `NormalizeMaintenanceOnly` 复用普通 Save 的唯一 source inspection，不放宽 `SaveStep` 非空约束；默认 Rotate-C decision 与旧 ImmediateRotation candidate 在 runtime Frame、OVD、address 和 estimate 上等价。
- **Observed**：共享 mechanical record builder 保持 Stay-B 行为；聚焦复核 15/15、TwoLegRotationProbe 236/236、solution build 0 warning / 0 error，独立审查无 blocking/high。
- **Decided / Next**：下一切片建立同一 facts 下的 caller-explicit Stay-B/Rotate-C paired evaluation，分别表达 exact physical feasibility 与原始 observations，不自动选 winner、不搜索 capacity repair，也不提前实现 durable publication。

### 2026-08-29：闭合 normalized Save 与显式 Stay-B candidate

- **Observed**：`parent PublishedRevision@B + SaveStep` 已归一化为单一 ObjectId-ordered immutable fact sequence，四个 typed views 与 Parent/PostLive states 均由它派生；source addresses 只来自 runtime OVD，并对 OVD replay chain 与全部 parent-live object 验证 A/B reconstruction closure。
- **Observed**：caller-explicit Stay-B candidate 在同一 B Revision 中合并 Insert、Update Base/Delta、Remove OVD mutation 与 unchanged A-debt same-state Base；对象 Delta 精确指向 source object head，OVD Delta 精确指向 source PublishedRevision，最终只由 `PlannedRevisionV0` 定尺。
- **Observed**：mixed fixture append 后 OVD/materialization 与独立 logical replay 一致，A debt exact set 从五个对象降为两个；canonical ordering、decision conflicts、oversized candidate 和 planning no-mutation 均有 executable evidence。
- **Decided / Next**：该 seam 仍是纯 candidate，不 append/publish，也不选择策略。下一切片在同一 normalized facts/plan shape 上实现 Rotate-C；heuristic、capacity repair、completion search 与 continuous runner 继续后置。

### 2026-08-29：双腿探针收敛输入分区与两阶段规划

- **Observed / Next**：caller-selected B migration、immediate C 与 terminal sizing 已足够；最大缺口仍是 workload 与 rotation actions 未接通。策略输入现按 `Insert / Update / Remove / NoChange` 建模，Remove 先改变 PostLive，NoChange 由 parent ObjectMap/OVD 派生。首版分别在假设可容纳下生成 Stay-B/Rotate-C 偏好计划，再 exact-filter；容量失败暂不搜索次优修补，只保守 fail closed。下一步建立 unified candidate 和连续多轮转 runner；搜索等待真实 `RejectedUnproven` 或 `RejectedCapacityUnsearched`。

### 2026-08-29：选择 Revision shared prior-snapshot anchor

- DB-010 选择 one-Revision/one-accepted-prior law；Base 删除 per-record parent，Delta 属性明确为 `DeltaParentFrameTicket`。
- Base lineage 从 containing OVD parent 查询 prior ObjectId；mixed new/domain/relocated Base 与 exact-parent Delta、AA/BA、genesis/Absent/visible Remove/malformed anchor 均有 executable evidence。
- provisional Base record 删除 `NoneToken` 占位；更新后的 hot/cold modeled file/final-read 为 `1864/1132`、`1428/1396`、`1500/1132`，fixed mixed 为 `516/176`、`460/444`、`460/296`。
- OVD Base checkpoint 会丢弃旧 tombstone；跨 reopen 的 no-ID-reuse 仍需未来独立 ID epoch/retired-ID authority。mixed-snapshot import/rescue/stale Save 明确不在当前模型。

### 2026-08-29：闭合 relay-free runtime C append

- planned C 改以 immutable runtime `Frame` 为唯一语义 authority；provisional grammar 仅作尺寸投影，删除 plan 内重复的 `ProjectedStateMap`。
- `ImmediateRotationAppender` 在 source/candidate preflight 后，通过“首帧成功再注册文件”的 in-memory store seam 加入 C；不发布 StateStore head。
- append 后只由 `MaterializeLive(C)` 产生 B/C StateMap；AA/BA/BB 的 logical state、reconstruction 与 lineage 已闭合，当时 Probe 204/204。
- 完整历史 lineage 损坏仍由离线诊断暴露，但不再阻塞 current reconstruction 或 immediate rotation。
- 当时的下一步 DB-010 已由后一切片裁决；completion search/`CanPrepareAndRotate` decision procedure、bytes codec 与 publication/reopen/crash 继续分离。

### 2026-08-29：选择 relay-free 并删除 forwarding 主线

- 为三方案并存的可执行岔口 `b84620b` 创建 annotated tag `research/relay-vs-relay-free-20260829`，随后把 DB-009 裁决为 Revision locator。
- runtime 删除 direct-Base/locator 双 inspection：唯一 lineage 语义为 Delta exact parent、Base Revision locator。Delta 必须正 payload 且 ordinal 为 `parent + 1`；same-version maintenance 只保留 RelocatedBase。
- OVD 新增 `MaterializeLive(PublishedRevision)`；WorkloadSimulator 每次 Save 写 runtime OVD，StateMap 只由 OVD replay 派生，并以 point lookup 交叉校验。V0 Frame adapter 直接投影同一 OVD，不再从 SaveStep 重造计费副本。
- relay-free `ImmediateRotationPlanner` 删除 caller StateMap、RelaySet、RelayRevision、B capacity debt 与 Relay grammar role；source 只来自 B PublishedRevision OVD，所有 evacuation Bases 以 B 为 locator，planner 只产生 C full Base/OVD。
- canonical AA/BA/BB、Published Remove、OVD insertion order、C capacity、B exhausted tail、source scope 与零 mutation 均有 executable tests；Probe 198/198，独立 correctness review 无 blocker/high/medium。
- preparatory B Base migration 仍是一般 `CanPrepareAndRotate` 的 correctness path；当时遗留的 runtime C append 与仍 Open 的 DB-010 均已由后续切片闭合/裁决。

### 2026-08-29：历史 discriminator：建立 OVD authority 并比较 forwarding/relay-free（已归档）

- 用户澄清原始中继设想是 `B zero-payload Delta + OVD Self`，C Base 把 B Revision 当 locator；这与当前 planner 的 `direct parent + empty OVD` 不是同一形状。
- runtime `Frame` 新增显式 nullable OVD；`null` 表示未建模 authority，不冒充 empty Base。`LookupLive` 不接收 caller StateMap，区分 Found/Removed/AbsentAtBase，并验证 decisive binding、source-scope relative decode、same ObjectId 与 earlier address。
- canonical C full OVD 成为唯一 current authority：AA/BA Self、BB External(B)，所有 current heads 先经 C OVD lookup。24 个聚焦 cases 覆盖 OVD model/lookup、AA/BA/BB、Remove/Base absence、malformed locator、确定性与只读 inspection；完整 probe 194/194。
- Base Revision-locator discriminator 证明：relay + OVD Self 的 AA exact lineage 为 `C/Relay/A`、OVD read 为 `Relay`；relay-free 为 `C/A`、OVD read 为 `B/A`；relay + empty OVD 会跳过 helper。两者 current state、logical ordinal 和 root 相同，且 current reconstruction 只读 C。
- 当前没有产品律要求每次物理跨文件都暴露 no-op ObjectVersion hop，故 relay-free 成为领先方向；relay 只保留为可能降低 historical OVD read amplification 的候选 optimization。planner 仍依赖 caller StateMap，尚不能删除旧 relay path。
- 新增 DB-010：在 single-prior-snapshot Revision 法律下，Base per-record locator 还可能与 `Revision.OVD.ParentRevisionTicket` 合并；merge/import/rescue 与 DurableId reuse 是明确重访条件。

### 2026-08-29：历史 forwarding 模型：logical version 与 transparent maintenance lineage（已归档）

- 将 probe 的 `VersionOrdinal` 明确重命名为 `LogicalVersionOrdinal`；物理先后继续由 append address、direct parent 与 cycle gate 表达，不增加 physical ordinal、maintenance flag 或 runtime kind。
- executable semantics 选择 `same ordinal = maintenance`、`parent + 1 = domain change`：在 synthetic size-state 模型中，zero-payload Delta 可作 Relay，Base 可作 RelocatedBase；Base current reconstruction 停止，独立 lineage inspection 继续穿过 Base。
- 12 个聚焦 cases 覆盖 C-only reconstruction / C-B-A lineage、Relay head、后续 Delta/Base 只推进一次、payload/result/cumulative/growth corruption、skip/regression 与确定性；logical equality 只观测 `(BasePayloadBytes, LogicalVersionOrdinal)`，完整 probe 170/170。
- 独立化简审查提出 DB-009：Base lineage parent 或可改指 earlier Revision，再经其 OVD point lookup prior ObjectVersion，从而删除 relay。AA/BA/BB 未发现反例，但当前无 replayable OVD authority，caller StateMap 不能冒充历史 OVD；Remove 后同 DurableId 重新接入也尚待裁决。
- 下一 discriminator 优先建立单一 authority 的 OVD Base/Delta point lookup，再决定 materialize relay plan 还是删除 relay；不先扩展 multi-frame completion planner 或策略 heuristic。

### 2026-08-29：历史 direct-forwarding ImmediateRotationPlan（已归档）

- 将 `ProvisionalRevisionV0` 的唯一尺寸算法改接显式 grammar IR；普通 `Frame + SaveStep` 入口降为 adapter，旧 golden 不变。IR 现在能独立表达 domain Relay、带 parent 的 OVD Base/Delta，以及 Self/External/Remove。
- 新增 source-chain 只读 inspection 与 `FileScope.Relativize`；pure planner 从 caller 提供的 StateMap 推导 `EvacuationSet=Base@A`、`RelaySet=EvacuationSet∩Head@A`，不接收第二份集合 authority。
- dedicated B relay 使用 zero-synthetic-payload helpers + empty OVD Delta，C 为全部 evacuation objects 写 Base，并用 mixed Self/Previous full OVD 产生唯一 ProjectedStateMap。planner 不 CreateFile、不 Append、不 publish。
- AA/BA/BB fixture 得到 `Evacuation={AA,BA}`、`Relay={AA}`；provisional relay ticket 为 `32/40`，C ticket 为 `4/88`。hard-coded golden 分别冻结 relay `0+4+5+4` 与 C `35+8+12+6` 的 body/header/OVD/TailMeta 分量。
- tests 覆盖 no-relay、empty graph、canonical order、B relay TailMeta overflow、C combined capacity overflow、失败零 mutation 和同 store retry；Probe 158/158、root tests 147/147、solution build 0 warning/error，两路独立复核无 blocker/medium。
- 该结果只证明“至多一个 B relay Frame + 一个 C evacuation Frame”的 immediate constructive witness；失败不排除多个 relay Frames 或 published B maintenance，不能当作一般 `CanPrepareAndRotate == false`。planned maintenance records 仍不 materialize，因为当前 `VersionOrdinal` 尚未拆分领域版本与物理 lineage 次序。

### 2026-08-29：用 contextual self 建立 ProvisionalRevisionV0 尺寸基线

- 复核本地 Atelia RBF commit `fec021295828fcfe638434d69d04ff078c87c8ce`：现有 envelope 常量与边界正确；完整 L3 read 仍读取整个 Frame，TailMeta preview 只有 L2；append/read context 均提供 containing ticket。
- literal self-ticket 反例在 `start=4, non-self=98, count=1` 下同时得到 width/frame `2/124` 与 `3/128` 两个 fixed points，故“迭代至稳定”不能独自定义 canonical wire。采用 OVD 字段级 `BindSelf=1`，通用 relative grammar 不变。
- 新增 `ProvisionalRevisionV0` run scope：domain record、OVD record、TailMeta directory、address-token subset 与 RBF layout 分项计量；preflight 后 Append 必须返回完全相同 ticket/layout，reconstruction 按逐地址 provenance 统计 unique full frames。
- 默认和显式 `ObjectPayloadOnly` goldens 完全一致；当时仍含 Base `NoneToken` 的旧 V0 grammar 得到 hot/cold `1900/1148`、`1440/1408`、`1516/1148` 与 fixed mixed `536/184`、`464/448`、`468/300`；DB-010 后的当前数字见较新的船长日志。
- executable tests 覆盖 Base128/SizedPtr projection、same/previous、contextual Self/external alias 拒绝、component conservation、remove-only、canonical order、TailMeta 65535/65536、combined capacity、512 GiB start 和 policy matrix；真实 bytes writer/parser、mixed-binding full OVD、relay/two-file planner 与 publication 仍未实现。
- 验证：Probe 133/133、root tests 147/147、root solution build 0 warning/0 error、两套 format verify 与 diff check 通过；两路独立复核最终无 blocker/medium。

### 2026-08-29：加入 object-local payload read-amplification 基线

- 第三策略保留 StateJournal `ShouldRebase` 的 ratio=3/threshold 形状，但删除与多对象共享 Frame 冲突的 38-byte per-object overhead；名称明确为 `ObjectPayloadReadAmplification3`。
- ObjectVersion 记录 `ReconstructionObjectPayloadBytes`：Base 重置，Delta checked 累加；Builder 漏填 fail closed，materialization 从 Base 重算并拒绝 tamper。该字段仍排除在 ObjectPayloadOnly layout 外。
- 四场景 matrix 覆盖 threshold tie/reset、增长/缩小/等尺寸 direct Base、hot-one/cold-eight shared Frame 和 fixed-seed Field/List mixed；write totals 与 final read snapshot 分栏，不定义 TotalReadBytes。
- fixed mixed 中三策略 modeled file/final read 分别为 Base 436/148、Delta 364/348、local 372/232；hot/cold 中为 1700/1048、1268/1236、1340/1048。结果不是 winner，完整 codec/shared-frame/rotation-aware cost 仍待后续。
- 验证：Probe 100/100、root tests 147/147、root solution build 0 warning/0 error、两套 solution format check 与 diff check 通过；独立复核最终无 blocker/medium。

### 2026-08-28：建立 ObjectPayloadOnly RBF envelope 与 raw metrics

- `FrameTicket` 从 frame 序号改为 RBF byte-range `(OffsetBytes, LengthBytes)`；文件以 4-byte HeaderFence 起步，Append 精确计入 24-byte frame fixed overhead、padding 与独立 trailing Fence。
- 原生 RBF 约 1 TiB frame-start 与 DurableGraph 约 512 GiB relative-start gate 分层建模；最后合法 start 的 frame end 可以越界，下一次 Append 才失败。
- 每个 Save 记录 Base/Delta object payload write；每个 post-save state 记录 required versions、unique frames、required/in-frame/co-read payload 与 object-payload-only full-frame read bytes。读指标保持状态快照，不默认跨 Save 求和。
- 固定 generated mixed workload 首次让两基线显出可复现 tradeoff：AlwaysBase modeled file 436 bytes/final read 148 bytes，AlwaysDelta modeled file 364 bytes/final read 348 bytes；这些不是完整 wire bytes，也不构成 winner。
- OVD/index/header、TailMeta、relative VarUInt、self-ticket fixed point、two-file/relay/rotation 与 `CanPrepareAndRotate` 继续暂缓到对应具体 codec/planner 切片。
- 验证：Probe 90/90、root tests 147/147、root solution build 0 warning/0 error、两套 solution format check 与 diff check 通过；独立复核最终无 blocker/medium。

### 2026-08-28：跑通单文件 Base/Delta physical baseline

- 当时的单文件 baseline 将同一冻结 `WorkloadTrace` 分别编译为 fresh `AlwaysBase` 与 `AlwaysDeltaWhenLegal` runs；每个 Save 都产生一个 Frame，remove-only Save 产生空 Frame。当时 Base/Delta 都保存 object parent；DB-010 后 Base direct parent 已删除。
- 当时 live StateMap 使用 `AbsoluteFrameAddress`，ObjectVersion parent 使用 `RelativeFrameTicket`；当前只保留 Delta exact parent，Base lineage 已改由 Revision shared anchor 承担。
- Delta payload size 只作写成本；symbolic apply 先重建并校验 expected parent size、ordinal 与无压缩增长下界，再产生 result size。每个 Save prefix exact-match logical cursor 后才推进 private run。
- 本切片只是 S1 前置基线；frame bytes/layout、capacity/representability、two-file closure、relay、rotation 与自适应策略仍未实现。
- 验证：Probe 74/74、root tests 147/147、root solution build 0 warning/0 error、两套 solution format check 与 diff check 通过；独立复核最终无 blocker/medium。

### 2026-08-28：建立 deterministic workload generation/replay substrate

- 新增 immutable `WorkloadTrace`：以 canonical SaveStep 承载 Create/Update/Remove；logical replay 维护 live set、不可复用 ObjectId、Base size 与自动 VersionOrdinal。
- Update 的 previous size 只来自 replay state；在无压缩 literal-image 假设下拒绝 `DeltaPayloadBytes < max(0, currentBase - previousBase)`，同尺寸更新仍推进版本。
- 固定 SplitMix64 root 按 lifecycle/create/update 与 step/object/lane 非消费式分流；完整 trace 在策略运行前只生成一次，所有候选策略未来共享同一个内存实例。
- `GeneratedScenario` envelope 同时冻结结构相等的 ScenarioDefinition 与策略输入 WorkloadTrace；无需 trace 文件即可解释同名同 seed 下的参数差异，mixed-scenario golden 则约束 GeneratorVersion。
- 薄泛型 ObjectBehavior 以 candidate-first/measure/freeze/commit 保证失败不半提交；Field 维护固定 component sizes，List 维护 item sizes 并只生成合法 Insert/Remove/Replace。
- ScenarioGenerator 当前采用固定计数 lifecycle、Field/List 权重和显式 synthetic size accounting；生成后强制经过 independent logical replay 并核对 final live Base sizes。
- 当前未实现 trace 文件格式、真实 codec、Frame layout、BaseOrDeltify、rotation policy 或综合评分；随机生成补充手写边界 trace，不替代 exact-fit/relay/evacuation 反例。

### 2026-08-28：启动 Two-leg rotation S1 probe

- 建立隔离的 `experiments/TwoLegRotationProbe` .NET 10/xUnit 项目，不改产品 runtime 或根 solution 项目集合。
- 首轮只固定 one-based 单调 FileNumber、RbfFile append-only/random-read 容器和 `PreviousFileNumber = CurrentFileNumber - 1` 的相邻语义。
- `FrameBuilder/ObjectVersionBuilder` 是落盘前可变态；`Build()` 防御性复制为只读 ObjectId→ObjectVersion map，并冻结 Delta-only `DeltaParentFrameTicket`。
- `RelativeFrameTicket` 已建模为 `(IsPreviousFile, FrameTicket)`；`FileScope.ReadFrame` 以承载 ParentId 的 origin file 选择 Current/Previous。FileScope 固定 origin，迈腿时创建新文件与新 scope，旧 frame 仍使用旧 scope 解读。
- `FrameTicket` 暂为文件内零基 List key；ParentId/FileScope pair 不冒充 `SizedPtr` 或已经冻结的 durable ticket encoding。
- 下一步可从该骨架逐层加入 Base/Delta 内容、跨文件地址、Revision layout estimator 和可替换策略，不提前把候选 heuristic 写入容器层。

### 2026-08-28：将研究优先级切换到 StateStore 双腿轮转

- 根据 StateJournal/RBF 本地实现复核，选择 A=OldPrevious、B=Current、C=Next 的 current-head two-file reconstruction 模型；R4 logical StateMap/apply 保留但暂缓。
- 地址层选择独立 `RelativeFrameTicket` / `AbsoluteFrameAddress`，以 LSB selector + 左移后的 `SizedPtr.Serialize()` 保留 VarUInt 紧凑性，并接受约 512 GiB frame-start 上限。
- 当时的候选轮转模型把 terminating Base 位于 A 的 live objects 以 Base 写 C，并设想用 B 中 lightweight per-ObjectId RelayRevision 保留 direct lineage；该方案后来由 DB-009 拒绝并归档于 tag `research/relay-vs-relay-free-20260829`。
- one Revision/one RBF Frame 保持为有界首版，Extent、固定性能阈值与持久 `TotalPersistBytes` 均等待模拟或容量证据。
- 建立 `state-store-base-design.md`、`state-store-base-derived.md`、`state-store-addressing-design.md` 与 DB-007；下一步先商定纯内存策略模拟模型，不宣称持久 Store 已实现。

### 2026-08-28：完成 Two-pass CLR graph materialization R3b probe

- 只 materialize normalized current root closure，allocate-all 后 hydrate-all，恢复 sharing/self-cycle/two-node cycle；disconnected source rows 留给 Save。
- constructor-bypassed placeholder 只允许一次 identity/hydration bind；materialized root 不成为 StateMap/baseline authority或 cache。
- allocator/hydration late failure 均 no-root 且可重试；复审促成真实 allocator fault injection 并删除非必要 ID sorting。
- 聚焦 7/7、完整 147/147、solution 0 warning / 0 error、PackageConsumerProbe passed；最终无 blocker/medium。下一主线仍为 R4，并增强 logical apply → normalized load → CLR materialize 组合闸门。

### 2026-08-27：完成 Stored Graph normalization R3a probe

- 用 immutable record table + full-table exact preflight + typed V1/V2 decode/upgrade 产出 current-Snapshot baseline，未引入产品 Reference TypeTag 或 wire format。
- 保留完整 SourceRecordIds；upgrade 删除 edge 后由下一次 R1 Save 将 target 分类为 Unreachable，而不是 normalization 提前裁掉。
- decode/upgrade late failure 均无 partial result，并能在同一 image 上修复重试；handler 自身外部副作用仍明确不回滚。
- 聚焦 9/9、完整 140/140、solution 0 warning / 0 error、PackageConsumerProbe passed；两路独立复审最终无 blocker/medium。下一主线为 R3b two-pass hydrate。

### 2026-08-27：完成 Generated Graph Operations R2 probe

- 选择 internal、无 `[Generator]` 的隔离 probe，而不是现在冻结 Reference TypeTag 或让产品 Generator 产生无法 hydrate 的半成品 boxed Serializer。
- 以 provisional `TIdentity : struct` resolver 生成 typed Snapshot/CapturedReferences/Capture/equality/visitor，并用 literal expectations + R1 oracle 双重验证 sharing、cycle、locality 与 replacement。
- 聚焦 31/31、完整 131/131、solution 0 warning / 0 error、PackageConsumerProbe passed；独立复审促成 semantic no-`object` gate 和 probe-only DG0018 元数据收口，最终无 blocker/medium。
- 默认产品 Generator 仍 DG0007 拒绝 self-reference；下一主线为 R3a logical StoredGraphImage normalization。

### 2026-08-27：完成 Flat Graph Delta R1 semantic probe

- 以 internal single-type fixture 跑通 latest Snapshot baseline、`RequiresRewrite`、identity-aware stack traversal、whole-object Upsert、Unreachable 与 root transition。
- 独立复审找到 accepted baseline 可残留 disconnected clean entry 的漏洞；新增 exact result-root closure gate 与 malformed candidate tests 后复核通过。
- 合并 visited/reachability 状态为单一 ID→instance table，并移除 core sorted containers；确定性只在展示边界显式排序。
- 聚焦 21/21、完整 121/121、solution 0 warning / 0 error；未修改 runtime 或 Generator public/product behavior。

### 2026-08-27：选择 latest-Snapshot flat baseline 作为 Graph Delta 下一探针

- 后续主线转向先验证 identity、reachability、sharing/cycle 与 whole-object delta，再为已证明的逻辑 IR 增加 binary codec。
- baseline 保存来源 StateMap 的 flat live ID set；每项 historical payload 在 Load 边界升级成 current typed Snapshot，并以 `RequiresRewrite` 记录下一次显式 Save 的推进义务。
- `RequiresRewrite` 属于 baseline entry envelope；失败时不清除，只有成功 publication 后通过安装新的 clean baseline 一次性消解。
- normalized projection 不成为第二 authority；未来 old record reuse 和 stale-baseline gate 由 exact head + authoritative StateMap 在整图层绑定。
- 将路线与实验 gate 写入 `DurableGraph-research-roadmap.md`，将最小算法、不变量和反例写入 DB-006。

### 2026-08-27：用 goto labels 线性化 generated upgrade chain

- 技术 probe 证明 C# definite-assignment 支持 switch case decode 后 goto typed Snapshot-ready labels，并能顺序穿透后续相邻边。
- 删除每版本复制升级后缀的 `DeserializeVn` methods；现在每个版本 decode、每条 edge 和 materialize 都只生成一次，代码规模从 O(N²) 降为 O(N)。
- 保留单方法帧、唯一动态 switch、required partial handlers、typed failures 和 Load 无写回语义。
- 将所有 Snapshot locals 共处单方法造成的 JIT/stack tradeoff 保留为未来实测问题，而不是宣称已优化运行时性能。

### 2026-08-27：跑通 generated static read-time upgrade

- 初始方案采用每个 stored version 一个单帧直达-current协调器；该代码形状随后由 EXP-010 的 goto-label 共享后缀线性化。
- Generator 现在产生一次 version switch、exact historical Schema、required adjacent partial handlers 和强类型 Snapshot chain；DG0017 阻止 SchemaId 映射到多个 current CLR types。
- V1 Save→V2 Load、重复无写回、显式 Save 后推进、V1→V2→V3 顺序以及关键失败路径均由 executable tests 覆盖。
- 下一步尚未自动确定；wire format、领域 invariant/rebuild、malformed payload error、一般图与统一 commit 仍保持分离。

### 2026-08-27：将 Snapshot History feedback 固化为单一 NuGet 包

- `Atelia.DurableGraph` 包现在同时交付 runtime、Generator、自动导入的 build assets 和私有 publish/verify tool；下游无需复制脚本或手写 hook。
- production Generator 能从 checked-in `.dgsnapshot` 重建历史 private structs，并对连续版本、冲突和 current shape fail closed。
- package-only consumer probe 跑通 V2 负例、V1/V2 累积、强类型 Snapshot 使用、CI read-only failure 与重复发布幂等。
- 下一 vertical slice 回到 read-time upgrade：生成 historical deserialize/version switch、required adjacent handler，再让 Load 只在内存中升级到 current；本轮没有提前实现它。

### 2026-08-27：固定 Snapshot struct/in-out 原型方向

- 通过正反编译 probe 保留 ordinary struct 与 `in/out`，并删除当前无消费者的 bool failure channel。
- 快速原型选择成功 local build 自动发布 history；显式 Accept/CLI 与普通 build purity 保留为 DB-004 分叉。
- 将 Snapshot representation/signature、history authoring/publisher 和 durable inheritance flattening 分别记录为 DB-003/004/005。
- 下一 vertical slice 聚焦 generated version-aware serializer、required adjacent handler 与 read-time upgrade；不开放继承或跳版。

### 2026-08-27：完成 Source Generator history feedback probe

- 用同一 consumer project 的 V1/V2 负对照证明 `AddSource` 物理输出不会自动进入下一轮 `AdditionalFiles`。
- 用 opt-in post-compile publisher 跑通 V1 history 发布、clean V2 强类型 Snapshot 恢复与幂等重复发布。
- 将 runtime registry 暂缓，下一步先围绕闭世界 generated version switch/typed adjacent calls 继续实验。
- 保留 history 格式、publisher owner 和普通 build 是否允许修改工作树为开放设计问题。

### 2026-08-27：Compaction 前冻结升级阶段入口

- 收窄 EXP-005 的保证：当前仅有 State → exact Schema 的结构闭合与 schema-gated load，不保证对象语义 invariant 或两个 Store 的原子合集。
- 下一阶段聚焦 read-time version upgrade；加载只产生当前版本内存对象，不隐式回写，后续显式 Save 才写入升级版本。
- 在 `DB-002` 记录 historical serializer binding、upgrade path 与 failure gates 的候选设计。

### 2026-08-27：完成 EXP-005 boxed State round-trip

- 新增 `InMemoryStateStore` 与 boxed serializer runtime seam，固定 Schema-first Save 和 validate-before-Deserialize。
- Generator 产生 `Serializer`，通过 `RuntimeHelpers.GetUninitializedObject` 绕过构造器并恢复四种 scalar field。
- 用 sealed-type、readonly field 和生成成员冲突 diagnostics 保持当前不支持范围 fail closed。
- 明确 boxed dictionary、零值 transient 和 allocation-before-validation 均是原型边界，不是持久化承诺。

### 2026-08-27：建立候选设计分叉库

- 建立 `docs/design-branches/`，把尚未获得足够证据的竞争方案与实验结论、正式决定分开。
- `DB-001` 记录 typed `DurableSchema` 与 canonical schema blob 的 authority/runtime representation 分叉。

### 2026-08-27：完成 EXP-004 Schema Generator walking skeleton

- 生成每个合法 durable type 的静态 `Schema`，并固定首批四种 scalar TypeTag 映射。
- 落地 DG0001-DG0009 fail-closed diagnostics 和 Roslyn 动态编译/emit 测试。
- 经独立审查修复嵌套 lookalike metadata 误认，补齐类型/字段顺序确定性证据。
- 保持 properties、继承、Store registration、SchemaHash 和 serialization 在本轮范围之外。

### 2026-08-27：完成 EXP-003 内存 SchemaStore

- 固定首次注册、等价幂等、同键异形冲突和 exact lookup 语义。
- 使用 typed exception 保留冲突双方或缺失 key，且不提供 latest fallback。
- 暂缓 Store interface、线程安全与持久化实现，等待后续真实消费者塑形。

### 2026-08-27：完成 EXP-002 Schema 值模型

- 定义首批稳定 TypeTag、字段描述和不可变 Schema 描述。
- 固定 FieldId 排序、重复字段拒绝和结构相等语义。
- 隔离 wire format，并明确普通 `GetHashCode()` 不是 durable SchemaHash。

### 2026-08-27：完成 EXP-001 元数据契约

- 落地 `DurableBase`、Type/Field 两类持久化 Attribute 和显式 `TransientAttribute`。
- 因 BCL 已占用 `Attribute.TypeId`，将 Attribute 的 CLR 属性名定为 `SchemaId`。
- 保留“所有实例字段必须显式分类”为下一阶段 Generator 诊断，而非声称当前已强制。

### 2026-08-27：建立协作与记录基线

- 完成 .NET 10、xUnit、Source Generator、CLI 和 `.slnx` 空骨架。
- 建立根目录 `AGENTS.md`，将稳定的协作方式与易变的实验认识分开。
- 决定先维护单一实验簿；只有实际内容增长到难以导航时才拆分目录或 ADR。
