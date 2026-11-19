# DurableText 主线接入与消费合同

研究稿及评审位置见[本片入口](README.md)。本文描述 DB-101/103 时期的内建类型。现行图字段改用[注册叶 TextEditor](../0104-text-editor/README.md)。当时的类型位于 `Atelia.DurableGraph`。领域模型可声明 `DurableText? Body`，普通 string 字段继续适用于一句话 Gist。多个字段或容器元素可指向同一正文。同一版本的独立 checkout 分别持有自己的可编辑对象。

## 编辑和读取

`Replace(start, length, replacement)` 使用当前正文坐标。`TryApplyEdits(expected, edits, out failure)` 使用本编辑器快照上的 UTF-16 范围，整批成功才发布。输入可乱序，同点插入保持输入顺序并排在该点的非零替换之前。孤立代理、NUL、CR/LF 都是可保存内容。[条件编辑合同](../0103-durable-text-search/README.md#按快照提交范围编辑)

```csharp
var before = entry.Body.Snapshot();
int first = before.IndexOf(needle);
if (needle.Length == 0 || first < 0 || before.IndexOf(needle, first + 1) >= 0) {
    throw new InvalidOperationException("Expected exactly one matching start position for a non-empty needle.");
}
entry.Body.Replace(first, needle.Length, replacement);
entry.Body.AppendTo(contextBuilder);
```

这段唯一替换配方按起点计数。例如 `aaa` 中的 `aa` 在 0 和 1 都匹配，所以拒绝改写。

这段配方在一次串行工具调用中完成查找和替换。若基于旧搜索结果或预览延后提交坐标，应把当时捕获的快照交给 `TryApplyEdits`。稳定快照可延后追加到调用方的 StringBuilder；短窗口用 CopyTo。同一份快照上的完整正则搜索可以一次 ToString 后反复使用标准 .NET Regex，匹配区间属于该快照。大量长期保留的大快照可能同时持有多个字符 backing。

## 保存与恢复

可编辑对象、不可变视图与持久对象版本分别承担编辑、内容读取和重建依赖。图级捕获拥有 ObjectId、准确前驱和安装时机。共享同一 CLR 文本的两个 graph builder 可能有不同 prior，只有来源身份经验证匹配时才能复用 Piece 的 Copy 区间。来源不匹配时按实际 prior 比较，必要时以完整新内容编码。净零编辑可复用已提交对象，强制 Base 和 NoChange 的维护写沿用图级策略。

文本 Base 由可独立解码的 Bare string 组成。Copy 和 New 组成的 Delta 只依赖该对象的一个准确前驱。盘上类型表达式区分 Text 与 string，字段通过 ObjectReference 槽保存 ObjectId。可编辑结构和整理阈值属于内存实现。

首版业务 typed Base 使用 ABI 5 的结构路由 2 和零元 Text 类型表达式（11）。字段 history 中的零元模式写成 `t`；直接或嵌套包含它的记录使用 v10。文本 raw body 的编码如下，整数均为 VarUInt32，范围单位均为 UTF-16 code unit。

| 记录 | raw body |
|---|---|
| Base | 总单元数 N，随后读到 body 末尾的每段都是一条非空 Bare string。按段选择无损 UTF-8 或 UTF-16LE，解码后的长度之和必须等于 N。空正文只写 `00` |
| Delta | 结果单元数 N，随后为产生恰好 N 个单元的指令序列。Copy（字节 1）后接前驱起点及长度；New（字节 2）后接长度及相应 UTF-16LE 单元 |

文本编码器合并相邻 New 及来源连续的相邻 Copy。读取时验证实际前驱范围、长度、指令和完整消耗。恢复沿链迭代，Copy 复用前驱字符串切片，New 解码新增内容；片段积累达到内部资源界限时，可在这一层改用平坦恢复。该界限与编辑器接受时的整理阈值分别用于冷读和热编辑。[实际 codec](../0104-text-editor/README.md#native-text-sources "Git 06c57dd4:src/DurableGraph/Runtime/Text/TextBodyCodec.cs")

业务准备对冻结文本按需生成 Base。图策略用片数和总单元数估算延迟 Base 大小，纳入完整记录的封套开销。选择完成后，在追加前生成实际所选记录。安装后的重建字节使用实际记录累计。State 接受后才可条件安装已准备的整理结果；放弃、独立 Event 或捕获后又有新编辑时，都不得用旧视图覆盖领域编辑。[分段编码与估值](segmented-base-refactoring-plan.md)

## 后续范围

工作负载确实需要大量常驻历史快照或频繁随机读取时再评估 Rope。跨块通用正则、最大 Delta 链配置和公开块枚举各自等待真实消费者的成本证据。
