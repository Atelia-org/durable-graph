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
