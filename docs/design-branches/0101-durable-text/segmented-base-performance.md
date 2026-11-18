# 分段 Base 产品成本见证（2026-11-18）

产品 `TextBodyCodec.ReadBase` 在本批 8 个工况、40 次直接解码中，分配均为**一个最终字符串加固定 280 字节的 state/view/list 等包装**。把相同正文从 1 个物理段拆成 4,096 或 65,536 段，没有增加解码分配；分段头增加了落盘字节，逐段处理仍有时间成本。这里验证的是已接入产品 codec，不是独立模拟解码器。

## 方法和边界

[可复现 probe](../../../tests/DurableGraph.Persistence.Tests/DurableTextProductProbeTests.cs) 的 `RecordSegmentedBaseCosts` 使用两种正文：全 `a`，以及 `汉a` 交替；长度为 65,536 和 1,048,576 个 UTF-16 单元。每份正文分别从一个字符串开始，或通过一次公开 `ApplyEdits` 构造独立 16 单元 literal backing。输入构造、Base 编码和验证都在解码计时外。每 case 预热一次 codec，随后记录五个原始样本；这是内存中准备好的 body 的重复解码，不是 OS 冷读或跨进程稳态性能测试。

每次分别测 `ReadBase(body)`、单独 `new string(expected.AsSpan())`、相同 `DurableTextState(TextSnapshot.FromString(existing))` 包装，以及恢复后 `ToString()`。检查完整字符相等、恢复后只有一个 piece、`ToString()` 返回相同 backing string。独立包装控制不会复制已有字符串；不把 Repository/图恢复周边分配混进 codec 数据。累计调用线程分配不等于存活堆大小。

此外，每 case 实际创建 Repository 并执行一次首次 `SaveGraph`。事后用 `TypedBaseBodyCodec.Decode` 去掉类型头，确认真正落盘的 Base body 与预先编码逐字节相同。保存包含捕获、规划、编码、安装以及可能的 Piece 整理；其原始时间/分配保留在 JSON，首 case 包含 JIT，不能作为八路严格性能排名。

## 字节和解码观察

`Base body` 含全文单元数前缀和各段 Bare string；`对象 payload` 另外含类型头及对象版本封套，不含整个图及仓库文件。时间为五次中位，单位 ms；原五值见 [segments.json](segmented-base-product-20261118/segments.json)。

| UTF-16 单元数 | 正文 | 段数 | Base body B | 对象 payload B | soft estimate B | 解码 ms |
|---:|---|---:|---:|---:|---:|---:|
|65,536|ASCII|1|65,542|65,549|131,076|0.0749|
|65,536|ASCII|4,096|69,635|69,642|135,171|0.2142|
|65,536|汉a|1|131,078|131,085|131,076|0.0920|
|65,536|汉a|4,096|135,171|135,178|135,171|0.4417|
|1,048,576|ASCII|1|1,048,583|1,048,590|2,097,156|1.5188|
|1,048,576|ASCII|65,536|1,114,115|1,114,122|2,162,691|1.9555|
|1,048,576|汉a|1|2,097,159|2,097,167|2,097,156|1.2284|
|1,048,576|汉a|65,536|2,162,691|2,162,699|2,162,691|1.4622|

| UTF-16 单元数 | 每次 ReadBase 分配 B | 最终 string 控制 B | 包装控制 B | 恢复后 ToString 分配 B |
|---:|---:|---:|---:|---:|
|65,536|131,376|131,096|280|0|
|1,048,576|2,097,456|2,097,176|280|0|

第二表每一项在两种正文、两种段数及五次样本中均完全一致。它支持本批没有每段中间字符串/字符数组的分配，不能单凭字节数证明任意执行中的对象数量。固定包装来自当前 `FromString`/state 构造，后续实现变化可改变其数值。

相同 ASCII 正文多段比单段增加 4,093 / 65,532 字节；交替汉字/ASCII也是同样的头部差额。`V(N)+K+2N` 是 soft estimate：大段 `汉a` 实际比它多 2 / 3 字节，不能称上界。ASCII 的 UTF-8 节省没有体现在这个估值中，因而 eager 精确 Base 与 deferred soft estimate 在一般负载下可以改变 checkpoint 决策。

