# DB-070：读取放大阈值的默认选择

> 状态：已实施并验收。2026-09-12。用户批准将 ReadAmplificationThreshold 的生效默认值改为 5。
> 前置：[DB-069](0069-incremental-save-baseline.md) 已消除受控热保存的重复对象链读取；测试策略已独立固定。

## 1. 问题、选择与验收

在缺少真实用户工作负载时，为偏重细粒度保存与历史保留的基础库选择一个可解释的初始默认值，
并给出偏向冷读、偏向存储的典型调整方向。

- EventHistory 的有效默认由 `{3,5}` 改为 `{5,5}`；只改变阈值，保留 5% 可选 Base 软预算。
- 选择含义：在稳定小 Delta 的长期模型中，接受约 25% 的额外 Base 摊销 payload。
- 最小验收：省略/null 策略继续走新的默认值，显式覆盖不跨调用继承；原有固定 `{3,5}` 策略测试保持；
  根 README 的配置代码用本次源码打出的真实 PackageReference 包执行。
- 不改变候选排序、预算、强制 Base、持久格式或发布行为；不把模型估算写成产品性能保证。

## 2. 策略量与模型

当前实现以 B 表示当前完整 Base payload，H 表示已有对象链实际累计 payload，D 表示本次 Delta payload 上界。
Update 在 `H+D > LB` 时产生可选 Base 动机，NoChange 在 `H > LB` 时产生动机，等号没有动机。
之后还要经过可选预算筛选，因此 L 不限制实际冷读放大的最大值。
Insert、必须重写，以及 `B <= D` 的 Update 独立选择 Base。
实现权威见 [ReadAmplificationBaseBudgetPolicy](../../src/DurableGraph.Persistence/ReadAmplificationBaseBudgetPolicy.cs)。

以下是解释选择的近似模型，不是实验测量。假设单对象 B 大致稳定，每次更新的实际 Delta d 小且稳定，
连续运行足够长，预算不推迟 Base，冷重建在周期内均匀取样。
两次 Base 之间约积累 `(L−1)B` 的 Delta，间隔约为 `(L−1)B/d` 次更新。

令 ε 为相对持续写 Delta 的额外 payload 比例，W 为总写入倍率，R 为平均重建 payload/B：

\[
\varepsilon\approx\frac1{L-1},\qquad
W\approx1+\frac1{L-1},\qquad
R\approx\frac{L+1}{2}.
\]

“全 Delta”是初始 Base 之后持续写 Delta 的分析参照，不是库承诺始终能够选择的存储模式。
该模型忽略初始 Base 的有限历史影响、Base 替代当次小 Delta 的修正、严格大于与离散更新/D 上界的偏差，
以及 frame、map、Journal 等外围成本。L=1 不适用这个小 Delta 渐近公式。
事件快照不安装 State 基线，图内对象变化频率不同，实际整库结果也不能直接由单对象模型外推。

在容忍度边界取等号，消元得到 `R≈1+1/(2ε)`；以 L 为横轴，写入开销递减而平均重建量线性增长。
[独立 HTML 函数图](../research/read-amplification-tradeoff/index.html) 可离线查看、拖动联动或导出 SVG，
公式及使用说明见[图页 README](../research/read-amplification-tradeoff/README.md)。

## 3. 为什么选择 5

