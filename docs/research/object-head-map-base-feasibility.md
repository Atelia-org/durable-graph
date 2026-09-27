# ObjectHeadMap 大量删除时选择 Base

> 2026-09-27；调查后用户批准实施。**已实施并通过产品与真实包验收**。
> 问题来自普通 State 大幅收缩时的 Remove 编码成本；不改变 DB-083 公共 API 研究范围。

## 结论与范围

现有 wire v3、Store、目录恢复和 State 基线安装已经支持有 Parent 的 ObjectHeadMap Base。
本次在对象内容写入决策完成后，加入普通 State 的内部目录表示选择；产品逻辑集中在 ObjectRevisionPlanner。
不新增公开策略配置、持久格式、事务阶段或通用优化框架。Event 独立快照仍固定使用目录 Base。

最小可观察标准：仅替换目录表示，保留精确 Parent、每条本地对象 Base/Delta 和未重写对象的旧 head；
大量删除时 wire payload 确定更小，正常 State 安装、续写与恢复的逻辑对象集合不变。

## 当前接缝与证据

| 位置 | 当前事实与影响 |
|---|---|
| [ObjectRevisionPlanner.PrepareCore](../../src/DurableGraph.Persistence/ObjectRevisionPlanner.cs) | 先决定对象内容写入，再构造目录。Event 的 independentSnapshot 分支构造 Map Base；普通有 Parent 的 State 在方法末尾按下述规则选择 Map Base/Delta。 |
| [StateRevision](../../src/DurableGraph.Storage/StateRevision.cs) | CreateObjectHeadMapBase 接受非空 Parent、本地对象记录和 external heads；Map Base 不限制本地对象必须是内容 Base。 |
| [wire writer](../../src/DurableGraph.Storage/StateRevisionWireWriter.cs) / [目录恢复](../../src/DurableGraph.Storage/LiveObjectHeadMapMaterializer.cs) | 两种格式都已实现；Base 的存活目录是本地 ID 与 external heads 的并集，Delta 才继承父目录并应用 Removes。 |
| [NormalizedRevision.WithAddress](../../src/DurableGraph.Persistence/NormalizedRevision.cs) / [PreparedWorldSave](../../src/DurableGraph.Persistence/PreparedWorldSave.cs) | 安装依据完整候选与本地写入更新 head/H，没有要求普通 State 使用 Map Delta。State 是否安装由候选角色决定，不由目录 kind 决定。 |
| [EventHistoryRepository.ValidateGraph](../../src/DurableGraph.Persistence/EventHistoryRepository.cs) | 校验精确 Revision Parent、根成员和对象链，没有把 State 角色绑定到 Map Delta；新产品测试覆盖收缩后的正常重开与发布前后失败。 |

不能把“选择 Map Base”实现为把普通 State 改走整个 independentSnapshot 生命周期；后者不会安装 State 基线。
应只复用目录构造方式，普通 State 仍完成 PrepareInstall/Install，Event 仍保持独立快照规则。
也不能清空 Parent：本地对象 Delta 的 prior 验证与 Journal/Revision Parent 合同仍依赖它。

## 已实施的选择规则

设 `R` 为从父目录删除的 ID，`E` 为新目录中没有本地写入、需要指向旧 head 的对象。
本地对象记录及其 ID、Revision Parent、版本与 kind 字段的长度在两个候选中相同，只需比较目录尾部：

```text
V(x) = x 的 unsigned varint 字节数
DeltaBytes = V(R.Count) + Σ V(removedId)
BaseUpper  = V(E.Count) + Σ [V(objectId) + 5 + V(oldHead.FrameTicket.Serialize())]

BaseUpper < DeltaBytes：选择 Map Base
否则：保留 Map Delta（含相等情况）
```

旧 head 地址编码含 UInt32 的 backward file distance 与 UInt64 ticket；前者最坏 5 字节，ticket 已知。
实际写入文件要到 OpenActiveWriter/rollover 后才确定，因此使用上界避免提前取得 writer、预测地址或增加准备阶段。
每个 external head 最多高估 4 字节；会漏掉一部分微小收益，但选中的 Base 在任意合法目标 file scope 下都确定更小。
这是目录表示的局部选择，不声称联合对象重写、RBF 对齐和未来读成本达到全局最优。

实现基于已有父目录与完整候选累计费用；无删除时直接保留 Delta，只在选中 Base 时构造 external heads。
不需要额外 Store I/O，不应为了比较复制两套完整图或编码两遍对象 body。
计量与 wire varint/地址编码的对应必须用测试约束，避免将来 wire 修改后估算漂移。

