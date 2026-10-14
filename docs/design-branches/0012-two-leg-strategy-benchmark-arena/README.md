# DB-012：TwoLeg 多策略 Benchmark Arena 的最小边界

> 历史迁移（2026-10-14）：原路径 `docs/design-branches/0012-two-leg-strategy-benchmark-arena.md`；来源修订 `8ef9fca`，正文保留原时期语义。
> 适用范围：冻结的 internal-track vertical proof；正式候选赛道、物理 artifact 离线验真与独立发布未启动，不是 MVP 插件机制。 今日事实从 [PROJECT-STATE](../../../src/PROJECT-STATE.md) 续接。

> 状态：Deferred；internal-track vertical proof 已实现并冻结
>
> 创建日期：2026-08-31
>
> 冻结边界：静态链接、确定性、workload/product-first 的 Arena 已可执行；正式候选赛道、任意物理
> artifact 离线验真和独立发布均未启动。2026-09-02 起 TwoLeg 路线暂停。

## 目标

将 `TwoLegRotationProbe` 变为可由多个独立 C# project 消费的实验赛场：

1. organizer 冻结 workload、bootstrap、horizon、settlement 与评价协议；
2. candidate 自主组织算法、状态和内部类型，不要求继承基类或实现策略接口；
3. 每个 `{workload, strategy}` 使用独立 Store fork；
4. Arena 独占 exact planning/admission/apply、terminal settlement、合法性验证与 canonical raw metrics；
5. typed non-admission 不转成罚分，首阶段不建立 scalar score、排行榜或产品默认策略。

这不是动态插件 ABI、恶意代码沙箱或 submission service。

## 已实现的依赖形状

```text
TwoLegRotationProbe.csproj                    Arena class library
├─ public StrategyStepViewV1 / StrategySelectionV1
├─ public StrategyRunContextV1 / StrategyRunProductV1
├─ internal model/planning/admission/apply/evaluator/runner
└─ workload corpus + canonical report

Baselines/TwoLegRotationProbe.Baselines.csproj -> Arena
└─ Adaptive (3,5%) / (4,4%)

Tests/TwoLegRotationProbe.Tests.csproj -> Arena + Baselines
```

Arena 不引用 Baselines。Tests 的 `InternalsVisibleTo` 只用于保留深层白盒探针，不是 candidate contract。
SDK recursive compile globs 显式排除 `Baselines/`、`Candidates/` 与 `Tests/`，避免嵌套项目被 Arena 重复编译。

首轮不建立独立 Contracts 程序集。只有第二个 engine/runner、独立发布/版本化、public surface 失控或真实
依赖环出现时才重启。

## 为什么不是固定 selector 接口

早期方案把唯一入口固定为：

```text
PolicySelectionV1 Select(PolicyStepViewV1 input)
```

最新需求只冻结 workload 与产物形状，candidate 内部可以是静态函数、对象、状态机、组合 planner 或其他
结构。因此当前只要求 organizer 提供一个 whole-run adapter：

```text
StrategyBindingV1(identity, caseIdSuffix, fresh-per-case executor factory)
```

这是集成适配器，不是候选程序集必须公开的接口。factory 为每个 case 建立新的 whole-run executor
边界，避免复用捕获实例状态；它不是对 static mutable state 的恶意代码沙箱，正式开赛前仍需 repeat/order
determinism gate。两个现有 Baselines 恰好以逐步纯选择实现，但这只是
convenience implementation；后续 candidate 可以在一次 run 内持有私有状态或组合多个内部算法。

为保持 online 公平性，`StrategyRunContextV1` 逐步开放当前 Save，仍不暴露 future trace、step index、
workload identity、candidate feasibility、raw metrics、absolute addresses 或 apply 后观察。跨 Commit stateful
策略可以存在于 whole-run delegate 的局部实例中，不需要 Arena 预先冻结 lifecycle interface。

## 冻结的 workload view

`StrategyStepViewV1` 是 ObjectId-sorted、不可变、payload-only 的当前 Save 投影：

- `Insert / Update / Remove / NoChange`；
- post-live graph 全 Base payload 总量 `G`；
- post-live A-dependent evacuation Base payload 总量 `E`；
- parent 是否仍有 Previous debt；
- source Base payload 与 current reconstruction payload `H`；
- result Base payload `B` 与 Update Delta payload `D`。

