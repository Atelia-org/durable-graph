# DB-098：退役 ReadPair 与跨图共享规划

> 状态：已实施；源码回归、独立复核及三项普通包限定验收通过。2026-10-24。
> 本片是公开 API 的破坏性精简，不改变持久格式或 Storage pin；没有兼容壳、模式开关或旧存档迁移。

## 决策与适用范围

[DB-097](../0097-independent-event-graphs/README.md)让 Event 独立保存后，ReadPair 原本配套 State/Event 增量共享的主要用途消失。
它仍能在某些 State–State 双快照场景减少领域实例分配，但当前没有实际消费者的内存瓶颈证据，
而可信闭包、同环境与完整状态证明、共享分配/填充及 Transient 只读限制持续增加实现和应用设计负担。
本片选择完全退出该专用优化；不是声称它没有技术价值，也不以删除代码量推导运行速度提升。

多历史版本由调用方分别 LoadState/LoadEvent；需要当前图及最近严格祖先的相反角色图时继续 ReadCheckpoint。
历史读取不安装保存基线，交付后可分别初始化 Transient；编辑并提交仍使用 WorkingCopy。
本片不实现 Event Ref、Lazy load、通用 Blob 层或跨库内容引用，后续另行设计。

公开变更还包括删除 `StateEquality<TState>` 与 `StatePreparationBinding<TState>` 的第四个比较委托参数。
生成器不再为引用类发射仅供共享证明的整对象 `StateEquals`，inline/value 比较继续保留。
消费方更新包时应使用同批 Runtime/Generator 重新构建模型程序集；本片不维护旧生成程序集的二进制兼容，
也不因此增加 Schema 版本或改写 accepted history。console-agent 当前固定的旧包尚未切换，后续 Event Ref 接入另行选择新包。

## 删除边界与保留不变量

| 退出主线 | 必须保留 |
|---|---|
| Repository.ReadPair 与专用调用链 | LoadState、LoadEvent、LoadLatestSavedState、ReadCheckpoint |
| 同环境共享资格、候选集合、反向引用闭包及共享物化分支 | 每图完整 Decode/Normalize/引用校验、图内 alias/cycle、两阶段物化 |
| 仅供 ReadPair 的 StateEquality/ProvesSameState 接线、SG 登记与专用比较实现 | StateEquals、StateValueComparison 及 State/meta 差分、容器 canonical key 验证所需比较 |
| 共享命中统计、纯优化测试与活动消费者中的 Pair 断言 | 已有独立恢复、准确 Schema/升级、存储来源、故障、重入及生命周期守卫 |
| 可能跨图共享所带来的 Transient 只读使用限制 | 构造器不执行、Transient 由宿主重建、应用自有全局对象并非通用深拷贝 |

ReadCheckpoint 的 ReadIndependent 不是 ReadPair 的残留优化：它承担一个操作内的独立两图恢复。
必须先完成两图全部准备、分配和引用表构造，再开始 Hydrate；第二图失败不能交付半个 Checkpoint。
保留同操作 mutable 工厂实例冲突守卫，防止错误的 singleton 工厂让第二图覆盖第一图；
不能用两次无共同实例守卫的 ReadRoot 冒充等价替换。用户回调自身副作用不回滚。

owned 物理读缓存、操作内准确解码复用、最近 State 准备槽、默认关闭的 DB-082 不可变叶候选均不因删除 ReadPair 而删除。
State 保存的差分与内容来源、持久 Schema/DTO 编码、历史导航及发布协议保持。
完整状态比较能力逐个按消费者裁定，不按名字含 Equality 或 StateEquals 一刀切。

<a id="technical-reserve"></a>
## Git 技术储备

Annotated tag：`archive/readpair-before-removal-20261024`。
其来源提交为 `227be9a55cbbd75818592e00184b1a70474220f5`；这是退役前完整源码、测试及当时文档的恢复点。
只在本地创建，未推送或发布。无需在当前工作树留下备用实现或复制一份 archive 源码。

```powershell
git show archive/readpair-before-removal-20261024:src/DurableGraph.Persistence/GraphReader.cs
git show archive/readpair-before-removal-20261024:tests/DurableGraph.Persistence.Tests/SharedGraphReaderTests.cs
git show archive/readpair-before-removal-20261024:docs/design-branches/0066-readpair-comparison-and-transient-contract-slice/README.md
```

重点参考：DB-064 的闭包设计；DB-066 的完整状态比较与 Transient 反例；DB-079 的共用物化和失败顺序；
DB-097 的 Event 局部身份与独立图守卫。旧文档中的 Chosen/验收只说明来源版本，不能恢复为当前合同。
未来仅当真实多快照工作负载证明独立恢复的分配或驻留成本成为瓶颈，且消费者接受共享只读约束时重新评估。
届时复用证明和反例，不预设恢复旧 API 或原实现。

## 最小验收与证据边界

1. 主线产品源码及活动消费者不再暴露/调用 ReadPair；StateEquality/ProvesSameState 专用接线退出。
2. 完整根构建与相关测试通过；保留 State 差分、Schema 升级、单图 alias/cycle、独立 Checkpoint 和 singleton 拒绝见证。
3. 普通包消费者及 XML 门禁改为现行入口；实际运行何种源码/包模式分别记录，不沿用旧回执当新资格。
4. 持久编码及 Storage pin 未变化；共享节点限制从当前指南移除，历史材料限定适用范围，删除源码的链接指向本 tag 储备。
5. git diff --check、变更文档本地链接与集成 diff 复核通过；用户数据不删除，不推送 tag 或代码。

