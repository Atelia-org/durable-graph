# DB-049 首轮重放结果

后续针对性退化与边界控制见 [白盒实验](WHITEBOX.md)；本文保留首轮普通轨迹的证据。

2026-09-09，Release / .NET 10.0.5 / Windows 10.0.22000 / x64，16 个逻辑处理器，workstation GC。
代码基线记录为 `55045b4+working-tree-changes`，即本片待提交实现；完整 JSON 另含实际程序集 SHA-256。

```powershell
./experiments/ListDeltaReplayProbe/Run-Probe.ps1 -Counts '32,512,4096' -Repeats 3 -DiffRepeats 5
```

固定 seed 49001、X=8、Y=5，每轨迹 14 次编辑加初始保存；wide inline 的最大 Count 为 512。
共 **126 个独立测量仓库、1,890 个历史 Revision**，另有 60 次 warmup Commit。
所有历史图、最新重开、引用身份/循环和候选 Delta roundtrip 均通过，包括策略最终选择 Base 的候选。
之前 count=32 的 smoke 另验证了 15 个测量库、225 个 Revision。

原始产物保留在 ignored 目录：

- 正式：`obj/run-20260909082715-47256-4f0b463a/{report.json,steps.csv,summary.md}`。
- smoke：`obj/run-20260909082004-42552-0fccb2f7/`。

这些数字是合成轨迹的测量，不是通用性能承诺。报告中的 Position 使用同一 codec 2，而非旧格式 reader。

## 保存尺寸

下表是每轨迹 **14 次编辑实际写入的全部 ObjectVersion payload 字节**，不含初始保存，三次重复相同。
它包括实际 Base/Delta 选择及 child 更新，不等于候选 Delta body 或完整仓库文件大小。
ObjectId/membership/共享帧与 Schema/publication 开销在 JSON 的文件尺寸中另列。

| Workload | Count | Position | LocalResync | BoundedMyers |
|---|---:|---:|---:|---:|
| unique int | 4,096 | 81,552 | 32,858 | 32,858 |
| alternating int | 4,096 | 45,220 | 16,678 | 16,665 |
| 重复引用与 null | 4,096 | 36,971 | 4,345 | 4,345 |
| `Cell<int>` | 4,096 | 172,482 | 60,024 | 60,024 |
| `Wide<Cell<int>>` | 512 | 174,602 | 62,186 | 62,186 |

单次 unique-int 头插见证（Count=4096）：Position 准备的 Delta body 为 16,204 字节，策略最终选 Base，
实际对象 payload 8,137 字节；LocalResync/Myers 的 Delta body 均为 10 字节，实际对象 Delta payload 均为 17 字节。
该见证说明未变后缀被区间复用；不代表整个 StateRevision 只写 17 字节。

两个非位置 writer 的实际写入在多数所列样本相同，不代表所有候选脚本相同。
例如重复引用轨迹的候选 body 合计为 LocalResync 12,398 / Myers 9,347 字节，而策略后的实际总量均为 4,345 字节。
候选总量包含未写入的候选及 NoChange body，只用于解释准备成本。

## 耗时与分配

单位为每轨迹 14 次编辑的隔离 Diff 时间合计（ms，三次仓库重复的中位数；每个 pair 内另取 5 次样本中位数）。
该测量只包含产品 PrepareDelta，不包含 Capture、PrepareBase 或文件写入；完整 Commit 单独计量并保留在报告。

| Workload | Count | Position | LocalResync | BoundedMyers |
|---|---:|---:|---:|---:|
| unique int | 4,096 | 2.341 | 1.849 | 1.316 |
| alternating int | 4,096 | 2.014 | 0.792 | 0.674 |
| 重复引用与 null | 4,096 | 2.211 | 0.430 | 0.531 |
| `Cell<int>` | 4,096 | 6.908 | 3.098 | 2.639 |
| `Wide<Cell<int>>` | 512 | 1.677 | 0.787 | 0.855 |

时间优势并非单向。重复引用轨迹的隔离 Diff 分配合计，LocalResync 为 153,856 字节，Myers 为 1,261,192 字节；
unique int 则为 4,873,552 / 4,998,176 字节。这里只统计当前线程 managed allocation，不是峰值 scratch 或整个进程内存。

完整 Commit 明显比隔离 Diff 更慢且波动较大。例如 unique int 的 LocalResync 编辑总时间为
1,069.506 ms（范围 1,064.346–1,140.722），Myers 为 913.642 ms（811.640–1,653.245）。
完整计时包含 Capture、全量 Base、规划读链及 append/flush；本实验没有逐项分解，不能把差额全部归因于 SSD 或 flush。
仅三次重复且范围重叠，不据此宣称某算法具有稳定的整体 Commit 速度优势。

## 当前选择与限制

保留 **LocalResync 默认值** 和显式 Myers 选择。已观察到两个候选都能改善位置错位写入，
Myers 部分候选更紧凑或更快，LocalResync 某些场景更省分配；没有必要在本片强行删除其中一个。
完整14个 workload/规模组中11组实际对象 payload 相同，另3组 Myers 少写12/13/31字节；LocalResync 的隔离 Diff 总分配在14组均更低。
Position 留作同格式对照。算法不进入表示 ID，后续调整选择不要求改变 reader。

冷读速度仅作为低优先级背景观测；fresh handles/rebind 不等于清空 OS page cache。
本轮没有业务 key、哈希 matcher、局部 New/Patch 竞价、追踪容器或峰值内存 profiler。
所有元素微改再插入仍可能缺少 exact 锚点，正确回退不承诺高压缩率。
更大规模/更多重复与真实业务轨迹是后续调参的依据，不把这组固定合成输入当作长期唯一排名。

完整施工验证见 [DB-049](../../docs/design-branches/0049-list-range-delta-and-matcher-trial-slice/README.md)，
重跑和计量边界见 [README](README.md)。
