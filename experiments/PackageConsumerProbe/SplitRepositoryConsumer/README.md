# 公开 accepted-history 包消费者

此目录是 DB-087 P1 的有限公开回归，并覆盖 P2 写准入、同形私有新根／冷续写与完整目录 Event ToShared。普通程序集 `DurableGraph.PublicHistoryConsumer` 通过 PackageReference 调用默认公开 Repository／BranchCheckout；不借 IVT、内部工厂或产品私有反射。真实 SG Widget V1→V2 与共享 List／环经自举 meta、真实 refs 发布，全部关闭后按准确历史 S 恢复续写；另以私有不可达声明验证完整推广与新 Shared owner 增量保存。最小验收是下表五个独立进程成功，并完成包来源、history 与持久字节核对。

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
| `continue`／V2 | 无配置默认保存拒且全文件零变，显式 Shared 冷单 List Delta；同形 private 新根／不可达声明、私有冷 Delta及 ref2 保持；随后无配置 `CommitEventToShared` 完整推广、State 不变，两 ref 共同推进；全关后同 PID 新 Shared owner 单 List Delta／真实 prior/H／private Base origin |
| `verify`／V2 | 独立 PID 全程 readonly，核旧 tag、main／fork、旧 private State、推广 Event／Shared State、准确各图 S与完整 meta 声明；全部完成帧保持，完整文件集合／SHA不变 |

每次物理观察都在相应 Repository／Checkout 全关后进行。[WireInspector](WireInspector.cs) 仅用公开 FS／VS／Binary 独立只读打开，观察固定图／batch／Dictionary目录、inline三uint locator、对象版本、有限 Widget1/2 与固定 `probe.private.unreachable` V1 Int32field1 员工描述。H 从 record 的真实解码消费量计算：Base 重置、Delta 累加、retained 保持。它不是完整 Schema 投影、通用 wire validator 或产品读取权威；不读内部 baseline、其他未知 ABI／布局拒绝。完成帧比较为完整地址、tag、user payload／tail SHA，未声称原始 RBF 全编码逐字节比较。

公开 Event 故事证明本例业务候选丢弃后仍保 State rewrite；显式 Prepare/Discard 由源码回归覆盖，二者不混称。Pair 的 ReferenceEquals 仅见证所选实现的资格，不扩大公开身份保证。witness 是诊断材料，实际 refs／帧负责裁决；本例不授同键异形独立编译、容器替换、ToShared 后的跨 PID writer 续写、进程 Kill、断电、全类型组合、内部 baseline H 或性能资格。其他19个暂停 lane不因本例通过恢复资格。

<a id="2026-10-13-限定验收"></a>
## 2026-10-13 限定验收

