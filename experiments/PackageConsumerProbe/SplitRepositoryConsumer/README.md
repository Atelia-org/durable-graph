# Split Repository 包消费者

此目录是 DB-087 P1d 的有限消费者：通过真实 PackageReference 和现有测试 friend 身份调用内部 split `Repository` 工厂。默认公开工厂仍使用现有路径；本消费者不提供默认切换、private、进程 Kill 或断电资格。

项目没有手动导入 targets、ProjectReference、Analyzer 或 AdditionalFiles。由 Runtime 包正常交付的 SG／Build targets，按 `HistoryVersion=1|2` 编译两个独立宿主；V2 读取由 V1 实际 Publish 产生的 accepted `.dgschema`，保留历史 DTO／reader，再 Publish／Verify V2。历史目录通过 `DurableGraphSchemaHistoryDirectory` 显式指定，模式为 `DurableGraphSchemaHistoryMode=Publish|Verify`。一个 Widget 族的历史文件预期从 1 个增至 2 个，旧文件保持不变。

执行由 [Run-SplitRepositoryProbe.ps1](../Run-SplitRepositoryProbe.ps1) 统一完成；本目录仅维护消费者源码与协议。本片已由主线实跑五个独立进程，限定包资格及最终构建／receipt 证据见 [DB-087 §33](../../../docs/design-branches/0087-meta-bootstrap-refactoring-route.md#33-p1d-第四片准确包与-accepted-history)；不将该资格扩展到默认公开路径。

每个命令接收三个参数：`lane database witness.json`；成功后输出 `SplitRepositoryConsumer:<lane>:Passed`，并在 witness 所在目录写入 `<lane>.evidence.json`。

| lane／宿主 | 实际检查 |
|---|---|
| `seed`／V1 | 两个 Widget 成环、共享 128 项 List；真实 State／tag、必要 refs1/2、完整 IDs／实际 heads/H、typed Base 与根声明的 descriptor、完成帧图片 |
| `upgrade`／V2 | accepted V1 exact DTO 不调用 Upgrade；只读恢复两 Widget 的 int→long＋100；同 S 的未改 List 可共享，rewritten Widget 独立；文件集合／SHA 不变且不登记 V2 |
| `rewrite`／V2 | main 首 State 实际写两个 Widget Base、保留 List；新增未知业务 CLR 的 Extra meta。随后旧 tag Fork 按 State.S0 恢复，latest S1 仅作写权威；fork 首 State 同样写两个 Base。同 S 完整共享正例与跨 S 同 head／同内容 List 的独立反例；旧 descriptor ID 保留 |
| `continue`／V2 | 全资源关闭后重新导入 fork 的完整 IDs／实际 heads/H；改一个 List 槽，实际单 Delta、准确 prior／source／Schema pin，H 按 decoded wire payload 累加，Widget heads 保留 |
| `verify`／V2 | fresh readonly 核旧 tag、新 main／fork、准确各图 S、当前与历史 meta、完整存储和完成帧，文件集合／SHA 不变 |

evidence 顶层记录 `Passed`、`Lane`、`ProcessId`、`TypedBodyAbi` 与实际加载的 Runtime／Persistence／Storage／Host 程序集路径及 SHA256；`Observations` 是各 lane 的具体检查结果。witness 保存实际 State／Schema 地址、完整 ID→head/H 和完成帧的地址／tag／payload＋tail-meta SHA。witness 是诊断材料，实际 refs 与 Store 读取负责裁决。

这里只验证一个当前 V2／accepted V1 Widget 族与 builtin List。没有扩展 generic、容器值规则、comparer、跨程序集历史或 Upgrade 缺能力矩阵。同／跨 S 的引用身份断言是选定内部机制的见证，不提升为一般公开 `ReferenceEquals` 保证。完成帧比较不代表全库 audit，也不把 Schema pin 当成全部 retained Base 的来源：未重写 List 继续持有 S0 的实际 Base。
