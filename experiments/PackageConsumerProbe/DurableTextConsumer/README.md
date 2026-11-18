# DurableText 普通包消费者

本样例只引用 Runtime 与 Persistence 两个顶层 PackageReference，G 由 runner 参数提供；使用包内 Generator/Build 的 Publish→Verify，不手工加载 analyzer 或导入源项目。

`Run-DurableTextProbe.ps1 -PackageSource <完整feed> -Version <G> -ExpectedSourceCommit <40位SHA> -WorkDirectory <仓外新目录>` 执行三条独立进程：

- seed：Body/Alias/List 同一个 DurableText，null 与空对象；首次保存及两次编辑保存；快照稳定性；标准 str_replace 唯一匹配、AppendTo、完整字符串 Regex。
- resume：关闭后冷开、别名恢复、独立 OpenGraph 隔离、继续编辑保存，并回读原始图。
- verify：只读历史和最新图、UTF16/CRLF/NUL、CopyTo、资源释放后快照有效；数据库文件字节保持不变。

runner 检查独立 cache/no ProjectReference、准确 DG 四包 commit 与版本、十包原包及输出/实际加载 DLL 哈希、包内生成器和历史工具的实际消费、两份用户 schema history 的 Publish/Verify 不变。这里的进程验收不代替产品故障注入测试，不宣称掉电资格或真实 memo 负载。

旧 reader 单独使用 [DurableTextReaderAdmission](../DurableTextReaderAdmission/Program.cs) 和 [准入 runner](../Run-DurableTextReaderAdmission.ps1)。它不引用 DurableText 编译符号，先保存/恢复普通 Control 字段作为正控，再读取新库，逐阶段记录 OpenReadOnlyExisting、ListBranches、ResolveTag、LoadState、OpenGraphWithText 的实际异常；不能用缺程序集或编译失败代替格式拒绝。旧 reader 能否打开资源和能否恢复 Text 图是两个事实。

当前消费者源码已准备；新 G 的实际验收由主线切片结果记录，不能仅凭本文件视为通过。旧 G `0.0.0-graphheap.20261115.g7a98c7d` 的独立正控已通过，准入负控需等待新库。
