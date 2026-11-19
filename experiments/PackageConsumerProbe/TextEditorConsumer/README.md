# TextEditor 普通包消费者

应用直接引用 `Atelia.Text`、`Atelia.DurableGraph` 和 `Atelia.DurableGraph.Persistence`。程序把 `TextEditorPersistence.Register(registry)` 加入模型注册，并在业务字段和 `List<TextEditor>` 中使用编辑器。编译器从 Runtime 包取得生成器与 Schema history 工具。

使用同一提交的完整候选 feed、版本 G、完整 Git SHA 和两个尚不存在的仓外目录运行两个独立项目。

~~~powershell
./experiments/PackageConsumerProbe/Run-TextEditorProbe.ps1 -PackageSource $feed -Version $version -ExpectedSourceCommit $revision -WorkDirectory $consumer
./experiments/PackageConsumerProbe/Run-TextEditorReaderAdmission.ps1 -PackageSource $feed -Version $version -ExpectedSourceCommit $revision -WorkDirectory $reader -TextDatabase (Join-Path $consumer 'database')
~~~

第一个项目用三个独立进程创建数据库、冷开续写及只读验证。它检查跨片查询、代理对和孤立代理、CRLF、NUL、旧快照、范围回执、别名、空文本、条目历史和只读数据库字节。runner 还核对五个产品包的源码提交、实际加载的 DLL、文本包 XML、Runtime 包的生成器和两份宿主 Schema history 的 Publish/Verify。

第二个项目仅引用 DG Runtime 与 Persistence。它可保存和冷开普通 Control 条目，随后在没有文本实现和 codec 的情况下读取第一个项目的文本图。它要求持久化层以 `InvalidDataException` 指明确实遗漏的 `Atelia.Text.TextEditor` 注册，并在前后校验数据库文件摘要。

旧内建 `DurableText` 包和旧 reader 之间的四格互读实验保留在 Git 提交 `06c57dd4` 的 `experiments/PackageConsumerProbe/DurableTextConsumer` 及相邻 runner 中。
