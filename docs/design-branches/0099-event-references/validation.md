# DB-099 实施与验收

状态：源码、新普通包与 Agent 联合验收完成。2026-10-25。

## 实际实现

DG 产品/首批测试来源：`95c540d44c4c118eebb46efdab240c543dda0297`。`EventRef` 位于 Persistence 程序集根 namespace，普通 V1 inline schema 为 `Atelia.DurableGraph.EventRef`，仅三个 UInt32。`EventRef.Definition` 单独注册；签发、未绑定检查与 CommitId 编码转换不访问目标。

Repository 新增 `LoadEvent(EventRef)` 和 `LoadEvent<T>(EventRef)`；二者走原完整历史恢复管线，泛型只检查返回根类型。没有改 TypeTag、记录封套或 Storage pin，没有缓存、访问器代理或外部依赖扫描。`LoadEvent(default)` 现在需要显式写 `default(CommitId)` 或 `default(EventRef)`；旧非泛型 API 快照测试已按新增精确重载修订。

应用说明和实际正文分工见 [console-agent 接入](../../../../atelia-console-agent/docs/event-references/README.md)。库只负责准确读取，Session 负责投影缓存、外部调用依赖准入及 E/S 恢复协议。旧 Agent 草稿布局不提供迁移；用户存档未删除。

## 源码门禁

在源码 HEAD 对齐 Storage pin `70b406cd8d1117733eee81a33e7f978399aa98b2` 下运行：

```powershell
dotnet build DurableGraph.slnx -c Release -p:UseStorageSources=true -p:StorageSourceRoot=E:/repos/Atelia-org/atelia-storage -m:1 -nr:false
dotnet test DurableGraph.slnx -c Release --no-build --no-restore -p:UseStorageSources=true -p:StorageSourceRoot=E:/repos/Atelia-org/atelia-storage -m:1 -nr:false
```

根构建 0 warning / 0 error。全量 3,169 项通过、0 失败、0 跳过：Persistence 1,205，Runtime/Generator 1,795，Serialization 133，Storage 36。主线程执行的日志/TRX 位于忽略目录 `.artifacts/db099/`。

新增 `EventRefTests` 七项覆盖：独立程序集字段/nullable（无值、含 default、有值）/List/array 的 schema export 与冷开；default/错角色/错返回类型拒绝；原参数和一次读取被修改后后续独立恢复不受影响；State 可在没有目标模型时恢复但显式解引用拒绝；准确历史 Event 的普通升级；坏源引用先于升级拒绝；同库 fork 保留祖先位置并冷续写。

首次 Persistence 扩大回归遇到旧“任何方法都不能泛型”的 API 断言失败；已改为只允许精确 `LoadEvent<T>(EventRef)`，随后上面的根构建和完整回归全通过。没有未解决源码失败。

## 新普通包

G=`0.0.0-eventref.20261025.95c540d`；四个 DG 包同源于上述干净提交，Generator/Build 随 Runtime 包交付。S 仍为 `0.1.3-dev.20261019.g70b406c`，没有重打 Storage 或公开发布。

候选由 `eng/Prepare-DurableGraphCandidate.ps1` 生成于 `.artifacts/db099/candidate-ready/`，包含十个 Atelia 原包及示例所需 K4os 原包。本轮首次输入 Agent 的十包 feed，因缺少上游 Storage Pack manifest 被准入拒绝；改用已有准确 manifest 和原包后成功。没有绕过来源门禁或伪造 manifest。

实际消费者入口均通过：

| 门禁 | 实际结果 |
| --- | --- |
| `Run-EventReferenceProbe.ps1` | 仓外新 cache/history，Publish/Verify 0 warning / 0 error；2份 consumer-owned history Verify 不变；seed/verify 两进程共36项断言；十包 assets/nupkg、缓存/输出/实际加载 DLL 哈希一致；四个 DG 包的 G/源码 SHA 准确，Generator/Build 与构建接线实际消费 |
| `Run-EventHistoryRecoveryProbe.ps1` | 新 EventRef 与泛型重载 XML 门禁通过，原门禁保留；4份 history Publish/Verify；热/冷恢复、重复恢复无追加、历史读取与后继 E/S 全通过 |

新夹具回执 `W:/TEMP/dg-event-reference-77926f81804d4e10b2fccfce628cbbfe/result.json`，独立进程 PID22692/22740；主线程回读 Passed、命令结果和 verify 日志。旧恢复门禁产物为 `experiments/PackageConsumerProbe/obj/event-recovery-20261024170210-15172-bfd9dc7a`。两项无待修失败；临时路径可能被清理，长期复现使用仓库 runner 与记录的包来源。

## Agent 与成本证据

Agent 接入、测试和可复现实验工件提交：`936a30f1211ebbf65eeb1d091abbfba656a83c7f`。性能复现入口已在独立目录完整运行，两组构建零警告错误，38 份结果齐备；长期回执随 Agent 提交保存。

完整验收见 [Agent 验收](../../../../atelia-console-agent/docs/event-references/validation.md)，相同工作负载的基线/新模型对照见[性能见证](../../../../atelia-console-agent/docs/event-references/performance.md)。Agent 的完整 Release/24项 Schema Verify/全部离线检查通过；由实测决定默认缓存32 MiB后，Verify重建和 EventRef 全组定点验收再次通过。主线程已回读日志和代码，不以库源码通过代替下游交付资格。

同 G 下旧 Agent 与新模型的1 MiB固定样本：State累计追加 `6,741,544 → 4,924 B`；未完成批次另建全Base根为 `6,737,880 → 796 B`，正文String行归零。首次完整内容投影增加独立Event读取工作；驻留后重复读取收益显著，具体分阶段/总冷延迟和分配计量限制只在性能记录维护。缓存预算不是进程内存上限；16 MiB确实产生循环逐出，32 MiB可驻留该约20.55 MiB的受测投影集，不承诺任意长度历史都驻留。

P2 的关键判据是实际捕获/保存对象行排除正文，并验证新根的对象/容器 head 确实都是 Base；字符串扫描仅可作辅助。P3 需同时保留实际进程中断、原 Event ID、部分 Apply、第二次恢复无新增提交及依赖失败无后续事件的证据。成本分开记录 Event 和 State 发布、纯 State 冷开、Agent 设置/完整 prompt 投影；缓存 charge 不等于总内存，投影构造计数不等于全部 provider JSON 解码次数。

## 明确边界

未验证真实在线模型、掉电、任意底层 I/O 故障或外部副作用 exactly-once。库引用不提供全局内容去重、跨库强身份、GC/pin 或 State-only 自足导出；没有把 Agent 缓存策略上移。跨应用总成本和泛化需求仍由后续真实用户故事裁决。
