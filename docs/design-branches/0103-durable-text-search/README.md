# DB-103 · DurableText 搜索一期设计

一期设计于 2026-11-19 定稿，产品实现与验收待进行。LLM 通过 memo 查询自己的记忆，需要像 `rg -n -C 1` 一样看到命中的条目、行号和原文。DurableText 将对一篇正文提供字面与正则两种 `Search`，并公开带显式上下文的 `VisitSpan`。一期选择单片直借、多片按调用租还、允许同线程重入编辑。现有字面查找与需求来源分别见 [TextSnapshot](../../../src/DurableGraph/TextSnapshot.cs) 和 [memo 下游提案](memo-proposal.md)。

memo 从当前世界及角色选出条目，分别搜索 `FullText`、`Summary`、`Gist`，给摘录加上条目 ID 和字段名。一次调用固定一份正文的版本。角色刚把北门改成南门，当回合搜索就能读到南门。回到旧世界时，memo 传入旧正文便能读到北门。全库遍历顺序、跨条目预算和给 LLM 的最终排版归 memo。

## 对外调用

以下是 `DurableText` 的一期目标签名。示例中的 `Regex` 由调用方构造，可跨条目复用。

```csharp
public TResult VisitSpan<TContext, TResult>(
    TContext context, Func<ReadOnlySpan<char>, TContext, TResult> visitor);

public IReadOnlyList<TextSearchExcerpt> Search(string pattern, int contextLines = 1, int maxMatches = 8);
public IReadOnlyList<TextSearchExcerpt> Search(Regex pattern, int contextLines = 1, int maxMatches = 8);
```

```csharp
var excerpts = entry.FullText.Search("北门仓库");
var nearby = entry.FullText.Search(placeRegex, contextLines: 0, maxMatches: 3);
bool mentioned = entry.FullText.VisitSpan(placeRegex, static (span, regex) => regex.IsMatch(span));
```

两个 `Search` 是在同一次同步全文访问内完成的预制算子。返回前完成枚举、行定位和摘录。每段结果持有可独立使用的字符串和命中元数据。编辑、离开回调或关闭来源仓库后仍可读取。结果的公共读取成员如下，集合只列入本次选中的命中。

| 类型 | 成员 | 含义 |
|---|---|---|
| `TextSearchExcerpt` | `int Start`、`long FirstLine`、`long LastLine`、`string Text`、`IReadOnlyList<TextSearchHit> Hits` | `Start` 是摘录在受搜正文中的绝对偏移。行号是含两端的上下文范围。 |
| `TextSearchHit` | `int Index`、`int Length`、`long FirstLine`、`long LastLine` | 范围为同版正文的 `[Index, Index + Length)`。行号是匹配本身触及的首末行。 |

偏移和长度数 UTF-16 单元，保留原始换行、NUL 和孤立代理。对段内每一项，`excerpt.Text.AsSpan(hit.Index - excerpt.Start, hit.Length)` 都能还原命中，零宽命中也适用。绝对坐标只对产生它的正文版本有意义。

## 搜索、行号和停止条件

