# DB-104 主线迁移与验收

2026-11-20，主线以 `06c57dd4` 建立新旧文本的共存对照，`3013b890` 明确公开叶协议，`51ac46c3` 统一生成模型和历史中的名义声明，`f829f849` 移除内建文本。应用直接使用 `Atelia.Text.TextEditor`，打开仓库前调用 `TextEditorPersistence.Register(models)`。文本和独立位图均可单向依赖 Serialization，图框架按普通名义类型处理它们。[应用示例](../../text/README.md) · [第三方协议](../../../src/DurableGraph.Serialization/DurableLeafCodec.cs)

## 新旧实现共存时的保存成本

在同一 Windows 机器、固定 Storage 提交 `3f780ad9` 和 .NET SDK 10.0.201 上使用真实文件仓。热周期包含编辑和保存，包括注册文本在下一次编辑时消费上一笔提交收据的成本。分配是同步调用线程的托管字节。重新打开仓库时 OS 仍可复用文件页。

| 共存提交上的完整周期 | 内建文本 | 注册文本 | 多分配 |
|---|---:|---:|---:|
| 4,096 个 UTF-16 单元，每次改八处 | 0.818 ms / 59,904 B | 0.824 ms / 62,184 B | 2,280 B |
| 65,536 个单元，每次改八处 | 0.935 ms / 57,252 B | 0.940 ms / 62,492 B | 5,240 B |
| 128 条各 4,096 单元，每次只改一条的两处，同核交替 | 1.286 ms / 444,496 B | 1.349 ms / 456,088 B | 11,592 B |

前两行的 96 对热 Delta 均逐字节相同，每条都指向本图该对象的准确前驱。热周期的 Base 编码次数均为零。128 条正文的九个实测仓中，每仓首次写入 130 个 Base，随后 30 次保存各只写目标文本的一条 Delta，合计 626 B。共 270 条 Delta 的前驱与内容逐项吻合。[原始记录、哈希和复跑驱动](coexist-results/README.md)

128 条的首次三路轮转测得 1.359 → 1.705 ms。为区分时段干扰与接口成本，又把两个独立进程固定在同一逻辑核上，令两份仓库逐步交替执行等价编辑。表中该行按各进程的热中位数汇总；直接比较 144 对相邻提交，时间差的中位数为 **+34.15 µs**，相对差的中位数为 **+2.67%**。注册路径先执行或后执行时，分组中位数为 −3.94% 和 +7.31%。结论限于这些工作负载。退役后的 `f829f849` 另跑三次 128 条工况，每个完整热周期分配 **456,072 B**。三个仓的 90 条 Delta 与共存原生对照相同。[逐提交样本](coexist-results/matched-commits.csv)

## 行为与故障

- 图内多个槽共享同一个可变实例；两个独立打开的图各自恢复实例。文本保留不可变快照、同步可重入的 `VisitSpan`、缓存复用和受快照约束的批编辑。两个图共用同一编辑器时，以各自准确前驱编码。[文本集成](../../../tests/DurableGraph.Persistence.Tests/TextEditorIntegrationTests.cs) · [独立编辑器测试](../../../tests/Text.Tests)
- 叶作者可只实现 Base，也可区分内容未变与真实 Delta。框架在读取每份 body 后检查字节完全消费。V2 codec 实际读取 V1 Base 和 Delta，保留旧图；续存会写当前版本 Base。另有生成器跨包、泛型与基础类型史的回归。[生命周期与历史](../../../tests/DurableGraph.Persistence.Tests/RegisteredLeafLifecycleTests.cs) · [生成器回归](../../../tests/DurableGraph.Tests/RegisteredLeafGeneratorTests.cs)
- Event 保存、丢弃候选及捕获后编辑均保留正确的文本来源。发布前与发布后安装前两处确定性故障分别冷开恢复旧正文和新正文，原收据均未接受。发布阶段由框架标记收据，类型包在后续编辑或捕获时使用。[生命周期与故障边界](../../../tests/DurableGraph.Persistence.Tests/RegisteredLeafLifecycleTests.cs)
- 旧内建结构标签、专用对象种类、生成器识别、v10 文本历史格式和安装路径已删除。普通领域字段、泛型和集合引用均使用现有名义 Schema。需读取历史的 codec 作者要维护 ID、版本和 reader；旧内建文本实验库可在共存提交读取。[历史源码](README.md#native-text-sources)

## 最终交付验证

对 `f829f849fbbf123bcfa83f4c1bd89dd3792d045b` 的 Release 根方案构建为 0 警告、0 错误。五个测试程序集全数通过，Persistence 1,280、Runtime/Generator 1,822、Serialization 133、Storage 36、Text 97，共 **3,368**，零跳过。源码模式的文本与位图应用也分别通过创建、冷开续写、历史复读、缺少注册四阶段。以下命令中的 Storage 路径指向仓内固定的提交。

```powershell
dotnet build DurableGraph.slnx -c Release -p:UseStorageSources=true "-p:StorageSourceRoot=$storage" -p:ShouldUnsetParentConfigurationAndPlatform=false -m:1
dotnet test DurableGraph.slnx -c Release --no-build -p:UseStorageSources=true "-p:StorageSourceRoot=$storage" -p:ShouldUnsetParentConfigurationAndPlatform=false -m:1
```

普通宿主从独立本地包源向空消费缓存还原 13 个包，由包内生成器编译业务模型，宿主显式登记文本和变长位图的 codec。创建、冷开续写、三代回读、遗漏任一登记分别使用独立进程。第一次局改的文本 Delta 为 20 B，位图 Delta 为 `00 01 00 03`，二者均指向各自准确对象版本。文本与位图的两个包均只直接依赖 `Atelia.DurableGraph.Serialization`。独立文本应用的三个进程分别通过 13、18、11 项检查；只安装 DG 的读者能正常打开仓库、读取普通 State，在 `OpenGraph` 读取文本图时，收到指明 `Atelia.Text.TextEditor` 的 `InvalidDataException`。前后数据库字节一致。候选版本为 `0.0.0-leafpkg.20261119184626.gdf3ec7bd`。[包、程序集与阶段回执](package-verification.json) · [两种叶的复跑入口](../../../experiments/RegisteredLeafProbe/README.md) · [文本使用者与读者准入](../../../experiments/PackageConsumerProbe/TextEditorConsumer/README.md)

可搬迁候选 `0.0.0-delivery.20261119184621.gf829f849` 的本地 feed 含 5 个同版本产品包、6 个固定 Storage 包、1 个原始 K4os 包。最小 QuickStart 在另一目录只还原用到的 11 个包。搬迁后通过两次独立运行、历史分叉冷续写、Event、升级及四份 Schema history 的 Verify。该候选的 `Atelia.Text` nupkg SHA-256 为 `9A739C154E666D86CBD01F95B096F0925AC3241AD59BAF76BF1CB38DDA95DD64`。[候选导出与复验命令](../../storage-dependency.md#candidate-delivery)
