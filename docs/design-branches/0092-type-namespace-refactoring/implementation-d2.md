# DB-092 D2：Generator 入口名称迁移

> 2026-10-14；源码基线 `3f309f0`，源码提交 `a94c547`。源码、根回归、生成等价与准确新包消费均已限定验收。
> 问题：入口名称能否覆盖实际 DTO/body、Capture/Hydrate、reader/model、Upgrade 和 exports 职责，同时保持生成合同？

## 当片合同

`Atelia.DurableGraph.Generator.DurableSchemaGenerator` → `Atelia.DurableGraph.Generator.DurableGraphGenerator`。
只改 17 份 partial 声明与 3 处测试直接构造点；测试宿主名称、文件路径、唯一 `[Generator]`、程序集／包／目标框架保持。
用户没有兼容负担：消费者重新编译，不保旧入口或双 Generator；派生生成输出可一次性清理。
Schema history 和仓库数据不属于生成输出清理范围。

最小验收：源码只有批准的 identifier 替换；有限代表输入的 generated texts／hints／canonical history 原始字节相同，diagnostics 只允许入口 FQN 带来的生成路径变化；根构建和相关 SG／Build tests 通过。
准确新 G 的 PackageReference 消费验证干净 Publish／Verify，以及保留旧入口输出时的现有重复 manifest 拒绝和显式清理后重建。
生成用户宿主、marker 元数据身份、算法、SchemaId／FieldId 和 history 字节保持；没有兼容层或常驻旧输出清扫机制。
两个 manifest 数量错误只改提示文案：显示生成路径后要求清理本项目的 DurableGraph 派生输出并强制重编译，保 Schema history；扫描、条件和工具调用保持。

## 源码与生成合同

实施者、独立复核者和主线程均从 Git 基线核完整源码：20 份 C# 仅有 20 个批准 identifier 替换，其他字节相等，旧入口没有壳或第二个 `[Generator]`。
主线程另核 targets 完整 XML：只有两条 `Error.Text` 改动，数量校验、扫描与 Exec 保持；证据 `.artifacts/db092-d2/root-source-acceptance.json`。

两侧从准确基线／冻结新源独立编译 Generator 和 Build，使用 D1 已验证包的相同 runtime 元数据引用；编译输入 SHA 逐项核对。
新快照中的 Build README 在提示文案更新前冻结，属于说明文档，不参与编译；实际新包另验证最终 targets。
14 个固定场景覆盖普通生成、非泛型／泛型 Family、record／enum／Nullable、Upgrade、accepted／retained history、forced definitions、外部 fixed base／inline 与诊断反例。

| 比较 | 实际结果 |
|---|---|
| generated texts／hints | 51 份，每侧 465,326 UTF-8 字节；完整原始字节、hint 和 SHA 相同 |
| provider DLL | 两个跨库 provider 的实际 emitted DLL 字节相同 |
| diagnostics | 13 个场景原始记录相同；`upgrade-v2` 的三条隐藏 `CS8019` 只改变 generated Location.Path 中的入口 FQN，ID／severity／message／行列保持；两侧 raw 记录保留，仅逆映射这一目录片段 |
| Build／history | 每侧 15 次 publish／再次 publish／verify 的输出与退出码相同；6 份 history 共 1,050 字节，路径、文件名、完整字节相同 |

主线程直接核 DLL、编译源、refs、fixture、51 份输出和 history，验收 `.artifacts/db092-d2/root-equivalence-acceptance.json`。
实施 receipt `.artifacts/db092-d2/equivalence/result.json`，SHA256 `A1AEC743CDA946BAC6E94F21CA5B22686211A8B2C96C51846D165A6B0488292D`。
这只是有限输入的生成合同资格，不是全体源程序的证明。

## 根构建与测试

准确 S 的 package mode：

```powershell
dotnet build DurableGraph.slnx -c Release --no-restore -m:1 -nr:false -p:UseStorageSources=false
dotnet test tests/DurableGraph.Tests/DurableGraph.Tests.csproj -c Release --no-build --no-restore -m:1 -nr:false -p:UseStorageSources=false
```