1. **匹配。** 字面重载按 Ordinal 从左向右选取不重叠的匹配，跨片或跨行都可命中。空字面串抛 `ArgumentException`，以免误把空工具参数当成处处命中。正则重载直接使用传入实例对完整原文的 `EnumerateMatches(span)`，沿用其方向、零宽推进、换行规则和超时。正则异常传播给调用方。需要自定义重叠策略时可用 `VisitSpan`。[.NET 正则枚举](https://learn.microsoft.com/en-us/dotnet/api/system.text.regularexpressions.regex.enumeratematches?view=net-10.0)
2. **限量。** `maxMatches` 限制每篇、每次调用所选的命中数。按上述匹配器的枚举顺序取前 K 个。取够即停，不再探测第 K+1 项。结果恰含 K 项只能说明已用尽本次名额。随后按正文顺序展示摘录和段内命中，所以 `RightToLeft` 可以先选右端，再从上到下展示所选内容。默认前后各 1 行、最多 8 个命中，调用方可显式覆盖。空结果返回空集合。两个参数均接受零，负值抛 `ArgumentOutOfRangeException`。空引用 `pattern` 抛 `ArgumentNullException`。先校验参数，合法的 `maxMatches: 0` 直接返回空集合。
3. **定位。** 第一行编号为 1。CRLF 算一个行结束符，单独的 CR、LF 也各结束一行，其他 UTF-16 单元作行内内容。行结束符归前一行。末尾换行后的空行、空正文的第 1 行均可承载零宽命中。非空匹配按首单元和末单元定行。零宽位置按其右侧单元定行，EOF 归最后一行。行计数及行号扩展用足够宽的中间值，`contextLines: int.MaxValue` 也应夹到正文首末行。
4. **取上下文。** 每个所选命中的首末行各向外扩 `contextLines` 行。相交或首尾相邻的行区间合成一段，每项命中列一次。摘录保留这些行的全部原文，包括末行原有的行结束符。即使一段上下文中碰巧还有未选中的匹配，其文字照常保留，`Hits` 仅标出已选项。
5. **长行。** 一期采用完整行。合并后各段互不重叠，摘录字符串的总单元数至多为该篇正文长度。一个命中也可能带回整篇单行正文。命中上限无法限制字符或 token 数。memo 需要在分配结果字符串之前限制片段长度时，可用 `VisitSpan` 只提取展示预算内的文字。多片正文仍需先准备完整的输入 span。内置裁短规则留给真实工具调用检验。

实现可先保存至多 K 个范围，再按正文顺序向前扫描换行，合并行区间后一次复制各段。这样不会为每个候选命中反复从头数行或复制同一行。提前结束匹配仍允许继续读取确定行界和上下文所需的文字。

## VisitSpan 的版本与借用

- 方法先校验 visitor，空引用抛 `ArgumentNullException`。捕获进入时的 `TextPieceView`。准备成功后同步调用 visitor 恰好一次，参数依次为整篇正文的 `ReadOnlySpan<char>` 和传入的 context。空正文传空 span。原样返回 `TResult`。普通泛型 `TResult` 让安全 C# 在编译期拒绝直接带出 span 或正则的 ref struct 枚举器。context 按值传递。静态 visitor 可以通过它复用查询、写入调用方的集合，值类型计数器也可以通过返回值带回。
- 单片直接借底层不可变字符串的准确切片，偏移可以非零。多片从 `ArrayPool<char>.Shared` 为本次调用租一块数组，仅复制和传递 `[0, view.Length)`。成功 Rent 后立即进入 `try/finally`，复制和回调均在保护范围内，最终恰好归还本次租到的数组。准备失败则 visitor 未运行。回调异常原样传播，回调内已完成的编辑仍然有效。
- 沿用编辑器的串行使用约定。回调可在同线程编辑同一对象，然后再调用 `VisitSpan` 或 `Search`。内层固定新版，外层直到返回都能读原版。多片嵌套即使恰好同版，也各持独占租借。单片直接读原字符串。调用返回后结束本次借用。这个合同无须编辑锁、跨调用版本缓存或修改持久化路径。[编辑与安装](../../../src/DurableGraph/DurableText.cs) · [不可变片表和切片复制](../../../src/DurableGraph/Runtime/Text/TextPieceView.cs)
- 单片和空正文的输入准备为 O(1)，多片每次准备为 O(n) 时间及 O(n) 临时容量，哪怕 visitor 只取首项。池可能分配、按桶放大并保留数组。嵌套时应合计仍活跃的多片借用。预制算子还承担正则执行、行扫描、命中元数据和返回字符串的成本。

## 可观察的验收样例

最小端到端验收是在编辑产生的多片正文中搜到跨片词，memo 给返回的行号和摘录附上条目 ID 与字段名，呈现可读的记忆证据。同一编辑器在回调中改写后，外层与随后进入的内层分别看到各自进入时的版本。以下判定值供产品实现编写可执行测试。

| 输入与操作 | 应观察到的结果 |
|---|---|
| 在 `"序\n门\n绕到北门\n尾记"` 的偏移 2 插入“北”，再搜“北门” | 默认合成第 1–4 行一段。命中为 `(2,2,第2行)`、`(7,2,第3行)`。取走结果后再编辑，原摘录仍能按偏移切出两处“北门”。 |
| `"a\r\nb\n"`，`contextLines: 0` | 字面 `"\nb"` 为 `(2,2,第1–2行)`。正则 `(?=\n)` 命中 `(2,0,第1行)`、`(4,0,第2行)`。`\z` 命中 `(5,0,第3行)`，摘录正文为空。 |
| `"aaaa"` 搜 `"aa"`，或以 RTL 正则 `aa` 取 1 项 | 字面索引为 0、2。RTL 选索引 2，虽然整行原文也含索引 0 那一处。 |
| `"甲\n甲\n甲"` 以 `RegexOptions.RightToLeft` 搜“甲”，取 2 项、上下文 0 行 | 枚举先取索引 4、2。展示时相邻的第 2–3 行合成一段 `"甲\n甲"`，`Hits` 索引依次为 2、4。 |
| `"😀北北"` 以 `(?=北)` 取前 1 项。空正文以 `\z` 搜索 | 分别得到 `(2,0,第1行)`、`(0,0,第1行)`。无匹配、合法的零名额和空字面模式按各自合同处理。 |
| 单片底串切片。外层读多片“去北门仓库”，在外回调里改为仍是多片的“去南门仓库”，随后进入内层 | 单片只见切片内容。多片内层读到南门后抛错，外层捕获同一异常并继续读北门，退出后编辑器仍为南门。还要覆盖准备阶段异常的归还路径。 |
| 超长无换行正文上有两处命中 | 返回一段完整原文、两个绝对范围。命中数上限与最终输出字数可分别验证。 |

## 选择依据与后续比较

定稿时以 .NET 10.0.5 独立核对了 14 组范围、行号和摘录样例，其中包括 RTL 的选择与展示、CRLF 中间与 EOF 的零宽命中、最大上下文以及超长单行。另一项机制原型验证了单片带偏移、空正文、外层借用期间安装保存视图并编辑、嵌套多片两租两还和异常对象原样传播。这些是设计见证，产品验收仍按上节样例进行。

源码基线 `25105fa4` 的 `Snapshot().IndexOf` 可流式跨片查找。DB-101 已观察到早命中能省去全文复制，重复前缀近末尾命中时，计入物化的标准查找也可能更快。[已有字面搜索测试](../../../tests/DurableGraph.Tests/DurableTextTests.cs) · [DB-101 成本对照](../0101-durable-text/mainline-performance.md)

设计期 .NET 10.0.5 原型用 1000 篇 × 4000 UTF-16 单元的常驻合成正文复用一个正则。每篇 9 片时，逐篇租还和按版本缓存字符串的同版复扫中位各约 1.88 ms 和 1.16 ms。逐篇缓存首扫后相对预热基线多留约 8.02 MB 托管内存。只有两成多片、其余整串单片时，对应为 1.32 ms、1.25 ms 和 1.60 MB。每组取三个进程，只测存在性。真实 memo 中的摘录生成、仓库读取和 LLM 输出成本尚待测量。按调用租还可让编辑器直接发布新版。禁止访内编辑需要在编辑入口与嵌套访问之间维护禁写状态。预收集后倒序回调会失去按需停止，适合另行考虑的批量替换用例。[原提案及备选](memo-proposal.md)

本次按可重设公开 API 的前提立案。新 API 实现后，再单设一阶段，用相同的真实调用任务比较现有搜索相关入口与新算子的易用性和性能。至少覆盖取首项、返回若干行证据、早停、不同碎片比例与重复查询。同时看调用代码、定位正确性、完整耗时、分配与留存。届时逐项决定保留、改内部实现或删除，现阶段搁置这些裁决。[后续路线](../../DurableGraph-research-roadmap.md)
