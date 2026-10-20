# 2026-10-20 DurableGraph 重构后质量体检

> 此目录记录体检基线及当时的缺陷观测。后续修复已获授权；当前问题状态、验收与续接统一查看 [2026-10-20 维护状态](../2026-10-20.md)，不要把下文“未实施”或观测见证的断言当作修复后的现状。

本轮问题：DB-096 大规模重构后的当前代码，是否仍有实现遗漏、责任混杂、重复权威或缺乏验证的关键边界？只记录有源码依据且便于后续 Coding Agent 接手的问题；不把 SOLID 当作扩建接口、拆程序集或补兼容层的理由。

检查基线：`1beca7cf2477d369010b3577b7a795c41dbc45f1`。开始时只有既有未跟踪文件 `maintenance/2026-10-20.md`；本轮不修改该文件。

范围为一轮七方向并行检查及主线程复核。重大语义、API、持久格式和恢复契约调整只提出建议；允许直接实施明确安全的细碎修补。最小验收：七份独立报告落盘，区分事实/风险/建议，提供源码定位及可执行的后续验收；主线程核验主要发现并汇总交叉项，注明实际构建、测试与未覆盖范围。

## 报告导航

| 报告 | 负责范围 |
|---|---|
| [01 持久化与发布](01-persistence-and-publication.md) | 格式、保存规划、内容来源与发布故障 |
| [02 Repository 与历史](02-repository-and-history.md) | Branch/Commit/WorkingCopy、创建、树、分页和多分支流程 |
| [03 恢复、缓存与寿命](03-restoration-cache-and-lifetime.md) | exact 读取、恢复、共享与隔离、资源和缓存 |
| [04 Runtime 与容器](04-runtime-model-and-containers.md) | Schema、绑定、Capture、Upgrade、数组/List/Dictionary |
| [05 Source Generator](05-source-generator.md) | 符号分类、诊断、生成路径、泛型和跨程序集 |
| [06 构建与交付](06-build-history-and-delivery.md) | history 发布、工程依赖、候选包和验证脚本 |
| [07 架构与契约](07-architecture-and-contracts.md) | 跨层职责、CLI、公共入口和活动文档一致性 |

## 验证与主线程裁决

七份报告已交付；主线程已阅读全文，并抽查主要调用链、生成参数/缺版循环、打包资产路径以及全部小修 diff。报告中的“没有发现”仅适用于列明的抽检范围，不代表无缺陷或形式化证明。

建议按下面的依赖顺序选择后续工作，而非把所有维护线索同时变成施工队列：

| 顺序/触发 | 主线程裁决 | 后续最小边界 |
|---|---|---|
| 优先：生成器合法输入失败 | [SG-01](05-source-generator.md) 的固定生成参数遮蔽领域参数，有限编译反例已复现 CS0452 | 仅修生成名字避让及交叉回归，保持 history、DTO、body 与公开签名 |
| 同一生成器维护期 | SG-02 的逐版本缺版循环无早退、无循环内取消，错误输入成本随声明版本增长；SG-03 是 helper 碰撞诊断漂移，最终 C# 仍拒绝 | 缺版有界失败；按实际生成路线检查保留名字，不扩大到生成器重写 |
| 已有具体工作量反例 | [R03-01](03-restoration-cache-and-lifetime.md) 的无缓存/小预算重复 batch 解码已用计数复现 | 操作内物理读取复用；保完整 source/head/prior/角色检查，说明临时驻留边界，不建长期 DTO cache |
| 下次隔离输出/打包前 | [F06-1](06-build-history-and-delivery.md) 的包工具路径硬编码是事实；错包/失败后果尚待独立 checkout 的 pack 实验 | 跟随真实输出或明确拒绝不支持配置；验证 nupkg 内 SG/Build 实际字节 |
| 下一次基础包门禁维护 | F06-2 是验收证据债，F06-3 是同 checkout producer 并发边界 | 补实际输入字节闭环；准备/pack 先串行，不把不同 feed 目录当构建隔离 |
| 大图保存/长 Event 尾成为负载时 | F1 的容量检查时序可提前；F2/R02-01 分别是重复完整图资格与重复历史回扫，两者不能混称磁盘 I/O | 前置纯计数限额；分别测图资格/分配和历史描述访问，不能删除必要校验 |
| 有实际修改需求再做 | 安装 Action 协议、容器升级重复、selected/origin view 重载、诊断上下文均有定位，但尚不足以启动广泛重构 | 随触碰处做局部收束；R02-02 的自定义 MemoryManager 重入仍是未复现候选 |

