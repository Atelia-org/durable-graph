# 公开 accepted-history 包消费者

此目录已就地转为 DB-087 P1 的有限公开回归，并为 P2 写准入首片提供代表包见证。普通程序集 `DurableGraph.PublicHistoryConsumer` 通过 PackageReference 调用默认公开 Repository／BranchCheckout；不借 IVT、内部工厂或产品私有反射。问题是：真实 SG Widget V1→V2 与共享 List／环，能否经自举 meta、真实 refs 发布后，关闭全部资源、按准确历史 S 恢复并继续增量保存？最小验收是下表五个独立进程全部成功，并完成包来源、history 和持久字节核对。

项目只有 Runtime／Persistence 两个 PackageReference，另八包传递取得；没有手动 Import、Analyzer、AdditionalFiles 或 ProjectReference。包内 SG／Build inputs 实际参与 V1/V2 编译及 accepted-history Publish／Verify，历史文件从1个增至2个且旧文件不变。旧内部 friend 形态及其资格仅见 [DB-087 §33 归档](../../../docs/archive/2026-10-13/0087-meta-bootstrap-refactoring-route.md#33-p1d-第四片准确包与-accepted-history)，不将当前源码回写为旧证据。

由 [Run-SplitRepositoryProbe.ps1](../Run-SplitRepositoryProbe.ps1) 完成全部阶段。读取[依赖指南](../../../docs/storage-dependency.md)后，在仓根传入经过 Prepare 核验的完整六包 feed：

```powershell
./experiments/PackageConsumerProbe/Run-SplitRepositoryProbe.ps1 -StoragePackageSource $storageFeed
```

默认新建 repo 外的临时 work directory，受控打四个 DG 包，与准确六个 Storage 包构成十包 feed；DG 版本 G 与 Storage 版本 S 独立。使用新 NuGet.Config／cache、屏蔽父级 Directory.Build.*，两版 assets 不得含 ProjectReference。可传 `-WorkDirectory` 选择新的 repo 外目录；不覆盖既有证据。复用完整 feed 时传 `-PackageSource <feed> -Version <G>`，还必须有默认的 `dg-source-provenance.<G>.json` 或显式 `-PackProvenancePath`；仅 nuspec commit 不足以授来源资格。

来源记录保 tracked 生产输入、HEAD 和 dirty 状态，跨 pack／消费核对；未跟踪生产输入明确拒绝，标准忽略的构建产物不在该审计范围。记录生产输入与 HEAD 一致不等于整个工作树干净。包 DLL 逐份核 nupkg ZIP→cache→stage→每进程实际加载路径／SHA；SG／Build DLL、props／targets 同样核 ZIP 与 cache，并由真实 Rebuild 的 Csc／工具 Exec 日志确认消费。没有新增产品诊断 API。

| lane／宿主 | 实际检查 |
|---|---|
| `seed`／V1 | 两 Widget 成环、共享128项 List；真实 State／tag、必要 refs1/2、完整 IDs／实际 heads/H、动态 descriptor／meta root 声明与完成帧 |
| `upgrade`／V2 | own SG historical BodyV1 保 exact DTO／引用 ID 且不调用 Upgrade；公开 readonly 使两 Widget int→long＋100。V2 尚未登记到该 S；同 S 未改 List 可共享、rewritten Widget 独立，完整文件集合／SHA不变 |
| `rewrite`／V2 | main 首 State 两 Widget 写 Base、List 保原 origin S/head/H，实际 V2 closure 推进 shared S1；旧 tag Fork 仍按 State.S0 恢复，只更新 ref。升级环的 Event 保 State 地址／ID／实例，随后 State 以 Event 为业务 Parent、S0 为内容来源，仍写两 Base；核同／跨 S Pair 及旧 descriptor ID复用 |
| `continue`／V2 | 无配置 RW Open可读／Checkout，State／Event 保存提前拒且健康，关闭后完整文件集合／SHA不变；再显式 Shared fresh owner 恢复当前 V2，无再次历史 Upgrade；同一 List 改一个槽，实际单 Delta，核完整 IDs、准确 source／same-ID prior／S 与实耗 H累加，Widget head/H沿用 |
| `verify`／V2 | fresh readonly 核旧 tag、main／fork／Event、准确各图 S与原／新 meta descriptor；旧完成帧保持，完整文件集合／SHA不变 |

每次物理观察都在相应 Repository／Checkout 全关后进行。[WireInspector](WireInspector.cs) 仅用公开 FS／VS／Binary 独立只读打开，观察固定图／batch／Dictionary目录、inline三uint locator、对象版本与有限 Widget1/2 员工描述。H 从 record 的真实解码消费量计算：Base 重置、Delta 累加、retained 保持。它不是完整 Schema 投影、通用 wire validator 或产品读取权威；不读内部 baseline、未知 ABI／布局直接拒绝。完成帧比较为完整地址、tag、user payload／tail SHA，未声称原始 RBF 全编码逐字节比较。

公开 Event 故事证明本例业务候选丢弃后仍保 State rewrite；显式 Prepare/Discard 继续由既有源码回归覆盖，二者不混称。Pair 的 ReferenceEquals 仅见证所选实现的资格，不扩大公开身份保证。witness 是诊断材料，实际 refs／帧负责裁决；没有 private、进程 Kill、断电、全类型组合、内部 baseline H或性能新资格。其他19个暂停 lane不因本例通过恢复资格。

<a id="2026-10-13-限定验收"></a>
## 2026-10-13 限定验收

主线程实跑并独立核 source／包／加载／history 工件，另有只读独立复核，有限 PASS；[DB-087 P1 停点](../../../docs/design-branches/0087-meta-bootstrap-refactoring-route.md#public-package-gate)成立，当片后继 P2 尚未实施，只改消费者／runner／observer和文档，生产代码／API不变；当前 P2 写准入首片另见[下方复验](#p2-admission-package-evidence)，不回改本节历史资格。

| 证据 | 结果 |
|---|---|
| 生产包来源 | `57587dfc5cbe92f74c3cfcc5e74f06558fb02c42`；受控pack／验收时冻结的187份输入 hash与该 commit真实blobs一致，无未提交生产输入；消费者／runner dirty单独记录，整个工作树并非clean |
| DG G / Storage S | `0.0.0-public-history-e2e.20261013023751.13876` / `0.1.3-dev.20261011.g974fd47`；S来源 `974fd47abb73792f9d350af87ab91a53848952eb`，未远程发布 |
| 隔离包／工具 | 10包、两版 PackageReference assets／唯一fresh cache；5独立PID成功，50份实际loaded DLL核对；4个包内编译输入核ZIP，4次真实Rebuild／Publish／Verify，零warning/error |
| readonly／history | 两 readonly lane各8个文件完整集合／SHA不变；accepted history1→2，旧V1字节不变 |
| 实际对象／计量 | root1／child3／List2；descriptor12保留、V2新21；Widget Base H8→9重置，List原head/H202保留；冷开单Delta实耗24B，H202→226，Widget head/H9不变；21个完成帧保持 |
| 源码回归 | 显式准确source模式：solution build零warning/error；2700通过、0失败／跳过（Persistence862／Serialization133／Storage36／Runtime与Generator1669）。源码不代包资格 |

首轮受控打包成功后，普通消费者首次编译暴露漏导入 `StateModelRegistry` namespace；补公开 Persistence导入，修来源脚本未跟踪输入漏记后，在新隔离目录／新cache复用同一准确feed与sidecar完成最终验收，未改生产代码或重包原版本。失败日志原地保留，不算成功运行。

验收完成后，`src/DurableGraph.Persistence/MetaBootstrapP0/README.md`只更新当前导航；它位于保守来源scope内，因此187项并非永远都与旧HEAD字节相等。该文档尾修不改变已冻结的包来源或生产代码／API；后续复跑以新受控输入为准。

最终工件在 `W:\TEMP\dg-public-history-final-20261013-024034-873163f9`：`result.json`／各lane evidence、witness、assets、history及编译日志。控制记录与主线程独立审计在仓库忽略目录 `.artifacts/db087-public-history-20261013/`，入口为 `final-run-path.txt`／`independent-acceptance.json`；原pack／失败记录由 `run-path.txt`定位。receipt和SHA是本次本地证据，不要求后续同路径或同hash。

- final result SHA256：`0DA1B1AD9420FDE5359B361A7BD096FB8F0136E04004C032CB8A782EF95EEC1C`
- witness SHA256：`0E79EDE9A857061A23D755DD001FB89BA79158C263C208C5071C923DD065231E`
- 主线程独立审计 SHA256：`93817BBA4207D6960F1417DD6F689C1CCA27EA431FBED5C4BC0250662D0E65AD`

<a id="p2-admission-package-evidence"></a>
## P2 写准入首片的公开包复验

当前消费者所有实际 writer Open 显式选择 `SchemaWriteMode.Shared`，读取工厂保原义。`continue` 在冷 Delta 前用普通公开 API 完成一个无配置反例：准确读取与 Checkout 成功，State／Event 保存各以 InvalidOperationException 拒绝，Head／State 地址／ID／实例和 owner 健康不变；全部资源关闭后整个文件集合／SHA不变，然后显式 Shared 继续原来的单 List Delta。

主线程实跑、独立核工件并经只读反审，有限 PASS。runner 现在把 `UnconfiguredSaveRejected` 列为 continue 必需判据；尾修后在全新 stage／cache 复用同一不可变 feed，再完成五个进程，未重包或覆盖原版本。源码 2708 项、活动 Replay Count8（20 个实测库／300 修订）、CrashProbe 仅构建和 README 原文包故事另有各自资格，不把它们混为 package crash／性能证据。

| 证据 | 结果 |
|---|---|
| DG G／Storage S | `0.0.0-public-history-e2e.20261013031919.30616`／`0.1.3-dev.20261011.g974fd47`，准确 S 来源仍为 `974fd47abb73792f9d350af87ab91a53848952eb`；未远程发布 |
| 受控生产来源 | 源 HEAD `f7c0e614ecb56525764b258141b33b98b7d6ed2c`＋三份生产文件修改／一个新文件；188 项生产输入冻结 hash 与本轮接线相同。`ProductionInputsMatchHead=false`／`IncludesUncommittedProductionSource=true`，**不是 clean f7c0e61 包**；消费者／runner 修改单列，不掩盖 dirty |
| 包与执行 | 十包 ZIP→cache→stage→五进程 50 份实际加载 DLL 匹配；普通宿主无 friend，两版 assets 无 ProjectReference；四份编译输入、四次真实 Rebuild／包 Generator／Build Publish及Verify，history1→2且旧V1保持 |
| 准入与持久轨迹 | 无配置可读／签出，State／Event 保存拒且健康、全文件零变；显式 Shared 后真实单 List Delta，ID／prior 不变，H202→226，Widget head/H9保持；两 readonly lane 各8文件集合／SHA不变；现例全部完成帧保持 |
| README 原文 | 同一准确十包 feed 运行新示例：Hp99→98→97／Upgrade Day1、history保持、策略覆盖Hp96、named Fork source96／left95／right94和冷重开，最后Verify通过；只授该示例 API／包故事 |

最终工件 `W:\TEMP\dg-p2-admission-final-20261013-032303-0e8b0013` 的 `result.json`／witness／evidence／assets／history／编译日志可复核。原受控 pack 在 `W:\TEMP\dg-p2-admission-20261013-031916-95079f2c`，完整 feed和source sidecar只读复用。仓库忽略目录 `.artifacts/db087-p2-admission-20261013/` 中 `package-run-path.txt`／`final-package-run-path.txt` 分别导航两次运行，`independent-acceptance.json` 保存主线实际核验；`readme-package.log` 与 `replay-path.txt` 导航其余验证。

- final result SHA256：`4BA34C65C3345C83A9E82B2AC9528F0CCE23CCD662EB321C310E0D93DCD09226`
- 主线程独立审计 SHA256：`321259DC53F880C01E64054E37306FD873259D578AE5CC3207B1D53342ACC818`

包仍只覆盖 Widget／List 的有限 Shared／history 故事；完整 private view、显式 Discard 的包见证、全类型组合、任意 Pair 身份、进程故障和性能均未新增资格。
