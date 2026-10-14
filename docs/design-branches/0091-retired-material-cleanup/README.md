# DB-091：退出已淘汰的设计与实验

本片清理已被当前产品覆盖或替换的旧设计、叙述和实验代码，减少默认搜索与续工干扰。
删前基线为 `6dbaf0e`；用户于 2026-10-14 明确授权分批删除、验证与本地提交。

验收边界：逐组核当前继任和依赖，独立复核删除范围，保留仍有效的约束与模型资产；
删除后的根 solution 可编译，相关产品回归通过，修正受影响的本地链接。
不恢复旧存储兼容路径，不将模型资产的编译等同于已退出的包故事验收。

## 清理范围

已完成分组调查、独立反证复核、删除和集成验收。删除以下本地材料，保留 Git 来源：

| 已退出范围 | 当前继任或保留边界 |
|---|---|
| DB-006–014 与三份 StateStore base/addressing 稿 | 旧逻辑图、TwoLeg/Self/lineage/距离与分层方案退出；真实 Capture/恢复测试保共享、环、退边、两阶段与失败边界，FS 提供 opaque 地址与轮转 |
| DB-016/021/026/028/031/046/085 | 旧首片排期、raw Revision/wire、SchemaCatalog 与迁移调查退出；当前 DTO/body、ABI5、完整 view、prior/H 与自举合同由源码/tests及 DB-087 承担 |
| 旧缓存隔离 Probe、raw-map 可行性与三份测量 JSON | 依赖已删除 Store 的 runner/harness和历史样本退出；不以旧样本授当前 FS/P0 性能资格 |
| 六份冻结旧稿、原 archive 索引、引用 Capture 工作单/Goal、DB-086 三份长稿 | 退出默认全文搜索；有效内容已提取到现行知识 home，独有历史包输出已收束到 DB-024 |
| 未注册 graph operations generator 与七份逻辑图 Probe 源码/tests | 引用闭合于旧实验；正式 SG 自引用正控迁入 DurableSchemaGeneratorTests，其余关键问题由真实产品回归承担 |
| 19 个旧 PackageConsumer runner、失效执行程序/诊断与三个纯 Host | 保 19 类领域/history/Upgrade 资产，26 项目为 Library；混合 StateStore 模型抽取保字段与升级，HistoryCapability 仅移除执行 helper 依赖 |

DB-015/029/067/069/086 压缩为仍在用的短合同，保完整 post-live、精确整数策略与实际 B/D/H、
确认后 State 安装、Event/Discard、owned LRU、预算/寿命/fault、正常 backing 恢复与离线救援。
当前 Storage 的 payload/地址/只读字典/LRU、meta 核心、活动包 runner及两类故障 Probe 保留。

同步修正 target/roadmap/PACKAGE/P0 README 的旧单根、SCB1、合帧、严格启动检查和失效示例宣称。
当前目录按员工 Dictionary 实际 Base/Delta payload 与重建 H 选择，已不采用 raw Remove/BaseUpper 公式。
退出编号不复用；今后退役随任务检查继任、依赖与仍有效约束，在局部 AGENTS 中落实，无额外状态机或周期清理框架。

## 验证与限制

- 根 `dotnet build DurableGraph.slnx -c Release --no-restore -m:1 -nr:false -p:UseStorageSources=false`：零警告/错误。
- 同模式 `dotnet test DurableGraph.slnx -c Release --no-build --no-restore -m:1 -nr:false -p:UseStorageSources=false`：2690/2690，零失败/跳过（Persistence 893、Serialization 133、Storage 36、Runtime/SG 1628）。
- 26 保留库默认 V1、StateStore V2与HistoryCapability V2/V3，共29次编译及7次跨库模型包 pack通过；实际 PackageReference、accepted history 旧字节与模型源码保持。唯一警告为 StateStore V2 原 Transient `_cache` 的 CS0414。
- 模型资产验收复用已验 DB-089 G `0.0.0-public-history-e2e.20261014041822.37600` 和准确六存储包，十包 SHA 对照原 receipt 后消费；这不是本次源码新 G 的包运行资格。未复建其他模型多代矩阵、运行已退出故事、重跑 Kill或性能测量。
- 独立复核实际 diff及编译工件；五份短合同与当前源码对齐，历史链接的源路径/原章节可从 Git 查回。受影响本地链接与 anchors无新增缺口；117 个既有历史 source/tool目标缺口保留，不声称全库零断链。集成 diff/格式检查通过。

本轮工作树由876个版本控制文件收缩至787个（净少89文件），净删约4万行、约2.1 MiB。
这些是工作树规模，Git 历史未重写；ignored 构建缓存/实验产物不计入且未清理。
根产品测试授当前源码资格；模型编译只授保留资产的 SG/history 消费资格，不能代替已退出的运行门禁。

详细模型工件在忽略目录 `.artifacts/retirement-20261014/fixture-validation/run-20261014T052551Z/result.json`
（SHA256 `022562DD4D8D97B965A79C95506039F0497B4FBE98F3B3899649B2E03E961C0F`），保存完整命令、包/源码/history hash与日志。

<a id="git-history"></a>
## Git 历史

已退出材料不在工作树另留副本。旧链接指向本节时，其链接标题保留删前路径与章节；
按该路径在删前提交只读查看，路径和正文具有当时的含义：

```powershell
git ls-tree -r --name-only 6dbaf0e -- docs experiments src tests
git show 6dbaf0e:docs/state-store-base-design.md
```

更早的四个独立 Probe 源码已于 2026-09-07 退出，恢复点为
`research/probes-archive-20260907`（`96885b691dc20c2a8861694c47f83aa1a0a65c0b`）。
先读取所需文件；不把旧项目整体恢复为当前产品的必经续工上下文。

Sept6 原知识 home 的原始出处为 `54a33df:docs/DurableGraph-target-design-v0.md`、
`54a33df:docs/DurableGraph-research-roadmap.md`、`54a33df:docs/DurableGraph-lab-notebook.md`。
DB-087 冻结长稿来自 `ecc80683f35e2f42795530d28c8974dfb43d95dd`，在删前 `6dbaf0e` 的
`docs/archive/2026-10-13/0087-meta-bootstrap-refactoring-route.md` 可读，覆盖 P0/P1逐片过程；
后来 P2完整记录另在 `d8c0ecb:docs/design-branches/0087-meta-bootstrap-refactoring-route.md` §9，
不能用较早长稿替代。原实验源码恢复还有 [实验来源索引](../../../experiments/ARCHIVE.md)。

这些是本地 Git 查证入口，不宣称本次提交或标签已推送。
