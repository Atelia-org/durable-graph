# 用工具改对第二个“北门”

`2ec3c373` 把字面和正则 `Search`、字面 `ReplaceUnique` 及两个公开工具放进 `Atelia.Text`。这次试用考察 Agent 能否仅凭有界回执，在同一长行的两个重复词中只改对第二个，并验证这项编辑只对当前工作副本可见。[文本包用法](../../text/README.md) · [试用宿主](../../../experiments/PackageConsumerProbe/TextEditorConsumer/TextToolTrial.cs)

## 一次实际试用

独立受试 Agent 拿到任务和一个通往 `ToolSession.ExecuteAsync` 的本地传输。工具说明取自会话注册表；受试者没有看到样本全文、位置、候选前后文或预设调用脚本。宿主用 DurableGraph 从同一持久 State 分叉两个世界，当前世界可写，旧世界只读。本角色可见正文及摘要，另有一个包含“北门”的私有字段。正文第 2 行长 120,022 个 UTF-16 单元。

| 受试者发出的调用 | 可观察的回执 |
|---|---|
| 在可见范围 `search("北门")` | 仅命中 `signal-watch` 的 `FullText`，地址为 `memo:signal-watch/full`。两处都在第 2 行，偏移 40010、80021。每处带 120 单元原文，左右均标为截断。 |
| 对该地址把 `北门` 改成 `南门` | `ambiguous`。第一处紧邻“晨钟班在”和“领取灯具”，第二处紧邻“潮汐班在”和“收回灯具”。 |
| 把 `潮汐班在北门` 改成 `潮汐班在南门` | `replaced`。实际替换从 80017 开始，移除和写入各 6 个 UTF-16 单元。 |
| 在同一地址搜索 `[北南]门`，指定正则及 18 单元摘录 | “晨钟班在北门领取灯具”和“潮汐班在南门收回灯具”。 |
| 切到旧世界后重复同一查询 | 地址相同，两处均为“北门”。 |

第一笔搜索的整个 JSON 回执长 636 个 UTF-16 单元，歧义回执 663 个。受试者从第二个候选摘出四字前缀，用同一前缀保留上下文并提交编辑。宿主逐字核对完整正文，另行装入已存 State，检查摘要和私有字段，并在两个工作副本上重查原词及新词。[受试者实际请求，可直接回放](../../../experiments/PackageConsumerProbe/TextEditorConsumer/text-tool-trial.jsonl) · [交互与回放命令](../../../experiments/PackageConsumerProbe/TextEditorConsumer/README.md#agent-工具试用)

## 决定工具形状的三个例子

- **旧串长于摘录。** 两处相同的 200 单元旧串，用 120 单元的居中摘录只能读到同样的文字。搜索采用居中摘录，歧义回执则把字符预算全给旧串紧邻的 `before` 和 `after`。用例从第二个候选取回原文，成功构造唯一替换。[单篇回归](../../../tests/Text.Tests/TextSearchTests.cs) · [工具回归](../../../tests/Text.Tests/TextToolTests.cs)
- **显示出的文字会成为下一笔编辑的依据。** 在同时包含孤立代理码元和真正的 `U+FFFD` 的正文中，JSON 默认字符替代曾使受试代码从第一处复制的锚点唯一命中第二处。工具现在对实际返回的原文、标签和地址检查能否无损表示，失败回报 `unrepresentable_text`。正文中未返回的范围无需为此扫描。[序列化边界](../../../src/Text/Tools/TextEditorTools.cs)
- **同一轮有多笔编辑。** 四笔调用按“有歧义、成功、旧串消失、继续修改上一笔产物”排序。实际 `ActionMessage.ToolCalls` 分成一轮四笔或四轮各一笔，连续使用同一个 `ToolSession`，逐笔回执和最终正文一致。文件用例给宿主管理的真实 UTF-8 文件缓冲区一个地址，同一套工具在内存中修改，宿主显式写回。[调度与文件用例](../../../tests/Text.Tests/TextToolTests.cs)

## 交付验收

.NET SDK 10.0.201 使用固定 Storage 源码 `3f780ad9` 构建 Release 根 solution，零警告、零错误。五个测试程序集共 3,386 项全部通过，零跳过，其中 Text 测试 115 项。混合文本与位图包的四个消费工况以及搬迁候选目录后的 QuickStart 也通过。

普通消费者从同一提交导出的本地候选 `0.0.0-texttools.20261120.g2ec3c373` 独立构建。单一源和新缓存提供 15 个原包，包含 `Atelia.Completion.Tools 0.1.0-preview.6` 及其依赖。消费项目只使用 PackageReference。实际加载的 Text DLL SHA-256 为 `2A51C313AFD97AD144FAAF12B1E64B83DD462FBE53086A82987515D10BB94953`。包 runner 重放上述调用，主线程用同一消费者在另一新仓再次核对当前与旧世界的正则回执。打包与重跑方法见[普通包消费者](../../../experiments/PackageConsumerProbe/TextEditorConsumer/README.md)。

第二位独立 Agent 直接使用候选包新开的会话。它也从歧义回执选择了“潮汐班在”前缀，自行把核对时的摘录预算设为 20，确认新旧世界并通过宿主终检。两次试用均通过本地传输接入真实工具注册表和会话。

本片提供宿主接口及可运行的 memo、文件接入样例。产品 memo 的角色策略、条目目录和文件写回属于具体宿主。正则替换及超过一个屏幕的批量预览需要让受试者实际选择多个候选来评估。[下一轮实验](../../DurableGraph-research-roadmap.md#文本替换的下一轮实验)
