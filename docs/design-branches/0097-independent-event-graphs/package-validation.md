# DB-097 普通包验收

2026-10-20：本片的四个普通包 runner 及独立字节复核通过。结论限于下列新包、模型与用户故事；源码回归和成本测量另见[总验收](validation.md)，设计合同见[本案入口](README.md)。

## 来源与输入

- DG 来源：干净提交 `bd445204e7ecbe2b844d84aea7ae84a544f39eba`。实现来自 `7cd96f8`，其后提交只修正预算参数 XML 文档的旧措辞；本次重新打包，实际消费新 XML。
- DG 四包 G：`0.0.0-public-history-e2e.20261020114023.22264`。
- Storage 六包 S：`0.1.3-dev.20261019.g70b406c`，来源 `70b406cd8d1117733eee81a33e7f978399aa98b2`。使用既有准确六包 feed 与上游 Pack manifest；未升级依赖、重打存储包或公开发布。
- 环境：Windows、.NET SDK 10.0.201；显式 `UseStorageSources=false`。先串行 Prepare/pack 一次；其余消费者只读复用这批十包，各有新 cache、history、obj/bin 和数据库。
- Split 的来源凭据记录 203 项生产输入：`WholeWorktreeClean=true`、`ProductionInputsMatchHead=true`、`IncludesUncommittedProductionSource=false`。Prepare 前后及全部故事结束后的输入摘要一致。四个 DG nuspec commit 均为上述 DG 来源；六个 S 包与准确来源及原始摘要一致。

包来源凭据的 SHA256 为 `AB6FEA5F4190982B26D8CF2B784895038390CB918F10F72721510B594D601633`。本机持久工件入口为 `.artifacts/db097/package/bd44520-20261020T114021-e1114751/index.json`；该目录受 Git 忽略，不作为跨机器固定路径。

## 真实执行结果

| Runner | 实际通过的故事 |
|---|---|
| [SplitRepository](../../../experiments/PackageConsumerProbe/Run-SplitRepositoryProbe.ps1) | V1/V2 accepted history Publish/Verify、普通非 friend 编译正反例；seed/upgrade/rewrite/continue/verify 五个独立 PID。独立 Event 保 State rewrite；同 Event 两次读取与 State–Event Pair 独立；State–State same-S 共享、cross-S 独立。冷 State 单 List Delta 保 ID/prior、旧 Widget head 与 H，H 实测 202→226→250→274；Catalog Event 不安装 State，全关冷读保持。 |
| [EventHistoryRecovery](../../../experiments/PackageConsumerProbe/Run-EventHistoryRecoveryProbe.ps1) | 四份 history 与包内 facade XML；热/冷两条应用恢复路径均从 HP10 到 HP7，再到 HP4。重复恢复返回 Completed=false 且库字节不变；仅登记 Event 模型仍可只读旧 Event，原 HP10 和 observations 保持，数据库字节/时间戳不变。 |
| [GroupCommit](../../../experiments/PackageConsumerProbe/Run-GroupCommitProbe.ps1) | 四份 history；seed/continue/verify 三进程验证跨分支共同 Schema、请求执行时捕获、summary 复制隔离、混合 State/Event 安装差异、过期请求、准确 tags、树及分页冷读。 |
| [Tag](../../../experiments/PackageConsumerProbe/Run-TagProbe.ps1) | 两份 history；seed/metadata/continue/verify 四进程验证 Event-first、无模型只读解析、CommitId 跨重开、旧点 State/Event-only Fork 续写、环与不可变 tags。 |

四个 runner 均 exit 0；消费者成功构建均为 0 warnings / 0 errors。Split 的 protected raw-query 编译负例按 runner 精确拒绝，恢复合法源码后的重编译成功，再继续正常故事。打包日志中 SDK 关于其余包尚无 README 的建议不是编译 warning；Runtime 自带 README。

本次 Persistence 包 XML 中三条 Event 保存入口的签名均不含重建预算参数：`CreateRootBranchFromEvent`、`CommitEvent`、`CreateEventCommitRequest`。实际包内 XML 摘要为 `2EAE7A104064B40D8E99044CB953A0BA978A2428EC3724E115323424CE2AB212`。

## 独立身份复核

验收没有以 feed 文件存在或 source build 代替实际消费。包消费完成后，另以 `Verify-PackageEvidence.ps1` 读取真实 ZIP、assets、缓存与输出进行复核：

- 10 份原始 nupkg 的 SHA256 与受控 Prepare 结果一致；S 包仍匹配原上游凭据。
- Split V1/V2 和另外三个消费者共 5 组 stage/output：assets 全为 package，均准确解析十包，各自仅使用 runner 新 cache；缓存原始 nupkg、缓存 DLL 和输出 DLL 逐项匹配原包。
- 同样核对各 cache 中真正提供给编译器/targets 的 SG、Build DLL/deps/runtimeconfig、props/targets。Split 编译日志实际观察到该 cache 内 SG 和 Build Publish/Verify 调用。
- 共完成 180 次文件与原包内容的摘要比较；这是含重复缓存比较的检查次数，不是独立文件数。5 组输出中的 50 份 Atelia DLL 均匹配。
- Split 五个实际进程报告的 50 份 loaded assembly 路径/摘要再次匹配原包。其余三个 runner 的复核为输出程序集与对应原包匹配，没有将其扩大为另有加载程序集遥测。

独立复核结果为 `independent-verification.json` 的 `Passed=true`；`frozen-source-and-api.json` 保留最终 203 项输入复核及包 XML 签名。主线程对 Split 的再次独立复核另存 `.artifacts/db097/final/package-main-review.json`。

## 工件与复现

原始 Split 工作目录是 `W:/TEMP/db097-bd44520-20261020T114021-e1114751`，含 feed、来源凭据、result、编译日志和五进程证据。仓内忽略工件目录保留 `split-result.json`、`pack-provenance.json`、`feed-identities.json`、四份 controller 日志、独立复核脚本/结果，以及原始消费目录索引。每个目录均为本次新建，没有覆盖旧候选。

在相应源码版本、准确 storage feed 和新的仓外工作目录下，复现顺序为：

```powershell
$env:UseStorageSources = 'false'
./experiments/PackageConsumerProbe/Run-SplitRepositoryProbe.ps1 -StoragePackageSource $storageFeed -WorkDirectory $newOutsideDirectory
$result = Get-Content "$newOutsideDirectory/result.json" -Raw | ConvertFrom-Json
./experiments/PackageConsumerProbe/Run-EventHistoryRecoveryProbe.ps1 -PackageSource $result.PackageSource -Version $result.DgVersion
./experiments/PackageConsumerProbe/Run-GroupCommitProbe.ps1 -PackageSource $result.PackageSource -Version $result.DgVersion
./experiments/PackageConsumerProbe/Run-TagProbe.ps1 -PackageSource $result.PackageSource -Version $result.DgVersion
```

新跑会产生新的唯一 G 与缓存。本次限定资格不包含公开 NuGet 分发、旧库兼容/迁移、OS crash/power-loss、完整网络隔离或任意业务图的性能保证。普通包结果也不替代源码层故障注入和成本证据。
