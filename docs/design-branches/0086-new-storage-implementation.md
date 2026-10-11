# DB-086：新存储栈实施与验收

> 日期：2026-10-11。状态：源码整库验收通过；真实包消费验收进行中。
> 合同：[重构方案](0086-new-storage-refactoring-plan.md)；取舍：[简化评审](0086-new-storage-simplification-review.md)。

本片的问题是：删除旧存储栈、DG binary cursor 与全库打开屏障后，真实生成模型能否保留保存、冷重开、模型升级、历史选点和故障结果。
验收边界只包括新建库；不读取、迁移或原位改写旧 DG 存档。公开发布不属于本片。

## 实施合同

- `GraphResources` 是公开的具体资源 owner，按 FS→VS 顺序打开 `data/` 与 `versions/`，由两底座取得锁；Schema 与 State facade 借入同一资源。
- VS 创建时预留编号 1，唯一仓库 ref 为 1。根头为 `DGR1` 与版本字节 1，body 使用上游 RootMap；`schema` 与 `branch/名称` 是两个键域。
- Schema 使用完整目录快照，SCB1 v2 的目录语法从空登记集合读取，表示 ID 从 2 连续分配。只认仓库根选中的快照，不认领完整 orphan。
- StateRevision 使用 wire v4，所有 DG links 固定为上游地址的 12B 编码。图帧的 Payload 是 Revision，TailMeta 为 `DGH2`、版本 1、角色、可选业务 Parent、RootId。
- 业务 Parent 与 Revision 的最近 State Parent 保留为两个关系；所选图准备时检查其对应。VS 发布顺序不决定业务祖先。
- tag 的 payload 恰为一个 12B 图帧地址，创建和解析由 VS 承担。Checkpoint 仍绑定本次打开 owner。
- 写入直接使用 Bare 的最短编码；读取接受上游合法有界冗余表示。char 用 CharLE；Guid、decimal 与 temporal 的少量组合保留在 BclValueCodec。
- 对象 B/D/H 按实际对象记录计量，固定地址消除了旧文件距离上界。Schema、图元数据与物理 RBF 开销不计入 H。
- 打开读取必要根和所选 Schema，保留上游正常恢复及控制文件的惰性首次打开；不执行启动全图审计或全库重新确认。

根含格式头在内最多 1MiB；Schema 与 Revision 使用上游实际单帧容量。历史元数据导航仍执行整帧 CRC 读取。
旧生成 body ABI 不保留适配器，模型程序集需要重编译；新库中的模型 V1→V2 历史升级保持。

## 验收记录

源码模式整库构建零警告、零错误；四个测试程序集共 2793 项全部通过、零跳过：Serialization 133、Storage 132、Persistence 881、模型/Generator 1647。此前的旧栈基线不作为本片验收数。

跨进程、真实包资格与最终独立复核结果继续在本记录收束。

存储候选包由上游干净提交 `974fd47abb73792f9d350af87ab91a53848952eb` 的 `eng/Pack.ps1` 生成，
版本 `0.1.3-dev.20261011.g974fd47`；六包的 manifest、nuspec、来源与 hash 已核对，未公开发布。
本地依赖和源码联调入口见[依赖指南](../storage-dependency.md)，本机日志保存在忽略的 `.artifacts/db086/`。

## 已发现并修正的集成问题

独立复核发现，DG owner fault 而 FS 仍健康时，借出的 State facade 可能继续命中缓存。
现在组合 owner fault 会立即失效其 State facade；实际 backing 资格检查也触发同样的失效，已交付 owned 值仍可读。

普通根容量预检以及 VS 的确定 pre-I/O Result 拒绝与未知发布分开处理。
完整图或 Schema orphan 可以保留；普通拒绝不因此污染健康 owner。Unknown、Confirmed 后交付失败与原始证据仍各自保留。

## 边界

不承诺全库健康、旧调用曾返回 Confirmed、OS crash/power loss 或外部副作用 exactly-once。
没有进行 256MiB Schema 极限压力测试或性能基准；增量目录、metadata-only I/O 和大 branch 目录的替代结构仍按真实瓶颈重访。