主线程实跑并独立核 source／包／加载／history 工件，另有只读独立复核，有限 PASS；[DB-087 P1 停点](../../../docs/design-branches/0087-meta-bootstrap-refactoring-route.md#public-package-gate)成立，当片后继 P2 尚未实施，只改消费者／runner／observer和文档，生产代码／API不变；后继准入与同形私有见证另见[写准入](#p2-admission-package-evidence)／[私有新根](#p2-private-package-evidence)，不回改本节历史资格。

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

该次写准入复验包只覆盖 Widget／List 的有限 Shared／history 故事；完整 private view、显式 Discard 的包见证、全类型组合、任意 Pair 身份、进程故障和性能均未新增资格。同形私有后继的资格另见下节。

<a id="p2-private-package-evidence"></a>
## P2 同形私有新根与冷续写的准确包验收

本片在原五进程末尾加入公开 `CreatePrivateBranch`／`CheckoutPrivate`，以匹配 V2 模型的全新 Widget 环与共享 List 创建新业务根。私有目录保完整 Widget V1／V2 声明及原 descriptor IDs，再显式加入不被业务图引用的 `probe.private.unreachable` V1 Int32field1。普通无配置／Shared Checkout 的 State／Event 均拒绝且健康、全关文件零变；显式私有签出后实际单 List Delta 续写。

主线程实跑并独立核工件，有限 PASS；只读反审另核实际五宿主、包／工具来源及完整私有断言。runner 将四个 private continue 布尔与最终 `PrivateColdValues` 列为必需判据；没有新增 lane／协议／friend 或诊断 API。

| 证据 | 结果 |
|---|---|
| DG G／Storage S | `0.0.0-public-history-e2e.20261013040446.29200`／`0.1.3-dev.20261011.g974fd47`；准确 S 来源 `974fd47abb73792f9d350af87ab91a53848952eb`，未远程发布 |
| 受控生产来源 | 源 HEAD `5cb7667343a64fc671fdf0d126b9f8b7c058b518`＋八份生产修改／新增 `Repository.PrivateSchemas.cs`；189 项生产输入 hash 与接线相同。`ProductionInputsMatchHead=false`／`IncludesUncommittedProductionSource=true`，**不是 clean 5cb7667 包**；消费者／runner 来源另列 |
| 实际包与执行 | 十包 ZIP→cache→stage→五进程 50 份实际加载 DLL 匹配；普通宿主／两版 assets 无 ProjectReference；四份编译输入、四次实际 Rebuild／包 SG／Build Publish及Verify，history1→2且旧文件保持 |
| 私有持久轨迹 | 完整私有 S 与 shared S 不同，保 Widget V1 descriptor12／V2 descriptor21，新增不可达 descriptor26；首3live全 Base／空来源，冷仅 List Delta／exact prior／H203→227，Widget head/H9保持；ref2 actual完整 RefRevision／payload 不变，main／fork 保原轨迹 |
| 关闭与只读 | 所有物理观察在相应 owners全关后；最终回读第三分支、准确 S／IDs／值及完成帧；两 readonly lane 各8文件集合／SHA不变。新增9个私有完成帧（meta、初 State、续 State 各 batch／目录／graph）保持 |
| 其他验证 | 准确 source mode solution build 零警告／错误，2726项通过；18个新源码聚焦案例另通过。同一十包 feed 的 README 原文示例／named Fork／冷重开／最终 Verify 通过，不把它作为私有改形包故事 |

工件为 `W:\TEMP\dg-p2-private-20261013-040445-d93775b0`，含唯一 feed、source sidecar、`result.json`、witness／evidence／assets／history／编译日志。仓库忽略目录 `.artifacts/db087-p2-private-20261013/` 中 `package-run-path.txt` 导航，`accept-package.py`／`independent-acceptance.json` 保存主线实际核验；`readme-package.log` 导航原文包示例，工作目录为 `experiments/PackageConsumerProbe/obj/readme-20261013040901-19012-2db014e2`。

- result SHA256：`FC3F8CEC999D08F990AA14A579F47D625885D3537FF956A395EA64BE4B470847`
- witness SHA256：`A181A4E1FD546E11C9D566BDD03C91AED4D0A6FF855D80B25FB182E9799172F8`
- 主线独立审计 SHA256：`E9EF9C27219913F72B64500A56F1586A83CD308297E5AF2F0C2395FFACD6B46C`

该次包仅验证同形完整 fork＋新增不可达声明，未授 private→shared promotion；后继见下节，不回改历史资格。两套 same-key/version 异形独立编译、容器 layout 替换、显式 Discard、crash／断电与性能仍待各自资格。

<a id="p2-promotion-package-evidence"></a>
## P2 完整目录 Event ToShared 与 Shared 冷续写的准确包验收

本片保原五进程与全部 Shared／private 断言，在 `continue` 私有单 Delta／ref2 不变段之后调用公开 `CommitEventToShared(root)`。完整 Widget V1／V2 和不可达声明一起进入 shared；业务无变化时复用目录／heads/H、无 OwnedBatch，Event 不换 State 地址／ID／实例。全关后在同一 PID 用新 Shared owner 普通签出，继续实际单 List Delta；最终独立 `verify` PID 全程只读。此处冷续写是 fresh owner，未声称另一个 writer PID。

主线实跑、独立核工件及只读反审，有限 PASS；runner 将五个 promotion／cold continue 布尔及 `PromotedSharedColdValues` 列为必需判据。复用现有限 WireInspector，不扩格式白名单、产品诊断 API 或实验协议。

| 证据 | 结果 |
|---|---|
| DG G／Storage S | `0.0.0-public-history-e2e.20261013045456.6924`／`0.1.3-dev.20261011.g974fd47`；准确 S 来源 `974fd47abb73792f9d350af87ab91a53848952eb`，未远程发布 |
| 受控生产来源 | HEAD `66416728935286083822b95c94dd1b073b600303`＋五份生产修改；189 项输入冻结 hash 与本片接线相同。`ProductionInputsMatchHead=false`／`IncludesUncommittedProductionSource=true`，**不是 clean 6641672 包**；消费者／runner 修改单列 |
| 实际包与工具 | 十包 ZIP→cache→stage→五 PID 50 份实际加载 DLL 核对；普通非 friend／两版 assets 无 ProjectReference；四份编译输入、四次实际 Rebuild／包 SG／Build Publish及Verify，history1→2且旧V1保持 |
| 共同推广 | shared 接受三份完整声明及不可达 descriptor26；实际 ref1／ref2 revision 和 payload 均推进。推广 Event pin 新 shared S，目录／全部 heads/H 与先前 private State 相同；旧 private S 保含义 |
| Shared 冷增量 | 新 owner 从推广 Event 选最近旧 private State；仅 List 写 Delta，准确 Parent／source／prior、IDs 保持，实际 H227→251（24B）；Widget head/H9保持，各 Base 的原 batch／private origin S 不变 |
| 关闭与只读 | 物理观察在相应 owners 全关后；两 readonly lane 各8文件完整集合／SHA不变，独立 Verify 核私有旧值202、推广 Event202、Shared 新值203；旧帧、9个 private 帧、新增4个推广帧及3个续写帧保持 |
| 其他资格 | 准确 source mode 构建零警告／错误，2736项通过；10个新增聚焦案例另通过。同一十包 feed 的 README 原文／named Fork／冷重开／最终 Verify 通过，不能代替异形包资格 |

工件 `W:\TEMP\dg-p2-promotion-20261013-045454-537e7c43` 保唯一 feed／source sidecar、`result.json`／witness／evidence／assets／history／编译日志。仓库忽略目录 `.artifacts/db087-p2-promotion-20261013/` 的 `package-run-path.txt` 导航，`accept-package.py`／`independent-acceptance.json` 保存主线核验；`readme-package.log` 导航原文示例，工作目录为 `experiments/PackageConsumerProbe/obj/readme-20261013045922-25824-52a2b01e`。

- result SHA256：`14DBAD5193544354B6B80FAB1C8013900B18B52E953A76FA227A281905118EAA`
- witness SHA256：`5C1E131CD09F8029BDCCDE6C0950022474C8586A5912A39045A8F7E4DC50A5AE`
- 主线独立审计 SHA256：`28866995201E03C2421D82EAD19969E4C5F059651BB51400CAAD668EAC720243`

此包只授同形完整目录 Event ToShared＋新 owner Shared Delta；不同数字 descriptor／真实 Upgrade rewrite／layout-only CU 的新增资格在源码测试，不混称包证据。两套异形独立编译环境、容器改形替换、显式 Discard、内部 Unknown 窗口、进程 Kill／断电及性能未新增资格。
