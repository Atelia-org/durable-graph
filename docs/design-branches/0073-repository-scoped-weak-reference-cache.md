# DB-073：Repository 作用域的 ImmutableLeaf WeakReference 缓存

> 状态：**Chosen / Not implemented**。2026-09-21。前置为 [DB-072](0072-generator-immutable-leaf-classification-slice.md)。
> 问题：同一 opened repository 内，跨读取/跨 fork 操作复用已证明为 `ImmutableLeaf` 的 hydrated 领域实例。
> 最小验收：同 `(ObjectId, head, exact model binding)` 命中并复用；不同 head/model/repo 不命中；Dispose 后缓存不可用；不改公开 API 与持久格式。

## 1. 选择

引入一个**repository-scoped WeakReference cache**：

```text
key   = (ObjectId, head FrameAddress, exact StateModelBinding)
value = WeakReference<object>
scope = one opened repository session
```

只缓存 DB-072 分类为 `ImmutableLeaf` 的对象。
因为该类对象没有引用成员，所以不需要引用闭包传播，也不需要 deep-immutable 图分析。

## 2. Opened repository session

用户提出的“显式 opened repo / connection / session”方向与本片一致。
当前 `EventHistoryRepository` 已经有 `_identity`，`GraphFrame.Owner` 也以对象身份承载 opened repo 归属；
本片应把这个概念显式化为内部 owner，而不是把 repository id 写进缓存 key。

候选内部形状：

```csharp
internal sealed class RepositoryScope {
    public object Identity { get; }
    public ImmutableLeafInstanceCache ImmutableLeaves { get; }
}
```

`EventHistoryRepository` 创建并持有 `RepositoryScope`；
`GraphFrame`、`RevisionReadSession`、后续 fork/read 入口都绑定该 scope。
跨 repo 传入 handle、frame 或 revision 时，用 scope reference equality 直接拒绝。

这个思路与 `atelia-statejournal` 的做法同向：

- `Repository` 持有 `RepositoryLifetime`；
- `Revision` 绑定 owner lifetime；
- `DurableObject` 绑定 exact `Revision`；
- `Revision.EnsureCanReference` 拒绝外 Revision / unbound 对象；
- branch 级 `LoadedRevision` 缓存以 opened repository 为作用域。

DurableGraph 不照搬 StateJournal 的 GcPool 或 mark-sweep 生命周期，
但采用同样的“owner-bound + cross-owner reject”边界。

## 3. 缓存行为

### 3.1 Key

使用：

```text
(ObjectId id, FrameAddress head, StateModelBinding model)
```

不使用单独的 repository id，因为缓存本身就属于一个 `RepositoryScope`。

`StateModelBinding` 参与 key 是必要的：

- 同一 persisted head 在不同 model registry / snapshot 下可产生不同 current binding；
- 手写模型可能每次注册新 binding；
- 只有 exact binding identity 才能保守证明“同一个 hydrated 语义”。

对生成模型，binding 通常是稳定静态实例；对手写模型，不同 binding 自然 miss。

### 3.2 Value

每个 entry 保存：

```csharp
WeakReference<object> Instance
```

不保存：

- DTO；
- normalized state；
- reference closure；
- strong root；
- allocator callback；
- hydrator callback。

`ImmutableLeaf` 已证明没有引用成员，因此复用实例不需要再遍历引用闭包。

### 3.3 Hit / miss

Hit 条件：

1. same `RepositoryScope`；
2. same `(ObjectId, head, StateModelBinding)`；
3. `WeakReference.Target` 仍活着；
4. binding capability 是 `ImmutableLeaf`。

Hit 后：

- 直接复用 hydrated instance；
- 不重新 Allocate；
- 不重新 Hydrate；
- 不执行 Normalize/Upgrade；
- 不重新比较 `StateEquals`。

这依赖 DB-066 已确立的合同：binding 回调必须确定性且不保留可变领域状态。
capability 的 binding 传输通道已由 [DB-072](0072-generator-immutable-leaf-classification-slice.md) §3.2 裁定为公共可选构造参数 `isImmutableLeaf`（2026-09-22）；本片 hit 条件直接读 binding 的 internal `IsImmutableLeaf`。

Miss 时：

- 正常走现有 decode/normalize/allocate/hydrate；
- 成功交付后写入 weak cache；
- 失败不入缓存。

### 3.4 Lifecycle

- cache 生命周期等于 opened repository session；
- `RepositoryScope.Dispose` 清空字典；
- weak reference 不延长领域对象寿命；
- 不做跨进程、跨 reopen、跨 repository 共享；
- 不改变 append-only 纪律。

## 4. 跨 repo 操作

本片应把“opened repo 归属”作为硬边界：

```text
Repository A 的 GraphFrame / Revision / cache entry
    不得提交到 Repository B
```

实现上可以在 `EventHistoryRepository.CheckFrame`、`RevisionReadSession` 构造、
以及后续 fork/read 入口中统一校验 `RepositoryScope` reference equality。

错误应为明确异常，例如：

```text
The frame belongs to another opened repository.
```

不做路径字符串比较，不尝试把两个 opened repo 合并成一个身份。

## 5. 最小验收

| 场景 | 预期 |
|---|---|
| same repo + same `(id, head, binding)` | weak cache hit，复用实例 |
| same id + different head | miss |
| same id/head + different binding | miss |
| different repo | 拒绝或 miss，不共享 |
| non-ImmutableLeaf | 不入缓存 |
| weak target dead | miss 并重建 |
| repository disposed | cache 不可用 |
| read-only repo | 可使用自身 scope cache |

## 6. 非目标

- 不缓存 mutable object；
- 不缓存 deep immutable closure；
- 不缓存 string；
- 不引入 strong LRU；
- 不引入跨 repository 全局表；
- 不改变 EventHistory branch/ref 语义；
- 不改变公开 API。

## 7. 后继

本片落地后，才开始设计公开 fork API，并把 DB-074 中的暂缓方向按真实负载依次重访。
