# DB-041：非泛型 ObjectId 状态表示

> 文档整理：2026-10-14；来源为 Git `8ef9fca` 中的 `docs/design-branches/0041-object-id-state-representation.md`，正文保留当片合同与历史验收含义。
> 阅读范围：DTO/Runtime 引用与普通 uint 数值隔离、wire 等价和真实包证据见“验收结果”；未选择目标类型品牌，泛型候选仍只属 DB-040 研究。
> 当前能力与施工焦点见 [PROJECT-STATE](../../../src/PROJECT-STATE.md)；旧稿中的“下一步”不自动成为当前工作单。

> 状态：Chosen / Implemented — 2026-09-08。用户已授权实现；基线 `eef046f`。
> 前置研究：[DB-040](../0040-typed-object-id-representation-research/README.md)。泛型目标品牌暂缓。

## 目标与边界

把 DTO 引用槽与普通 UInt32 数值隔离，贯通 Capture、历史绑定、Upgrade、读取与 StateStore 对象身份。
使用 Runtime 中的 `readonly record struct ObjectId(uint Value)`，提供 `IsNull` 与按数字排序的 `IComparable<ObjectId>`。
不提供隐式数值转换；default/0 表示 null，实际对象记录继续在既有边界拒绝零。
string 与 durable class 共享同一种 ID；包装不承诺目标类型、版本或引用有效性，完整 Schema/目录验证仍有职责。

普通数值 uint 字段不变。SG 已知引用槽直接写 `id.Value`、读取后显式构造 ObjectId；
泛型引用投影与状态操作使用 ObjectId。历史反推能区分数字和引用，但不能仅凭 ObjectId 区分 string 与 durable nominal。
Schema、history、持久 wire 和 ID 分配/回收策略均不改变；历史 DTO 的 CLR 表示随本次生成器更新。
本次包装不要求 Schema 升版；手写 DTO/Upgrade 的引用参数须使用 ObjectId，按需显式构造或提取 Value。
Storage 和 Serialization 保持独立，在 StateStore 适配边界显式包装/解包 UInt32；不新增程序集或泛型扩展框架。

## 施工与验收映射

| 要求 | 实施所有者 | 最小验收 |
|---|---|---|
| ID 值语义与 Runtime 引用管线 | Runtime 子任务 | unmanaged、排序、null、数值隔离、Capture/读取/升级回归 |
| 所有生成 DTO/body 路径 | Generator 子任务 | legacy/family/inline/generic 引用槽；原 Base/Delta golden 不变 |
| 上层计划与持久边界 | StateStore 子任务 | 对象目录/World/连续 Commit/冷重开，Storage 无反向依赖 |
| 历史与实际包消费 | 测试及包子任务 | 旧 Schema 下生成新表示、历史 Upgrade、普通 uint 与引用参数分离 |
| 集成与独立审阅 | 主代理及只读审阅 | 根 build、完整 tests、真实包回归、diff/文档链接检查 |

不把现有研究 Probe 中的裸 ID 历史模型机械迁移；它们保留各自的机制见证意义。

## 验收结果

- 根 `dotnet build DurableGraph.slnx`：零警告、零错误。
- 完整 `dotnet test DurableGraph.slnx --no-build`：537 Runtime/SG + 317 StateStore + 155 Storage + 103 Serialization，合计 1,112，零失败/跳过；结果前缀 `db041-verified`。
- 新增六项测试：ObjectId 的布局/null/排序、引用与数值 wire 等价、双向隐式转换拒绝、生成 DTO 的双向槽误用拒绝。
  原引用 Base/Delta golden 保持；历史参数反推补充数值/引用表示不匹配拒绝。
- 独立只读审阅未发现生产阻塞；Storage/Serialization/Schema history 格式代码未变，未新增依赖或程序集。
- 六项真实 NuGet 消费者回归全部通过：Runtime-only、StateStore、Generic、InlineStruct、HistoryCapability、ValueUpgrade。
  包含历史 exact 读取、删除旧 inline 领域声明、显式相邻 Upgrade、强制 Base 后 NoChange/Delta、同实例连续 Commit、readonly 共享/循环引用，以及预期失败的能力缺失诊断。
- 真实包产物均在 `experiments/PackageConsumerProbe/obj/`：
  `value-upgrade-20260908071839-38860-78e1fac3`（其 feed 供后三种历史消费者复用），
  `generic-20260908071959-41812-21b1d497`、`inline-struct-20260908072022-41812-5feca94b`、
  `history-capability-20260908072044-41812-d6ff2536`、`run-20260908072116-40448`、
  `state-store-run-20260908072155-40448-6f750624`。
- 受影响文档本地链接/锚点与 `git diff --check` 通过。
