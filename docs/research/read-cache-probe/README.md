# 读缓存隔离测量

服务于 [DB-067](../../design-branches/0067-owned-revision-read-cache-design/README.md) 的早期缓存机制研究，不在 solution 内，不是产品实现。
保留 [初稿](../read-cache.md)。DB-067 后续实施 append-only 普通 Store 与双数组二分表示；这里的脚本和记录冻结为树表示证据，不是新表示或生命周期接线的验收。新实现的可重跑测量从 [DB-067 §7](../../design-branches/0067-owned-revision-read-cache-design/README.md#7-实施顺序与验收) 进入。

## 重跑

此脚本依赖下面记录的旧 Storage 源码形状。当前产品已改为缓存实现，直接对当前源码运行会明确报 Source drift；不要删除这个保护或在产品树中回退源码。
重放历史实验时，在独立 checkout 恢复 DurableGraph 的 f68388f88ba09354e9fa90420dc2cf22b146b6cf 与 sibling Atelia 的 539088414686f51a210798b35ee56d02de8c845c，将本目录脚本和 harness 放在相同相对位置，然后在该 checkout 根目录运行（需要 .NET 10 SDK）：

```powershell
./docs/research/read-cache-probe/Run-Probe.ps1 -OutputPath ./docs/research/read-cache-probe/results.json
```

[脚本](Run-Probe.ps1) 在新建临时目录复制 Storage `.cs`，使用当前真实 Serialization/RBF/Segment 项目引用，
只在副本增加 [ProbeHarness](ProbeHarness.cs) 的计数、缓存候选与 map 封装。替换位置必须唯一，否则以 source drift 失败。
临时数据和源码路径写到输出，不自动递归删除；脚本不修改 `src/`、`tests/` 或 solution。
副本保持 Storage 程序集名以使用 Serialization 的既有 IVT，但编译为隔离 executable，不覆盖产品 DLL。

## 固定证据与限制

2026-09-12 记录：[results.json](results.json)。源码 `f68388f88ba09354e9fa90420dc2cf22b146b6cf`，
Atelia `539088414686f51a210798b35ee56d02de8c845c`，SDK 10.0.201，Probe 实际 runtime 10.0.5；OS 信息在 JSON。
六种 fixture × 十种配置；每格预热后测三次空内容缓存读取和各自紧随其后的重复读取，保存所有样本及按时间选取的中位样本。

- `direct/page64` 使用现有读取/重放算法；所有配置统一使用私有 owned map wrapper，避免把所有权修复开销混成缓存差异。
- `frames-*` 是有界帧缓存。`maps-only/frames+maps` 的 map 是**无界、每操作清空**的消融对照，不能作为产品模型。
- `unified-*` 是候选三态 entry、统一预算、整项 LRU、跨操作保留的有界对照；预算包含两组件的显式估算 charge。
- 预算不足或组合超过预算均记入 JSON 的 `Oversized` 计数；它实际表示 admission bypass，并不专指单组件超大。
- 未模拟产品 fault/dispose 生命周期集成或统计接口；这些由 DB-067 实施验收负责。早期讨论的 owner callback 已被后续普通 Store 生命周期方案替代。
- cold 只表示缓存内容为空，OS 页、JIT 和字典容量已预热；tiered compilation 关闭。没有测真实冷盘、冷进程打开或完整 Commit。
- `RequestedFrameBytesOnMiss` 是成功 miss 所请求的完整 ticket 长度之和，不是物理 I/O，也不同于对象链 H。
- `AllocatedBytes` 使用当前线程分配计数；charge 是驻留代理，不是 GC 堆大小、容量高水位或峰值存活内存。
- `ReadAll` 复现 Decoder 的 Storage 调用形状，body 是 opaque bytes；没有 Schema/DTO/Upgrade/Hydrate。
- 每模式校验链 H、对象/长度与首尾字节构成的 checksum 相同。这是实验防偏检查，不代替完整语义测试。
- `ownership` 是原 ReadOnlyDictionary/SortedDictionary 组合的公开 SyncRoot.Clear 反例及新 wrapper 不暴露相同路径的见证。

早期研究时 root solution build 通过（0 warning/error），Storage 测试 155/155、StateStore 测试 711/711 通过；它们只验证当时的产品基线，不验证候选。
性能结论、预算反例与后续产品验收只在 DB-067 维护。
