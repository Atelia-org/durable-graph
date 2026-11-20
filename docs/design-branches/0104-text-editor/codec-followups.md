# 注册叶之后的两个研究方向

2026-11-20。主线基线为 `475cee24`，其中产品代码来自 `f829f849`。本文给继续研究 codec 的会话留下源码入口、尚待验证的设想和能作出取舍的实验。现行公共合同见 [DB-104](README.md#接入自己的叶类型)。文本工具层可独立按 [memo 用户故事](../0103-durable-text-search/README.md#memo-编辑工具与批量替换预览)推进。

## 方向 1 · 新类型如何登记，热保存的额外分配来自哪里？

**文本和独立位图已经使用同一登记模式。** 普通包宿主依次调用 `TextEditorPersistence.Register(models)` 和 `BitmapPersistence.Register(models)`。类型包各自持有稳定的 codec 实例，传给窄接口 `IDurableLeafRegistration.RegisterLeaf`。宿主选择要启用的类型，类型包保有私有冻结表示。同一实例可重复登记。建议把现有的“类型包提供登记方法”作为命名对照。第三个具体类型可以试写 `RopePersistence.Register(models)`，观察消费者能否从包和类型找到入口，以及作者是否需要复制实质逻辑。若一个包有多个总是一起用的类型，也可实测一个包级入口。若要支持开放泛型 `Rope<T>`，还需扩展类型声明。现有登记和生成器面向公开、顶层、非泛型的 sealed class。[双类型宿主](../../../experiments/RegisteredLeafProbe/Consumer/Program.cs) · [文本登记](../../../src/Text/TextEditorPersistence.cs) · [位图登记和私有 codec](../../../experiments/RegisteredLeafProbe/Bitmap/VariableBitmap.cs) · [公共协议](../../../src/DurableGraph.Serialization/DurableLeafCodec.cs)

**C3 提供了两种共享的分界。** 冻结的 C3 用宽 B+ Rope、不可变子树和跨存储包的节点地址。局改可指向旧叶并记录一个区间 patch。这在原实验中减少了一部分写入，也可能增加冷开必须读取的旧包。若树的所有节点都是一个领域对象的私有内容，可先试把局改编码为“对准确前驱的某个位置执行 patch”，在 `ApplyDelta(previous)` 中重用不可变子树。旧实验对更早存储包的直接寻址在现行叶协议中没有对应入口。第一问是单对象 Base/Delta 能保住多少共享收益。若应用还要求两个独立领域对象持久引用同一个树节点，并在冷开后读到同一 CLR 实例，再用方向 2 的样例验证。历史结果和新 codec 分别计量。

可以从任一主线检出用下面两条命令找回原实现和完整口径。该批本机原始输出在 `W:\DurableTextProbe\storage-policy-leaf-20261117-155156`，仓库内的冻结提交也保留了测量样本。[DB-101 冻结来源](../0101-durable-text/README.md)

```powershell
git show 0ad8b983:experiments/DurableTextProbe/Storage/NativeRope/README.md
git show 0ad8b983:docs/design-branches/0101-durable-text/storage-policy-leaf-results.md
```

**分配的第一个实验可以直接复用旧对照。** 共存提交 `06c57dd4` 上，4,096 和 65,536 个 UTF-16 单元各改八处再保存，注册路径分别多分配 2,280 B 和 5,240 B。128 条里只改一条，多分配 11,592 B。144 对同核相邻提交的耗时相对差中位数为 +2.67%，先后顺序对时间有明显影响。对 4,096 单元、33 片单独准备 Delta 时，两路各分配 **696 B**。先在一次完整提交上按分配类型和调用栈定位，再用分阶段计数或单点改动的前后对照核实来源。把下一次编辑消费收据的成本也计入。[完整周期与复跑命令](coexist-results/README.md) · [孤立编码数据](coexist-results/codec.csv)

| 候选位置 | 值得比较的做法 | 需要保持的性质 |
|---|---|---|
| 同步编码的临时空间 | 当前文本编码已有 `stackalloc`。量出 `ArrayBufferWriter` 扩容和最终 body 复制各占多少，再试小额栈空间、大额池化或由调用方提供 writer | 已返回的 Base/Delta 在规划和写入期间拥有稳定字节。下一次编码及异常归还缓冲后，前一份结果仍可读。[编码器](../../../src/Text/TextBodyCodec.cs) · [Base 所有权](../../../src/DurableGraph.Serialization/PreparedBaseBody.cs) · [Delta 所有权](../../../src/DurableGraph.Serialization/PreparedDeltaBody.cs) |
| 每个对象或每次捕获的开销 | 对照 1 条和 128 条、只改其中一条的调用栈，分别查看冻结对象、值类型装箱、适配记录和收据的分配次数。需要复用时先试操作内的容器，比较常驻容量 | `TFrozen` 可能同时是旧图、未发布候选、Delta 前驱的数据。栈上的 `ref struct` 适合短时传参。若采用线程静态暂存，还需验证同线程嵌套调用及大缓冲滞留。[叶绑定](../../../src/DurableGraph/Runtime/Binding/LeafObjectBinding.cs) · [生命周期回归](../../../tests/DurableGraph.Persistence.Tests/RegisteredLeafLifecycleTests.cs) |

有意义的结果是指出**哪一处分配、每次或每叶多少、改后完整周期省多少**，并用已有准确前驱、Delta 字节、旧版本冷开及故障用例复核。若收益在实现内部即可取得，保留简单的公开签名也是一个可交付的结论。

## 方向 2 · 第三方节点能否持有普通的 DG 图引用？

现有叶可以被字段、List 和 Dictionary 引用，DG 为它保留身份。新协议要让第三方节点也能持有指向独立图对象的槽。内建 List 的 `Capture` 按槽把成员投影为冻结值，`VisitReferences` 在冻结记录上访问引用，`Allocate` 先创建实例，`Hydrate` 再从对象表填回成员。Dictionary 对键和值分别投影，还要处理键校验与 comparer。当前叶在 `Allocate` 中就完成 `Restore`，历史 reader 的 `VisitReferences` 只校验冻结记录形状。设计需要回答三个问题。捕获时谁为边的目标取得身份？读完历史 Base/Delta 后谁枚举并校验边？循环引用何时有可回填的实例？[List 绑定](../../../src/DurableGraph/Runtime/Containers/ListObjectBinding.cs) · [Dictionary 绑定](../../../src/DurableGraph/Runtime/Containers/DictionaryObjectBinding.cs) · [叶绑定及历史 reader](../../../src/DurableGraph/Runtime/Binding/LeafObjectBinding.cs) · [恢复对象表](../../../src/DurableGraph/Runtime/Capture/ObjectReadTable.cs)

**最小见证**可由一个单独的消费程序集提供具体节点类型。普通领域根通过 List 持有父节点 A、B。两者都指向 C，C 有一条回边指向 A。

1. 保存并关闭全部资源。重新打开后，两个父节点读到的 C 满足 `ReferenceEquals`，从 C 沿回边得到的就是列表内的 A。独立打开第二份图，图内仍共享 C，两份图各自可编辑。
2. 只把 B 的边改到 D，保存，再分别冷开两代。旧图中 A、B 仍共享 C。新图中 A→C、B→D、C→A。增加一个含无效目标 ID 或目标类型不一致的读取反例，确认失败发生在图交付给调用方之前。

值得让两个原型跑同一组用例。一种做法由 codec 投影、枚举并回填带类型的槽。另一种由类型作者申报引用槽，DG 保存边并负责枚举与解析，codec 编码节点私有内容。比较第三方要写几遍边逻辑、仅改一条边时的编码、以及历史 reader 能否在领域实例构造前检查引用。若第二代仍需全量 Base，可先报告图身份已走通，再用准确前驱 Delta 检验局改成本。开放泛型、字典键和自定义 comparer 可以等具体消费者出现后再扩大样例。
