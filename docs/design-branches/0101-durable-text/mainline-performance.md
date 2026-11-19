# DurableText 产品路径有限成本见证

本文数据来自固定 UTF-16LE Base 的初次接入。现行分段 Base 的字节与解码分配见[分段 Base 实施记录](segmented-base-refactoring-plan.md)。

2026-11-18；三次独立测试进程全部通过，每次包含四条真实保存链；每个测试执行约 1.7 秒。按需 Base 编码保持实际写入相同，显著减少小改保存分配；1Mi 单元场景也观察到完整 Save 时间下降。64Ki 的时间有明显首次执行影响，不作稳定速度排序。接入与格式合同见[实施计划](mainline-integration-plan.md)。

入口是 [DurableTextProductProbeTests.cs](../0104-text-editor/README.md#native-text-sources "Git 06c57dd4:tests/DurableGraph.Persistence.Tests/DurableTextProductProbeTests.cs")。默认测试不运行性能工作；显式设置 `DG_TEXT_PRODUCT_PROBE` 为不存在的 W SSD 输出目录，`DG_TEXT_PRODUCT_PROBE_REP` 为 0、1、2，单独筛选 `RecordProductSaveAndConsumerCosts`。每次进程最多三分钟；三个进程顺序运行，测量期间不并行构建或其他测量。

- 两个规模：65,536 / 1,048,576 UTF-16 单元，每条真实生成模型包含一篇正文和一句 Gist。先初次保存，再固定 32 个不同位置的单字符改写并逐次 Save。
- 对照：默认按选择编码与内部测试 eager 开关；格式、图级策略、编辑整理、发布和安装相同。重复进程交替两臂顺序。
- 保存计时包括 capture、prepare、append、publication、install；编辑、初次保存、32 次后续保存分别保留时间/当前线程累计分配。真实 Base 编码入口计数；测量后回读文本记录的 Base/Delta、payload bytes 和 body SHA256，断言两臂相同。
- 冷开指关闭 Repository 后新 Repository + OpenGraph + 完整 ToString 消费，不声称操作系统缓存冷。正文与独立 char[] oracle 对比。
- 消费侧在同一份碎片正文上比较直接 AppendTo、逐篇 ToString 再 Append、快照 AppendTo，均拼四篇并计入最终上下文 string；普通文本和重复前缀文本分别比较跨块 Ordinal IndexOf 与显式一次物化后 string.IndexOf。消费路线各有一次本进程预调用，不混入保存计时。
- 编码小样保留 ASCII、中文、混排、跨物理片段代理对与孤立代理/NUL 的实际 Base/全 New Delta 字节，并对比现有 string 编码大小。样本编码计时包含将 owned body 复制为小证据字节数组，不作为纯 codec 吞吐微基准。

三个重复是有限描述，不给置信区间或服务时延保证。累计分配不等于驻留内存；文件逻辑长度不等于设备实际写入或写放大；文本 record payload 不含共享图目录/Schema/Head 发布开销。所有原值写入各进程 `results.json`，stores 留 W，不加入版本库。

## 保存结果

下表为每条链 **32 次改写之后的完整 Save 合计**，不包含另列的初次保存和编辑。分配为十进制 MB；括号保留三次原始时间，按 rep0/1/2 排列。每次都读取实际帧验证，正文冷恢复与独立 oracle 相等。

| UTF-16 单元 | Base 准备 | 32 Save 中位 ms（原三值） | 累计分配中位 MB | 后续全文 Base 编码次数 |
|---|---|---:|---:|---:|
| 65,536 | eager | 55.43（52.50 / 94.34 / 55.43） | 33.932 | 32 |
| 65,536 | 按选择编码 | 75.65（75.65 / 42.24 / 76.16） | 2.276 | 0 |
| 1,048,576 | eager | 200.64（179.71 / 218.41 / 200.64） | 477.993 | 32 |
| 1,048,576 | 按选择编码 | 37.25（37.60 / 37.25 / 36.58） | 2.141 | 0 |

1Mi 的后续 Save 中位时间下降约 81.4%，分配下降约 99.6%；64Ki 分配下降约 93.3%。64Ki 的两个时间范围重叠，且每个进程先执行的路线明显更慢。初次保存没有专门预热，rep0/2 先执行按需，rep1 先执行 eager；不要将此批初次调用成本归因于选择算法。

两种路线、所有重复都产生 **1 个文本 Base + 32 个文本 Delta**，初次 Base 编码一次。逐版本正文 body SHA256 与记录 payload 字节也完全相同；它们没有换编码或放弃保存。32 次编辑每条链仅累计分配 33,536 字节，原始编辑时间保留在 CSV。此轨迹不单独测量内部整理次数，不能将“Base 编码 0 次”理解为一般情况下编辑器绝不物化。

| UTF-16 单元 | 初次文本 record payload B | 32 次后续 payload B | 累计文本 payload B | Repository 文件长度 B |
|---|---:|---:|---:|---:|
| 65,536 | 131,082 | 1,029 | 132,111 | 142,296 |
| 1,048,576 | 2,097,163 | 1,044 | 2,098,207 | 2,108,412 |

payload 采用实际解码的 `EncodedPayloadBytes`，包括对象版本自身封套，排除共享 map/图帧和本地 ObjectId key；文件长度另含 Schema、图目录和发布等内容，不是设备写入量。省掉的是未选中 Base 的准备工作，盘上字节没有减少。

| UTF-16 单元 / 路线 | 初次 Save 三次范围 ms | 冷开+完整消费三次范围 ms | 冷开分配中位 MB |
|---|---:|---:|---:|
| 65,536 eager | 13.88–285.69 | 26.10–98.34 | 1.737 |
| 65,536 按需 | 14.43–307.21 | 27.96–138.97 | 1.738 |
| 1,048,576 eager | 34.98–45.44 | 36.99–38.10 | 11.569 |
| 1,048,576 按需 | 35.53–39.66 | 38.03–39.59 | 11.568 |

每个进程第一次 cold 路线包含尚未预热的恢复代码；两路线持久内容一致，不能从第一行差距推断按需编码会降低冷读性能。以上是 32 个连续 Delta 的真实产品恢复成本，未增加链上限。

## 渲染与窄查找

四篇同样的碎片正文拼成一个上下文，计入最终 `StringBuilder.ToString()`。以下为普通文本的中位时间/分配；重复前缀的所有原值同样留在证据。单次消费且有 GC/JIT 扰动，重点是复制/分配边界，不宣称微小时间顺序稳定。

| 单篇单元数 | 直接 AppendTo ms / MB | 逐篇 ToString 再 Append ms / MB | Snapshot AppendTo ms / MB |
|---|---:|---:|---:|
| 65,536 | 0.136 / 1.055 | 0.177 / 1.573 | 0.155 / 1.055 |
| 1,048,576 | 13.389 / 16.796 | 13.335 / 25.166 | 7.418 / 16.796 |

AppendTo 和稳定快照消费均避免四篇正文的中间 string，但仍有目标 builder 与最终上下文 string 的分配。公开快照并没有缓存完整 string。

| 单元数 / 查找样本 | 跨块 IndexOf 中位 ms / 分配 B | 物化后标准 IndexOf 中位 ms / 分配 B |
|---|---:|---:|
| 65,536 / 普通文本、偏移 11 命中 | 0.0900 / 136 | 0.1277 / 131,128 |
| 65,536 / 重复前缀、近末尾命中 | 0.2390 / 136 | 0.0391 / 131,128 |
| 1,048,576 / 普通文本、偏移 11 命中 | 0.0014 / 136 | 0.7354 / 2,097,208 |
| 1,048,576 / 重复前缀、近末尾命中 | 3.5257 / 136 | 0.4691 / 2,097,208 |

窄查找省掉连续全文分配，但本批重复前缀样本上标准 string 查找即使计入物化仍更快；保留显式物化路径有价值。普通样本的 64Ki/1Mi 查找时间差包含 tiered JIT/首次路径影响，不能解释成更长文本反而有更低算法复杂度。

## 编码样本

| 样本 | UTF-16 单元 | Text Base B | Text 全 New Delta B | 现有 string 编码 B |
|---|---:|---:|---:|---:|
| ASCII | 43 | 87 | 89 | 44 |
| 中文 | 19 | 39 | 41 | 39 |
| 混排 | 24 | 49 | 51 | 33 |
| 跨片代理对、孤立代理、NUL、CRLF | 10 | 21 | 23 | 21 |

固定 UTF-16LE 的英文/混排成本没有隐藏：ASCII 样本 Base 接近现有 string 的两倍。本文记录这一首版取舍，不将它改成未经验证的 UTF-8 路线。原 JSON 有完整 Base/Delta 十六进制字节；字符按 UTF-16 保真，包括孤立代理。

## 证据与复现

- [原始 rep0](product-path-20261118/rep0.json)、[rep1](product-path-20261118/rep1.json)、[rep2](product-path-20261118/rep2.json)；[12 条保存汇总](product-path-20261118/saves.csv)、[全部消费原值](product-path-20261118/consumers.csv)、[实际 DLL 身份](product-path-20261118/dll-hashes.json)。同目录保留三个测试 PASS 日志。没有删慢值或在结果后改变输入。
- 构建先于 commit，`fb2bef0ad2acc9c146c72c4edf4a2cd55ce35714` 固定受测产品与测试源码，随后仅更新文档；运行二进制另附 hash。测量 Runtime DLL `A87BAE8F…80A3`、Persistence DLL `DE2BD8C8…FA84`、test DLL `BFCD0812…F171`；完整 SHA256 在上表链接。测试源码 SHA256 为 `F3D1B258B59385C0E75E3C35ED73C4A978E52E1E6943233B2449A1F8002AB27C`。
- .NET 10.0.5、Windows；W SSD 用户指定。实际 stores 在 `W:/DurableTextProbe/product-mainline-20261118-124312`，不入库。源码所在 E 盘不承担本轮 store 测量。
- 这是源码模式资格，使用既有精确 Storage pin；普通包接入资格由独立消费者记录承担。

```powershell
# 在 fb2bef0a 的独立 checkout 中按仓库 storage-dependency.md 完成 Release/source-mode build；每次取全新的输出名。
$env:DG_TEXT_PRODUCT_PROBE = 'W:/DurableTextProbe/my-product-run/rep0'
$env:DG_TEXT_PRODUCT_PROBE_REP = '0' # 三个独立进程分别为 0、1、2
dotnet test tests/DurableGraph.Persistence.Tests/DurableGraph.Persistence.Tests.csproj -c Release --no-build --no-restore -m:1 -nr:false -p:UseStorageSources=true -p:StorageSourceRoot=E:/repos/Atelia-org/atelia-storage --filter FullyQualifiedName~DurableTextProductProbeTests.RecordProductSaveAndConsumerCosts
Remove-Item Env:DG_TEXT_PRODUCT_PROBE,Env:DG_TEXT_PRODUCT_PROBE_REP
```