根构建零警告／错误；相关 Runtime／SG／Build 的 1628 项 tests 全通过，失败／跳过为零。
两个 targets 提示修改后的最终根构建也通过；没有修改 Runtime／Persistence／Serialization 执行逻辑。
日志 `.artifacts/db092-d2/build.log`、`final-build.log`、`tests.log`。

## 当前包消费

一次调用现有 `Prepare-DurableGraphProbeFeed`，复用同一 G 完成两条有限故事；没有恢复退休 runner。
pack 前后使用现有 Split runner 的原生 provenance 函数，188 个生产输入对应已提交源码；主线程核闭包、Git 全文字节、十包身份／revision 和实际 SG／Build／targets 字节。
包制作时只有本片文档／PROJECT-STATE 尚未提交，不属于生产输入变更。

| 输入 | 准确值 |
|---|---|
| G／producer | `0.0.0-db092-d2-e2e.20261014100947.33564`／`a94c5475c2ddfd70639d0f1983d25a0f461e0c2e` |
| S／revision | `0.1.3-dev.20261014.g758cebf`／`758cebf8ba82788ba91435d34fd484dff32095fd` |
| feed | `.artifacts/db092-d2/package/feed` |
| consumer workroot | `W:/TEMP/dg-db092-d2-consumers-20261014T101301-33736` |

普通 Consumer3 与 forced Family 的 DomainLibrary V1 编译／运行均通过。两份 assets 只有准确新 G／S 的 PackageReference，实际编译日志和包缓存核对 SG／Build；编译器自动发现新入口，两条故事各 4 个 hints 原始字节与 D1 相同，7＋2 份 history 字节保持。
普通故事沿用完整 exercise；Family 的临时有限 host 检查 Base／Read／Delta／Apply golden bytes，没有增加永久验证框架。

两条故事各自实测如下：

1. 干净生成输出中仅新入口 FQN，每种 manifest 一份，Publish／CI Verify／run 成功。
2. 只复制 D1 的派生生成输出，强制重编译后每种 manifest 两份；按最终 candidate 数量错误文案拒绝。没有执行旧包或旧数据。
3. 当前 SDK `10.0.201` 的 `dotnet clean` 成功，仍保留旧／新共 8 份生成文件；实际 FileList 未登记这两个 manifest hints。
4. 解析并确认该消费者独占的 `CompilerGeneratedFilesOutputPath`，使用原生 PowerShell `Remove-Item -LiteralPath` 一次性清理；保源文件与 history，再强制重建。
5. Publish／CI Verify／run 恢复成功，旧 FQN 消失、新 FQN 唯一，每种 manifest 回到一份；history 保原字节。

主线程直接核源 fixture、D1 原始 hints／history、22 个命令日志、清理前后足迹、四个实际运行 PID 和 12 次实际 DG／Binary DLL 加载；验收 `.artifacts/db092-d2/root-pack-acceptance.json`、`root-consumer-acceptance.json`。
第二条 reference 数量错误的文本由源码 XML 复核保证；dirty 实测由先执行的 candidate 错误拒绝，没有声称同时执行了两条 Error。

终态 `.artifacts/db092-d2/package/qualification-result.json` SHA256 `927CFA95F4313E8387A74890BC854AB4A2399DFC8562FB27609584217D7547E1`；原始 `consumer-result.json` SHA256 `A3D1B2B8046544C29F6EF848E45EB2D9AE8CB930E5AE0F86669D405D821F7E51`。
这是入口迁移、当前包自动发现与派生输出恢复的有限资格；未重跑完整 Split／Meta 存档故事、crash／性能或远程发布，也不授旧包／旧数据兼容。

## 续工

D2 没有移动文件；下一片建议从 E1 的直接文件名对应开始，具体范围与其他候选只维护在[路线图](../../DurableGraph-research-roadmap.md)。程序集／namespace 仍依据真实消费边界判断，不预设拆包。
