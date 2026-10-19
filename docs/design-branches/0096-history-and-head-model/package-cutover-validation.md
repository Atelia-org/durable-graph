# DB-096：活动消费者迁移与普通包验收

2026-10-19，接续[根调用迁移](branch-cutover-plan.md#9-b2-调用闭包与回归迁移)与[目录查询收窄](directory-persistence.md#已接受目录的查询投影)。本片问题是：新 Branch/Commit/WorkingCopy 能否由现有活动 PackageReference 消费者真实使用，同时保留准确历史 Schema、内容来源、冷进程及失败诊断的判别力？范围为活动 runner、配套文档和公开 XML；不复活已经退役的模型资产运行故事。

**结论：11 个活动入口均已在本片执行通过。**根源码构建及引用固定 Storage 包的完整根回归也通过。公开签名、运行行为和持久格式本片未改变；产品 C# diff 剥离 XML 注释后与前版一致。成果仍在 `db096/branch-repository-cutover`，没有正式合入或远端发布；组提交、根/子树、分页和定向 origin 尚未由这些结果实现或验收。

## 来源与工件

- 消费者迁移和新 API XML 的来源提交：`651e51ca74d914a5a469761a4a0fe294ba0def96`，建立在 `0e638da` 上。由干净提交准备 G；后续只修单 Runtime runner 的输入隔离，不改产品包。
- S：`0.1.3-dev.20261019.gf8868b5`，来源 `f8868b57e8f012745413bb38a83d67f1dba29f26`。从独立干净 worktree 调用上游 `eng/Pack.ps1`，生成六个 nupkg、六个 symbols 和 manifest，再经 DG `Prepare-Storage.ps1` 核对准确版本、仓地址、commit 与包 SHA256。
- 主门禁 G：`0.0.0-public-history-e2e.20261019080354.17968`。Split runner 受控打包产生十包 feed 和 `dg-source-provenance.<G>.json`，记录包前后生产输入与产物摘要；其余领域门禁复用同一不可变 feed。
- README G：`0.0.0-db096-readme.20261019080650`，同一产品来源。候选导出器自制另一批包，使用独立版本，附原始 K4os 包形成11包便携目录；没有把不同打包产物覆盖为同一版本。
- SDK：10.0.201。源码构建显式选择固定 Storage worktree；包恢复/打包显式 `UseStorageSources=false`，使用任务配置和独立缓存，不借 source ProjectReference 生成正式包。

本机工件总入口为 `.artifacts/db096-package-cutover/`，`run-paths.json` / `readme-paths.json` 定位外部目录；主运行目录为 `W:/TEMP/db096-completion-56a954a6e13648d6bd7750a5f6a496f3/`。S feed 在本仓忽略目录的 `storage-feed/`，主 G feed 在外部 `split/feed/`，README 候选在 `candidate/`。工件不随 Git 分发；重跑按活动 runner 的参数生成新目录，不复用旧运行结果。

## 迁移裁决

- 创建返回 BranchInfo，再 OpenWorkingCopy 恢复新对象；后续成功 State 提交仍安装传入实例。标签唯一仅为小型 fixture 的应用约定，不能转述成库约束。
- 同库 CommitId 可编码后跨重开使用；旧 owner-bound 地址拒绝改成持久定位正例，保 default、未登记、角色不符和健康/fault 的现行拒绝合同。旧 Move 故事改为旧点 fork，并证明原线继续、不可变书签不动。
- Split/Container 的独立 wire observer 改读 P0R3/P0T2/P0E1/P0G2。D 决定成员；准确业务前序和严格最近 State 来自实际 HeadStore 历史。D/T Meta 不当作 Schema，S Catalog 只沿自己的 PreviousMeta 链读取。未登记业务产物仍保 inventory/hash，不凭图自报来源获得 Commit 资格。
- Fork 可以追加 D，完整帧校验继续逐项核地址、tag、payload 与 tailmeta 摘要，不退化为文件长度检查。这些 observer 是有限夹具解析器，不是任意仓库通用检查工具。
- Meta friend 消费者持久保存12B Entry 身份，按真实历史签发 snapshot；保无业务登记的 Meta 冷读、九对象真实 Delta、准确 prior/H 和安装实例检查。没有向产品添加旧 session 兼容入口。
- 新 API XML 说明创建/编辑分离、继承 current 与目标 Branch 分离、Event 可选且不自动重放，以及 ReadPair 的 Transient 共享约束。包中 XML 与实际恢复 XML 的一致性仍由门禁检查。[术语表](../../DurableGraph-glossary.md)已对齐当前模型，历史锚点保留语义迁移说明。

## 实际验证

主线程完成 source 根 build，以及 task NuGet.Config 下的 package-mode restore / Release Rebuild：两次 build 均0警告/0错误。随后运行整个根 solution 的 Release tests，实际 TRX 共 **2879/2879，0跳过**：Persistence 986、Runtime/Generator 1724、Serialization 133、Storage 36。此处 DG 自身仍由源码项目组成、Storage 来自准确包；下表才是独立 DG PackageReference 消费资格。

| 活动入口 | 实际通过的关键观察 |
|---|---|
| Run-SplitRepositoryProbe | 5个独立进程；当前与历史 Schema、Upgrade、rewrite、冷续写、Pair/Fork 与 source/prior/H；准确 CS0122 后恢复合法源码重新 Verify build；公开消费者无 friend |
| Run-PrivateContainerProbe | 7个进程；缺升级规则拒绝、两分支独立继续、Nullable/List 升级；真实 List Base＋Delta，H=3529；历史名称不恢复 Private 功能 |
| Run-DevelopmentSchemaProbe | 5个进程；关闭库副本 A/B/release、独立 history、正常升级；V2A 对已发布 V2B 的冲突准确 DG0015 |
| Run-RepositoryApiProbe | 18个逐条 CS 代码/位置负例；恢复正例重新构建后才执行跨重开保存，State 实例安装与 BaseCommitId 正控 |
| Run-ExplicitStorageClassificationProbe | Default/Family 两路线；准确 DG0024，修正后的 Publish/Verify，4个 write/read 进程 |
| Run-EventHistoryRecoveryProbe | 包/恢复 XML、summary/remarks 合同；hot/cold 应用处理进度、重复恢复不追加、仅登记 Event 时读旧 Event |
| Run-TagProbe | 4个进程；同库持久 ID、旧点另开、父线保留99、固定书签与循环引用、空 fork 冷开 |
| Run-ImmutableLeafProbe | Family 候选共享/可变图隔离/冷保存，Sibling 不安全 leaf 拒绝；两个实际输出标记通过 |
| Run-MetaBootstrapSchemaProbe | 4个进程；准确 handle/HS 来源、9个真实 Delta、准确 Schema/prior/H、只读文件不变；这是内部 friend 机制资格 |
| Run-ReadmeQuickStartProbe | 搬迁候选、本地相对配置、新缓存；旧命令准确 NU1100；原文 HP99→98、历史100/99/98、旧点 fork/冷续写、可选 Event、V2 Upgrade、策略及 Verify |
| Run-Probe | 单 Runtime PackageReference；DG0014、三个 legacy guard、缺 history 的 CI 拒绝、零类型空 manifest、history 摘要、`7:True:2` 及 body/Capture/string 流程 |

根日志为 `source-build.log`、`package-root-{restore,build}.log`、`root-tests.log`，TRX 和总计在 `root-tests/`、`root-test-summary.json`。各包日志为 `split`、`containers`、`development`、`api`、`classification`、`recovery`、`tag`、`immutable`、`meta`、`readme`、`runtime-probe-fixed` 的 `.log`；领域结构化结果在外部对应目录的 `result.json`。Recovery/Tag/Immutable 与单 Runtime 沿原 runner 在本仓 `experiments/PackageConsumerProbe/obj/` 建独立产物，不能笼统说所有消费者都位于仓外。

主线程核对实际 TRX、结构化回执、运行输出及集成 diff；分包成功报告不单独作为验收。Immutable 和原单 Runtime runner 没有逐进程加载程序集摘要回执，本片只报告其实际 PackageReference/运行断言，不将较强门禁的来源证明外推给每个入口。没有进程中止、掉电、总 I/O、吞吐或全部登记故障窗口资格。

## 唯一运行尾修与后续

单 Runtime runner 首跑失败，记录保在 `runtime-probe.log`：它直接构建源 Consumer，原 obj 同时残留旧 `DurableSchemaGenerator` 与新 `DurableGraphGenerator` 的两个 manifest；正常 Clean 没删除生成文件，新单 manifest 保护正确拒绝。修复仅改 [Run-Probe.ps1](../../../experiments/PackageConsumerProbe/Run-Probe.ps1)：每次复制10个明确输入到新目录，以局部 MSBuild props/targets 隔离父仓配置，所有 legacy 负例改在副本执行；另支持配对传入既有 feed/G。没有删除原 obj、放宽保护或覆盖包。修后用主 G 完整通过，原两份旧 manifest 仍保留，新副本只有一份；失败自打包的另一个 G 不作为资格来源。

本片收口后不继续重复消费者迁移。下一步完成定向 origin：已有只读调查定位了上游现成的单目标解析、header 检查、临时句柄和 cleanup 接点，可在隔离切片实施并验证，再更新 DG pin 与访问隔离反例；无需把“尚未交付”无限重复为等待。其后推进真实 CommitRequest 组提交、根/子树与分页，按完整目标复核正式合入。当前工作顺序只维护在 [PROJECT-STATE](PROJECT-STATE.md)。