| Kind | Source facts | Result facts |
| --- | --- | --- |
| Insert | 无 | `B` |
| Update | Previous-dependent、source Base、`H` | `B`、`D` |
| Remove | Previous-dependent、source Base、`H` | 无；不进入 `G/E` |
| NoChange | Previous-dependent、source Base、`H` | 同状态 `B` |

`Remove(A-debt object 10) + Insert(object 20)` 是必保留反例：`HasParentPreviousDebt=true`，但 `E=0`。
test-local parent-debt treatment 与 Adaptive 可产生不同 target，因此不能用 `E` 代替 pre-Save parent debt。

`StrategySelectionV1` 完整描述当前 canonical toolkit 所需 action：

- Stay-B 或 Rotate-C target；
- Stay 的全部 Update Base/Delta 与 unchanged migration membership；
- Rotate 的全部 B-contained Update Base/Delta 与 B-contained NoChange Base membership。

## 产物边界

裸 `RbfFileStore` 不足以独立产生四个指标：

- `P` 需要 outer Commit 分组；相同 Frames 分成一个或多个 Commit 会得到不同 Peak；
- `F` 需要各 accepted Revision 的 source/result Current scope/tail 时间线；
- `R` 需要最终 PublishedRevision；
- 最终 logical state 相同也不能证明每个 Save prefix 正确。

当前 `StrategyRunProductV1` 因而包含：

- final in-memory `RbfFileStore`；
- 每个 realized workload Commit 的 ordinal、selected target 与 result checkpoint；
- final checkpoint；
- typed termination；
- terminal-settlement realized Revision count（仅内部认证/诊断，不进入 canonical report）；
- **不包含 candidate 声明的 metrics**。

本轮采用 Arena-certified product：只有 `StrategyRunContextV1` 能构造 product；每次 `Commit` 仍通过唯一
normalizer、pair evaluator、estimator、admission 和 applier，`Complete` 由 Arena 执行 canonical terminal
settlement并产生 evaluator outcome。runner 只接受 originating context 返回的同一 product，并对 admitted
final state 做 full-trace replay 验证。因此当前产品足以进行内部赛道检查，但不是任意 artifact 的通用验真器。

## 若要接受任意 hand-built artifact

候选若以后需要绕过 canonical execution toolkit、直接构造 Store/ledger，须先增加新的独立切片：

1. deterministic exhaustive Frame/layout enumeration；
2. bootstrap prefix 不变与 append-only、无退休文件回写；
3. ledger 对全部新增 Frames 的无遗漏/无重复覆盖；
4. 每个 checkpoint 的 scope、ticket、tail、exact layout 与 PublishedRevision 可读性；
5. 每个 workload Commit 末的 prefix logical replay；
6. OVD、relative address、Delta lineage 与 final reconstruction closure；
7. Arena-owned terminal settlement；
8. 从已验证 tail/checkpoint ledger 离线重算 Wworkload/Pworkload/F，并对每个 workload head 重算累计 R/L；terminal writes、closed-horizon total 与 terminal head 只作内部诊断。

在这些 gate 实现前，不公开 product constructors，也不把候选声明的 capacity/rejection 当成官方结果。

## 不可约评价语义

- 同一 source fixture、expanded trace、horizon 与 settlement；
- 每 case fresh Store fork；
- same action grammar、exact estimator 与 read schedule；
- rejected selected choice 不 fallback，失败不变异 Store/cursor；
- workload 完成后统一 terminal settlement；
- 只有 admitted outcome 才有 canonical raw metrics；效率量为 workload-only W/P 与 R，F 是 closed-horizon
  容量 guardrail，并保留 Delta/Base payload references 与 R 的 L 分母；
- strategy identity/version 与 deterministic rerun；
- candidate 无权提交 official metrics。

取消其中任一项都会制造具体不公平：fallback 泄漏 feasibility oracle，缺 settlement 会失去
closed-horizon admissibility、final reconstruction closure 与 F guardrail 证据，复用 Store 污染 tail，
把 rejection 数值化则混合不同失败语义。terminal settlement 继续证明可闭合性、
final reconstruction closure 与 capacity admission，但它的人工收尾写入不参与策略 write comparator。

## Workload suite 与策略矩阵

