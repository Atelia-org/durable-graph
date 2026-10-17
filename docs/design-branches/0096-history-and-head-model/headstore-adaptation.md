# HeadStore 接入切片

日期：2026-10-17。范围：对齐已实施的上游术语/API，并保留 DG 任意历史 checkpoint 的不可变书签。不决定后续 domain-history 的结构。

## 上游事实

来源为 Storage `731eef7cd3158572532fbeea7ba95a79213a7328` 的[重命名记录](../../../../atelia-storage/docs/HeadStore/15-headstore-renaming-plan.md)和[HeadSetTag 方案](../../../../atelia-storage/docs/HeadStore/16-headset-tags-plan.md)，具体行为以对应源码为准。

- `HeadStore / HeadId / HeadEntryId / HeadEntry` 取代 VersionStore / RefId / RefRevision / RefBytesSnapshot。`HeadEntryPayload` 是概念，公开属性仍为 `HeadEntry.Payload`，没有新包装类型。
- `HeadSetTag` 是固定的非空 `HeadId → HeadEntryId` 表。`CreateTag(name, ReadOnlySpan<HeadId>, out outcome)` 捕获所选 Head 当前已接受位置，`ReadTag` 读表，`ReadEntry` 再读内容。没有 bytes/Related 或任意历史 EntryId 创建重载。
- `NamedFrameMapCodec` 是可选的应用 payload codec，不是上游持久名称目录。
- 上游采用格式 2，拒绝旧格式；保留 `versionstore.*`、`refs`、`tags` 等物理路径。术语变化不要求再改这些路径。

## DG 适配决策

DG 的 `CreateTag(name, CheckpointAddress)` 必须继续能标记任意历史 State/Event，包括 Event-first、已经移出当前逻辑历史的节点。当前 checkpoint 是 FrameAddress，并不独占一个 HeadEntry，因此不能直接转发到 HeadStore.CreateTag。

选择一个固定 **书签目录 Head（本地编号 3）**，payload 为 `P0T1` + `NamedFrameMapCodec` 编码的 Ordinal `名称 → FrameAddress` 字典。现有分支目录 Head 1 与共享 Schema Head 2 保持职责。创建书签复制并发布整个新目录，确认成功后替换内存基线；已有名字永不覆盖。仅书签 Head 发生发布，不追加业务图，不移动分支，不改 shared Schema，不触碰活动 checkout 的保存基线。

替代方案：把书签塞进分支目录会使业务保存和书签相互扩大写入范围；每书签建立一个 Head 会引入额外身份与名称映射；删除 DG 书签会破坏已有冷开定位用法。本片选择独立目录以复用现有原子发布机制。创建为 O(n) 编码/写入，受单个 HeadEntry payload 容量约束；不宣称解决大规模索引。容量失败须在发布前拒绝并保持 owner 健康。实际书签规模成为问题时再研究增量目录。

创建仓库预留并建立三个 Head；缺少任一入口视为未完成初始化，普通 Open 不补建。旧两入口布局拒绝，无兼容 reader、双写或原地迁移。业务图编码和 Parent/source 关系保持。

打开时读取三个必要目录，校验书签表结构和名称，但不遍历书签指向的图；精确解析书签时才检查目标图的 metadata。只读不恢复物理尾，可写沿上游正常恢复；不扩大为启动全库审计。书签错误属于 DG，名称沿用非空、最多 1024 UTF-16 code units、有效 Unicode、Ordinal 比较；空缺、重复、非法名采用 DG 错误标识。

发布沿 HeadStore 的 data-first 和 Outcome 合同：完整帧不回滚，Unknown/fault 后关闭重开按名裁决；确认后交付失败保持 Published。普通参数/容量拒绝不 fault。没有发布业务图并命名的复合事务。

## 验收边界

- 根 solution 源码构建与测试；固定准确上游 revision，显式 source mode。
- 任意旧 State/Event 书签、移动分支/继续保存、全关冷开/只读定位、无模型物化。
- 重复/非法/缺失名称、容量、坏目录/坏目标分层检查；发布前/追加后/确认后故障及重入。
- 三入口创建的每个中断窗口，完整帧稳定、缺失不补建；书签发布只改变第三个 Head。
- 活动探针和包准备脚本引用新的包名/API。未制作 feed 或普通包验收时明确保持待交付，源码构建不充当包资格。

## 实施结果（2026-10-17）

已完成源码接入、三个目录的初始化与读取、DG 书签目录发布、错误归属、名称/容量验证、回归夹具、活动探针和包准备脚本的改名。公开 DG `CreateTag/ResolveTag` 签名保持，底层包改为 `Atelia.HeadStore`；持久布局不兼容旧库。当前目录使用完整编码，仍不是增量索引方案。

验证使用 Windows / .NET 10，环境 `UseStorageSources=true`、`StorageSourceRoot=E:/repos/Atelia-org/atelia-storage`，干净上游为本页所列 revision：

| 验证 | 结果 / 证据 |
| --- | --- |
| `dotnet build DurableGraph.slnx --nologo -m:1 -nr:false` | 0 warnings / 0 errors；`.artifacts/headstore-build.log` |
| `dotnet test DurableGraph.slnx --no-build --no-restore --nologo -m:1 -nr:false` | 2,812 passed：Persistence 925、Serialization 133、Storage 36、Runtime/Generator 1,718；`.artifacts/headstore-final-tests.log` |
| MetaBootstrapCrashProbe 构建及 `Run-Probe.ps1 -InitializationOnly` | 3/3 外部 Kill 窗口通过；`.artifacts/headstore-init-crash/receipt.json` |
| MetaBootstrapCrashProbe `Run-Probe.ps1` | 6/6 保存窗口通过；`.artifacts/headstore-save-crash/receipt.json` |
| PublicationCrashProbe `Run-Probe.ps1` | 5/5 底层发布窗口通过；`.artifacts/headstore-publication-probe.log` |
| 四个受影响消费者源码编译 | Split(V1)、PrivateContainer(A)、Development(V1)、ExplicitStorageClassification(Corrected) 通过；临时复制项目将包引用替为源码引用并接 generator，见 `.artifacts/headstore-consumer-compile/`，不授普通包资格 |
| 文档和脚本 | 检查本片及受影响文档本地链接、PowerShell 语法、集成 diff 与 `git diff --check` |

新增/迁移的关键回归入口：[书签存储与容量](../../../tests/DurableGraph.Persistence.Tests/RepositoryTagStorageTests.cs)、[三入口初始化](../../../tests/DurableGraph.Persistence.Tests/SplitRepositoryInitializationTests.cs)、[书签用户语义](../../../tests/DurableGraph.Persistence.Tests/RepositoryTagTests.cs)、[发布故障](../../../tests/DurableGraph.Persistence.Tests/NewPublicationFailureTests.cs)。正常初始化读取三个必需 Head，未引用的其他 Head 仍按需首读，未扩大为全库恢复或审计。

本轮未修改上游，未制作或发布 feed；预留 S=`0.1.3-dev.20261017.g731eef7`。包交付前仍需用准确干净来源生成六包、执行 DG package-mode restore/build 与独立普通包消费验收。旧验收不回填为当前资格。进程实验不授断电/设备故障保证。
