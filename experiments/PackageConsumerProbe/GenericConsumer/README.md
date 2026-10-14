# Generic history package consumer

> 当前 G 门禁暂停：此 lane 仍依赖 `RevisionDecoder` 与 SCB1/raw Store 诊断；`Schemas/States` 已收为 internal，默认 owner 不授旧模式。后继迁移范围见 [DB-087 §36](../../../docs/design-branches/0087-meta-bootstrap-refactoring-route/0087-meta-bootstrap-refactoring-route.md#36-p1d-第七片旧typed恢复束退出)。下文保留匹配历史包版本的验收记录，不能作为当前门禁。

DB-038 的真实 `PackageReference` 消费者；由
[Run-GenericProbe.ps1](../Run-GenericProbe.ps1) 顺序构建、执行三个版本。
项目不引用源码项目、不手工导入 analyzer/build targets/history 文件。

```powershell
pwsh -NoProfile -File experiments/PackageConsumerProbe/Run-GenericProbe.ps1
```

已有匹配的本地依赖包时，可同时传入 `-PackageSource` 和 `-Version`。
运行产物位于 PackageConsumerProbe 的忽略目录 `obj/generic-*`，无需覆盖或删除先前结果。

| 构建 | 观察条件 |
|---|---|
| V1 | `Box<int>`、`Box<Point>` 对象与 `Pair<LegacyPoint>` inline 值；首次 Base、单个泛型对象 content change（V1 小 body 选 Base）；冷重开与 exact DTO 解码 |
| V2 缺闭合转换 | 删除 `Box<Point>` 的业务 provider；exact 解码仍可用，editable Load 在该对象首次 callback 前拒绝；仓库文件集合与 bytes 不变 |
| V2 | 同定义统一升版；`Box<int>` 通用透传、`Box<Point>` 闭合业务转换、World 显式转换 inline 状态；强制 Base 后 NoChange 和单 owner content change（V2 小 body 选 Base） |
| V3 | 编译中排除 `LegacyPoint.cs`；旧历史 DTO/显式 owner Upgrade 仍可工作；从 V1 执行两跳、从 V2 执行一跳；逐对象及逐边完整 Context Schema；再次保存稳定；V3 实际普通 Delta 与 exact prior |

runner 检查新增 history 使用 v9 格式，并逐阶段验证已有 `.dgschema` 的文件存在性和 SHA256 不变。
每代进程独立运行，通过磁盘 repository 传递状态。历史状态类型使用生成的独立 Family 宿主。

Box 还持久保存一个稳定的创建时间 ticks，所有版本的 owner Upgrade 显式透传并校验它。
DB-086 的 12B prior 改变了真实成本：V1/V2 小 body 的单字段编辑使用更便宜的 Base；
V3 多一个真实 stamp 字段，使该 edit 的 Delta 仍更便宜。验收保持 exact State Parent、单 owner
变更、其他 heads 与闭包不变、NoChange baseline 和 V3 exact prior，不人为固定所有模型都选 Delta。
三个版本的历史字段/类型均保持原义，未添加 padding 或策略替身。专门的 RawDelta wire 见证由
[StateStore consumer](../Run-StateStoreProbe.ps1) 承担。

删除旧 inline 领域类型与删除独立对象族的恢复能力是不同边界：V3 删除 `LegacyPoint`，但保留独立
`Box<Point>` 行所需的 `Point` 与闭合模型。这个消费者不把“World 后来不引用某对象”视为跳过 source Normalize 的理由。
恶意中间 Schema、phantom 中间歧义和目录隔离由 DB-038 的产品 Runtime 测试补充；本消费者聚焦包交付与三代可执行历史。

当前 runner 的前两条业务成功 marker 是 `GenericSeed:True:OwnerChange:True:ClosedSchemas:True:InlineNoObjectIds:True`
和 `GenericUpgrade:True:ClosedBusiness:True:ForcedBase:True:NoChangeThenChange:True:PerObjectContext:True`。
缺闭合转换与 V3 marker 保持下方历史名称，具体验收以 runner 的严格匹配和实际执行为准。

历史结果（旧栈，2026-09-08；不代表 DB-086 新栈验收）：首次运行通过：八个 Release 依赖包、四次消费者构建与四个独立运行进程均成功，消费者构建零警告、零错误。
history 数量依次为 5、10、10、12，旧文件 SHA256 全部保持不变；缺闭合转换的运行保持 repository 文件集合及 bytes 不变。
该旧运行的四行严格匹配结果为：

```text
GenericSeed:True:OwnerDelta:True:ClosedSchemas:True:InlineNoObjectIds:True
MissingClosedUpgrade:True:ExactDecodeAvailable:True:NoPointBusinessCallback:True
GenericUpgrade:True:ClosedBusiness:True:ForcedBase:True:NoChangeThenDelta:True:PerObjectContext:True
GenericThirdVersion:True:DeletedInlineDomain:True:StoredExactHistory:True:AdjacentContexts:True:StableResave:True
```
