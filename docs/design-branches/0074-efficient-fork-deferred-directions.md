# DB-074：高效 fork 的暂缓方向清单

> 状态：**Deferred / Research parking lot**。2026-09-21。
> 定位：集中记录 [DB-072](0072-generator-immutable-leaf-classification-slice.md) 与
> [DB-073](0073-repository-scoped-weak-reference-cache.md) 之后仍值得考虑、但当前不排期的方向。
> 本文不是实施授权，也不代表这些方向已被采纳。

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

在 DB-073 落地前，不应先做 deep clone。
如果 immutable sharing 已满足主要性能目标，deep clone 可能根本不需要。

## 6. 公开 fork API

在 DB-072/073 之后，才值得设计公开 API。

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

DB-073 会引入最小 `RepositoryScope`。

后续可考虑：

- 把 `GraphFrame.Owner` 从 `object` 强化为 typed scope；
- 把 `RevisionReadSession` 绑定 scope；
- 把 model snapshot 与 scope 关联；
- 统一 cross-repo error message；
- 支持同一进程内多个 opened repo，但禁止跨 repo 操作。

这属于内部结构清理，不应在 DB-073 中过度扩张。

## 8. 性能基线

DB-073 落地后应建立最小基准：

- allocation count；
- hydration count；
- cache hit count；
- cold vs warm read time；
- fork time；
- retained instance count。

不应只报告 body decode count。
若 weak cache 命中率低，再考虑：

- strong LRU；
- per-branch cache；
- root-level cache；
- closed graph cache。

## 9. 内存管理

WeakReference cache 不延长对象寿命，但字典本身可能增长。

后续问题：

- 是否需要定期清理 dead entries；
- 是否需要 entry 上限；
- 是否按 branch 分区；
- 是否需要内存预算；
- 是否在 GC 后主动 compact。

先不做这些，等真实负载证明有必要。

## 10. 手写模型 opt-in

当前手写 `StateModelBinding` 默认不可共享。

后续可以提供显式 opt-in，例如：

```csharp
SharingCapability = SharingCapability.ImmutableLeaf
```

但这是新的公共 API 面，应等生成模型路径先证明收益。

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

这些都应在 DB-073 之后按真实需求排期。

## 12. 重访触发

| 方向 | 触发条件 |
|---|---|
| DeepImmutable | ImmutableLeaf 覆盖率不足，且真实 fork 负载中 immutable 引用图占主导 |
| 泛型分类 | 下游真实模型大量使用泛型 leaf |
| string 例外 | 现有排除导致主要模型无法受益 |
| Transient 策略 | readonly Transient 成为常见模式 |
| deep clone | immutable sharing 后仍有明显复制瓶颈 |
| 公开 fork API | DB-073 提供性能基线 |
| opened repo 泛化 | cross-repo 误用成为实际问题 |
| 内存管理 | cache 字典增长或 dead entry 积累成为测量问题 |
