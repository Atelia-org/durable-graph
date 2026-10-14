# DB-035：公开合同与持久格式术语迁移

> 文档整理：2026-10-14；来源为 Git `8ef9fca` 中的 `docs/design-branches/0035-public-contract-terminology-migration.md`，正文保留当片合同与历史验收含义。
> 阅读范围：当片 legacy 删除、.dgschema 与术语迁移证据见 §4；State wire v3 不变仅描述当片，后续存储格式由 DB-086/087 替换。
> 当前能力与施工焦点见 [PROJECT-STATE](../../../src/PROJECT-STATE.md)；旧稿中的“下一步”不自动成为当前工作单。

状态：**Chosen / Implemented**；2026-09-07。
起始基线 `3f83dab`。当前事实以源码、测试与工具输出为准；本文只记录本批实施合同。

## 1. 问题与完成判据

项目尚未投入使用，无需保存 accidental prototype API、生成 ABI 或 Schema-history 文本兼容性。
本批要删除 legacy 状态表示，使公开名称直接表达 Schema、状态视图、对象 body、ObjectHeadMap、
Revision 地址与 B/D/H 计量边界，同时保持 StateStore Storage wire v3 完全不变。

最小可观察完成判据：裸 `[DurableType(schemaId, version)]` 是唯一生成模式；新 `.dgschema`
历史可 Publish/Verify 且旧 `.dgsnapshot` 不再参与构建；生成/runtime/Storage 消费者只使用新合同；
现有 v3 literal golden bytes 不改一字节仍全部通过。

## 2. 发布边界与冻结接缝

### 2.1 唯一 State model

- 删除 `SchemaOnly`、`GenerateBinaryBody`、legacy `__DurableSnapshotVn` / `__DurableSerializer`、
  `IDurableSerializer<T>`、`InMemoryStateStore` 及只服务该路径的代码和见证。
- `[DurableType]` 默认生成 exact Schema/history、各版 readonly state DTO、body 读写、Capture、
  reader/model 登记、Normalize、Allocate 与 Hydrate。不支持的类型形状直接诊断，不保留 metadata-only 半模式。

### 2.2 Schema history

- 构建属性、target、tool、manifest、内部模型和消息统一使用 `SchemaHistory`；默认目录
  `DurableGraphSchemaHistory`，文件扩展名 `.dgschema`。
- 文本 header 与 record marker 原子迁移到 schema-history/schema 用语。只接受新格式；
  不双读、不生成兼容副本。保持 canonical UTF-8、排序、hash、Publish/Verify 与 closure 语义。

### 2.3 生成 ABI 与对象 body

- SG 容器统一为 `__DurableState`；body 方法显式区分 Base/Delta body。
- `ObjectStateRecord` / `ObjectStateKind` 是 candidate、stored、current 共用的单行 carrier；
  外层容器继续表达视图来源，不复制三套行类型。
- Serialization 公开 raw `PreparedBaseBody` / `PreparedDeltaBody`。StateStore 内部以
  `EncodedBaseObjectBody` 品牌表示 `[type header | raw Base body]`；typed planner 只接收该品牌。
  Base header codec 与 decoded envelope 不再是公开 API。Storage 仍只处理 opaque body bytes。

### 2.4 Storage 术语

- `StateRevision.CreateObjectHeadMapBase/Delta` 显式命名目录表示轴；对象内容仍使用
  `ObjectVersionRecord.CreateBase/Delta`。
- Revision 查询参数使用 `revisionAddress`；读取 API 显式称 `ReadLiveObjectHeadMap`、
  `ReadObjectBaseBody`。
- B/D/H API 使用 `BasePayloadBytes`、`DeltaPayloadBytesUpperBound`、
  `ReconstructionPayloadBytes`；对象 head 与 containing Revision 地址显式命名。
- 不改变 wire version/tag、enum 数值、字段次序、VarUInt 编码或 literal golden arrays。

## 3. 非目标

- 不建设 Repository、Commit/Ref、发布屏障、持久 roots 或基线安装。
- 不迁移 State wire、SchemaStore binary 格式或 Base 类型头版本。
- 不增加 struct、数组、泛型或 BCL codec 能力。
- 不把 `LoadedWorld`、`CaptureSession.Current`、candidate lifecycle 强行改名成尚未实现的工作会话 API。
- 不机械改写 archive、冻结 Probe 或历史设计叙述；只修活跃说明与实际回归消费者。

## 4. 依赖顺序与验收账本

| 要求 | 状态 | 主要所有者 | 验证 |
|---|---|---|---|
| 删除 legacy，裸 attribute 生成唯一 State model | verified | Runtime / Generator / generator tests / package probe | generator/full tests；两项 package probe |
| 原子迁移 Schema history 与 `.dgschema` | verified | Generator / Build / MSBuild assets / history tests | 42 项聚焦测试；839 项根套件；Publish/Verify、旧扩展与旧 header 拒绝；两项 package probe |
| 迁移生成 ABI、对象状态行与 prepared-body 品牌 | verified | Generator / Runtime / Serialization / StateStore / PackageConsumerProbe | Serialization 103/103；Generator 175/175；StateStore 191/191；根套件 840/840；两项 package probe |
| 清理 Storage API 且 wire bytes 不变 | verified | Storage / StateStore consumers | Storage 155/155；StateStore 191/191；StateStore package probe；literal golden 只改符号引用 |
| 活跃文档与交付面一致 | verified | glossary / PROJECT-STATE / roadmap / package docs | 本地链接、旧符号检索、diff review |
| 独立审查与仓库级验证 | verified | review lane / main integrator | 根 build 0 warning/error；840/840；两项 package probe；`git diff --check` |

跨切面不变量：生成的历史 DTO 必须继续由 accepted exact Schema/history 决定；typed Base 只能包装一次
类型头；Storage 不解释 typed header；State v3 golden bytes 是本批不可修改的基准。

Wave 3 的已验证落点：SG 生成 `__DurableState` 与 `DurableStates.g.cs`，公开 raw body 为
`PreparedBaseBody` / `PreparedDeltaBody`，candidate/stored/current 共用 `ObjectStateRecord`；
StateStore 内部 `EncodedBaseObjectBody` 是 typed planner 接受的唯一 Base 输入。PackageConsumer 的 V1
进程通过公开 Prepare/Load 写出真实 Base + 两段 Delta，并把 exact Revision/WorldId 交给 V2 冷重开；
不再由外部手工调用 Base 类型头 codec。Storage 项目及其 wire tests 在本 Wave 无 diff。

Wave 4 将 ObjectHeadMap factories、Revision-address read API、对象 head/所在 Revision 地址与 B/D/H
计量名迁移为显式合同。`StateRevisionWireFormat` 与 writer 未变；两个 literal-golden 测试文件经反向替换
新符号后与迁移前逐字符相同，因此 v3 bytes 没有变化。三轮独立审查均无遗留实现 blocker。
