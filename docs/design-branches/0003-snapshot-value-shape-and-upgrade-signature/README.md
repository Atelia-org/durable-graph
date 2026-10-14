# DB-003：Snapshot 值形状与 Upgrade 签名

> 历史迁移（2026-10-14）：原路径 `docs/design-branches/0003-snapshot-value-shape-and-upgrade-signature.md`；来源修订 `8ef9fca`，正文保留原时期语义。
> 适用范围：legacy mutable Snapshot / in-out 已采用的历史材料；性能与长期 ABI 未冻结，新 DTO 路线另见后继分片。 今日事实从 [PROJECT-STATE](../../../src/PROJECT-STATE.md) 续接。

> 历史材料（2026-09-06 治理）：原 Open 标签保留；本文的 legacy mutable Snapshot/in-out 已用于产品，
> 不代表新 readonly DTO 路径已有 Upgrade。未来问题统一见[路线图](../../DurableGraph-research-roadmap.md)，正文保留当时取舍与证据。

> 状态：Open
>
> 创建日期：2026-08-27
>
> 当前快速原型选择：generated ordinary mutable `struct` Snapshot；版本限定的 required partial `void UpgradeV1ToV2(in oldValue, out newValue)`。

> 实现状态：EXP-009 已把该形状用于 generated read-time upgrade coordinator，并由 V1→V2→V3、CS8795、CS0177 与 exception-path tests 验证；性能和长期 ABI 仍未冻结。

2026-09-05 校准：本文的 mutable Snapshot/out Upgrade 仍描述 legacy boxed 路径。
用户已选择保存管线先捕获状态，[DB-022](../0022-versioned-state-dto-capture/README.md)在 SchemaOnly + GenerateBinaryBody
路径生成 readonly scalar DTO 与 typed binary Read/Write；该路径尚无 Upgrade，不能混同两者的 API。

## 问题

historical Snapshot 是升级 handler 的强类型数据面。它需要在不复制旧领域类型及其方法的前提下表达 exact historical durable fields，同时避免把 boxed state 或未来 wire format 暴露给用户代码。

本分叉比较：

- Snapshot 使用 class、ordinary struct、readonly/record struct 还是 `ref struct`；
- Upgrade 通过返回值还是 `out` 传递目标 Snapshot；
- handler 使用 `void`、`bool`/Try pattern 还是 richer result；
- 方法名是否依赖返回类型或 out target type 区分版本边。

## 已观察到的 C# 事实

`experiments/SnapshotUpgradeShapeProbe/` 固定了以下结果：

1. ordinary struct 可以通过直接强类型 `in`/`out` 调用完成 V1 → V2。
2. 当实现逐个写入可见 struct fields 时，漏写一个目标 field 会产生 CS0177。
3. `newValue = default` 可以绕过逐字段 definite-assignment tripwire；它不保证 nullability、范围或领域 invariant。
4. 带显式 accessibility 的 partial method 缺少 implementation 会产生 CS8795。
5. 返回类型不参与 method overload；仅返回 V2/V3 的两个 `Upgrade(V1)` 会产生 CS0111。
6. `out V2`/`out V3` 可以区分 overload，但 `out var` 调用会产生歧义。
7. 含 `string` 的 managed Snapshot 不能作为 `stackalloc` element type。

ordinary struct 表示值直接存储；它不等于语言保证的“总在栈上”。Snapshot 被转成 `object`/接口时仍会 boxing，string 所指向的对象也仍在托管堆上。因此当前选择依据首先是值语义与逐字段赋值检查，不是已经测得的性能收益。

## 当前快速原型选择

概念形状：

```csharp
private struct __SnapshotV1 {
    public string Field1;
}

private struct __SnapshotV2 {
    public string Field1;
    public bool Field2;
}

private static partial void UpgradeV1ToV2(
    in __SnapshotV1 oldValue,
    out __SnapshotV2 newValue);
```

约束：

- Snapshot 与 handler 放在当前 durable partial type 的 generated private scope，暂不成为公共 API。
- 成员使用 `Field{FieldId}` 机械名称；领域字段改名和同名不改变 historical C# surface。
- generated pipeline 只使用强类型 locals 和 direct calls，不把 Snapshot 放入 `object`、非泛型 registry 或 interface。
- 正常返回表示 edge 成功且 out 已赋值；失败通过异常传播，后续可由 generated caller 补充 SchemaId/from/to 上下文。
- 只生成唯一相邻 edge；方法名显式包含 `VnToVn+1`，不依赖返回类型区分 overload。
- 这是可逆原型选择，不是分配、ABI 或最终公共 handler contract。

## 竞争方案

### A：`bool TryUpgrade(in old, out next)`

优点：可以区分“有效旧记录无法表示为新版”的预期拒绝与 handler bug。

代价：false 路径仍必须给 out 赋值，通常只能写入应被忽略的 default；bool 不携带拒绝原因。当前没有 batch continue、拒绝聚合或合法记录不可升级的消费者。

重访触发：出现首个结构合法、语义合法但允许被确定性拒绝的历史记录，而且拒绝属于正常控制流。届时同时比较 bool 与带诊断的 result。

### B：readonly/record struct + 返回新值

优点：不可变值语义清晰；全字段构造器可以让新增参数触发编译错误；调用形式简洁。

代价：`default` 仍始终合法；record equality/deconstruction/formatting 当前没有消费者。返回值复制通常可被 JIT 优化，但不是语言保证。

重访触发：真实 handler 表明 generated all-field constructor/init 比 out 的逐字段赋值更强或更易用，并且版本限定方法名已经消除 return-overload 碰撞。

### C：class + 返回新值

优点：API 最普通，适合大 shape，引用复制便宜。

代价：每个中间版本通常需要短命对象外壳；引入 null 与引用身份。它仍能保持 handler 强类型，所以是 struct 实验失败时的简单退路。

### D：`ref struct`

它能提供真正 stack-only 的语言约束，但会限制数组、字段、boxing、async、iterator、delegate 和普通泛型组合。当前 direct typed pipeline 不需要这些额外限制，因此暂缓。

## 跳版本与签名碰撞

用户指出的返回类型碰撞在统一 `Upgrade(V1)` 命名下成立。但当前版本限定名称已经避免碰撞：

```text
UpgradeV1ToV2
UpgradeV1ToV3
```

`out` 仍为未来同名 overload 留有表达力，但不构成现在开放跳边或一般图的依据。

## 重访触发条件

- 测量表明 Snapshot wrapper allocation、struct copies 或 stack pressure 是实际瓶颈。
- Snapshot shape 大到 ordinary struct 的复制/清零成本不可忽略。
- 出现 expected upgrade rejection，需要 Try/result 语义。
- handler 需要跨程序集成为稳定公共 ABI。
- runtime registry/plugin 迫使 Snapshot 进入 type-erased container。

## 相关材料

- `experiments/SnapshotUpgradeShapeProbe/README.md`
- `docs/design-branches/0002-read-time-version-upgrade-pipeline.md`
- `docs/design-branches/0005-durable-inheritance-flattening.md`
