# DB-103 · DurableText 同步全文访问与搜索用例

`DurableText.VisitSpan<TContext, TResult>` 已在主线实现。它为同步访问固定进入时的一篇正文，由调用方选择字面查找、正则枚举或其他 span 运算。产品与四项专项回归见提交 `8fd71257`。[公开实现](../../../src/DurableGraph/DurableText.cs) · [回归测试](../../../tests/DurableGraph.Tests/DurableTextVisitSpanTests.cs)

## 基础 API

```csharp
public TResult VisitSpan<TContext, TResult>(
    TContext context, Func<ReadOnlySpan<char>, TContext, TResult> visitor);
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

- 方法校验 visitor，空引用抛 `ArgumentNullException`。成功准备输入后同步调用 visitor 恰好一次，空正文也调用一次。context 按值传递，可以是持有结果集合的元组。返回值直接交给调用方。普通泛型 `TResult` 令编译器拒绝将输入 span 或正则的 ref struct 枚举器直接带出回调。
- 空正文传空 span，单片直接借底层不可变字符串的准确切片。多片从 `ArrayPool<char>.Shared` 为本次调用租一块数组，按逻辑长度复制完整 UTF-16 原文，只暴露已填充前缀。成功租借后，准备和调用都受同一个 `try/finally` 保护。正常返回或抛异常都会归还本次租到的数组。准备失败时 visitor 尚未运行。
- 按编辑器的串行使用约定，visitor 可在同线程编辑同一对象并嵌套访问。新调用读新版，外层 span 仍然稳定。多片嵌套各持独占缓冲。visitor 抛出的异常原样传播，已完成的编辑保留。回调外可以保存自有字符串或索引，索引只对受搜的那版正文有意义。
- 空正文和单片的输入准备为 O(1)，多片每次为 O(n) 时间及 O(n) 临时容量，即使 visitor 首项即停也须先准备输入。池可能分配、按桶放大并保留数组。多层访问同时活跃时应合计各层仍持有的缓冲。[编辑与视图切换](../../../src/DurableGraph/DurableText.cs) · [片表与复制](../../../src/DurableGraph/Runtime/Text/TextPieceView.cs)

## 搜索层的用户故事

LLM 用 memo 工具检索自己可见的记忆，需要看到类似 `rg -n -C 1` 的条目、字段、行号和邻近原文。memo 先按当前世界和角色选出条目，再分别查询 `FullText`、`Summary`、`Gist`。当回合改成南门可以立即查到南门，切换旧世界后则由 memo 传入旧正文。条目身份、跨条目预算及最终展示归消费方。[原始下游提案](memo-proposal.md)

设想中的 `Search(string pattern, …)`、`Search(Regex pattern, …)` 及摘录结果说明消费方需要完成的工作。它们可由 DurableGraph 更外层的组件或下游应用承载，是否成为 `DurableText` 成员仍待决定。

- 跨片字面或正则命中仍按原文 UTF-16 偏移定位。需要摘录时在同一版计算行号和范围，并带走自有文本。字面 Ordinal 和复用同一个 .NET 正则都能直接消费 `VisitSpan` 的输入。
- 满足命中数目标就停止枚举。多个命中邻近时合并展示区间，同时保留各命中的绝对位置。正则的选择顺序与最终展示顺序可以不同，例如从右端选两项，再按行号展示。
- 极长单行可能只命中一次。曾讨论给展示摘录增加默认长度上限、围绕命中裁切以及用 ASCII `...` 标出省略处。默认值、超长匹配、重叠窗口和展示坐标归属留给搜索层决定，基础访问仍提供完整原文。

2026-11-19 首版方案在 Git `00638810` 中保留了完整行摘录的候选签名、CRLF 与 EOF 零宽示例及验证范围。后续先决定搜索层归属。新旧搜索入口的易用性与性能比较另作一个阶段，再逐项决定保留、改实现或删除。[后续路线](../../DurableGraph-research-roadmap.md)

## 证据与适用范围

主线使用固定的 Storage 来源完成 Release 根构建，0 警告、0 错误。全量测试 3330 项通过，0 跳过，受测产品与测试源码见 `8fd71257`。四项专项回归覆盖空正文、非零偏移单片共享原字符串、未保存编辑的跨片 Ordinal 与正则命中、内外均多片时重入编辑、异常传播和旧版本稳定。额外用 .NET 10.0.5 的 ArrayPool 事件观察实际产品 DLL，空正文及单片没有租借，内外回调均因同一异常退出时两块不同的 char 数组按逆序各归还一次。复制阶段的异常保护由源码中同一 `try/finally` 覆盖。[专项测试](../../../tests/DurableGraph.Tests/DurableTextVisitSpanTests.cs)

同一提交从独立 checkout 导出本地候选 `0.1.0-db103.g8fd71257.035a7196`，Storage 六包版本 `0.1.3-dev.20261118.g3f780ad`。普通 net10.0 消费者用独立缓存和本地源验证跨片匹配、嵌套编辑及异常身份。实际加载的 runtime DLL 与 nupkg 中的 DLL 的 SHA-256 同为 `75F344C8FBD164523323DC89C4A96D1D1D21212D6C1A60DEA380CF0F2C14D56A`。分别尝试将 span 和 `Regex.ValueMatchEnumerator` 作为 `TResult` 返回，两次独立编译均报 CS9244。本机回执和编译日志位于 `W:/TEMP/db103-pkg-035a7196/`。

设计期 .NET 10.0.5 原型用 1000 篇 × 4000 UTF-16 单元的常驻合成正文复用一个正则。每篇 9 片时，逐篇租还和按版本缓存字符串的同版复扫中位各约 1.88 ms 和 1.16 ms。逐篇缓存首扫后相对预热基线多留约 8.02 MB 托管内存。三进程测的是存在性查询，未测真实 memo 工具调用及生成摘录的性能。流式字面查找与物化后查找的早命中、近末尾对照另见 [DB-101](../0101-durable-text/mainline-performance.md)。
