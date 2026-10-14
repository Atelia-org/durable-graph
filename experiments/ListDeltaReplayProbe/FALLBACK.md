# Bounded List matcher fallback research

设计选择与实验边界见 [DB-050](../../docs/design-branches/0050-list-fallback-research/README.md)；本文件保留复现与证据。

后继 [DB-051](../../docs/design-branches/0051-bounded-list-delta-competition/README.md) 已实施完整 Local 基准与限长竞争，
使用独立搜索预算，新默认实测见 [ADAPTIVE.md](ADAPTIVE.md)。下面的共享/半预算实验结果保留原历史条件。

本轮比较 Myers-first 失败后转 Local，以及 Local-first 窗口停滞时单次 Myers 救援，
共用原有总比较额度，并与三种原算法对照。协调器留在 Probe，产品公开选择和 codec 不变。

## 研究结论

**LocalThenMyers 是更值得继续打磨的候选，但本轮不提升为产品默认或新增公开策略。**
它能用早期窗口失败信号修复已知坏例子，通常沿用原 Local 路径；不过搜索成功并不保证 bytes 改善，
反证已发现 207 B → 1455 B 的退化。后续应解决候选配对的字节代价验收。

## 复现与验收

```powershell
$priorTiering = $env:DOTNET_TieredCompilation
try {
    $env:DOTNET_TieredCompilation = '0'
    ./experiments/ListDeltaReplayProbe/Run-Probe.ps1 -Suite fallback `
        -Counts '32,512,4096' -Repeats 3 -DiffRepeats 9 -Seed 49001
    ./experiments/ListDeltaReplayProbe/Run-Probe.ps1 -Suite fallback `
        -Counts '32,512,4096' -Repeats 3 -DiffRepeats 9 -Seed 49002 -NoBuild
} finally {
    $env:DOTNET_TieredCompilation = $priorTiering
}
```

2026-09-09，Release / .NET 10.0.5 / Windows 10.0.22000 / x64 / 16 logical processors / workstation GC。
每轮 **220 对 frozen states × 5 策略 × 3 重复 = 3300 条验证测量**；两 seed 都通过。
每轮包括 196 对普通轨迹、10 对旧白盒及控制、14 对反证。更换 seed 只改变原脚本的随机位置，
固定白盒/反证不变；这些重复不应宣称为 440 种独立业务分布。

每次启动另完成 **18,235 次计划检查、3,815 次 body 检查**：512 随机重复值序列对、9 个命名边界、
7 种预算。检查独立比较计数、实际扣账、baseline 坐标/计数一致、完整目标覆盖、单调源坐标、
合并规范、编码确定性、共享 Apply、NoChange、输入不变性、非对称游标和已证明的无匹配尾段截断。

根 solution build 与 probe Release build 0 warning/error，根 **1545 tests** 全通过（Runtime 788、
StateStore 499、Serialization 103、Storage 155）。新增 16 个 kernel 边界测试。
原公开算法的 whitebox 模式另重跑 15 个真实仓库、30 Revision，原有坏例子及控制仍按原行为通过。
实现与证据经独立 agent 审查。

正式产物：

- Seed 49001：`obj/run-20260909095324-8864-06c263ef/{fallback.json,fallback.md}`。
- Seed 49002：`obj/run-20260909095434-31448-b884c125/{fallback.json,fallback.md}`。
- 先行 smoke：`obj/run-20260909094758-48048-d0bdfd80/`；无短尾截断、仅 65 项 Marker 的先行矩阵：
  `obj/run-20260909094837-44136-0ad72d3e/`。下面表格不混用这些旧候选版本。
- 原公开算法真实落盘回归：`obj/run-20260909095621-45924-628b7415/`。

正式 source baseline：`c1e618e+working-tree-changes`，两轮使用相同程序集；SHA-256：

| 程序集 | SHA-256 |
|---|---|
| Probe | `125896695F6CCC98215D31977EC2172A3595FB6B041DBD33FC16CB664EB5B9A2` |
| Runtime | `CD12321B8A7144552E790E0964838FE267EF3E852B0157E4A8CF17ED8BD71EF5` |

### 计量边界

所有策略拿到完全相同的、由实际 SG Capture 产生的 frozen pair；保留 List ID、布局与 preparation binding，
领域图的共享引用/循环在 Capture 两侧验证。各策略输出都经共同产品 writer/decoder 完整 roundtrip。

此模式**没有发布仓库**，不测整体 Commit，也不模拟不同算法导致的累计 H／Base 策略历史。
所有大小都是 **raw List body bytes**；`Settings` 中 X/Y 沿用通用 runner，但在该模式不使用。
既有真实落盘基准仍由普通与 whitebox 模式承担，不能拿本轮 raw body 宣称实际文件节省。

完整 Diff 包括匹配和一次 payload 准备；反射闭合、Capture、Base 编码、8 次预热、验证和 fingerprint 不计时。
matcher 另预热 16 次。计数使用不参与计时的 wrapper，计时使用真实静态 ops。
**三个 baseline 也经过实验协调器的诊断记账和 planFactory**，因此适合本轮内部比较，
不能当作未改动产品直达入口的绝对性能。NoChange 不调用 matcher，报告相应空诊断及零 matcher 成本。
分配是当前线程累计托管字节，不是峰值内存。以下时间为每组 9 样本中位数，再取三重复中位数。

## 已知坏例子：互补性可以兑现

Seed 49001、N=4096，raw Delta bytes：

| 操作 | Local | Myers | Myers→Local | Local→Myers |
|---|---:|---:|---:|---:|
| 头插 33 + 改末项 | 16236 | 47 | 47 | 47 |
| 分散插删各 65 | 770 | 16197 | 770 | 770 |

