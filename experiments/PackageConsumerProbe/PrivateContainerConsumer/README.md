# Private container package consumer

本目录验证 [DB-087](../../../docs/design-branches/0087-meta-bootstrap-refactoring-route.md#private-plan) 的同键同版异形私有新根。普通程序集 `DurableGraph.PrivateContainerConsumer` 通过 PackageReference 调用公开 Repository；A/B 独立编译、各有合法 V1 accepted history。有限包门禁已通过，与既有 [V1→V2／ToShared 门禁](../SplitRepositoryConsumer/README.md)及源码故障回归一起闭合 DB-087 的有限 P1/P2 产品停点。

模型是同一 `pc.root` V1、`pc.point` V1、`pc.keeper` V1。Point 的 X 在 A 为 int、B 为 long，另有三个相同 Double 字段；两 Root 成环、共享 128 项 `List<Point?>`，实际包含 absent/present，B 的 present 值超过 Int32。Keeper 在另一分支接受，其声明在 B 根中不可达。Root 的完整 Schema 在两个环境中相同，List 的 nominal TypeExpr 相同而完整 Nullable inline 布局不同。

old/new 布局用公共 DurableSchema／DurableFieldInfo／ListLayout／ObjectLayout 工厂表达，owner 核实际冻结目录。没有 IVT、internal Snapshot、产品私有反射、手动 Analyzer／AdditionalFiles／Import 或 ProjectReference。Persistence 的 extern alias 只解决既有命名空间别名，不授内部访问。

## 运行

从仓根执行，存储 feed 须带准确六包及其 manifest：

```powershell
./experiments/PackageConsumerProbe/Run-PrivateContainerProbe.ps1 `
    -StoragePackageSource '/path/to/verified/storage-feed'
```

也可提供完整十包 `-PackageSource <feed> -Version <G>` 与对应 source sidecar，跳过 pack。WorkDirectory 必须全新且位于 DG／storage 仓外。runner 创建独立 NuGet.Config／cache、空 Directory.Build.* 隔离材料、A/B stage 和 history；各执行 Publish＋Verify Rebuild，共四次。只复制本目录五个 .cs／.csproj 输入；不共用或改写 A/B history。

| 独立进程 | 实际验收 |
|---|---|
| A `seed` | main 三 live Base、环／共享 List、old tag；另存 Keeper，shared 的完整目录包含其不可达声明 |
| B `create` | 缺 pair 在输出前拒绝、健康且名字未占、全文件／两 ref 不变；关闭后同名 fresh owner 完整修正，新业务空来源、三 live 全 Base；只改 private 声明，保 Root／Keeper／旧员工 rows／shared ref2 |
| A `continue-a` | 新 PID 冷签出 main，单 List 真 Delta；selected S 可取较新 shared，Base origin S 仍为原 main；准确 prior／H、两 Root retained、private 分支不变 |
| B `continue-b` | 新 PID 冷签出 private；完整 ToShared 因同键 Point 异形健康拒绝，关闭后实际 refs／frames／全文件不变；再 fresh private owner 单 List Delta、准确 prior／H、shared ref2 保持 |
| A `verify-a` | 全程独立 PID readonly，main／old tag 正控、private 错误环境拒绝；实际根／链／旧帧和全部文件集合／bytes 不变 |
| B `verify-b` | 全程独立 PID readonly，private 正控、main 错误环境拒绝；同样核准确根／链、完成帧及全部文件集合／bytes 不变 |

缺 pair 的同名修正与 ToShared 拒绝后的续写都使用关闭后的 fresh owner，不授同一 hot rejected workspace 重试资格。A/B 两次 cold Delta 则各由真正的新 writer PID 执行；不承诺跨两个新根的 ObjectId 连续。

[WireInspector](WireInspector.cs) 仅在产品资源全关后以公开 FS／VS／Bare 和 Schema 工厂核本例 ABI5、三份定义及动态 Nullable inline List。它观察实际两 ref revision／payload、声明与 live IDs、graph／OwnedBatch／目录来源、Base origin S、selected 完整布局、同 ID prior 和 record 实耗 H；声明 List 支持普通 Copy/New/CopyAndPatch。领域值由实际公开读取核验。此有限全 inventory 观察不是产品启动 audit、公共诊断 API 或格式冻结。

## 2026-10-13 限定验收

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
