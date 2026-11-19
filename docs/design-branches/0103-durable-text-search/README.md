# DB-103 · DurableText 同步全文访问与搜索用例

`DurableText.VisitSpan<TContext, TResult>` 为同步访问固定进入时的一篇正文，由调用方选择字面查找、正则枚举或其他 span 运算。空正文和单片直接访问，多片正文首次访问后由编辑器缓存连续字符串，供同一内容版本的后续访问复用。[公开实现](../../../src/DurableGraph/DurableText.cs) · [回归测试](../../../tests/DurableGraph.Tests/DurableTextVisitSpanTests.cs)

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
- 按编辑器的串行使用约定，visitor 可在同线程编辑并嵌套访问。内层读取新版，外层的 span 保活其进入时的正文。多层回调同时读不同版本时，各版文字会同时存活。visitor 抛出的异常原样传播，已完成的编辑保留。带出回调的索引只对受搜的那版正文有意义。[编辑与视图切换](../../../src/DurableGraph/DurableText.cs) · [片表与复制](../../../src/DurableGraph/Runtime/Text/TextPieceView.cs)

## 搜索层的用户故事

LLM 用 memo 工具检索自己可见的记忆，需要看到类似 `rg -n -C 1` 的条目、字段、行号和邻近原文。memo 先按当前世界和角色选出条目，再分别查询 `FullText`、`Summary`、`Gist`。当回合改成南门可以立即查到南门，切换旧世界后则由 memo 传入旧正文。条目身份、跨条目预算及最终展示归消费方。[原始下游提案](memo-proposal.md)

设想中的 `Search(string pattern, …)`、`Search(Regex pattern, …)` 及摘录结果说明消费方需要完成的工作。它们可由 DurableGraph 更外层的组件或下游应用承载，是否成为 `DurableText` 成员仍待决定。

- 跨片字面或正则命中仍按原文 UTF-16 偏移定位。需要摘录时在同一版计算行号和范围，并带走自有文本。字面 Ordinal 和复用同一个 .NET 正则都能直接消费 `VisitSpan` 的输入。
- 满足命中数目标就停止枚举。多个命中邻近时合并展示区间，同时保留各命中的绝对位置。正则的选择顺序与最终展示顺序可以不同，例如从右端选两项，再按行号展示。
- 极长单行可能只命中一次。曾讨论给展示摘录增加默认长度上限、围绕命中裁切以及用 ASCII `...` 标出省略处。默认值、超长匹配、重叠窗口和展示坐标归属留给搜索层决定，基础访问仍提供完整原文。

2026-11-19 首版方案在 Git `00638810` 中保留了完整行摘录的候选签名、CRLF 与 EOF 零宽示例及验证范围。便捷搜索层的归属及真实查询轨迹仍待确定。[后续路线](../../DurableGraph-research-roadmap.md)

## 证据与适用范围

缓存版产品和回归来自 `ea8f9236`。固定 Storage 六包对应的源码构建 Release 根 solution，0 警告、0 错误；全量测试 3335 项通过，0 跳过。新增回归检查跨独立访问的存储身份、保存压平、同内容多片重绑、准备过期捕获，以及两种编辑入口在旧快照存活时释放旧缓存。独立普通包消费者观察到同版嵌套 span 的地址相同，改写后新版地址和正文都发生变化，内层抛错后外层仍读到旧版。7 单元三片正文的首次和第二次顶层访问分别分配 40 B、0 B。[专项回归](../../../tests/DurableGraph.Tests/DurableTextVisitSpanTests.cs) · [包来源与字面查找复测](indexof-cache-comparison.md)

初版 `8fd71257` 采用逐次租还，固定 Storage 来源的 Release 根构建通过，全量测试 3330 项通过。四项专项回归覆盖空正文、非零偏移单片、未保存编辑的跨片 Ordinal 与正则命中、重入编辑和异常传播。[专项测试](../../../tests/DurableGraph.Tests/DurableTextVisitSpanTests.cs)

同一提交从独立 checkout 导出本地候选 `0.1.0-db103.g8fd71257.035a7196`，Storage 六包版本 `0.1.3-dev.20261118.g3f780ad`。普通 net10.0 消费者用独立缓存和本地源验证跨片匹配、嵌套编辑及异常身份。实际加载的 runtime DLL 与 nupkg 中的 DLL 的 SHA-256 同为 `75F344C8FBD164523323DC89C4A96D1D1D21212D6C1A60DEA380CF0F2C14D56A`。分别尝试将 span 和 `Regex.ValueMatchEnumerator` 作为 `TResult` 返回，两次独立编译均报 CS9244。本机回执和编译日志位于 `W:/TEMP/db103-pkg-035a7196/`。

设计期 .NET 10.0.5 原型用 1000 篇 × 4000 UTF-16 单元的常驻合成正文复用一个正则。每篇 9 片时，逐篇租还和按版本缓存字符串的同版复扫中位各约 1.88 ms 和 1.16 ms。逐篇缓存首扫后相对预热基线多留约 8.02 MB 托管内存。三进程测的是存在性查询，未测真实 memo 工具调用及生成摘录的性能。流式字面查找与物化后查找的早命中、近末尾对照另见 [DB-101](../0101-durable-text/mainline-performance.md)。