头插例中 Local→Myers 只先花 **1089** 次搜索比较就触发，Myers 再花 **4723** 次，
合计 **5812** 次额外比较，没有用尽原 B=69896。完整 Diff 从 Local 的 **0.643 ms / 1,605,960 B 分配**
降为 **0.0312 ms / 6056 B 分配**。这说明早于 payload 准备切换，确实能避免大量无益的元素编码。

插删各 65 例中 Local→Myers 不触发救援，沿用 Local 的 **4224** 次额外比较；
完整 Diff 为 **0.0502 ms / 9024 B 分配**。Myers→Local 先支付失败的 Myers 搜索，
再做 Local，总计 16609 次额外比较，**0.1215 ms / 50184 B 分配**，结果字节相同。

两候选都只进行一次 Myers 尝试、按实际用量扣除共享额度，没有每次切换重置预算。
头插 129+改末项、头插 33 加大量分散编辑等混合坏例子仍会同时失败；互补不是完整性保证。

## 普通轨迹：Local 先行更保守，但救援也有成本

每轮 196 个普通状态对，相对原 Local 的 raw bytes：

| 候选 | Seed | 更小 / 相同 / 更大 | 总 raw bytes | Local 总 raw bytes |
|---|---:|---:|---:|---:|
| Local→Myers | 49001 | 0 / 196 / 0 | 279364 | 279364 |
| Local→Myers | 49002 | 0 / 196 / 0 | 279337 | 279337 |
| Myers→Local | 49001 | 5 / 187 / 4 | 279313 | 279364 |
| Myers→Local | 49002 | 5 / 188 / 3 | 279304 | 279337 |

Myers-first 的几个普通退化只多 1–3 B，集中在 reverse；不能以总和更小掩盖逐例退化。
Local-first 在 Seed 49001 的普通轨迹中仅尝试 Myers **17 次，均未完成**；相比之下 Myers-first 调用它 165 次。
普通轨迹本来主要适合 Local，这些救援就是无收益开销：Local-first 分配合计约 21.19 MB，
原 Local 约 20.62 MB。两 seed 的完整 Diff 中位数求和分别约 8.86/8.88 ms，原 Local 约 8.54/7.93 ms。
这是合成样本的成本观察，不是固定百分比承诺或业务发生率。

已证明的短尾截断能消除部分误触发。例如 32 个 wide struct 均只改一字段时，
整个 32×32 笛卡尔积检查完即可确定无相等锚点：Local-first 仅比较 1024 次而非原 Local 耗完 4608 次，
相同 193 B Delta；完整 Diff 约 0.0141 ms，原 Local 0.0304 ms。
但 512 元素的相同形态不满足证明前提，仍有纯开销救援，不能将短尾结论外推。

## 成功救援也会更差：字节成本反证

构造宽 struct，绝大多数字段相同，只有一个小整数字段不同：

```text
prior   = [Marker, U1, U2, ..., Uk]
current = [U1', U2', ..., Uk', Marker]
```

每个 Ui' 都只是 Ui 的小字段变化，Marker 是唯一完全相等的值。
在本构造中所有元素之间也仅该小字段不同，所以按位置配对同样只需小 patch。
Myers 把 Marker 匹配到尾部后，前面的 k 个元素只能作为 New 完整写入。

| 总元素数 | Local | Myers | Myers→Local | Local→Myers |
|---:|---:|---:|---:|---:|
| 34 | 207 | 1455 | 1455 | 1455 |
| 48 | 291 | 2071 | 2071 | 291 |
| 65 | 393 | 2819 | 393 | 393 |

N=34 时两个候选都成功，原 207 B 变为 **1455 B**，接近完整 Base 的 1495 B。
它们匹配到了 1 个相等元素，原 Local 为 0；**匹配数更多与字节数更少并不等价**。
分配甚至从 Local 的 39,344 B 降至 18,736 B，说明 CPU/分配/写入并不总同向，不能只按一个指标判优。
N=48/65 的某些候选因为半预算不足而失败，碰巧保留了较小输出；提高预算反而可能暴露退化。

另一个限制是检测假阴性：`0100 → 1001` 的 Local 贪心路径不会窗口失败，只保留 2 个相等配对，
生成 13 B；Myers 有 3 个相等配对，生成 7 B。Local-first 不触发，就仍是 13 B。
此例展示未检测出的较差匹配；它与上述成本反证是两个不同问题。

## 后续研究的具体入口

1. 保留 Local-first 单次救援、共享预算和已证明的短尾截断为优先候选；先不扩大算法数量、全局配置或格式版本。
2. 研究**采用候选配对前的字节代价验收**，比较救援计划与实际可行的替代配对。
   Marker 反例涉及整段对应关系变化，单独给一个 New 或 Patch 定价不一定足够。
   不先承诺“完全零额外编码”，也不采用专门针对此反例的 literal 数量阈值；需测量成本验收自身的开销。
3. 同时保留已知坏例子、普通轨迹和反证。成功条件应包含写入、比较、分配及保证恢复，
   不能只消灭两个已知测试，也不要求逐例胜过所有策略后才能继续探索。

代码入口：[协调器](FallbackMatcher.cs)、[生成 DTO 夹具](FallbackFixtures.cs)、[性质检查](FallbackChecks.cs)、
[测量](FallbackTrial.cs)、[共享 kernel](../../src/DurableGraph/Runtime/Containers/ListDeltaMatcher.cs)。
此前证据：[白盒](WHITEBOX.md)、[普通落盘重放](RESULTS.md)。
