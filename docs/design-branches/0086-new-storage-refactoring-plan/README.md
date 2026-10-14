# DB-086：FS／VS 正常恢复与资源寿命

底座迁移已完成。这里只保仍生效的恢复、验证与寿命边界；旧单根、SCB1 完整目录快照、Revision／TailMeta 合帧及 R0–R5 施工计划退出工作树。当前图格式与 meta／业务共同保存合同见 [DB-087](../0087-meta-bootstrap-refactoring-route/README.md)。

## 当前合同

- FrameStore 拥有物理文件发现、锁、分配、轮转、帧完整性、必要输出确认与正常不完整尾恢复；VersionStore 借用数据 FS，拥有根／tag 发布资源与 data-first barrier。DG 使用 opaque FrameAddress，不穿透文件位置恢复旧轮转机制。
- 正常新开依次打开 FS、借入它的 VS，读取所需仓库根，再构建 DG Store/cache。VS 未访问的普通 ref/tag 等控制文件保留底座的惰性首次打开与恢复；不预开全部控制文件完成全库恢复、不恢复 strict writable preflight、重开全库确认或启动全图审计。
- 只读打开／读取沿底座只读合同，不创建、flush 或修尾；需要的坏内容报错，不回退旧 State 或把不完整结果当合法句柄。未访问内容不宣称健康。
- 在打开 owner 的寿命内，已完成 State、Schema 与 history 帧的内容和地址稳定。DG 不截断、重写、删除或修复这些完成帧；合法未完成尾由正常底座恢复处理。refs／锁／可重建派生文件各遵循自身协议。
- 必要帧 CRC、所选图的角色／Parent／prior／成员／typed 来源、发布 outcome／fault、reentry 与 mutable 隔离检查仍保留；允许正常尾恢复不减免数据验证。
- backing 必须比借用的 Store facade 活得久；操作串行，不与外部 backing writer lease 重叠。owner fault、backing fault 或关闭后停止使用 facade，暖缓存也必须拒绝并释放驻留；关闭顺序为 DG Store/cache、VS、FS。
- 正常底座恢复之外的数据修复属于明确的离线救援。先关闭所有受影响 Repository／Stores／文件并丢弃关联缓存，再离线救援；随后新开资源与缓存，不能临时禁用缓存后恢复旧 owner。
- 发布异常按实际 NotPublished／Unknown／Published 与 fault 处理，关闭重开后检查持久选择，不透明重试，也不把 outcome 当作 owner 健康证明。

## 当前实现与验证入口

- [GraphResources](../../../src/DurableGraph.Persistence/GraphResources.cs)：owner／backing 检查、故障时清 facade、按 Store→VS→FS 关闭；[实际打开与发布](../../../src/DurableGraph.Persistence/GraphResources.MetaBootstrap.cs)：必要根读取后构建缓存。
- [GraphRecordStore](../../../src/DurableGraph.Persistence/GraphRecordStore.cs)与 [DB-067](../0067-owned-revision-read-cache-design/README.md)：借用寿命、命中资格、必要图关系及缓存释放。
- [正常不完整 data 尾恢复与未选 ref 惰性打开回归](../../../tests/DurableGraph.Persistence.Tests/SplitRepositoryBackingRecoveryTests.cs)、[owner／backing 关闭与 fault 回归](../../../tests/DurableGraph.Persistence.Tests/GraphResourcesSplitOwnerTests.cs)、[暖缓存失效回归](../../../tests/DurableGraph.Persistence.Tests/P0OwnedReadCacheTests.cs)。
- 准确上游版本、来源与源码／包模式操作只查[依赖指南](../../storage-dependency.md)和 [pin](../../../eng/StorageDependency.props)。

历史计划、评审与 R0–R5 产品／包／进程停止证据可从 `6dbaf0e` 下本目录的 `0086-new-storage-refactoring-plan.md`、`0086-new-storage-simplification-review.md`、`0086-new-storage-implementation.md` 恢复。那些窗口对应当片旧单根／合帧产品，不能作为当前 DB-087 两根／分帧共同保存的资格；上述当前测试入口也不授 OS crash、断电、设备故障或全部平台保证。
