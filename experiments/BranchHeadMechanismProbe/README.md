# Branch Head 组合机制见证

状态：DB-096 的独立机制实验；不在根 solution 内，不引用 DG 产品项目。

问题：只用 HeadStore 公开操作，是否能组合出“先建立物理 Head、后登记正式 Branch”，以及引用 fork 后的准确历史分页？最小通过条件是程序退出 0，所有断言通过，输出 `receipt.json`。设计依据见 [机制调查 §6–8](../../docs/design-branches/0096-history-and-head-model/mechanism-feasibility.md#6-创建登记-b-有组合路径但有两层-outcome)。

## 运行

本实验独立锁定 Storage `f8868b57e8f012745413bb38a83d67f1dba29f26`，**不依赖或修改 DG 的 Storage pin**；DG 后续独立完成[B1 源码接入](../../docs/design-branches/0096-history-and-head-model/storage-handle-adaptation.md)，不改变本实验原资格。项目不继承 DG 的 Directory.Build.props/targets，并检查显式传入的源码目录 HEAD 和 tracked source 清洁度。需要 .NET 10 与本机可恢复的上游构建依赖。

在 DG 根目录执行 PowerShell；首次创建独立 worktree，已存在时直接复用：

```powershell
git -C E:/repos/Atelia-org/atelia-storage worktree add --detach E:/repos/Atelia-org/durable-graph/.artifacts/db096-head-mechanism/storage-f8868b5 f8868b57e8f012745413bb38a83d67f1dba29f26
$probeStorage = (Resolve-Path .artifacts/db096-head-mechanism/storage-f8868b5).Path
dotnet run --project experiments/BranchHeadMechanismProbe/BranchHeadMechanismProbe.csproj -c Release "-p:ProbeStorageRoot=$probeStorage" -- .artifacts/db096-head-mechanism/runs
```

传入的唯一程序参数为输出根目录。每次在其下建立独立时间戳/随机后缀目录，保留数据、Head 文件与 receipt，不删除旧运行。10,000 次追加均走真实持久发布，运行可能需要几分钟。没有新包发布、PackageReference 资格或对 DG 产品依赖的替换。

## 覆盖的组合

- D 是 JSON 示意目录（HeadId codec → 标签），S 是字符串示意 Schema 版本，B 是 State/Event 标记 payload；这些不是候选 DG wire 格式。
- root 无 Schema 增长：CreateHead(B) 后冷开证明隐藏，再 PublishHead(D) 登记。
- fork：ForkHead(准确旧 Entry) 后冷开证明隐藏，再 PublishHead(D)。登记后尚无本地 Entry 也可枚举/读取继承 payload；父线继续不改变其 current；首次发布产生自己的 Entry。
- root 有 Schema 增长：CreateHead(B) 后冷开证明 D/S 未推进，再 PublishHeads(D,S)。完成并冷开后同时看到登记和示意 Schema 新值。没有把新 B 假设为创建事务成员。
- 内部 D/S 与最终未登记 B 均不会进入示意目录；未登记的完整物理 Head 仍保留。
- 跨 fork 历史 `A0(State), A1(Event) → C1(Event), C2(State)`：包含准确起点、Before 重读并跳过起点、父线 fork 后的新后缀不进入子线、没有 clone Entry、页满恰到根后允许空 Complete 页。
- 页 materializer 在 HS 正常返回后才交付对象；工作预算不足，即使 visitor 已接收非空前缀也抛出失败。提高预算后可重新从原起点读取。没有把损坏、取消或清理异常当部分成功。
- 固定旧 anchor 的 20 项页面，在同一 Head 继续增加 0/100/10,000 条较新 SingleEntry 后寻找最小成功工作预算。分别使用每次查询新开的 owner，以及先准确读目标再重复查询的 warm owner；不驱逐 OS 缓存。

## 2026-10-19 结果

Release 构建 0 warnings/errors；45 个断言通过。固定旧 anchor 的 20 项页在 newer suffix 为 0、100、10,000 时，cold owner / warm owner 的最小成功工作预算均为 **45**。逐个从 1 增加预算，失败仅接受准确的 `HeadStore.HistoryBudgetExceeded`。

本机详细 receipt：`.artifacts/db096-head-mechanism/runs/20261019-043159-594159a6/receipt.json`；构建与运行输出：同一实验根下 `build.log`、`run.log`。第一次运行仅暴露 Probe 自身没有继续处理零 visitor 交付的预算拒绝，修正后才取得上述完整通过结果。源码和本节结果随 Git 保存；数据与长日志留在忽略目录。

## 资格边界

这是公开原语的成功路径组合与有界分页见证，**不是 DG 图恢复、Schema 闭包、故障原子性或完整性能资格**。示意 Schema 字符串不能证明历史对象的准确 Schema 解释；示意 State/Event 不能证明最近 State 内容来源、Delta prior 或 GraphEnvelope.Parent 可删除。干净 Dispose/重开不是进程 kill 或设备断电。

目录登记只模拟成员资格过滤；尚未实现完整 BranchInfo 的 origin、根/子树查询或恢复未知创建结果。没有读取故障注入、CU 未成立负向记录、1000 Branch 目录/句柄压力、实际字节/I/O 计数。最小 work budget 仅是 HS 对健康 SingleEntry 历史的逻辑计数，不能推导所有路径 O(1) I/O，也不包括启动扫描的完整成本。

实验可在以下情况下重访：登记协议变化、HeadStore 历史/预算合同变化、开始实现 DG 物化分页。生产接入仍应在其自己的依赖 pin、恢复验证与失败测试下验收。
