# Atelia.Text

`TextEditor` 用于串行编辑 UTF-16 文本。内存草稿只需引用 `Atelia.Text` 包即可使用。同一个实例也可以作为 DurableGraph 领域对象的字段持久化。

## 编辑和读取

```csharp
using Atelia.Text;

var draft = new TextEditor("交接地点是北门。");
TextSnapshot before = draft.Snapshot();
int offset = before.IndexOf("北门");
if (offset >= 0 &&
    draft.TryApplyEdits(before, [new TextEdit(offset, 2, "南门")], out _)) {
    Console.WriteLine(draft);  // 交接地点是南门。
    Console.WriteLine(before); // 交接地点是北门。
}
```

- `Replace(start, length, replacement)` 立即使用当前正文的 UTF-16 坐标。空替换串执行删除，零长度范围执行插入。
- `Snapshot()` 以常数时间固定当前版本。快照保留原片段，可以在后续编辑后继续使用 `IndexOf`、`CopyTo` 或 `ToString()`。单个切片可能保留比可见正文更大的底层字符串。
- `TryApplyEdits` 的所有坐标都属于传入的快照。其他编辑器的快照、已经过编辑的版本或相交的删除范围都会导致整批拒绝。失败原因可从 `TextEditFailure.Kind` 读取。同一位置的插入保留原输入顺序，并排在该位置的一项替换之前。
- `VisitSpan` 把进入调用时的完整正文传给一个同步回调，支持静态委托与调用方上下文。多片正文首次访问会复制成连续字符串，后续同版访问可复用。回调在同一线程编辑后，内层访问读到新正文，外层仍读进入时的正文。需要保存摘录时，在回调中复制所选范围。

## 搜索与唯一替换

```csharp
using System.Text.RegularExpressions;
using Atelia.Text;

var log = new TextEditor("晨班走北门\r\n晚班走北门");
var locations = log.Search("北门");                       // 第 1、2 行
var late = log.Search(new Regex(@"晚班走.门"));           // 可复用同一正则
var attempt = log.ReplaceUnique("北门", "南门");           // Ambiguous
var applied = log.ReplaceUnique("晚班走北门", "晚班走南门"); // Replaced
```

字面搜索按 Ordinal 返回非重叠命中。正则沿用调用方的选项及超时，支持零宽匹配。默认至多返回 8 处命中，每处自有摘录至多 120 个 UTF-16 单元。行号从 1 开始，偏移从 0 开始。`HasMore` 表示确实还有命中，摘录两端的截断标志表示相应一侧还有正文。摘录保留原文，可复制进下一次调用。

`ReplaceUnique` 每次在当前正文上检查非空旧串的所有可能起点。`aaa` 里的 `aa` 有两个候选。只有唯一命中才接受替换，空替换串表示删除。歧义回执提供候选前后紧邻的原文。在旧串和新串中保留相同的辨别文字，即可只修改所选位置。

## 为 Agent 提供工具

应用实现 `ITextToolHost`。`GetSearchTargets()` 依当前世界和角色枚举可读字段，每个字段给出地址、条目名、字段名和活的编辑器。`Resolve(target, forEdit)` 按本次读写权限解析地址，不可用时返回 null。例如 `memo:watch/full` 可以由宿主映射到当前工作副本的值，`file:notes.txt` 可以映射到宿主管理的文件缓冲区。工具仅操作解析到的编辑器，提交图和写回文件的时机由宿主决定。

```csharp
using Atelia.Completion.Tools;
using Atelia.Text;

ToolSession session = new ToolRegistry(TextEditorTools.Create(host)).CreateSession();
foreach (var call in action.ToolCalls) {
    var receipt = await session.ExecuteAsync(call, cancellationToken);
    results.Add(receipt.ToToolResult());
}
```

把 `session.VisibleDefinitions` 提供给模型。`search` 接受 `pattern`，可指定 `regex`、`target`、`limit` 和 `snippet_length`。省略地址会搜索宿主提供的可见集合。找到目标后，将回执里的完整地址传给 `replace_unique` 的 `target`，并提供 `old_text`、`new_text`。同一条消息里的调用也要按声明顺序逐笔等待、逐笔传回结果。下一笔可以继续编辑上一笔写出的文字。

工具的 `limit` 范围为 1–32，`snippet_length` 为 1–512。所有返回字段的地址、标签和摘录合计最多 8192 个 UTF-16 单元；JSON 结构和转义另计。`has_more` 和 `limited_by` 指明命中数或总文字预算是否截断了结果。正则使用 .NET 的固定区域性和 250 毫秒匹配超时。若摘录或地址含有孤立代理码元，JSON 工具返回 `unrepresentable_text`，避免把替代字符作为下一次编辑的定位依据。直接调用编辑器可读取准确的 UTF-16 码元。

## DurableGraph 字段

在使用 DurableGraph 的应用中，将 `TextEditor` 作为领域字段类型，并在打开仓库前登记文本 codec。每个负责读写这些模型的注册表都需要这一步。

```csharp
using Atelia.DurableGraph.Persistence;
using Atelia.Text;

var models = new StateModelRegistry();
TextEditorPersistence.Register(models);
// 在同一个 models 上登记使用 TextEditor 字段的领域模型。
```

保存成功后，编辑器在后续编辑或持久化捕获时接受新基线。单纯保存保持已有快照的条件编辑资格。相同 `TextEditor` 实例的多个字段共享对象身份，冷重开后仍可继续增量保存。配套实现与测量见 [DB-104](https://github.com/Atelia-org/durable-graph/blob/master/docs/design-branches/0104-text-editor/README.md)。
