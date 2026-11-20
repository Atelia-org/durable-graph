# TextEditor 普通包消费者

应用直接引用 `Atelia.Text`、`Atelia.DurableGraph` 和 `Atelia.DurableGraph.Persistence`。程序把 `TextEditorPersistence.Register(registry)` 加入模型注册，并在业务字段和 `List<TextEditor>` 中使用编辑器。编译器从 Runtime 包取得生成器与 Schema history 工具。

两个项目使用同一份 feed、版本 G、完整 Git SHA 和两个尚不存在的仓外目录。传入便携候选的 `feed/` 可离线还原。也可传入 `Prepare-DurableGraphProbeFeed -IncludeText` 准备的小型 feed，这时两个项目都从 nuget.org 补入 K4os，其余包均从指定 feed 还原。

~~~powershell
./experiments/PackageConsumerProbe/Run-TextEditorProbe.ps1 -PackageSource $feed -Version $version -ExpectedSourceCommit $revision -WorkDirectory $consumer
./experiments/PackageConsumerProbe/Run-TextEditorReaderAdmission.ps1 -PackageSource $feed -Version $version -ExpectedSourceCommit $revision -WorkDirectory $reader -TextDatabase (Join-Path $consumer 'database')
~~~

第一个项目用三个独立进程创建数据库、冷开续写及只读验证。它检查跨片查询、代理对和孤立代理、CRLF、NUL、旧快照、范围回执、别名、空文本、条目历史和只读数据库字节。runner 还核对五个产品包的源码提交、实际加载的 DLL、文本包 XML、Runtime 包的生成器和两份宿主 Schema history 的 Publish/Verify。

第二个项目仅引用 DG Runtime 与 Persistence。它可保存和冷开普通 Control 条目，随后在没有文本实现和 codec 的情况下读取第一个项目的文本图。它要求持久化层以 `InvalidDataException` 指明确实遗漏的 `Atelia.Text.TextEditor` 注册，并在前后校验数据库文件摘要。

## Agent 工具试用

第一个 runner 同时把[独立受试者发出的调用](text-tool-trial.jsonl)送入普通包的 `ToolSession` 并保存回执。试用宿主从同一持久 State 分叉两份工作副本，只把当前世界和当前角色可见的字段交给工具。终检会比较完整正文、读回已保存状态，并通过工具逐项核对两个世界的命中、行号、原文范围和截断标志。

可用已构建的同一消费者再次执行回放，数据库路径须尚不存在。省略最后一个参数则从标准输入逐行读取同格式 JSON，开头会列出两项真实工具定义。`world` 和 `verify` 是宿主命令。

~~~powershell
$dll = Join-Path $consumer 'bin/Release/net10.0/Atelia.TextEditorConsumer.dll'
dotnet $dll tools-trial $freshStore ./experiments/PackageConsumerProbe/TextEditorConsumer/text-tool-trial.jsonl
~~~

旧内建 `DurableText` 包和旧 reader 之间的四格互读实验保留在 Git 提交 `06c57dd4` 的 `experiments/PackageConsumerProbe/DurableTextConsumer` 及相邻 runner 中。
