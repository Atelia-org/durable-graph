# Branch wire format 候选见证

问题：DB-096 B2 的成员目录、准确书签与提交摘要，能否用严格、紧凑且有界的简单编码承载？最小成功条件是候选 round-trip、畸形输入拒绝、摘要独占，以及真实 HeadStore payload 限长下的容量算术成立。

这是独立候选 codec，**没有改变 DG 产品协议或公开 API**。它不实现 Graph codec、历史资格、成员验证、事务或存储 I/O。所用 ID/FrameAddress 是通过上游 codec 构造的数值坐标；解码成功不证明目标存在或可用。设计入口见 [B2 计划](../../docs/design-branches/0096-history-and-head-model/branch-cutover-plan.md)。

## 运行

要求 .NET 10 与干净的 Storage `f8868b57e8f012745413bb38a83d67f1dba29f26` 源码。项目独立校验 revision，不继承 DG 默认包选择；没有测试框架依赖。

```powershell
$probeStorageRoot = (Resolve-Path .artifacts/db096-head-mechanism/storage-f8868b5).Path
dotnet run --project experiments/BranchWireFormatProbe/BranchWireFormatProbe.csproj -c Release -p:ProbeStorageRoot=$probeStorageRoot -m:1 -nr:false
```

没有该 worktree 时，可自行准备同 revision 的干净 checkout，将其绝对路径传入。程序失败抛异常并返回非零；成功输出容量行及断言数。

## 试验格式

以下整数均为固定 UInt32LE；文本无编码器添加的 BOM，严格 UTF-8，不做 normalization/trim。标签与标签名均非空；主候选仍为最多1024 UTF-16 code units。256上限只用于容量比较，不是产品决定。

| 载体 | 候选字节 |
|---|---|
| D | `P0R3 + count + (HeadId[4] + byteLength + labelBytes)*`；按无符号编码字节 lex 升序，HeadId 不重复，文本可重复 |
| T | `P0T2 + count + (byteLength + nameBytes + HeadEntryId[12])*`；名称 Ordinal 升序且唯一 |
| Entry | `P0E1 + FrameAddress[12] + summaryByteLength + summaryBytes`；没有 Kind、Parent 或 Schema 副本 |

BranchId/CommitId 薄包装直接调用上游的4B/12B codec。它们没有内存布局合同或完整库身份保障。D/T 解码在分配结果表前按剩余 payload 限制 count；每个文本长度先检查编码容量再取片，拒绝溢出、非法 UTF-8、截断、尾随和非规范排序。所有载体检查 `HeadStore.MaxHeadEntryPayloadLength`。摘要在接收、解码和返回时复制，返回值没有共享可变数组。此处为看清所有权而直接返回副本，不代表最终 API 必须每次属性访问都分配。

T 在这里仅证明 map 编码与重复键拒绝；不可变名不能重绑仍是未来 Repository 的操作合同。旧版本拒绝只是 magic 不匹配测试，没有格式迁移或完整旧 store fixture。

## 2026-10-19 实测

Release 构建、运行通过 **159个断言**（其中包含三个小样本的每个截断位置）。覆盖默认/严格长度 ID、短目标写入不改变目标、重复中文标签、byte-lex 顺序、非法 UTF-16/UTF-8、计数/长度 UInt32 最大值、重复键、超长文本、截断/尾随/版本、摘要独占以及 payload 边界。

当前上游 payload 上限为 **1,048,576 B**。本候选 Entry 固定开销 **20 B**，摘要上限 **1,048,556 B**；恰好上限可往返，超过1 B拒绝。这是 HeadStore payload 算术，不是建议应用把摘要写满，也不覆盖外层记录开销。

1000条分支的 D 字节数为 `8 + Σ(8 + UTF8(label).Length)`：

| 每条标签长度 | ASCII：D总字节 | BMP中文：D总字节 | 中文同长标签容量 |
|---|---:|---:|---:|
| 32 code units | 40,008 | 104,008 | 10,082 |
| 64 code units | 72,008 | 200,008 | 5,242 |
| 256 code units | 264,008 | 776,008 | 1,351 |
| 1024 code units | 1,032,008 | 3,080,008（拒绝） | 340 |

有效 Unicode 的最坏 UTF-8/UTF-16 code-unit 比是3；补充平面字符占2 units/4 bytes。因此256上限足以让1000条最坏标签落在1 MiB内，但保留1024上限同样可用，必须按总编码大小拒绝。实验不设1000成员硬上限，并实际编码了1001条短标签。容量不代表查询速度、目录更新写放大或存储性能；没有做基准，也没有 DG 新布局、package、冷重开或故障资格。

后续：B2 在产品 codec 中选择并锁定实际格式，复用必要反例；不要将这个本地实现保留为第二套生产协议或扩成独立框架。