## 实施前的隔离实验

使用本地临时控制台引用当前 Persistence 项目，没有修改仓库产品文件。
选择器只比较上述费用；实际字节用现有 StateRevisionWireWriter 编码计量。
父目录含 ID 1–1000，各有 100 字节不透明 body；保留项不重写，只删除尾部对象。
下表计整个 Revision payload，不含 Schema/Journal/RBF 外层，不代表完整应用提交大小，也不是性能 benchmark。

| 保留对象数 | 原 Map Delta 字节 | 选择后字节 | 选择 |
|---:|---:|---:|---|
| 1000 | 12 | 12 | Delta |
| 999 | 14 | 14 | Delta |
| 900 | 212 | 212 | Delta |
| 500 | 1013 | 1013 | Delta |
| 100 | 1786 | 812 | Base |
| 10 | 1876 | 92 | Base |
| 1 | 1885 | 20 | Base |
| 0 | 1886 | 12 | Base |

空对象集合仅为底层格式夹具；产品 State 根仍必须非空。具体字节数依赖 ID 与地址，表中的数量不构成固定百分比阈值。

另有两项机制见证通过：

1. 对现有 WorldWorkspace 测试模型，以反射在临时夹具内将一个普通 State 待保存候选的 Revision 换成语义相同的 Map Base，
   保留本地对象 Delta 与外部字符串引用；原 State 安装、后继热保存、新建无缓存 Store facade 的 Load 和续写通过。
   这验证基线机制，未把临时反射注入作为产品实现，也不冒称为完整进程重开。
2. 原始 Store 夹具混合本地对象 Delta、新对象 Base、external head 和大量删除；强制跨 segment 写入 Map Base，
   再写后继 Map Delta；关闭后只读重开，成员集合及对象 Delta 链读取通过，旧修订仍可读。

当时未修改的产品回归亦通过：Storage 的 StateRevision/Store/wire 测试 77 项；Persistence 的 ObjectRevisionPlanner、
IndependentSnapshotPlanner、WorldWorkspace 与 EventHistoryRepository 测试 75 项。
这些计数验证既有机制，不代表新的自动选择器已经集成或通过完整产品验收。

## 产品实施与验收

产品改动集中在 ObjectRevisionPlanner 的最终目录构造及两个私有 helper；未改格式/reader、公开 Commit API、
Schema/Generator、发布顺序、基线安装或缓存协议。
新增[目录选择回归](../../tests/DurableGraph.Persistence.Tests/ObjectRevisionPlannerTests.ObjectHeadMap.cs)与
[Repository 收缩/故障回归](../../tests/DurableGraph.Persistence.Tests/EventHistoryRepositoryTests.ObjectHeadMap.cs)。

2026-09-27 产品验证：`dotnet build DurableGraph.slnx` 为 0 警告、0 错误；全部四个测试项目通过，
Persistence 744、Runtime/Generator 1620、Storage 202、Serialization 163，共 2729 项，无失败或跳过。
真实 PackageReference 消费者 `Run-StateStoreProbe.ps1`、`Run-HistoryCapabilityProbe.ps1` 与 `Run-InlineStructProbe.ps1`
全部通过，覆盖对象图断开、历史模型升级/删除、冷重开与续写。三个探针共用同一份本次构建的隔离包 feed；未发布包。

验收范围：

- 大量删除选 Base；少量删除、无删除与费用相等保留 Delta；varint 边界和跨段地址上界正确。
- Base 仍携带原 Parent；混合本地对象 Delta、外部旧 head 与新对象时，逻辑集合和对象链完全相同。
- 真正通过 Repository 的大图收缩、后继保存、关闭重开与再续写；原实例安装、升级重写和 publication/fault 回归保持。
- 调整现有测试中把“逻辑删除”直接等同 RemovedObjectIds 的断言；纯目录 Delta 编码测试保持原合同。
  [规划器测试](../../tests/DurableGraph.Persistence.Tests/ObjectRevisionPlannerTests.cs)、
  [工作区测试](../../tests/DurableGraph.Persistence.Tests/WorldWorkspaceTests.cs)及容器测试需逐项区分物理策略和逻辑成员语义。

当前普通 State 大幅缩小会因此受益；选择本身仍要遍历现有集合，不解决大 State 上小 Event 的全基线核验成本。
若进一步要求精确择最小、联合重选对象 Base/Delta、目录读放大阈值或公开可配置策略，应另行评估，不扩进这个局部优化。
