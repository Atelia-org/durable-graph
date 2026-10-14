# DB-050：有界 List 匹配回退研究

> 文档整理：2026-10-14；来源为 Git `8ef9fca` 中的 `docs/design-branches/0050-list-fallback-research.md`，正文保留当片合同与历史验收含义。
> 阅读范围：实验验证两候选并保留 payload 增大反例，未提升实验协调器为默认；DB-051 已放松共享/半预算和单次编码约束，旧数据只代表本实验条件。
> 当前能力与施工焦点见 [PROJECT-STATE](../../../src/PROJECT-STATE.md)；旧稿中的“下一步”不自动成为当前工作单。

状态：**Research / 实验已验证，尚未采纳新产品策略**。2026-09-09。

已实施的后继为 [DB-051](../0051-bounded-list-delta-competition/README.md)：采用完整基准与限长竞争，
放松本实验的共享/半预算与单次编码约束。本文及数据保留原研究条件，不直接提升实验协调器为默认。

问题：在 [DB-049](../0049-list-range-delta-and-matcher-trial-slice/README.md) 的共同区间 codec 上，
能否廉价识别 matcher 停滞、借另一种算法救援，并在 payload 准备前避免大量无益编码？
[此前白盒](../../../experiments/ListDeltaReplayProbe/WHITEBOX.md)给出了 Local 窗口外位移与 Myers 深度越界两种原因。

## 两个候选与已选实验约束

- **MyersThenLocal**：裁剪公共前后缀，Myers 获得一半额外比较额度；不完整时，Local
  使用实际剩余额度处理整个中间区域。Myers 失败不修改已有输出。
- **LocalThenMyers**：Local 首次完整窗口找不到锚点时，在尚未处理的尾段尝试一次 Myers，
  获得剩余额度的一半；成功则接续结果，失败则消费已查过的一个位置配对，继续 Local，不再次救援。
  已选 Local 前段保留，包括可能不理想的重复值锚点；不宣称找到全局最佳路径。

共同额度为 `B = min(1000000, 4096 + 8*(oldCount+newCount))`；扣除实际使用量，
切换不重置预算。公共前后缀扫描仍在额外预算外，与原算法一致。
Myers 深度 128、trace 上界不变，无时钟决定的结果，无多份完整 payload 竞价。
半预算只是此次实验常数，未增加用户配置。

一条已证明的剪枝：完整 WindowMiss 后，若两侧剩余长度均 ≤33，整个剩余笛卡尔积都已经比较过，
没有任何相等锚点。直接生成位置 gap，与继续两种搜索的输出相同；预算未查完或仅一侧短，不适用。

## 实施与验收范围

产品内部暴露可暂停 Local、独立 old/new offset 的 Myers，以及共享 body 的 planFactory；
候选协调器、诊断及试验在 `experiments/ListDeltaReplayProbe`。原三个公开算法、默认选择和 wire-format 不变。

最小验收：默认行为等价、坐标规范、完整覆盖、编码确定性、共享 decoder roundtrip、输入不变性、
独立 NoChange、独立计数与额度扣账一致。候选必须同时接受旧白盒、普通五类领域轨迹、双算法失败、
错位游标、原位替换、重复值、inline 小字段变化等反证；计时不作自动胜负门槛。

## 结论与后继

两候选都修复两个已知坏例子。Local-first 在两轮普通轨迹保持原 Local 的全部 body 大小，
但仍存在无收益救援，也有成功救援令 inline payload 明显增大的反例。
因此保留它为优先研究候选，尚不提升默认。匹配数与搜索完成状态不足以代表字节成本。

实现、度量口径、两 seed 数据与验证证据集中在[实验报告](../../../experiments/ListDeltaReplayProbe/FALLBACK.md)；
后继候选配对的字节代价验收在[路线图 §3.2](../../DurableGraph-research-roadmap.md#32-list-差分算法选型与设计)维护。