收益递减本身不指定唯一最优点。上面的关系 `ε(R−1)≈1/2` 是平滑双曲线，
换成两轴对数后成为直线；以曲率或视觉肘点选值，需要先选择坐标尺度和考察范围。
[Kneedle 原论文](https://lass.cs.umass.edu/papers/pdf/simplex11.pdf) 提供归一化后的肘点检测方法，
也明确其启发式性质。多目标取舍还需权重或约束；
[Boyd / Vandenberghe §4.7.5](https://web.stanford.edu/~boyd/cvxbook/bv_cvxbook.pdf#page=198)
将权重比解释为目标之间的交换率。

当前选择采用可说明的容忍度：先取 `ε≈25%`，再选满足 `1/(L−1)≤ε` 的最小整数 L，得到 5。
这保留了作者对存储与冷读的偏好，但把偏好表达成明确代价，方便后续负载证据修订。
25% 本身是工程选择；它不是与现有 `BaseBudgetPercent=5` 等价的另一种写法。

若已有可用成本数据，令 x=L−1、a 为单位追加/保留成本、b 为单位重建成本、r 为每次同对象更新对应的有效冷读次数，
与阈值有关的成本近似为：

\[
C(x)\approx\frac{ad}{x}+\frac{rbB}{2}x,
\qquad x_*\approx\sqrt{\frac{2ad}{rbB}}.
\]

它与[经济订货批量 EOQ](https://ocw.mit.edu/courses/esd-260j-logistics-systems-fall-2006/120bf3cdc3e9b1947ae2a8d0a264d901_lect7.pdf)
的固定成本摊销/累积成本取舍同形。在该模型中，x 偏离最优为 t 倍时，相关成本比为 `(t+1/t)/2`：
偏至 1.5 倍约增加 8.3%，偏至 2 倍或一半增加 25%。这支持寻找稳健区间，不能证明任意默认值都足够好。
“几百次保存才冷读一次”仍不足以确定最优阈值，还取决于同一对象的 d/B、更新/冷读频率及逐链项处理代价。

## 4. 典型配置与预期方向

以 L=5 为比较基准，以下数值均沿用 §2 模型：

| L | W：写入/全 Delta | 相对默认写入 | RB：平均重建 payload | 相对默认重建量 |
|---|---:|---:|---:|---:|
| 3 | 1.5× | +20% | 2B | −33.3% |
| 5 | 1.25× | — | 3B | — |
| 10 | 约 1.111× | 约 −11.1% | 5.5B | 约 +83.3% |
| 11 | 1.1× | −12% | 6B | +100% |

用户指导选择 3/5/11：L=11 恰好表达约 10% 的额外 payload，平均重建量相对默认翻倍。
从默认改为 3，重建量减少三分之一，代价是总写入 payload 增加五分之一；
额外 Base 部分从 25% 增至 50%，不能把它与“总写入增加 50%”混淆。

公共调用方式及可执行示例集中在[根 README](../../README.md#调整保存策略)。
覆盖只对一次 CreateBranch/Commit 生效；后续省略参数不会继承覆盖，固定策略应逐次显式传入。
参数类型本身不提供可用的无参默认值；默认属于 EventHistory 的 nullable 参数回退路径。

真实读取速度还受链项数、map 回溯、缓存与解码影响。正常 append-only 存储保留旧历史；
改变 L 只影响后续选择，不回收历史，也不改变已发布旧版本的链。
DB-069 后热保存不再逐链读取，但全量 Base 准备仍在，因此少写 Base 也不等于同比例降低准备 CPU。
后续校准的触发条件与测量方向只在[路线图](../DurableGraph-research-roadmap.md)维护。

## 5. 实施与验证证据

- 默认入口：[EventHistoryRepository.DefaultPolicy](../../src/DurableGraph.Persistence/Repository.cs)；
  参数含义及默认调用合同写入[包内 XML 文档](../../src/DurableGraph.Persistence/ReadAmplificationBaseBudgetParameters.cs)。
- 测试共享 [TestSavePolicies.Baseline](../../tests/Shared/TestSavePolicies.cs) 保持 `{3,5}`；
  [默认/覆盖集成轨迹](../../tests/DurableGraph.Persistence.Tests/EventHistorySavePolicyTests.cs) 继续动态读取默认权威，
  对照显式/省略调用的真实 Base/Delta/NoChange 选择，并冷重开验证状态。
- [README 包探针](../../experiments/PackageConsumerProbe/Run-ReadmeQuickStartProbe.ps1) 增加对配置代码块的原文执行。
- `dotnet build DurableGraph.slnx` 通过，0 warning/error；串行完整回归 StateStore 732/732、
  Runtime/Generator 1,571/1,571，共 2,303 项，无失败或跳过。已有测试无需修改。
- 真实包验收：`Run-EventHistoryRecoveryProbe.ps1` 新打九包，通过默认保存、热/冷 Pending 恢复、
  重复恢复不重放及只读 Event 浏览；实际 nupkg XML 已核对新默认、当次覆盖与 3/5/11 指导。
  `Run-ReadmeQuickStartProbe.ps1` 复用同版 feed，通过 README 两进程重开、V2 Upgrade/history 保留、浏览、
  新配置代码原文执行与 Clean/Verify；新增保存得到 `Hp=96`、无 PendingEvent。
  忽略目录中的两次产物为 `experiments/PackageConsumerProbe/obj/event-recovery-20260912074304-64016-d6dfb873`
  与 `experiments/PackageConsumerProbe/obj/readme-20260912074425-55816-c638b4d2`。
- HTML 在本机 Chrome 无头浏览器验证已知点、小数/边界输入、联动、独立 SVG 导出、移动布局及离线运行；
  模型数值已独立复算，图不声称实测性能。受影响文档检查本地链接、新增锚点与 diff 空白。

协作落款：Codex 主线程 `01a09448-c5d7-7113-8fb9-8eb5bfa30267`，2026-09-12。
分析承接 DB-069 后的默认值讨论；此次只选择默认值并补充配置指导。
