# DB-045 持久表示 ID：最小贯通分片

> 文档整理：2026-10-14；来源为 Git `8ef9fca` 中的 `docs/design-branches/0045-persisted-representation-id-slice.md`，正文保留当片合同与历史验收含义。
> 阅读范围：初次证据见 §6.2，旧 Base 读取兼容移除见 §7，双登记由 DB-046 替换；RepresentationId/旧 Base header 又由 DB-087 退出，本文保有限历史资格。
> 当前能力与施工焦点见 [PROJECT-STATE](../../../src/PROJECT-STATE.md)；旧稿中的“下一步”不自动成为当前工作单。

状态：**Implemented / G0–G3 已通过验收**。2026-09-09。
完整表示登记、新 Base ID 头与历史读取已贯通；根构建、完整测试及真实包回归通过，证据见 §6.2。
前序比较：[DB-044](../0044-type-header-blind-review/README.md)；当前实现：[PROJECT-STATE](../../../src/PROJECT-STATE.md)。

后续修订：初次验收时保留的 Base v1–v3 兼容已决定移除，当前合同见 [§7](#7-后续清理移除旧-base-读取兼容)；下文初次施工与验收记录保留历史含义。
目录内部的双批次和 SchemaKey 转接其后由 [DB-046](../0046-unified-schema-catalog-slice/README.md) 替换；下面相关链接转向后继记录，初次格式与故障窗口仍是历史证据。

## 1. 本片只回答什么

问题：能否复用完整的现有 ObjectLayout，把新写 Base 的类型信息缩成一个持久 ID，
使对象内容层通过登记/解析入口取得布局和历史 reader，而不再编码或解释 SchemaKey、TypeExpr 和 ArrayLayout 的组成？

最小成功见证：string、泛型 class 与历史 struct 元素数组保存后完全关闭；重开只凭 Base 的 ID、SchemaStore 与保留的代码目录，
读回 exact DTO；引用目标独立升级不改变 owner 表示 ID，owner 真正改变布局后获得新 ID 并写 Base。

这是类型头/表示目录边界的解耦。typed 图验证、Upgrade 与恢复仍需完整布局；不宣称 StateStore 全部逻辑都无需类型知识。
不拆程序集，不改 SG/DTO/body、开放模板闭合、Upgrade 路由、定义版本传播、StateRevision/Delta/prior 格式或发布协议。

## 2. 复用现有描述，不先设计替代类型体系

[ObjectLayout](../../../src/DurableGraph/Schema/ObjectLayout.cs) 已提供所需载体：String、完整 DurableSchema、ArrayLayout。
class 的 exact base/inline 依赖、数组 exact 元素槽及 nominal 类型身份全部复用。
引用槽保留声明约束，目标对象的版本继续由目标自己的 Base 负责。

相等性使用完整 ObjectLayout.Equals，不使用 DTO CLR Type、实例引用或“编码长度一样”。
不同 nominal 类型即使共用同一 DTO/reader，也不能合并；数组长度/shape 不属于表示身份。
同逻辑 Schema key 异形仍须拒绝，不能通过新分配 ID 绕过既有一致性检查。

本片 ID 覆盖作为独立对象保存的 string/class/array 完整表示；inline struct 依赖继续由 SchemaStore 保存 exact Schema，
不为了没有独立 Base 的值另建一套 ID 消费 API。它们的完整解释仍可从 owner 的 ID 经 SchemaStore 得到。

## 3. 窄接口与所有权

已落盘的入口位于现有 StateStore 程序集；不进入 Capture DTO 或 SG 参数：

```csharp
// 位于现有 StateStore 程序集，不进入 Capture DTO 或 SG 参数。
public readonly record struct RepresentationId(uint Value);

// SchemaStore 的持久目录能力。
RepresentationId[] RegisterRepresentations(IReadOnlyList<ObjectLayout> layouts);
ObjectLayout GetRepresentation(RepresentationId id);

// SchemaStore 使用本次操作已经冻结的代码目录。
ObjectReaderBinding ResolveReader(RepresentationId id, StateBindingContext bindings);
```

Register 返回与输入对应的 ID 数组，内部去重；取得已有 ID 也不能跳过完整 Schema 冲突预检。
ResolveReader 复用 `bindings.ResolveObjectReader(layout)` 并核对完整布局，不扫描程序集，不写 SchemaStore。
历史描述持久，CLR Type/reader 根据本次代码目录绑定；不得在 SchemaStore 全局按 ID 缓存另一个 snapshot 的 reader。
原先接受 expected Schema/static body 的显式读取入口可以继续保留，其匹配事实统一来自解析后的完整布局。

表示目录由 SchemaStore 拥有，继续使用同一 `schemas.rbf`，不增加 Repository 文件、服务层或注册表接口家族。
一个 ID→layout 索引与一个 layout→ID 索引描述同一份持久事实，进程缓存不能重新分配既有 ID。
实际代码：[RepresentationId](../../../src/DurableGraph.Persistence/RepresentationId.cs)、
[SchemaStore](../../../src/DurableGraph.Persistence/SchemaStore.cs)。`SchemaStore.Count` 仍只统计用户 Schema 定义，
不把内建 string 或表示登记数量混入原计数。

## 4. ID 与登记日志

- `0` 无效；不是 ObjectId 的 null，也不用于缺失 Schema 的占位。
- `1` 为 string 内建表示。它是格式规定的恒定条目，不需写登记记录；string/class/array 均经同一 ID 入口解析。
- 其他完整对象表示从 `2` 单调分配 UInt32；不回收，不承诺跨仓库同号同义。耗尽只拒绝新表示，已有表示仍可取得。
- 相同完整表示重复登记无追加；已分配 ID 重开保持。输入顺序变化不重编号；未分配项的先后不构成跨仓库规范身份。

新增 RepresentationBatch RBF tag `0x31425052`（小端 ASCII `RPB1`），批次版本为 `1`；
和现有 SchemaBatch `SGB1` 共用 `schemas.rbf`，保留 SchemaBatch v4 本体及已有旧版读取。
表示批次的逻辑内容为 `formatVersion + count + (explicit ID + descriptor)...`，按新分配 ID 升序。
descriptor 复用今天的 class SchemaKey / 数组 codec、构造码和元素槽描述，但编码由 SchemaStore 一侧拥有；
不调用“写空 Base 再拆字节”复用，不将 Base envelope 格式版本绑定到表示目录版本。

不在这片为表达式换 prefix/postfix、压缩描述内部逻辑名称，或为所有依赖再编号。
独立 golden 固定新 tag/目录版本；`.dgschema` history 格式不变。

登记顺序：

1. 冻结全部输入，预检完整 Schema 闭包、布局、ID 容量和两种待写 payload 的限额；复用/提取既有 SchemaStore 预检，避免第二份校验权威。
2. 如有缺失 Schema，沿用现有 SchemaBatch 追加并 flush。
3. 如有新表示，追加一批 ID→descriptor 并 flush；完成后才能向调用方交付新 ID。
4. 两步之间的失败允许留下已持久 Schema。表示追加/flush 不确定时沿用 SchemaStore faulted/重开规则，不能继续发布使用未确认 ID 的 State。

这比改造成“Schema 与表示同一新格式帧”多一次可能的 flush，但复用既有合同，且只在新增元数据时发生。
统一注册帧/flush 合并以后有测量再做，不为本片引入联合事务。
恢复按日志顺序重建；表示引用的 exact Schema 必须此前已登记，整个表示批次校验通过才安装其索引。
可写非空重开仍需确认持久屏障，不能只以用户 Schema 数量判断非空：纯基元数组也会产生表示登记帧。
非法 ID、跳号、重复/重绑定 ID、同表示重复绑定不同 ID、缺依赖、未知格式、坏尾和溢长均拒绝；不按字典枚举顺序恢复编号。
定义 kind/arity 的声明一致性覆盖 Schema 和数组表示登记，恢复时亦统一检查。
参考 [SchemaStore](../../../src/DurableGraph.Persistence/SchemaStore.cs)；原 SchemaBatchWireCodec、RepresentationBatchWireCodec、
RepresentationDescriptorCodec 的后继为 [DB-046 统一目录](../0046-unified-schema-catalog-slice/README.md)。

## 5. Base 与读写贯通

新写 Base 类型头 v4：`version=4 + canonical VarUInt32(RepresentationId) + raw body`。
不再含独立 string/class/array tag、SchemaKey 或数组元素描述；kind 从解析出的布局取得。
Delta 仍只含变化 body，按终止 Base 继承表示；ObjectVersion/StateRevision 外层格式不变。

复用已有 v1–v3 Base 读取作为有限的只读适配，不追加迁移框架或回写旧帧。
旧头的描述解释集中到同一表示解析边界；旧记录没有持久表示 ID，不能在只读时虚构/登记一个。
读路径可统一交付“完整布局 + raw body”，v4 reader 解析必须从持久 ID 出发；新写一律 v4。
string 的内建固定 ID 可保留 `ReadString(chain)` 无 SchemaStore 的便捷读取；用户/数组读取仍需所属目录。
实际 `DecodedBaseObjectBody` 交付 `Layout`、owned raw body 和可空 `RepresentationId`；
旧 v1–v3 的 ID 为 null，不在读取时补登记。旧描述与表示批次的描述语法都由 SchemaStore 一侧的
`RepresentationDescriptorCodec` 拥有，原 `ObjectPersistence` 类型分派 helper 已删除。

写入两条 planner 在既有 Parent/来源预检之后批量 RegisterRepresentations，再用相应 ID 包装 PreparedBaseBody。
原 Base/Delta 策略继续接收实际完整 payload 尺寸；B 含新的 ID 字节宽度，D/prior 上界与已有链 H 的计量规则不变。
共享 Schema/表示登记帧仍不摊入单对象 B/D/H。
因此同一 DTO 在缩短 Base 头后，既有策略可能合法地从 Delta 改选 Base：当实际 B 已不大于 D 时，
原先依赖较长类型头的微型对象未必还有 Delta 收益。本片保留该真实尺寸结果，不为维持旧测试选型而改变策略。

读入统一解析入口，覆盖 RevisionDecoder、TypedObjectVersionReader、两个 planner 的来源校验和 GraphRepository 的严格重开验证。
world-kind/string-no-delta、完整 reader 匹配、空数组声明校验及 Upgrade requirement set 继续执行，不能因数字相等就跳过。
这片的解耦完成条件是这些路径不自行解释新 Base 中的逻辑 Schema/数组表达式，统一经 SchemaStore 解析；不是只增加一个无人消费的 ID 字典。

## 6. 施工顺序与验收

| 步骤 | 改动入口 | 最小验收 |
|---|---|---|
| G0 表示目录 | SchemaStore、ID/描述与批次 codec；复用现有 ObjectLayout | 幂等、完整相等性、重开稳定、先 Schema 后表示、失败/faulted；同 key 异形不分配新号逃逸；only-array/string 目录 |
| G1 新 Base 边界 | BaseObjectBodyCodec、RepresentationDescriptorCodec、TypedObjectVersionReader | v4 独立 golden；0/未知/溢长 ID 拒绝且业务 reader 不调用；已有旧头仍只读；整数宽度边界 127/128、16383/16384 |
| G2 全路径替换 | CapturedRevisionPlanner、LoadedRevisionPlanner、RevisionDecoder、GraphRepository | string/class/array 仅写 ID；旧来源/NoChange/升级强制 Base 正确；Base/Delta/H 尺寸无旧头常数残留 |
| G3 整体验证 | 既有 StateStore/生成器集成与真实包消费 | 完全冷重开恢复历史 DTO、同实例 Commit、泛型+数组+struct 升级、引用身份与 malformed/fault 回归 |

G0–G1 需要测试以下容易被 DTO 类型相等掩盖的案例：不同 nominal 但同空 DTO、不同 inline 版本、相同数组布局不同 shape。
引用目标升版而 owner ObjectId 槽不变时，owner 的表示 ID 复用；嵌套数组外层也不锁定内层元素版本。
保留旧 ID 后重新按另一顺序请求登记，旧历史读取仍解析同一完整布局；先登记后放弃 State 不破坏后续分配。
恢复及绑定没有旧领域 CLR 声明时也可用 retained reader；只有元数据而缺执行能力时明确失败。

施工可按 G0 的目录/格式、G1–G2 的集成、G3 的独立审查分派文件所有权；新 API 先对齐，dotnet build/test/package 串行运行。
最终根 solution build、完整 tests，真实 ArrayConsumer + 既有 Generic/ValueUpgrade package 的相关历史路径；
成功后把验证证据留在本片，更新 PROJECT 的当前能力，不能提前把计划写成已实现。

### 6.1 代码与验收映射

| 闸门 | 代码与回归入口 | 状态 |
|---|---|---|
| G0 表示目录 | [SchemaStore](../../../src/DurableGraph.Persistence/SchemaStore.cs)、[目录测试](../../../tests/DurableGraph.Persistence.Tests/RepresentationStoreTests.cs)、[批次格式测试（DB-046 后继）](../../../tests/DurableGraph.Persistence.Tests/SchemaCatalogReplayTests.cs) | 通过 |
| G1 Base 格式 | [BaseObjectBodyCodec](../../../src/DurableGraph.Persistence/BaseObjectBodyCodec.cs)、[DecodedBaseObjectBody](../../../src/DurableGraph.Persistence/DecodedBaseObjectBody.cs)、[表示头测试](../../../tests/DurableGraph.Persistence.Tests/RepresentationHeaderTests.cs)、[旧头兼容测试](../../../tests/DurableGraph.Persistence.Tests/BaseObjectBodyCodecTests.cs) | 通过 |
| G2 保存与读取 | [CapturedRevisionPlanner](../../../src/DurableGraph.Persistence/CapturedRevisionPlanner.cs)、[LoadedRevisionPlanner](../../../src/DurableGraph.Persistence/LoadedRevisionPlanner.cs)、[RevisionDecoder](../../../src/DurableGraph.Persistence/RevisionDecoder.cs)、[TypedObjectVersionReader](../../../src/DurableGraph.Persistence/TypedObjectVersionReader.cs)、[GraphRepository](../../../src/DurableGraph.StateStore/GraphRepository.cs)、[集成测试](../../../tests/DurableGraph.Persistence.Tests/RepresentationIntegrationTests.cs) | 通过 |
| G3 实际包与历史 | [ArrayConsumer](../../../experiments/PackageConsumerProbe/ArrayConsumer/README.md)、[GenericConsumer](../../../experiments/PackageConsumerProbe/GenericConsumer/README.md)、[ValueUpgradeConsumer](../../../experiments/PackageConsumerProbe/ValueUpgradeConsumer/README.md) | 通过 |

### 6.2 验证证据

2026-09-09，主线程集中执行并确认：

- 根 solution 构建：0 warnings/errors；日志 `experiments/PackageConsumerProbe/obj/db045-build.log`。
- 定向回归：196 passed、0 failed、0 skipped；日志 `experiments/PackageConsumerProbe/obj/db045-focused-tests.log`。
- 完整 solution tests：1352 passed（Runtime 636、StateStore 458、Serialization 103、Storage 155），0 failed、0 skipped；
  日志 `experiments/PackageConsumerProbe/obj/db045-full-tests.log`。
- 真实 Array package 通过全部新增表示 ID marker、历史冷读、数组 Upgrade 与同实例续写；
  日志 `experiments/PackageConsumerProbe/obj/db045-array-package.log`，产物目录 `obj/array-20260908180819-32052-35900c81`。
- 真实 Generic package 与 ValueUpgrade package 各通过全部 4 阶段，保留旧 history 数量/hash 闸门；
  日志分别为 `experiments/PackageConsumerProbe/obj/db045-generic-package.log`、`experiments/PackageConsumerProbe/obj/db045-value-package.log`，
  产物目录分别为 `obj/generic-20260908182150-25752-4daf133e`、`obj/value-upgrade-20260908182355-34420-0ccb3bbf`。
  两者复用 Array runner 打出的隔离 feed，版本 `0.0.0-array-e2e.20260908180819.32052`，无手动 analyzer/ProjectReference 替代。
- 补充真实 StateStore package 全部通过：持久 Schema、旧版 World 升级、readonly 恢复、正常 Delta、
  GraphSession 连续提交与共享/循环图；日志 `experiments/PackageConsumerProbe/obj/db045-statestore-package.log`，
  产物目录 `obj/state-store-run-20260908182622-34708-b47ebba1`。
- 独立审查及对最终 fixture/package 样本的复核均无阻塞问题；两项非阻塞建议已处理。
- 文档检查：10 份修改文档、377 条本地链接/锚点通过，`git diff --check` 通过。

实际观察：首轮完整 tests 有 9 项旧尺寸/选型预期失败，Generic 包亦遇到相同的微型 DTO 缩头效应。
新 B≤D 时原策略合法改选 Base；一个 Base payload golden 从旧 99 bytes 变为 72 bytes。
专测 Delta 的场景增加真实稳定字段并在历史 Upgrade 中透传，非 Delta 职责场景接受合法 Base。
上述最终回归均基于修订后的样本；产品策略未修改。

## 7. 后续清理：移除旧 Base 读取兼容

2026-09-09 用户确认项目尚未投入使用、没有旧数据兼容需求，选择移除初次实现中的 Base v1–v3 只读路径。
这减少旧描述解析、可空表示 ID 和 reader fallback 三处维护面，不影响当前格式中历史 Schema 的解码与 Upgrade。

范围：BaseObjectBodyCodec 仅接受 v4；DecodedBaseObjectBody 的 RepresentationId 必填；
RepresentationDescriptorCodec 只服务当前目录描述，删除旧 Base 专用参数；RevisionDecoder 主读取路径统一经 ID 解析 reader。
当前 v4 字节不变，旧版本立即 InvalidDataException，不提供迁移器。
SchemaBatch、`.dgschema` history 的格式兼容不是本次范围，保留其现有行为。

验收：旧 Base v1/v2/v3 拒绝；当前 v4 的冷重开、历史 DTO 与 Delta 链仍可读；根构建与完整测试通过。
验证结果：根 solution build 为 0 warnings / 0 errors；四个测试项目共 1350 项通过
（Runtime 636、StateStore 456、Serialization 103、Storage 155），无跳过。
首次完整测试发现新截断测试漏计泛型 arity 的最少剩余字节检查；仅修正测试的精确异常预期后，
重新构建并重跑全部 StateStore tests 通过，其余三个项目已在完整测试中通过。
独立审查无阻断问题；4 份修改文档的 296 个本地文件链接和新增锚点检查通过，git diff --check 通过。
本次未更改公开 API、当前编码字节或包交付方式，未重复真实包回归；初次 DB-045 包证据仍见 §6.2。

## 8. 后继才处理什么

SchemaKey、TypeExpr 目前确实是 public 类型，并用于 SG/runtime/history/binding；本片不删除或改变其公开含义。
ID 令新的 State 类型头不暴露它们的组合细节，并不证明“名义约束”和“exact 版本”两类信息可以丢掉。
后续可合并其内部描述载体/寻址方式，但须保留 phantom nominal、固定/参数 inline 依赖与引用边界。

单 ID 能找回本对象所有必要历史解释，**不锁住各引用目标的版本**；各目标仍通过其 ObjectId 在所选 Revision 中找到自己的 Base。
开放模板登记、模板版本与闭合版本的政策、字段物理分组、通用 TypeCodec、ID 回收、BCL 与新集合、跨仓库身份、
SchemaStore 自举/联合 Store 视图及新的旧数据迁移均不属于本片。
