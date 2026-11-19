# DB-103 · DurableText 同步全文访问与搜索用例

DB-103 的同步访问、快照查找和条件编辑合同已迁入独立包 `Atelia.Text`。以下基础 API 可用于内存草稿和登记为图字段的同一个编辑器。性能表与普通包回执记录的是迁移前的 `DurableText` 版本。[现行接入](../../text/README.md) · [持久化与历史源码](../0104-text-editor/README.md#native-text-sources)

`TextEditor.VisitSpan<TContext, TResult>` 为同步访问固定进入时的一篇正文，由调用方选择字面查找、正则枚举或其他 span 运算。空正文和单片直接访问，多片正文首次访问后由编辑器缓存连续字符串，供同一内容版本的后续访问复用。[公开实现](../../../src/Text/TextEditor.cs) · [回归测试](../../../tests/Text.Tests/TextEditorVisitSpanTests.cs)

常驻 memo 语料的同版复扫与少量编辑后再扫适合使用缓存版 `VisitSpan`。大而碎的正文首次访问且能早停、单片周期文本的特定模式仍有利于快照查找，本轮保留 `TextSnapshot.IndexOf` 的流式 KMP。[缓存版与 KMP 对照](indexof-cache-comparison.md) · [逐次租还版本的历史基线](indexof-comparison.md)

## 基础 API

本轮考察少编辑、多次查询的常驻正文。验收条件是多片正文同版复访共用一份连续文本，编辑后重新访问读到新版，外层 visitor 继续读旧版。字面检索分别测首次访问、同版复扫和编辑后首扫，并与已取得的同版快照 `IndexOf` 对照。

```csharp
public TResult VisitSpan<TContext, TResult>(
    TContext context, Func<ReadOnlySpan<char>, TContext, TResult> visitor)
    where TContext : allows ref struct;
```

用静态回调传入查询。同一个正则可扫过多篇正文，在匹配附近截取需要带走的文字，或在第一项处停止。例如以下两个调用分别返回字面位置和首个正则匹配的自有字符串。

```csharp
int location = text.VisitSpan("北门仓库",
    static (span, literal) => span.IndexOf(literal, StringComparison.Ordinal));

string? first = text.VisitSpan(regex, static (span, query) => {
    foreach (var hit in query.EnumerateMatches(span)) {
        return span.Slice(hit.Index, hit.Length).ToString();
    }
    return (string?)null;
});
```

- 方法校验 visitor，空引用抛 `ArgumentNullException`。成功准备输入后同步调用 visitor 恰好一次，空正文也调用一次。context 按值传递，可以是持有结果集合的元组、查询词的 `ReadOnlySpan<char>` 或调用方提供的栈上结果缓冲。普通泛型 `TResult` 令编译器拒绝将输入 span 或正则的 ref struct 枚举器直接带出回调。
- 空正文传空 span，单片直接借底层不可变字符串的准确切片。多片首次访问花 O(n) 时间复制到等长的字符串，准备成功后才调用 visitor。同版复访准备输入只需 O(1)，适用于相同或不同的查询。
- 每个编辑器只为已访问的当前多片版本保留一个额外字符串，正文载荷为每个 UTF-16 单元两字节。`ToString()` 优先使用现有缓存，单独调用它时不会建立访问缓存。成功编辑时释放旧缓存引用，按需物化新版。保存仅重绑同版多片时可继续复用；保存需要压成单片时可直接采用已有的同版字符串。快照及持久化差分仍使用各自捕获的片表。
- 按编辑器的串行使用约定，visitor 可在同线程编辑并嵌套访问。内层读取新版，外层的 span 保活其进入时的正文。多层回调同时读不同版本时，各版文字会同时存活。visitor 抛出的异常原样传播，已完成的编辑保留。带出回调的索引只对受搜的那版正文有意义。[编辑与视图切换](../../../src/Text/TextEditor.cs) · [片表与复制](../../../src/Text/TextPieceView.cs)

## 按快照提交范围编辑

`bool TryApplyEdits(TextSnapshot expected, IReadOnlyList<TextEdit> edits, out TextEditFailure failure)` 只接受该编辑器同一内容版本上定位的 UTF-16 范围。快照可跨保存重绑和压平使用。另一编辑器的快照或在实际编辑之前取得的快照返回 `StaleSnapshot`，即使两个编辑器从同一状态恢复，或正文改动后又改回原样。验收以重复词旧坐标拒写、同版保存后准确引用新基线、失败时零发布以及有序范围一次构造片表为准。

输入可乱序。非空删除范围可以首尾相接，相交或在删除范围内部插入则返回 `ConflictingEdits` 和一对冲突的原输入下标。同一点的插入保持输入顺序并先于该点的非空替换。范围有效且零删零增的项忽略，整批为空也可成功。单项无效时报告字段对应的原因及原下标，结果长度超过 `int.MaxValue` 时报告 `ResultTooLong`。传入空引用参数抛出 `ArgumentNullException`。返回 false 时本次调用写入零笔，读取外部列表期间发生的重入编辑仍保留。

已按正文顺序给出且可直接执行的范围，只复制和校验输入，再顺序构造一次片表。其他顺序才按原下标保序整理。批内冲突由调用方决定如何处理。[范围整理](../../../src/Text/TextEditBatch.cs) · [快照与发布](../../../src/Text/TextEditor.cs)

## 搜索层的用户故事

通用文本库与工具的分工见 [DB-104](../0104-text-editor/README.md)。以下搜索行为仍用作单篇编辑器和下游 memo 的验收场景。

LLM 用 memo 工具检索自己可见的记忆，需要看到类似 `rg -n -C 1` 的条目、字段、行号和邻近原文。memo 先按当前世界和角色选出条目，再分别查询 `FullText`、`Summary`、`Gist`。当回合改成南门可以立即查到南门，切换旧世界后则由 memo 传入旧正文。条目身份、跨条目预算及最终展示归消费方。[原始下游提案](memo-proposal.md)

设想中的 `Search(string pattern, …)`、`Search(Regex pattern, …)` 及摘录结果说明消费方需要完成的工作。单篇搜索、替换与下游工具展示的分工留待后续实验决定。

- 跨片字面或正则命中仍按原文 UTF-16 偏移定位。需要摘录时在同一版计算行号和范围，并带走自有文本。字面 Ordinal 和复用同一个 .NET 正则都能直接消费 `VisitSpan` 的输入。
- 满足命中数目标就停止枚举。多个命中邻近时合并展示区间，同时保留各命中的绝对位置。正则的选择顺序与最终展示顺序可以不同，例如从右端选两项，再按行号展示。
- 极长单行可能只命中一次。曾讨论给展示摘录增加默认长度上限、围绕命中裁切以及用 ASCII `...` 标出省略处。默认值、超长匹配、重叠窗口和展示坐标归属留给搜索层决定，基础访问仍提供完整原文。

2026-11-19 首版方案在 Git `00638810` 中保留了完整行摘录的候选签名、CRLF 与 EOF 零宽示例及验证范围。便捷搜索层的归属及真实查询轨迹仍待确定。[后续路线](../../DurableGraph-research-roadmap.md)

## memo 编辑工具与批量替换预览

- 后续一起研究 `Search(string pattern, …)`、`Search(Regex pattern, …)` 和 `ReplaceUnique(string oldStr, string newStr)`。查询返回带行号的有界摘录。唯一替换在指定条目和字段的当前正文中按 Ordinal 查找非空旧串，恰有一个匹配起点才写入；空新串表示删除。`aaa` 中的 `aa` 有两个起点。工具分别回报成功、未找到或有多个候选。
- 同一目标的多次唯一替换按工具调用声明顺序独立执行，每笔读取前一笔完成后的正文并产生自己的回执。某笔未写入时，后续调用继续按当前正文判断。相同起始状态和调用序列在没有其他写入时，与拆成多个模型回合执行得到相同结果。
- `ReplaceAll(string/Regex, …)` 的预览另行研究。先用真实 tool-loop 比较怎样展示候选、选择部分命中以及跨回合确认。确认时提交预览所依据快照上的那批范围，过期则重新预览。工具归属、预览交互和正则替换表达式留待该阶段确定。

## 证据与适用范围

`0c39ea1a` 的 Release 根构建 0 警告、0 错误，固定 Storage 源码模式的全量测试通过 3,352 项，0 跳过。当时的字面查找探针分别加载变更前后的 Release DLL，均通过全部 162 个工况的正文、命中与语义检查。

条件编辑对照在 Windows x64、.NET 10.0.5 Release、关闭分层 JIT 的条件下运行。旧版 `bbb6c2c4` 与新版各用三个独立进程，每进程预热三轮、记录九轮，共 540 批，逐份核对正文与预期结果。下表为预持快照和范围数组的一次提交，按进程取中位数；分配量在调用线程测量。

| 正文单元 / 片数 / 编辑数 | 输入 | 旧版 µs / B | 新版 µs / B |
|---|---|---:|---:|
| 4,096 / 9 / 8 | 有序 | 1.47 / 2,104 | 0.56 / 880 |
| 65,536 / 65 / 128 | 有序 | 19.14 / 28,504 | 6.87 / 9,904 |
| 65,536 / 65 / 128 | 同一组范围打乱 | 25.80 / 28,504 | 17.61 / 13,000 |
| 65,536 / 65 / 129 | 打乱并加入一项冲突 | 15.54 / 3,912 | 11.42 / 5,208 |

将首次获取快照也计入新版有序路径，两种规模各多分配 56 B。大批次冲突的诊断比旧版捕获异常多分配 1,296 B，可取得冲突双方的输入下标。以上是内存中给定坐标的提交成本；查找、生成预览和持久化不在计时内。

条件编辑来自 `0c39ea1a`。普通包 `0.1.0-db103.g0c39ea1a.29290c02` 使用固定 Storage 六包 `0.1.3-dev.20261118.g3f780ad`。独立消费者完成 seed、resume、verify 三进程的 13、18、11 项检查，覆盖保存前取得的快照、过期快照、独立恢复对象之间的拒写及冲突下标。实际加载 runtime DLL 的 SHA-256 为 `0A6B5ABEA2356D3F10E902D751DD12BF43AE824F1B194542978E195E7C4E93BA`，同包 XML 交付新签名。在该 DLL 上另外穷举三单元正文的 13,824 组三项编辑，逐字符核对 7,296 组成功结果及 6,528 组冲突的原始下标和零写入。[回归测试](../0104-text-editor/README.md#native-text-sources "Git 06c57dd4:tests/DurableGraph.Tests/DurableTextTests.cs") · [普通消费方](../0104-text-editor/README.md#native-text-sources "Git 06c57dd4:experiments/PackageConsumerProbe/DurableTextConsumer/README.md")

公开 API 修订来自 `a66b1ee6`。Release 根构建 0 警告、0 错误，全量测试 3338 项通过，0 跳过。独立 checkout 导出的普通包 `0.1.0-db103.ga66b1ee6.0a8577ed` 在隔离还原后，runtime DLL 旁的 XML 与包内文件一致。消费者用切片查询词命中跨片文本，用栈上结果缓冲取回位置。分别尝试返回 span 和正则匹配枚举器均报 CS9244。4096 个 UTF-16 单元的多片正文经一次访问后，连续 100 次 `ToString()` 在调用线程分配 0 B。包消费者还验证了 `aaa` 中的 `aa` 有两个匹配起点，唯一替换会拒绝写入。[消费方回归](../0104-text-editor/README.md#native-text-sources "Git 06c57dd4:experiments/PackageConsumerProbe/DurableTextConsumer/README.md") · [现行文本包入门](../../text/README.md)

缓存版产品和回归来自 `ea8f9236`。固定 Storage 六包对应的源码构建 Release 根 solution，0 警告、0 错误；全量测试 3335 项通过，0 跳过。新增回归检查跨独立访问的存储身份、保存压平、同内容多片重绑、准备过期捕获，以及两种编辑入口在旧快照存活时释放旧缓存。独立普通包消费者观察到同版嵌套 span 的地址相同，改写后新版地址和正文都发生变化，内层抛错后外层仍读到旧版。7 单元三片正文的首次和第二次顶层访问分别分配 40 B、0 B。[专项回归](../0104-text-editor/README.md#native-text-sources "Git 06c57dd4:tests/DurableGraph.Tests/DurableTextVisitSpanTests.cs") · [包来源与字面查找复测](indexof-cache-comparison.md)

初版 `8fd71257` 采用逐次租还，固定 Storage 来源的 Release 根构建通过，全量测试 3330 项通过。四项专项回归覆盖空正文、非零偏移单片、未保存编辑的跨片 Ordinal 与正则命中、重入编辑和异常传播。[专项测试](../0104-text-editor/README.md#native-text-sources "Git 06c57dd4:tests/DurableGraph.Tests/DurableTextVisitSpanTests.cs")

同一提交从独立 checkout 导出本地候选 `0.1.0-db103.g8fd71257.035a7196`，Storage 六包版本 `0.1.3-dev.20261118.g3f780ad`。普通 net10.0 消费者用独立缓存和本地源验证跨片匹配、嵌套编辑及异常身份。实际加载的 runtime DLL 与 nupkg 中的 DLL 的 SHA-256 同为 `75F344C8FBD164523323DC89C4A96D1D1D21212D6C1A60DEA380CF0F2C14D56A`。分别尝试将 span 和 `Regex.ValueMatchEnumerator` 作为 `TResult` 返回，两次独立编译均报 CS9244。本机回执和编译日志位于 `W:/TEMP/db103-pkg-035a7196/`。

设计期 .NET 10.0.5 原型用 1000 篇 × 4000 UTF-16 单元的常驻合成正文复用一个正则。每篇 9 片时，逐篇租还和按版本缓存字符串的同版复扫中位各约 1.88 ms 和 1.16 ms。逐篇缓存首扫后相对预热基线多留约 8.02 MB 托管内存。三进程测的是存在性查询，未测真实 memo 工具调用及生成摘录的性能。流式字面查找与物化后查找的早命中、近末尾对照另见 [DB-101](../0101-durable-text/mainline-performance.md)。