没有证据支持为了 SOLID 拆程序集、添加通用事务/容器框架、重开兼容层或 strict preflight。手写 allocator 的跨调用 singleton 问题属于已明示的 provider 合同边界，未立为产品缺陷。

### 已实施的小修

- `src/DurableGraph/PACKAGE.md`：退出已删除的四个 Shared API，明确创建返回 BranchInfo、单独 OpenWorkingCopy，以及当前 P0R3/P0G2 布局。
- `src/DurableGraph.Persistence/README.md`：旧隔离分支/待合入导航改指最终交付验收。
- `src/DurableGraph/Runtime/Binding/StateValueBinding.cs`：只补 IStateOps 的完整表示比较、PrepareDelta 一致性、拒绝 no-op 子 patch 和 prior 不变的 XML 合同。
- `src/PROJECT-STATE.md`：接入本轮报告入口，修正当前 CommitKind 名称和实际依赖边。

上述修改不改变公开签名、运行行为、持久格式或依赖 pin。生成器、性能及包脚本建议未实施。

### 本轮执行证据

构建由主线程串行执行，根回归统一运行；两个反例程序集在各自结果目录执行，不并行写共享 bin/obj。当前固定 Storage 六包由 `eng/Prepare-Storage.ps1` 校验；使用任务级 NuGet.Config 进行 package-mode 根构建和回归。这不是新 DG 普通包消费者资格，也不推导掉电或任意外部破坏后的恢复能力。

根 Release Rebuild 通过（0 warnings / 0 errors），完整根回归 **2995/2995** 通过、0 跳过：Persistence 1101、Serialization 133、Storage 36、Runtime/Generator 1725。此次运行以当前源码为准，不引用旧交付测试计数。

另外临时编译运行 **9 项观测见证全部符合预期**，意义是复现问题，不能解释为缺陷已修复：

| 见证 | 本轮观察 |
|---|---|
| 泛型参数遮蔽 | 支持输入 `Box<TState0> where TState0 : class` 无 DG 错误，但生成文件出现 10 条 CS0452 |
| 缺版诊断扩张 | Family Version=32、空 history，得到 31 条 DG0014；未执行极端大版本 |
| 普通 Family helper 碰撞 | 无 DG 错误，生成文件出现 CS0102 |
| batch 解码，N=16 | 预算 0/1024 时资格阶段 17 次，枚举后累计 33 次；默认 8 MiB 时均为 1 次 |
| batch 解码，N=32 | 预算 0/1024 时资格阶段 33 次，枚举后累计 65 次；默认 8 MiB 时均为 1 次 |

计数不是磁盘 I/O、业务延迟或峰值内存测量；默认预算超大 batch 的后果仍是源码推断。根回归通过也不包含这些临时反例，二者分别记录。

两份观测源码分别保留在 [GeneratorProbe.cs](probes/GeneratorProbe.cs) 与 [RestorationCacheProbe.cs](probes/RestorationCacheProbe.cs)，复现步骤见对应报告。它们断言本轮观察到的问题行为，不能直接当修复后长期回归；运行后的临时 tests 副本已删除。最终工作树构建结果见 `final-build.log`。

依赖身份：当前 S 为 `0.1.3-dev.20261019.g70b406c`，完整来源 `70b406cd8d1117733eee81a33e7f978399aa98b2`；Prepare 核六包 feed/manifest，另核六份缓存原包与 feed SHA256 相同，以及 Persistence.Tests 输出六份 DLL 与选定 cache 相同。没有新增 DG pack 或普通消费者资格。

本地证据：`restore.log`、`build.log`、`tests.log` 与四份 TRX；`probe-build.log`、`generator-probe.log`/`generator-probe-results`、`cache-probe.log`/`cache-probe-results`；`storage-validation.json`、`storage-cache-hashes.json`、`storage-output-hashes.json`。

收尾：移除临时 tests 副本后，根 Release build 再次通过，0 warnings / 0 errors（`final-build.log`）。11 份报告/活动文档的 432 个本地链接目标均存在；本轮没有更改既有标题/锚点，新导航链接不含手写标题锚点。集成 `git diff --check` 通过。本轮交付为 7 份独立报告、此总览及 2 份持久观测源码；既有 `maintenance/2026-10-20.md` 保留，未遗留临时 tests 文件。

本地临时日志和实验放在 `.artifacts/maintenance-20261020/`，持久结论写入本目录；本轮不提交或发布。
