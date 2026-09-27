# DB-074：高效 fork 的暂缓方向清单

> 状态：**Draft / 尚待进一步修订**。2026-09-23；初稿日期 2026-09-21。
> 现行术语见[项目术语表](../DurableGraph-glossary.md)：公共分支工作副本拟为 `BranchCheckout`，签出拟为 `Checkout`；下文旧正文保留其草稿时期的 session/fork 语义，不据此说明现有 API。
> 待 [DB-076 高效 fork 技术路径](0076-efficient-graph-fork-technical-path.md) 收敛后重新整理本清单；下文保留研究材料，不代表当前已裁定的取舍或排期。
> 近期推荐顺序已另拆为 [DB-077–082](0076-efficient-graph-fork-technical-path.md#10-分片施工导航)；本清单继续 Draft，其方向不作为这些施工片的隐含依赖。
> 定位：集中记录围绕 [DB-072](0072-generator-immutable-leaf-classification-slice.md) 与
> [DB-073](0073-repository-scoped-weak-reference-cache.md) 的暂缓方向，不规定必须串行实施的顺序。
> 本文不是实施授权，也不代表这些方向已被采纳。
> 2026-09-23 校准：DB-075 已修复 Family capability；DB-073 改为独立读取的有界实例缓存提案。
> 下文相应修正 owner、内存、手写 opt-in 与后继触发条件，不把缓存完成视作公开 fork 的自动排期。

## 1. DeepImmutable

### 1.1 问题

`ImmutableLeaf` 只解决“自身无引用成员”的对象。
真正的 deep immutable 需要证明：

```text
对象自身 readonly
+ 每个引用目标也是 deep immutable
+ 整个引用闭包稳定
```

这比 leaf 复杂得多，因为引用目标可能是：

- 基类型声明的字段，运行时实际是派生实例；
- 泛型参数；
- 数组 / List / Dictionary 元素；
- 循环引用；
- 跨程序集类型。

### 1.2 Sealed 与 exact runtime type

用户提出的判断是对的：

> `DeepImmutable` 似乎需要目标类型是 `sealed`，否则无法确定运行时 field 里存的实例是不是 Immutable。

更精确地说，有两条可选路线：

1. **保守路线**：所有引用目标类型必须 `sealed`，且 exact model 已证明 deep immutable；
2. **精确路线**：不要求 sealed，但运行时必须携带 exact runtime type 证明，且缓存 key 需包含该证明。

v1 不做 DeepImmutable。若后续重访，应先从 sealed 路线开始。

### 1.3 循环与闭包

Deep immutable 需要处理：

- 环；
- 共享子图；
- 引用目标版本；
- 同一对象在不同 Revision 中 head 不同；
- 跨 branch 的引用环境差异。

这基本回到 DB-064 的闭包传播问题，只是从“运行时比较”推进到“静态分类 + 运行时身份”。

## 2. 泛型 ImmutableLeaf

泛型模型不能按 open definition 一概分类：

```csharp
Box<int>     // 可能是 ImmutableLeaf
Box<Node>    // 不是
```

需要决定：

- 按 closed instantiation 分类；
- Generator 是否能为每个 closed family 发出独立 capability；
- 跨程序集泛型参数如何参与证明；
- `Nullable<T>`、inline struct、enum 的递归规则如何与泛型组合。

在 DB-072 中先排除泛型，是刻意保守。

## 3. string 作为不可变引用叶子

`string` 是引用类型但 CLR 语义不可变。
DB-072 先按“无引用类型成员”排除它。

后续可以考虑：

- 把 `string` 作为唯一已知 immutable reference leaf 例外；
- 允许 `readonly string` 字段；
- 复用现有 string object identity 规则。

但这会扩大证明面，且 string 在 DurableGraph 中已有独立对象行与 Empty 例外，
不应和第一批 ImmutableLeaf 混在一起。

## 4. Transient 策略

当前选择：有任何 `[Transient]` 字段即不分类为 `ImmutableLeaf`。

后续可区分：

1. **无 Transient**：安全；
2. **readonly Transient**：可能安全，但仍需证明不会被视图特定初始化污染；
3. **mutable Transient**：不安全，跨视图共享会泄漏状态。

如果真实模型大量依赖 readonly Transient，可重访为独立 capability。

## 5. Deep clone 生成

若 fork 需要在内存中复制 mutable graph，可考虑生成 deep clone。

但这会引入：

- 继承链复制；
- 泛型复制；
- 循环引用；
- Transient 是否复制；
- allocator / hydrator 语义；
- 与现有 deserialize path 的重复。

暂不排期 deep clone；先由真实负载区分解码、Normalize、Allocate/Hydrate 的成本。
DB-073 首片不覆盖 Resume 或普通 Family 跨调用命中，不能以它的完成直接证明 fork/复制瓶颈已解决。

## 6. 公开 fork API

已有分支入口之外的公开 API 由具体用法触发；DB-073 不再构成必然的排期前置或收益证明。

需要回答：

- fork 是现有 `CreateBranch(newName, selectedFrame)` 的别名，还是新语义？
- 是否返回 session、read-only view，还是两者；
- 是否内置 resume；
- 是否默认启用 immutable cache；
- 是否暴露 cache 统计；
- 是否允许显式禁用。

当前 EventHistory 已有 branch/ref/Move/Resume。
公开 fork API 应复用这些能力，而不是另建第二套 ref 层。

## 7. Opened repo session 的进一步泛化

DB-073 复用 `EventHistoryRepository._identity`、`GraphFrame.Owner` 与现有跨 owner 检查，
不引入 `RepositoryScope`，也不把独立的 `RevisionReadSession` 强绑到 repository。
只有现有 owner 边界暴露具体缺陷时才重访 typed scope/统一 handle，而非为名称一致迁移。

Family 的跨读取身份是另一问题：每操作 snapshot 会创建新的 binding。
若真实叶模型要求该路径跨调用命中，需要独立证明稳定投影或 binding 的寿命，
不能直接把整个 snapshot 放到 repository：这还会改变 comparer/factory 结果的有效期及异常出现时机。
现有 callback 的 per-snapshot 合同及否决原因见 DB-073 §2.2。

## 8. 性能基线

DB-073 先从独立读取建立最小基准，分清 binary/稳定 concrete binding 与 Family：

- decode / Normalize / allocation / hydration count；
- 实例 cache hit count（与操作内 DTO cache hit 分开）；
- cold vs warm read time；
- 分配量、缓存条目数及调用者是否保活目标。

当前没有该缓存的业务加速测量，也不以实例复用次数替代总耗时。
若实际模型全走 Family，先处理身份覆盖问题；不是靠强引用就能提高命中。
在同 key 可稳定复用、真实丢失原因已测清后，再考虑：

- strong LRU；
- per-branch cache；
- root-level cache；
- closed graph cache。

## 9. 内存管理

WeakReference 不保活目标，但字典仍保留 binding/DTO/目录闭包。
DB-073 首片已要求有限条目上限、简单触顶淘汰和 Dispose 清理；这不是总堆字节预算。

后续问题：

- 简单淘汰是否导致真实大图扫描反复 miss；
- 是否需要更精细的 dead entry 清理；
- 是否按 branch 分区；
- 是否需要内存预算；
- 是否在 GC 后主动 compact。

这些增强等真实负载触发；不能据本节把 DB-073 的基本条目上限延后。

## 10. 手写模型 opt-in

公共可选参数 `isImmutableLeaf` 已由 DB-072 提供，省略为 false，手写 true 是调用者断言；
DB-075 保留此通道。无需再设计 SharingCapability 枚举或第二套 opt-in API。
DB-073 还要求当前 DTO 的完整比较 proof，true 本身不允许跳过 Normalize 或比较。

## 11. 盘上 ref 与 fork 的进一步整合

EventJournal 已有：

- branch；
- reflog；
- CAS advance；
- move；
- archive；
- chronological replay。

DurableGraph 后续可以考虑：

- `ForkBranch` 便捷 API；
- 源 branch provenance；
- detached head；
- symbolic head；
- branch rename / alias。

这些按独立的真实需求排期，不由 DB-073 自动触发。

## 12. 重访触发

| 方向 | 触发条件 |
|---|---|
| DeepImmutable | ImmutableLeaf 覆盖率不足，且真实 fork 负载中 immutable 引用图占主导 |
| Family 跨操作 binding/投影寿命 | DB-075 已完成 capability 发射；真实 Family 叶负载需要跨读取命中时，独立审查目录与回调生命周期 |
| 泛型分类 | 下游真实模型大量使用泛型 leaf |
| string 例外 | 现有排除导致主要模型无法受益 |
| Transient 策略 | readonly Transient 成为常见模式 |
| deep clone | immutable sharing 后仍有明显复制瓶颈 |
| ReadPair/Resume 的叶缓存接入 | 独立读实验已有实际收益，且真实消费者需要这些入口的跨操作复用；另验收闭包、整对交付与可编辑身份导入 |
| 公开 fork API | 现有 CreateBranch/Resume 组合存在具体使用缺口，而非缓存完成即自动排期 |
| opened repo 泛化 | cross-repo 误用成为实际问题 |
| 更精细内存管理 | 基本条目上限已在 DB-073；淘汰抖动或驻留字节成为实测问题时增强 |
