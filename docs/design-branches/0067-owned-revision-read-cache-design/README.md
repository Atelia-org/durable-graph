# DB-067：owned 物理读缓存与紧凑只读索引

保留成功完成帧与完整目录的有界复用，不保旧 StateRevisionStore、raw-map materializer 或 cache adapter。当前缓存服务实际 P0 图封套、对象 batch、目录 version 与完整 heads/H；typed DTO、Normalize、Upgrade、领域实例和准备材料不是这层缓存内容。

## 当前合同

- 每个 Store 的同一完整 FrameAddress 只对应一个 entry；frame／完整目录两个 owned 组件共用字节 charge、预算与整项 LRU。地址不跨 Store／owner 认证来源；命中仍先核 backing／owner 寿命与帧角色。
- 只准入成功读取／解码的不可变 owned 内容，不保存 pooled FrameRead 视图、不做负缓存。失败读取可再次尝试；H-only 回溯不准入不完整 map。
- 预算是显式驻留 charge 估算，不是 GC 总堆、峰值内存或物理 I/O 上限。默认预算值在具体 Store 的常量中；负预算拒绝，zero 禁用缓存但仍完整执行正常读取／验证。
- 单组件或合并 entry 超预算时 bypass，不扫掉其他热项，也不删除已经驻留的另一组件。正常准入按 LRU 驱逐完整 entry；预算内驻留不等于全部临时构建内存有界。
- 完整目录用 private keys／values 双数组冻结，按默认 key 顺序二分查询、升序枚举。构建器可使用排序树，冻结结果不保留树，也不暴露可修改 backing；对象 batch 的已排序 local records 直接二分查找。
- 物理命中不能免除所选图的 source／Parent／prior／成员关系、准确 Schema view、完整布局、typed body 与引用验证；正常完成帧在打开 owner 寿命内保持地址和内容稳定。
- Store facade 借用 backing，其寿命不得超过 owner；操作串行，不能与外部 backing writer lease 重叠。owner/backing fault 或关闭后拒绝暖读与新写，释放驻留；已经交付的 owned 值不依赖缓存继续存活。
- 正常 FS/VS 打开可按底座合同恢复不完整尾，包含未访问控制文件的惰性首次打开；DG 先读必要仓库根再建 Store/cache，不做全库启动审计或重开确认。额外数据救援必须全关资源、丢弃缓存后离线执行，再新开，见 [DB-086](../0086-new-storage-refactoring-plan/README.md)。

## 当前实现与验证入口

- [共享 LRU](../../../src/DurableGraph.Storage/OwnedFrameReadCache.cs)、[双数组字典](../../../src/DurableGraph.Storage/FrozenSortedDictionary.cs)、[P0 组件与 charge](../../../src/DurableGraph.Persistence/GraphPhysicalReadCache.cs)、[实际读取／关系验证／释放](../../../src/DurableGraph.Persistence/GraphRecordStore.cs)。
- [真实缓存回归](../../../tests/DurableGraph.Persistence.Tests/P0OwnedReadCacheTests.cs)覆盖 zero／负预算、整项 LRU、两组件超额、完整地址键、fresh 缺帧／CRC 错误不准入、暖目录不能认证坏图、坏 prior、owned 隔离及 owner fault/close 清驻留。
- [双数组所有权与排序](../../../tests/DurableGraph.Storage.Tests/FrozenSortedDictionaryTests.cs)、[当前字典机制测量](../../../tests/DurableGraph.Storage.Tests/ReadCacheMeasurementTests.cs)、[资源寿命](../../../tests/DurableGraph.Persistence.Tests/GraphResourcesSplitOwnerTests.cs)。测量耗时不是测试通过阈值或整体恢复性能承诺。

原 tree 表示消融、旧 raw Store 样本与详细施工稿退出工作树。历史正文可用 `git show 6dbaf0e:docs/design-branches/0067-owned-revision-read-cache-design/README.md` 查阅；原数据在同修订的 `docs/research/read-cache-probe/results.json`、`docs/research/read-cache-implementation-results.json`。它们记录旧源码、旧底座和当时测量，不是当前 FS/VS 或完整 Commit 的性能资格。
