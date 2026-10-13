# Private container package consumer

本目录承接 [DB-088 §6](../../../docs/design-branches/0088-development-schema-workflow.md#6-下一轮实施合同与停点)，使用普通 PackageReference 和公开 Repository 验证固定真实用户基线上 Point V1→V2 的 private Upgrade、完整 ToShared 与冷增量。**第一轮仅迁移消费者和 runner，尚未用删除特例后的新 G 编译／执行；以下当前故事不继承旧包运行资格。**

A 为 `pc.point` V1（int X），B 为 V2（long X）；Root／Keeper 保 V1。两 Root 成环并共享含 absent/present 的 128 项 `List<Point?>`。先保存 Keeper，再保存 main/tag，因此用户 baseline 的准确 S 已包含不可达 Keeper。B 的一个 child provider 将 X 加 5,000,000,000，注册规则集开启 `AllowNullableLifting`，通过 `UseListElementUpgrades` 应用于 List；不另设 Nullable wrapper、Root 强制升版或容器 replacement。

普通程序集 `DurableGraph.PrivateContainerConsumer` 无 IVT、ProjectReference、手动 Analyzer／AdditionalFiles／Import 或产品私有反射。WireInspector 仅在全部产品资源关闭后经公开 FS／VS／Bare 观察 ABI5、本故事有限 Schema、实际 refs／完整目录、origin／selected S、source／prior／H 与旧完整帧；不作为产品启动 audit 或通用格式平台。

## 运行与待验收故事

```powershell
./experiments/PackageConsumerProbe/Run-PrivateContainerProbe.ps1 `
    -PackageSource '/path/to/new-ten-package-feed' -Version '<new G>'
```

新 feed 须含准确六 Storage 包、四 DG 包及 source sidecar；亦可通过 `-StoragePackageSource` 由 runner 受控 pack。第二轮使用新 G，不能复用历史 G 证明 API 删除后的资格。WorkDirectory 须全新、位于两个仓外；旧工件不修改或清理。

runner 复制五份本地源码／项目文件，独立 NuGet.Config／cache、空 Directory.Build.* 隔离；A Publish／Verify 形成三份 V1，B 先复制这些精确 history 再 Publish／Verify，形成 V1＋Point V2 共四份。四次真实 Rebuild 的 package／SG／Build 输入与 loaded DLL 保留核验，运行前后 history 保原字节。

| 全新进程 | 待新 G 验收行为 |
|---|---|
| A seed | Keeper 后 main；真实 refs/tag、环／共享 List、三 live Base；baseline 含 Keeper |
| B create | `CreateBranch(old)`→`CheckoutPrivate`，正常 Point Upgrade；保 baseline 三对象 ID、业务来源与 Root heads/H；List 写升级 Base，旧／新 Point 和 List 声明共存；ref2 不变 |
| A continue-a | 冷开 main，单 List 真 Delta，exact source/prior/H；两 Root retained、private 和 ref2 不变 |
| B continue-b | 冷开 private，单 List 真 Delta，exact source/prior/H；两 Root retained、main 和 ref2 不变 |
| B promote-b | 再冷开，无冲突完整 ToShared 成功；全部旧／新声明和不可达 Keeper 导入，存活用户身份保持，旧完整帧保留 |
| A verify-a | 全程 readonly main/tag 正控、实际 refs／链／完成帧及全部文件集合／bytes 保持；不要求旧代码读取未来 V2 |
| B verify-b | 全程 readonly private 正控，也能将 main V1 升为当前 V2；精确 refs／链／完成帧与零写 |

同版双向错读由 [DevelopmentSchemaConsumer](../DevelopmentSchemaConsumer/README.md) 的独立 V2 草稿故事承担。本例退出 missing-pair／同名修正、双向 wrong-read 与同版 ToShared 必冲突观察，不重建替代入口。Array／Dictionary、发布故障、Unknown／Kill／断电、性能不属于本包片；源码回归保各自资格。

## 历史资格：2026-10-13 DB-087 原入口（已被本故事替代）

本片只新增消费者／runner 和文档，生产代码保持提交 `d92a3b15e18d0d7309584c0933ef6f6ca8bd8c2c`。受控 pack／验收时冻结的保守 189 项输入匹配该 HEAD，并经 pack 前／后／最终 sidecar 和主线实际 Git blob 对照；消费者未提交工作树单独记录，不称整个工作树 clean。验收通过后，仅在该保守范围内更新 P0 README三句活动导航并单独记录；原包／sidecar和生产代码字节不变，不把当时189项匹配说成当前文档仍全相同。包未远程发布。

| 证据 | 实际结果 |
|---|---|
| DG G／Storage S | `0.0.0-private-container-e2e.20261013055132.27864`／`0.1.3-dev.20261011.g974fd47`；S 来源 `974fd47abb73792f9d350af87ab91a53848952eb` |
| 准确包与编译 | 十包 ZIP→fresh cache ZIP／DLL→两 stage→六 PID 60 份 loaded DLL；assets 无 ProjectReference，四份包 SG／Build 输入核对，四次真实 Rebuild／Publish／Verify，零 warning／error |
| history | A/B 各三份 V1，Root／Keeper 文件相同、Point X 的 int／long 文件不同；Verify 与所有运行前后文件及 SHA 不变 |
| PID | seed 14776／create 9768／continue-a 15412／continue-b 30516／verify-a 11524／verify-b 8476；六进程 exit0 |
| 完整私有替换 | Point descriptor 10→35、List 23→38；旧两行保持 live、退出新声明，Root20／Keeper28 复用；新业务三 Base，meta 来源为实际 frozen shared，ref2 revision／payload 始终原样 |
| 实际冷增量 | A List H `3094＋26＝3120`，B `3500＋29＝3529`；准确源 State／prior batch，两个 Root head/H7 保持，旧 tag 不变 |
| 只读／完成帧 | A/B 两个全程 readonly PID 各八文件的集合／length／SHA 相同；保 24 个完整用户帧的地址／tag／payload／tailmeta；主线独立 raw RBF3 payload 对照并直接核 meta／业务封套与 batch 来源及 record bytes |
| 本轮源码核验 | solution build 零 warning／error；既有两条真实 SG 和 14 条保存边界聚焦通过。上一片 2752 项源码回归保其原资格，本轮未重复全套运行 |

工件为 `W:\TEMP\dg-p2-container-20261013-055131-2b6f6c43`，保唯一 feed、source sidecars、result／witness／六 evidence、assets、history 和编译日志。仓库忽略目录 `.artifacts/db087-p2-private-container-package-20261013/` 的 `package-run-path.txt` 导航；`accept-package.py`／`independent-acceptance.json` 保主线实际字节、来源、记录及原始 payload 核验。

| 本地证据 SHA256 | 值 |
|---|---|
| result.json | `6A9E645E6D37507A37C0DE0F07F4675E6E8567E46EDC673FF9E4EBDA2690519F` |
| witness.json | `0BFAD5374EED6B446326AA5958F66DADB3D41944B5DCA0C83C988399D2635730` |

此门禁只授实际 List／Nullable inline 组合；源码 Array／List／Dictionary 与失败窗口保各自资格。没有新 Upgrade、显式 Discard、Array／Dictionary 包、同 Open 双异形模型、Unknown／Kill／断电、性能或全类型矩阵资格；19 个暂停旧 lanes 仍不恢复。后续增量按 [DB-087 条件延期](../../../docs/design-branches/0087-meta-bootstrap-refactoring-route.md#open-questions)的实际消费者或测量触发，不继续追加本案必经门禁。