`BenchmarkV1Corpus.Create(strategyBindings)` 由 organizer 显式形成 `workloads x strategies`；Arena 不持有
concrete strategy registry，也不 reflection/scan assemblies。当前 manifest schema 仍把 strategy identity
保存在 `selectionProfile` 字段中，以保持旧 writer/hash；只有出现分批 report 缓存/比较 consumer 时，才拆成
strategy-neutral suite identity 与 per-strategy run identity。

多样性按因果轴而非 seed 数量定义：

- update locality / hot-cold cadence；
- Base/Delta ratio 与 object size skew；
- lifetime、Remove 与 churn；
- A-debt share / migration pressure；
- rotation threshold band；
- grouped burst；
- horizon/epoch phase；
- 独立于 performance corpus 的 typed capacity qualification；
- 仅在 canonical outcome 反转时升级的 Frame provenance；
- 已选 `after-every-workload-save-cold-load/1`：每个成功 workload outer Save 后独立冷载一次；
  per-Save vector 与 terminal T 内部保留，canonical report 只发布累计 R/L。

第一轮使用同一冻结公开 suite。依据第一轮结果改进、或准备宣称扩展前沿的第二轮 candidate，应在实现
冻结后接受一个新命名 workload/family 的 post-freeze validation。这不是 hidden test 或总分。

## 当前证据

- Arena、Baselines、Tests 三程序集保持单向依赖；两个 active executor 位于独立 Baselines assembly；
- corpus revision 18 由十八条可调 trace 与两个 Adaptive profiles 组成，共 36 admitted cases；
  `active-hundred-mixed-cold-debt` 用 aligned channels 将原 100-object 活跃轨与 20 个 bootstrap-only
  冷对象叠加；两 profile 的 64 个 workload Saves 全为 Stay，末端冷对象仍从 A 重建；另有
  `oversized-cold-nochange-tiny-clock` 对抗 trace，以 10000B stable A-dependent NoChange 和 1B 活跃
  clock 隔离软 Base 预算逃逸：旧实现的 `Pworkload=10052B`，修复后两个 profile 均 `<10000B`；
- 两个 profile 保持原 identity ID 与 case suffix，但行为变化使 component version 均升为 v2；历史 v1
  manifest/result 只能作为旧算法证据，不能与 revision 18 当前结果混同；
- runner/report 锁定 fresh-per-case executor、typed termination、Arena-owned settlement/metrics、
  workload-only P、累计 R/L 与 Delta/Base references；
- `active-hundred-mixed` 的两个 profiles 共享
  `Delta/Base references=67206/165606` 与 `L=273804`；修复后 `(3,5%)` 的 `W/P/F/R` 为
  `116424/2128/58460/3501092`，`(4,4%)` 为 `114756/2104/91380/3754140`；
- no-migration 与 paced-one-debt profile 已不再承担 write baseline：完整旧实现和 64-case 结果保存在
  Git tag `research/no-migration-paced-baselines-20260901`，主线使用 strategy-independent references；
- workload 不再逐条 hash/vector 锁死；只有 candidate 白盒复审暴露具体 blind spot 时才添加或调整最小 trace；
- typed capacity tests 继续证明 selected rejection、no fallback、zero mutation/no metrics；
- Arena-certified product 暴露 Store、workload receipts、final checkpoint 与 termination，不暴露 candidate metrics。

## 冻结时未执行的扩展

- stable A-debt 的预算内 progress、Ready/Should hysteresis 与 hotness/marginal-cost candidate 未实现；
- determinism/order/artifact qualification 没有冻结正式 competition packet；
- hand-built physical Store/ledger 的 untrusted artifact validator 未实现。

这些不是当前 backlog。只有 TwoLeg 明确恢复并出现真实多策略或 direct-artifact consumer 时才重访。

## 明确暂缓

- plugin discovery、MEF/DI、assembly scanning 与 stable ABI；
- CLI/parser/artifact database/submission service；
- weighted score、leaderboard、跨 workload 总分、自动搜索/进化与 `/goal` optimizer；
- 独立 Contracts package、NuGet、compatibility shim、DLL hash；
- hostile-code sandbox、防作弊与隐藏测试；
- Frame-aware V2 facts、feasibility feedback、fallback/retry；
- 在没有实际 candidate consumer 时开放全部 RBF builder/estimator surface。