## 实施与验证记录

主线程完成源码与测试 diff 复核，并由独立审查确认共享机制的删除没有削弱 Checkpoint 的两阶段物化/工厂守卫、State 准备槽、inline/value 差分或 immutable-leaf 实验。

源码模式使用 `UseStorageSources=true`、`StorageSourceRoot=E:/repos/Atelia-org/atelia-storage`；上游 HEAD 与 pin `70b406cd8d1117733eee81a33e7f978399aa98b2` 相符。

| 验收 | 实际结果 |
|---|---|
| 根 solution Release build | 0 warning / 0 error；首轮发现并修正四处测试的旧比较参数 |
| 全量测试 | Persistence 1197、Serialization 133、Storage 36 全通过；Runtime/Generator 1795 项中首次 1794 通过，1 项旧生成文本断言失败 |
| 修正与补充回归 | 修正该旧构造器文本断言后重建、定向 1/1 通过；补充损坏源图验证后重建恢复边界集合 15/15 通过，无未解决失败、无跳过 |
| 真实 Repository 纵切 | 44 个业务 + 54 个完整性断言通过；原实验仍用旧四参数 AppendBusiness/Event 增量反例，已迁移为现行 State 错历史来源反例后重跑 |
| 静态检查 | 产品源码无 ReadPair/ProvesSameState/StateEquality<T>/共享闭包残留；活动脚本只保留 API 缺席/编译负例；diff 检查通过 |
| 文档链接 | 修改范围内 1195 个本地引用检查无新增断链；8 个旧历史断链未扩大处理 |

主要回归为 `DB079GraphRestorationTests`、`SplitRepositoryReadTests`、`ReadPreparationBoundaryTests`：覆盖升级不能隐藏坏源引用、第二图失败不交付、singleton 在 Hydrate 前拒绝、Transient/可变内容独立、读取不调用保存 preparer。
旧纯共享测试退出，通用 `SharedReadModel` 已提取继续供独立恢复测试使用；不以保留旧测试数量作为保留退役机制的理由。

本机原始日志及 TRX 保存在忽略目录 `.artifacts/readpair-retirement/`。实际命令主干：

```powershell
dotnet build DurableGraph.slnx -c Release -p:UseStorageSources=true -p:StorageSourceRoot=E:/repos/Atelia-org/atelia-storage -m:1 -nr:false
dotnet test DurableGraph.slnx -c Release --no-restore -p:UseStorageSources=true -p:StorageSourceRoot=E:/repos/Atelia-org/atelia-storage -m:1 -nr:false
```

### 普通包限定验收

产品/测试提交：`a8f6ed115496cf30f5ac748fa385b1d44fe8bb41`。两批 G 均由该提交生成，S 继续为 `0.1.3-dev.20261019.g70b406c`；未切换 console-agent 的固定包。

| 门禁 | 包版本与实际证据 |
|---|---|
| RepositoryApi | G=`0.0.0-readpair-retired.20261024154844`；20 个精确编译负例通过，ReadPair 为 `Negative.cs(4,136)` 的 `CS1061`；恢复合法源码后重新构建并冷保存通过。回执 `W:/TEMP/dg-repository-api-6ccf9d1f8ec7495fa08085a2f25b1d72/result.json` |
| EventHistoryRecovery | 同一 G；包内/恢复后的 XML 成员检查通过，ReadPair 缺席；正常及待处理 Event 的冷恢复/再次恢复幂等通过。日志 `.artifacts/readpair-retirement/package-recovery-final.log`；消费目录 `experiments/PackageConsumerProbe/obj/event-recovery-20261024155025-8184-a75bba69` |
| SplitRepository | G=`0.0.0-public-history-e2e.20261024155026.19032`；受控打包前后来源一致且生产输入匹配干净 HEAD，十包身份/hash、真实包 Generator/BuildTool、V1/V2 accepted history、seed/upgrade/rewrite/continue/verify 五进程均通过；改用独立读取后的内容与 mutable 隔离、准确 State 来源、Delta、冷续写成立。回执 `W:/TEMP/dg-split-repository-package-20261024T155025701Z-19032-37ab0b5e/result.json` |

首次复用 feed 未附 Split runner 所需的受控打包前后凭据，门禁按预期拒绝；随后使用 runner 自带受控打包生成第二批 G，未事后伪造来源凭据。
EventHistory runner 初次收到相对 feed 路径导致 restore 指向错误位置，改传绝对路径后通过。两项均发生于消费者准备阶段，不作为产品故障或成功运行证据。

复现入口：`Run-RepositoryApiProbe.ps1`、`Run-EventHistoryRecoveryProbe.ps1` 使用绝对 `-PackageSource` 与对应 `-Version`；
`Run-SplitRepositoryProbe.ps1 -StoragePackageSource <准确六包feed>` 自行受控打包，若复用 feed 则须提供匹配的 provenance。
本机日志、包和回执位于忽略目录/TEMP，可能被清理；可执行门禁与关键结果保存在仓库。本片无未完成必需项。
尚未公开发布包或推送代码/tag。以上不构成性能加速、物理 GC、Event Ref 或旧生成程序集兼容承诺。
