# DB-106 · 业务保存分类的实验结果

**建议主线采用 `c19a8d2a` 的产品差异。** 实验分支为 `research/db106-business-save-classification`，对照为 `e1a4e42d`。业务写规划器移除了一处对注册绑定家族的识别，八代保存和冷开的回执逐字节相同。最终候选在所测分类路径上保持了分配量，也消除了第一版的额外调用。[候选设计](README.md) · [复现入口](../../../experiments/BusinessSaveClassificationProbe/README.md)

## 只移动哪项责任

业务准备行 `PreparedCapturedObject` 从已有的前驱、比较正文和对象种类计算 `ComparisonRequiresBase`。在唯一的业务生产入口中，已验证身份的旧 string 跳过比较，其余有前驱的对象必须成功完成比较才能交付准备行。注册叶允许对任意一对值返回 null，普通模型的非法 null 会在准备时抛出。写规划器使用该结论，并独立处理新对象与源布局强制重写。管理目录用自己的写入规则。第一版 `97d6189d` 替换写规划器的一处类型判断，第二版 `c19a8d2a` 只给同一属性添加 JIT 内联提示。两版均沿用原有五个实例字段。[准备行](../../../src/DurableGraph/Runtime/Capture/PreparedCapturedGraph.cs) · [唯一业务生产入口](../../../src/DurableGraph/Runtime/Capture/CaptureSession.cs) · [写规划器](../../../src/DurableGraph.Persistence/ObjectWritePlanner.cs)

## 八代实际落盘

两个进程分别从同一份已关闭空仓的副本开始，通过 `Repository.SaveGraph` 保存。图包含两个共用同一个 codec 的可变叶、一个 128 项的 List 和一个 string。程序在每次提交后读取实际对象批，再关闭 Repository，以新的只读 Repository 打开八个历史版本，检查领域值、记录与前驱链。[保存及冷开程序](../../../experiments/BusinessSaveClassificationProbe/records/Program.cs)

| 操作 | 实际对象记录 |
|---|---|
| 初存 | 根、两个叶、List、string 各写一条 Base |
| 保留原值与对象身份 | 无记录 |
| 同时改两个叶，其中一个比较返回 null | ID 2 写 Base，ID 3 写 Delta |
| 保持叶值 11，codec 对 (11,11) 仍返回 null | ID 2 再写 Base |
| 把该叶改为 12，并修改 List 的第 32 项 | ID 2、4 各写 Delta，各自指向准确前驱 |
| 替换字符串内容 | 根和新 string（ID 6）写 Base，旧 ID 5 退出目录 |
| 换成内容相同的另一 string 实例 | 根和新 string（ID 7）写 Base，旧 ID 6 退出目录 |
| 再次保存相同引用 | 无记录 |

八代共 11 条 Base、3 条 Delta。稀疏编辑那一步，叶与 List 的累计重建字节数 H 分别由 262 增至 278、202 增至 226。逐行复算，每个 Delta 的 H 都等于自身实际 payload 加同对象前驱的 H。基线、第一版和第二版的原始 Windows 回执均为 15,622 字节，SHA-256 均为 `D84747937562D45D816C48263C427694170CF8568175B0A97102B8162250743E`。仓内保留一份统一 LF 换行的回执，含记录 ID、完整 body、物理地址、对象目录、H 和冷开值。[逐记录回执](records.json)

## 为什么给 getter 指定内联

在 .NET 10.0.5、Windows x64、Ryzen 7 1700 上，用相同的 Release 程序构造 197 个已准备业务行，含 98 个未变、33 个更新、65 个必须写 Base 的已有对象和 1 个新对象。每个进程先核对全部结果和正文，再预热 1,024 轮，每个样本计时 806,912 次分类。固定一个逻辑 CPU，关闭分层编译取得 FullOpts 机器码。每个候选与同一基线各做八对独立进程，两组各四对以相反顺序开场。表中先取每个进程五个样本的中位数，再计算八对差的中位数。[夹具与测量循环](../../../experiments/BusinessSaveClassificationProbe/classification/Program.cs) · [160 个样本、产物身份及逐组数据](classification.json)

| 业务分类成本，相对基线 | 八对中较慢 | 耗时差中位数 | 相对差中位数 | 分类分配 |
|---|---:|---:|---:|---:|
| 第一版，普通计算属性 | 7/8 | +1.03 ns/行 | +3.76% | 64 B/行，与基线一致 |
| 第二版，同一属性提示内联 | 2/8 | −0.63 ns/行 | −2.46% | 64 B/行，与基线一致 |

基线先检查 `DeltaBody`，仅为空时才执行注册绑定的类型检查。第一版 FullOpts 机器码在已有且无需强制重写的对象上调用独立 getter，getter 再次检查 `Previous`。第二版的调用点已内联，已有行只有 Delta 为空时才读取 `Current.Layout.Kind`，重复的前驱检查也已消失。可令 `DOTNET_JitDisasm=Atelia.DurableGraph.Persistence.ObjectWritePlanner:ClassifyCaptured` 在相同设置下检查调用点。64 B 是返回的 `PreparedObject` 的分类成本。基准预先准备了冻结行，整次 SaveGraph 还包含捕获、编码、图规划及 I/O。

在同一颗 CPU 上清除 JIT 配置覆盖，复用同一消费程序与最终两套 DLL，另跑默认 .NET 设置的四对进程。候选四对都较快，差的中位数为 −2.29 ns/行，40 个样本的分配仍全部为 64 B/行。默认 JIT 可在进程内切换编译层级，这组数据只用于检查正常设置下是否出现明显退化。[默认 JIT 样本](classification-default.json)

## 合入范围

SDK 10.0.201，Storage 源码固定为 `3f780ad95239d7556f917e1adca6839cda144c92`。基线和两个候选的根 Release 构建均为零警告、零错误，两个候选分别通过全部 **3,455 项测试**，零跳过。根套件还覆盖普通比较回调错误返回 null 后重试、公开准备返回未变对象的零掩码、旧布局强制重写，以及独立 Event 保留 State 基线。

产品差异位于两个文件中，按 `97d6189d`、`c19a8d2a` 顺序可独立摘取。再调整注册绑定的内部层次时，业务规划器可以沿用现有规则。未来若一种非字符串业务对象也能证明未变并跳过比较，应在引入它时将结论改由生产点显式给出。Base 计长、字典键校验、家族登记仍沿用各自的边界。本实验在独立工作树完成，主线采用与包发布另行决定。
