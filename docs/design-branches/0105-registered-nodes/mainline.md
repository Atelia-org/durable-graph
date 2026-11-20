# 登记含图引用的类型

第三方包可以引用 `Atelia.DurableGraph`，让自己的类成为图中的节点。领域模型使用该 CLR 类型声明字段，数组、List 与 Dictionary 也可以引用它。同一图中的引用共享实例，独立打开的图各自恢复可编辑实例。图根沿用生成模型的 `IDurableObject` 合同。

## 类型包怎样登记

例如一家笔记应用要让页面之间互相引用。类型包声明当前布局，并将同一份声明交给使用它的宿主。

```csharp
using Atelia.DurableGraph.Runtime;
using Atelia.DurableGraph.Schema;
using Atelia.DurableGraph.Serialization;

[DurableNode("Notes.Page", 1)]
public sealed class Page {
    public int Score { get; set; }
    public Page? Link { get; set; }
}

public static class PagePersistence {
    private static readonly DeclaredNode<Page> Declaration = new(static () => new Page(), [
        DeclaredMember<Page>.Int32(p => p.Score, (p, value) => p.Score = value),
        DeclaredMember<Page>.Reference<Page>(TypeExpr.Named("Notes.Page"),
            p => p.Link, (p, target) => p.Link = target),
    ]);

    public static void Register(IDeclaredNodeRegistration models) => models.RegisterNode(Declaration);
}
```

宿主在打开 Repository 前执行 `PagePersistence.Register(models)`。同一登记表可以重复登记同一个声明实例。节点应是公开、顶层、密封、非泛型的类，持久家族名由类型包独占。分配函数每次创建一个新实例。DG 先分配可达对象再回填槽，setter 此时可以接收一个稍后才会完成回填的目标。[公共声明](../../../src/DurableGraph/Runtime/Binding/DeclaredNode.cs) · [完整跨包示例](../../../experiments/PackageConsumerProbe/RegisteredNodes/README.md)

| 作者声明 | 落盘及恢复语义 |
|---|---|
| `Int32` | 有符号整数。 |
| `Reference<TTarget>` | 一个可空引用。持久类型表达式约束实际目标，CLR 访问器须能接收该目标。要共享数组、List 或 Dictionary 实例时，登记对集合本身的单引用。 |
| `References<TTarget>` | 节点拥有一个有序的内嵌引用序列。保留 null、重复和别名，回填时创建元素数组。长度相同的两版可用稀疏位置补丁。 |
| `Private<TValue>` | 作者给出稳定格式名、默认值和读写函数。DG 为整个私有值加长度并冻结字节。发生变化时提交整块替换候选。读函数验证并消费整个值，返回可直接丢弃的数据。图出边须登记在引用槽中。 |

例如，页面包的读函数验证评分范围并返回一个 `byte`。格式构造、保存和历史重放期间，DG 都可能调用读函数并丢弃结果，只有回填时才调用字段的 setter。读函数也可以返回独立的托管数组。若解码临时借用缓冲区，作者须在回调内归还，包括异常路径。若应用需要打开文件，可把文件标识存入私有槽，等整张图成功交付后再由应用打开并释放文件。[页面包的评分格式](../../../experiments/PackageConsumerProbe/RegisteredNodes/Page.cs) · [公开读取合同](../../../src/DurableGraph/Runtime/Binding/DeclaredPrivateFormat.cs)

## 从 v1 删除 Link

v2 保留同一家族名，把属性版本改为 2，在成员数组中仅保留 `Score`，并把 `1` 加入 `DeclaredNode<Page>` 构造函数的 `acceptedVersions`。v2 可以删除 CLR 属性 `Link`，也可以单独发包。新读者无需引用 v1 程序集。

每条对象链的 Base 定位到其写入时的 Schema，描述符连同有序槽、引用目标或私有格式名一起持久化。DG 按这个描述重放 Base 和 Delta，然后在所选 State 的完整目录上检查最终仍存在的历史边。即使该节点已经不可达，或 v2 随后会丢弃这条边，悬空或错型引用也会使加载在分配领域对象前失败。某个引用若已被 Delta 改写，所选目录只需包含它的最终目标。校验之后才进行版本投影；用 v2 保存升级后的节点时会写当前布局的 Base。[旧目录回归](../../../tests/DurableGraph.Persistence.Tests/RegisteredNodeHistoricalReferenceTests.cs) · [相同字节的整数与引用](../../../tests/DurableGraph.Persistence.Tests/RegisteredNodeEqualBytesTests.cs)

显式接纳版本表示作者保证共同前缀的业务含义一致。保留位置需有相同种类、引用目标或私有格式名。末尾新增槽分别得到零、null、空序列或已冻结的私有默认值。历史私有槽需要当前位置仍提供同名格式的 reader，Base 和每次更新该槽的 Delta 都要通过它的校验。两个相同类型的槽互换业务含义，单凭格式无法判别。[私有槽回归](../../../tests/DurableGraph.Persistence.Tests/RegisteredNodeStoredPrivateTests.cs)

## 主线验收

主线产品提交为 `ffa19b45`，Storage 提交为 `3f780ad9`，.NET SDK 为 10.0.201。Release 根方案构建零警告、零错误，五个测试程序集通过 3,455 项，零跳过。Persistence 1,321 项、Runtime/Generator 1,850 项、Serialization 133 项、Storage 36 项、Text 115 项。`e48dd56c` 收录普通包探针和交替读取旧目录的回归，根方案重新构建通过，改过的 Persistence 测试程序集再次通过全部 1,321 项。

- 两个同名同版 v1 仓的节点实际写出相同的 Base 和 Delta。对于 Delta 的最后一个字节 `04`，整数仓解出 2，引用仓要求对象 4 存在且为 string。同一个 v2 登记正确读取两个健康仓，目录缺少对象 4 的引用仓在分配前拒绝。[双仓回归](../../../tests/DurableGraph.Persistence.Tests/RegisteredNodeEqualBytesTests.cs)
- 4,096 个位置的内嵌引用序列只改中间一项。已提交批里只有一条 Delta，检查了补丁位置、目标 ID 和准确前驱，其余对象保留原来的头地址。再次保存没有业务对象记录。两个独立冷开的图分别保留自己的可变状态。[宽节点与图身份](../../../tests/DurableGraph.Persistence.Tests/DeclaredNodeGraphTests.cs)
- 从 v1 分叉成工作副本，以 v2 直接保存。原节点 ID 得到当前版本的单条 Base，退役边独占的目标退出新目录。旧提交用 v1 仍然冷开可读。同一打开的仓交替读取健康和损坏的旧目录，每次都按本次选择的目录复核节点引用。[历史分叉与坏目录](../../../tests/DurableGraph.Persistence.Tests/RegisteredNodeHistoricalReferenceTests.cs)
- 用本地候选包分别构建第三方 v1 和 v2 模型包，再以普通 PackageReference 编译两个宿主。四个独立进程依次写入 10、21，用 v2 冷开并改边续写 34，用 v2 回读三代，最后用 v1 复读前两代及旧引用。每个宿主解析 12 个包，v2 的依赖和实际加载程序集只含 v2 节点包。候选版本、包和 DLL 的 SHA-256 及各进程读数保存在[包验收回执](package-verification.json)。

原型中的两种编码对照、116 B 首次目录开销及万槽工况的分配测量保留在[研究记录](stored-slots.md)。主线验收逐项检查实际提交和冷开行为，本轮没有重新采样分配和耗时。