## 原 eager / deferred 探针的限定复核

本轮另启用了原 `RecordProductSaveAndConsumerCosts`，固定 64Ki / 1Mi 正文、32 次单字符保存。它实际核对了两路均为初始 Base + 32 Delta，所有记录类型、payload 字节与 body SHA 相同，changed-save Base 编码次数分别为 eager 32、deferred 0。因此这一个稀疏修改 fixture 仍能比较“未选 Base 的编码成本”；不将这个相同策略路径结论外推到所有负载。

| UTF-16 单元数 | deferred 32 次 Save ms / 分配 B | eager 32 次 Save ms / 分配 B |
|---:|---:|---:|
|65,536|117.6207 / 2,275,200|72.7087 / 18,038,320|
|1,048,576|46.0582 / 2,144,008|193.9421 / 242,256,200|

这里仅一个进程，先 deferred 后 eager；64Ki 时间仍受首路径 JIT 影响，不据它判时间优劣。完整各 Save 原值、冷开与消费者数值留在 [save-chain.json](segmented-base-product-20261118/save-chain.json)，本片不重开三进程大对照。

## 来源、失败保留和复现

产品来源 `712ddf7f73b668d141083c7ef10401545b4ed6be`；Storage pin `3f780ad95239d7556f917e1adca6839cda144c92`。本片 probe 为该产品提交后的测试增量，准确源/DLL SHA 见 [identity.json](segmented-base-product-20261118/identity.json)。分段最终运行的 Persistence.Tests DLL 为 `F5428F0DEC01E8DE8F6F931B0DF2A8CE20EA2404C0E180043C9E6083C4683482`。本 probe 使用源码模式、独立测试项目 `-c Release` 构建，0 警告/0 错误；最终分段测试通过。这里不把根 solution 的外部 project 配置推定为 Release；根构建另按 SDK solution 的实际默认配置记录。原保存探针在移除分段方法分配 guard 前的构建通过，该重建没有修改产品或 SaveChain 路径。

最初 probe 的引用类型 `Cost` 在结束读数前可能引入自身 32 字节分配，造成“两个控制相加比一次解码多 32B”的错误断言；改成 value type，并在构造结果前读取结束计数。第二次观察又出现一次 6,952B 控制差额，测量 guard 失败。没有确认其来源，不能把它宣称为产品分段分配；两个失败日志原件均保留在 W 路径。最终去掉分配阈值断言，保留字符/单 backing/实际落盘等功能断言，完整输出所有样本后得到上表；不删除最终样本或挑选子集。

原 stores 和失败运行留在 W SSD：`W:/DurableTextProbe/segmented-product-20261118-152349`（首次失败 `...-152034`）。仓内仅保留最终原值、保存链原值、identity 和[小证据清单](segmented-base-product-20261118/files.json)等 JSON。清单的 `repository` 条目是仓内文件；`external-local-log` 条目记录 W 上构建及成功/失败日志的绝对路径、长度与 SHA256，日志原件不入 Git。stores 同样只留在 W。

```powershell
# 使用准确 Storage source pin，构建由共享工作窗口串行执行。
dotnet build tests/DurableGraph.Persistence.Tests/DurableGraph.Persistence.Tests.csproj -c Release -m:1 -nr:false -p:UseStorageSources=true -p:StorageSourceRoot=E:/repos/Atelia-org/atelia-storage
$env:DG_TEXT_SEGMENT_PROBE = 'W:/DurableTextProbe/segmented-product-new-run'
dotnet test tests/DurableGraph.Persistence.Tests/DurableGraph.Persistence.Tests.csproj -c Release --no-build --no-restore -m:1 -nr:false -p:UseStorageSources=true -p:StorageSourceRoot=E:/repos/Atelia-org/atelia-storage --filter FullyQualifiedName~RecordSegmentedBaseCosts
# 原保存链另设 DG_TEXT_PRODUCT_PROBE 为不存在的目录，DG_TEXT_PRODUCT_PROBE_REP=0，
# filter 改成 RecordProductSaveAndConsumerCosts。
```
