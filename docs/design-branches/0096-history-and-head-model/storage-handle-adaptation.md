# DB-096 B1：紧凑 Handle 版本的 Storage 接入

> 2026-10-19，实施起点 DG `2d118da`。本片只接入准确上游依赖，不切换 DG 的 Branch/Commit 布局；后续从 [PROJECT-STATE](PROJECT-STATE.md) 继续。

## 范围与最小验收

问题：DG 能否直接消费已交付紧凑 Handle、引用初始化和准确 Entry 历史的 HeadStore，而保持现有对象恢复、Schema、发布和故障合同？先通过源码构建及现有回归，再推进真正使用这些能力的新布局。

来源固定为 Storage `f8868b57e8f012745413bb38a83d67f1dba29f26`；六包候选版本 `0.1.3-dev.20261019.gf8868b5`，唯一当前值在 [StorageDependency.props](../../../eng/StorageDependency.props)。本轮没有打包、发布或普通 PackageReference 验收；旧 `731eef7` 包的结果不能沿用。

## 实际变更与兼容边界

- 更新依赖 pin；生产 C# 无需适配。初始化/恢复测试和 MetaBootstrapCrashProbe 中四处 `HeadId.LocalId` 改为对应 owner 的 `GetLocalHeadId(id).Unwrap()`。恢复报告自己的 `LocalId` 仍是合法字段。
- DG 当前仍是 `P0R2`、三个控制 Head、name→graph 目录和业务 Parent；公开 API 与 DG 编码本片未改。尚未用新业务 Head 实现 Branch，也未将准确历史挂到公开 API。
- 上游格式 4 拒绝旧 1/2/3，所以即使 DG 根标记未改，旧依赖创建的库也不能因此继续打开。无兼容 reader 或自动迁移；实验数据按当前原型约定重新创建。
- 新 HeadId/HeadEntryId 编码为4B/12B，局部文件号只通过 owner 解释；引用初始化 fork 不生成 clone Entry。此处是所接入的上游合同，DG 产品并未新增 ForkHead 调用。
- [依赖指南](../../storage-dependency.md)已同步新候选与源/包边界。旧交付记录固定其原版本，不把历史包验证改称本轮结果。

## 验收记录

主线程使用干净独立工作树 `.artifacts/db096-head-mechanism/storage-f8868b5`，确认准确 HEAD；没有切换或修改上游主工作树。验收源码为 DG `2d118da` 加本片 diff，不把父提交号误当成已包含此次变更的提交。

- 根 Debug 构建零警告/错误，全 solution 测试 **2817/2817**：Persistence 927、Serialization 133、Storage 36、DurableGraph 1721，无失败/跳过。
- 根 solution 外的 MetaBootstrapCrashProbe Release 构建零警告/错误；保存6窗、初始化3窗均由外部 Kill writer 和独立 fresh verifier 验证通过。保存恢复旧/新根、完成帧保留和真实 Delta 续写成立；初始化缺根拒绝/完整根可用保持。
- 独立设计复核确认 B2 的来源证明与调用迁移边界，主线程检查本片适配 diff、准确 pin 和上述运行结果。没有以旧 Probe 结果替代本轮运行。

```powershell
$storageSources = (Resolve-Path .artifacts/db096-head-mechanism/storage-f8868b5).Path
dotnet build DurableGraph.slnx -p:UseStorageSources=true "-p:StorageSourceRoot=$storageSources"
dotnet test DurableGraph.slnx --no-build --no-restore -m:1 -p:UseStorageSources=true "-p:StorageSourceRoot=$storageSources"
dotnet build experiments/MetaBootstrapCrashProbe/MetaBootstrapCrashProbe.csproj -c Release -m:1 -nr:false -p:UseStorageSources=true "-p:StorageSourceRoot=$storageSources"
pwsh -NoProfile -File experiments/MetaBootstrapCrashProbe/Run-Probe.ps1 -AssemblyPath experiments/MetaBootstrapCrashProbe/bin/Release/net10.0/Atelia.DurableGraph.Persistence.Tests.dll -RunRoot .artifacts/db096-storage-cutover/crash-save
pwsh -NoProfile -File experiments/MetaBootstrapCrashProbe/Run-Probe.ps1 -InitializationOnly -AssemblyPath experiments/MetaBootstrapCrashProbe/bin/Release/net10.0/Atelia.DurableGraph.Persistence.Tests.dll -RunRoot .artifacts/db096-storage-cutover/crash-init
```

本机日志为 `.artifacts/db096-storage-cutover/{build,tests,crash-build,crash-save,crash-init}.log`，TRX 在 `results/`；两个进程矩阵的 `receipt.json` 位于各自 RunRoot。重跑时须换全新 RunRoot，不覆盖旧数据。测试与 Probe 都运行准确的 source mode，未运行普通包消费者；进程中止不等于 OS crash 或掉电资格。新历史纵切和组内核仍未实施。

此前独立 [BranchHeadMechanismProbe](../../../experiments/BranchHeadMechanismProbe/README.md) 的45断言仍保其原日期与版本意义；本片没有重跑其长后缀预算测量。`ReadHeadOrigin` 仍未交付，[定向请求](headstore-origin-request.md)继续保留。
