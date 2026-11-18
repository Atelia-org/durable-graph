# DurableText 普通包消费者

本样例只引用 Runtime 与 Persistence 两个顶层 PackageReference，G 由 runner 参数提供；使用包内 Generator/Build 的 Publish→Verify，不手工加载 analyzer 或导入源项目。

`Run-DurableTextProbe.ps1 -PackageSource <完整feed> -Version <G> -ExpectedSourceCommit <40位SHA> -WorkDirectory <仓外新目录>` 执行三条独立进程：

- seed：主文本由三个独立 backing 串经公开编辑构成后才首次保存，包含跨片 `beta`、跨片合法代理对、孤立代理、中文、NUL 与 CRLF；Body/Alias/List 别名，null 与空对象；随后两次小改保存；快照稳定性；标准 str_replace 唯一匹配、AppendTo、完整字符串 Regex。
- resume：关闭后冷开、别名恢复、独立 OpenGraph 隔离、继续编辑保存，并回读原始图。
- verify：只读历史和最新图、UTF16/CRLF/NUL、CopyTo、资源释放后快照有效；数据库文件字节保持不变。

runner 检查独立 cache/no ProjectReference、准确 DG 四包 commit 与版本、十包原包及输出/实际加载 DLL 哈希、包内生成器和历史工具的实际消费、两份用户 schema history 的 Publish/Verify 不变。这里的进程验收不代替产品故障注入测试，不宣称掉电资格或真实 memo 负载。

2026-11-18 分段 Base 的普通包验收已通过。G `0.0.0-textsegments.20261118.g712ddf7` 来自独立清洁 checkout `712ddf7f73b668d141083c7ef10401545b4ed6be`；S `0.1.3-dev.20261118.g3f780ad` 来自 `3f780ad95239d7556f917e1adca6839cda144c92`，六包已另过 Storage 四个隔离普通包消费者及 metadata/PDB 校验。三条 DG 消费进程 seed / resume / verify 分别通过 8 / 17 / 11 项检查（PID 19004 / 37392 / 13816）；首次保存前的主文本由三个不同 backing 串组成，保存后继续小改，另一进程冷开再改，第三进程只读核对原始/中间/最新正文与别名。

本轮仍核对十包身份、准确来源、缓存原包/输出/实际加载 DLL 哈希、包内 Generator/Build 的 Publish→Verify、两份用户 history 不变和只读数据库字节不变。完整回执为 `W:/DurableTextSegmentedBase/dg-package-consumer/result.json`（SHA256 `BF6A7CA2595447781B897113BB3803B553EAB2CF57419D03FF8A6E22E3D8BFC9`），候选 feed 为 `W:/DurableTextSegmentedBase/dg-candidate/feed`。Storage 回执为同根 `storage-acceptance-final.json`，来源核验为 `dg-storage-verification-final.json`。均未外部发布。

分段正文按[重构合同](../../../docs/design-branches/0101-durable-text/segmented-base-refactoring-plan.md)作为 Text 首次发布格式，本轮从空目录新建数据；固定 UTF-16LE 的旧本地 Text 测试库须重新生成，未测试或承诺该格式之间的兼容/自动判别。以下旧 reader 四格仅保留首版原来源下的历史证据。

旧 reader 单独使用 [DurableTextReaderAdmission](../DurableTextReaderAdmission/Program.cs) 和 [准入 runner](../Run-DurableTextReaderAdmission.ps1)。它不引用 DurableText 编译符号，先保存/恢复普通 Control 字段作为正控，再读取新库，逐阶段记录 OpenReadOnlyExisting、ListBranches、ResolveTag、LoadState、OpenGraphWithText 的实际异常；不能用缺程序集或编译失败代替格式拒绝。旧 reader 能否打开资源和能否恢复 Text 图是两个事实。

以下为固定 UTF-16LE Base 首版的历史普通包验收；当时首次保存为单片，不代表当前多片分段 Base 的包资格。2026-11-18 的 G 为 `0.0.0-durabletext.20261118.gfb2bef0`，来自独立清洁 checkout `fb2bef0ad2acc9c146c72c4edf4a2cd55ce35714`；六包 S 为 `0.1.3-dev.20261101.g9ad39fc`，来源 `9ad39fc51a420e8302468533b82d8bc3a91a403e`。旧 reader 使用已存在的 G `0.0.0-graphheap.20261115.g7a98c7d`，来源 `7a98c7d224f87e0f7ec455417a0a4a2d13d5fa33`。

三进程 seed / resume / verify 分别通过 7 / 17 / 11 项检查；两份用户 Schema history 的 Publish→Verify、包内 Generator/Build 实际执行、原包/输出/已加载 DLL 哈希以及只读库字节不变均通过。[四格 runner](../Run-DurableTextAdmissionMatrix.ps1) 复用各 reader 的已验证独立包消费结果，再运行交叉读取：

| Reader | 数据 | 实际结果 |
|---|---|---|
| 旧 G | 旧 G 普通 Control 库 | Completed，`plain-before-text` |
| 新 G | 新 G Text 库 | 三进程通过，历史与续写正确 |
| 旧 G | 新 G Text 库 | LoadState 拒绝，`InvalidDataException: Unsupported or nonclosed TypeExpr constructor/arity.` |
| 新 G | 旧 G 普通 Control 库 | Completed，`plain-before-text` |

旧 reader 已成功打开 Repository、列出 branch、解析 tag；拒绝发生在 LoadState 加载 SchemaCatalog 的 TypeExpr 时，尚未调用 OpenGraphWithText。不能把这次结果写成“打开 Repository 就拒绝”或“尝试解释 Text body 后拒绝”。新读旧的通过仅覆盖此普通 Control fixture，不建立通用迁移或兼容承诺。两次交叉只读操作均未改变数据库文件字节。

本机完整证据分别位于 `W:/DurableTextProduct/package-new-consumer-20261118-01/result.json`、`W:/DurableTextProduct/package-old-reader-20261118-02/result.json`、`W:/DurableTextProduct/package-new-reader-20261118-01/result.json` 和 `W:/DurableTextProduct/package-admission-matrix-20261118-01/result.json`。候选十一包 feed 在 `W:/DurableTextProduct/candidate-fb2bef0/feed`；未向外部发布。

复跑四格需先分别运行两个版本的准入 runner 正控，以及新版本的三进程 runner，再把三个 `result.json` 传给 `Run-DurableTextAdmissionMatrix.ps1 -OldReaderResult <旧正控> -NewReaderResult <新正控> -NewConsumerResult <新三进程> -WorkDirectory <新目录>`。它检查包身份、已加载 DLL 哈希、真实拒绝原因与读取前后的库字节；不重新打包，也不向现有目录写入新结果。
