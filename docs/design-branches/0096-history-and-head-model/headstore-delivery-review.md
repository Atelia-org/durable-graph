# DB-096：HeadStore 交付复核与 DG 后续 API 方向

> 状态：2026-10-17，已完成源码复核和独立 public PackageReference 消费者验证。用户随后明确跨库检查只需开发期诊断；[概率与实际实验](handle-misuse-detection.md)支持现有紧凑 Handle。按澄清后的需求，交付可用，无需为强身份返工；撤回每个 DG 引用默认增加 16B scope 的建议。
> 对照：[持久定位值与准确历史请求](headstore-save-handles-request.md)、[五个调用故事](save-handle-stories.md)。本文不修改 DG pin 或运行时，不代表 DG 接入已验收。

## 1. 结论及来源

上游调查 HEAD 为 `3e1dc46041ce731768f18552639c18fbf73e111e`。编码与准确历史增量的实现/包来源为 `c6fb64a97ebc066e10c480e4439f20a2067683a2`；更早的 `34cd0e8` 已将 Handle 改为紧凑表示。不能只看最后一个 feat 而忽略这项前置改变。

两个 subagent 先分别搜集 P1/P3 与 P2 的 API、合同、测试和包证据；主线程复核关键代码、运行独立消费者，并由第三个只读 reviewer 检查仓库作用域包装建议。未改动上游现有工作树。

| 请求 | 实际交付 | 本次判断 |
|---|---|---|
| P1 两类 ID 持久化 | HeadId 4B、HeadEntryId 12B；纯值 TryWrite/TryRead，无 owner 或 I/O | 满足同库往返与已澄清的开发期误用诊断；不是强跨库身份 |
| P2 准确历史起点 | `ReadEntryHistory(startInclusive, budget, visitor, token)`，直接定位 anchor 后继承来源前缀 | 满足 inclusive 与现有分页方案，未发现阻断 |
| P3 导入位置检查 | 同库范围、记录首尾/CRC/Fence、正常 writer 转义与 CU 资格；错误不回退 | 支持正常存储模型中的局部准确验证；错库判断仍受紧凑 Handle 弱身份限制 |

可以继续 DG API 设计。原 P1 的强身份要求与上游紧凑表示确实不同，但用户已明确不需要这种强保证；默认保留紧凑 ID 和显式 Repository 调用上下文，不为每个引用增加完整仓库身份。原源码/消费者证据仍有效，需求校准与补充实验集中见[误用检出分析](handle-misuse-detection.md)。

## 2. 新 API 与需要接线的差异

```csharp
HeadId.EncodedSize == 4;
HeadEntryId.EncodedSize == 12;
bool head.TryWrite(Span<byte> destination);
bool HeadId.TryRead(ReadOnlySpan<byte> source, out HeadId value);
bool entry.TryWrite(Span<byte> destination);
bool HeadEntryId.TryRead(ReadOnlySpan<byte> source, out HeadEntryId value);

AteliaResult<HeadHistoryEnd> store.ReadEntryHistory(
    HeadEntryId startInclusive, HeadHistoryBudget budget,
    Func<HeadEntry, bool> visitor, CancellationToken cancellationToken = default);
```

以上列实际能力；实例方法签名的写法仅为阅读方便。TryRead 要求精确长度，拒绝零 Head 与零 ticket 长度；TryWrite 失败不改目标缓冲区。v1 Entry 编码为 4B Handle + 8B LE 完整 ticket，ticket 固定 38:26、4字节单位布局，不能改用 `SizedPtr.Serialize()`。version/kind 归外层容器，不能 dump CLR struct padding。

`HeadId.LocalId` 已退出；需要局部编号时用 `store.GetLocalHeadId(head)`，该方法只解释本库上下文，不证明目标存在。存储格式现为 4，拒绝 1/2/3。DG 仍固定旧 `731eef7`，本轮不更新该 pin，也不把普通消费者验证扩大为 DG build/test 资格；现有 tests/MetaBootstrapCrashProbe 中的 `HeadId.LocalId` 用法待实际接入时调整。

准确历史从 anchor 的 ticket 建立有界逆扫，不先遍历新后缀。直接 anchor 若是未成立 CU，失败；正常向旧扫描遇到合法未成立 CU 则跳过。重复 inclusive anchor、跨 Head 和必要 CU 工作均计预算。停止不证明更多项或完成；callback 中仍不能 fork，退出后可用保留的 ID fork。

## 3. 身份目标已澄清，无需反馈返工

**观察到的差异：** 原 P1 要求完整持久库身份、不同库同编号不混同。现行 `HeadId` 仅有 4B 编码值，HeadEntryId 再加 8B ticket；两者的相等与持久编码均不含完整 16B HeadStoreId。跨库误用只作概率性检出。

这不是普通 decoder 漏检。正式合同和测试均明确：不同完整库身份、相同 key 或相同高 key 字节时，导入 Handle 可以与本库目标相等，`ReadEntry`、`ReadEntryHistory`、`ForkHead` 也可以成功访问该健康目标。该性质按库对相关，不能描述成每次调用都有独立的低概率防护。

**用户补充后的裁决：** 目标是开发期误用诊断，不是所有跨库值都必拒绝。之前的分析强调范围检查约 255/256，未充分计入“解码到的文件号实际未使用”这层检出能力。随机独立库 key 下，目标库有 N 个 Head 时，已有文件命中概率约为 `N/2^32`；1000 个 Head 时约四百二十九万分之一。代码空间穷举与特殊值修正见[补充分析](handle-misuse-detection.md)。

因此撤回要求每个 DG 引用默认携带完整 HeadStoreId 的建议，不再把这项身份差异当成上游阻塞或待确认反馈。完整 StoreId 仍可用于仓库级管理，但是否放进应用的外层配置，由具体功能决定；默认不摊到每个存档 handle。

**证据：** [HeadId](../../../../atelia-storage/src/HeadStore/HeadId.cs)的类型备注/相等规则、[HandleCodecTests](../../../../atelia-storage/tests/HeadStore.Tests/Values/HandleCodecTests.cs)的 `ImportedHandlesRetainProbabilisticOwnerContextRatherThanFullIdentity`、[ImportedEntryValidationTests](../../../../atelia-storage/tests/HeadStore.Tests/Public/ImportedEntryValidationTests.cs)的 `ImportedWeakIdentityCanSelectHealthyTargetWithSameKeyOrSameHighKeyByte`，以及 [S4](../../../../atelia-storage/docs/HeadStore/04-versionstore-publication.md)紧凑 Handle 合同。

## 4. DG 可以继续收敛的公开模型

建议按下列角色推进，名称和字节布局尚不冻结：

```text
LineRef = HeadId 的世界线角色包装（若包装有实际收益）
SaveRef = HeadEntryId 的存档角色包装（若包装有实际收益）
操作 = 在调用方选定的 Repository 上使用对应定位值
```

包装用于区分业务角色，不额外生成 GUID，也不默认增加 16B 字段。Repository 上下文由调用方选择；相等/哈希按紧凑值解释，不宣称全局唯一。确需跨仓库集合或缓存时，用集合外层/调用上下文区分；需要持久仓库选择的应用可在配置中保存一次仓库信息。

成功读取不能恢复 Handle 原来的完整库身份，概率性检查也不是认证。不过这不妨碍在本轮明确的开发期诊断目标下使用它，无需为当前故事引入强 scope 引用层。复制出来且分化的同身份目录仍由应用选择，不新增复制管理协议。

定位值编码的版本和种类可由外层容器或字段 schema 承担，不必每项重复存 discriminator。解码成功只证明表示合法；业务成员资格、State/Event 种类、准确图和 Schema 仍在使用时检查。

| DG 操作角色（非最终签名） | 输入与输出 | 现已明确的分工 |
|---|---|---|
| 列世界线 / 定位世界线 | Repository → LineRef 与小描述 | DG 正式成员与标签；不直接暴露全部管理 Heads |
| 当前预览 | LineRef → 所选 LineRef + 当前 SaveRef + 摘要 | 空 C 的 SaveRef 可属于 A，选中的 C 保持 |
| 读取 / 恢复存档 | SaveRef → 描述或领域读取视图 | schema pin 和模型能力归 DG，不取最新 shared 代替旧环境 |
| 历史窗口 | SaveRef inclusive 起点 → 页面、扫描继续位置及终止状态 | 复用 ReadEntryHistory；筛选 State 时仍记录最后扫描 Entry |
| 分叉 | SaveRef + 标签 → 新 LineRef | HS fork + DG 完整可见创建协议；不复制业务存档 |
| 继续编辑 | LineRef + Shared/Private 编辑意图 → checkout | 续写目标明确；State/Event/外部工具语义保持 |

新的细化顺序建议是：按紧凑值和仓库调用上下文定义两类定位角色，再定义存档小描述和历史窗口结果，最后与标签 A/B 方案、新根/shared Schema 创建协议组合。身份目标已澄清，精准历史起点与 codec 也已交付，不再等待额外强 scope 方案。

## 5. 独立消费者执行证据

使用本地包 `0.8.0-persistenthandles-local.20261017105233`，对应源码 `c6fb64a97ebc066e10c480e4439f20a2067683a2`。主线程核对 feed manifest 的六个 nupkg SHA256；新独立 restore 的实际 assets 只有六个准确版本的 Atelia 包和 `K4os.Compression.LZ4/1.3.8`，没有 ProjectReference。没有构建或修改上游工作树。

临时消费者位于本机 `.artifacts/headstore-request-review/`，与 DG 的 props/targets 隔离，使用独立 packages 目录及明确源映射。build 为 0 warning / 0 error。writer PID `23736` 正常退出后，reader PID `29716` 在任何 Open 前解码，然后重开执行：共 **38 个断言通过**，不是 38 个独立测试用例。

| 覆盖 | 本次实际观察 |
|---|---|
| 无 owner codec | writer 全关后编码，reader Open 前解码；zero/尾随字节拒绝，失败写缓冲区保持 |
| 当前线与固定位置 | 空 C 冷开仍选 a1；写 C 不改 A；固定 a1 字节正确；同值两次提交 ID 不同 |
| 固定起点分页 | c2,c1 后跨来源读 a1,a0；两页之间 A/C 联合推进不污染选定历史；重复根后空完成页 |
| 预算与 callback | 工作预算不足为失败且零项，不是空历史；visitor 内 fork 拒绝、退出后成功 |
| 导入位置 | 错长度坐标可结构解码，但 read/history/fork 都拒绝；无回退、无 fork 输出 |
| scope 建议的最小信息 | 完整 16B identity 可独立保存并在 Open 后比较；这里只验证材料可用，不是 DG wrapper 实现验收 |

本次未构造跨库概率碰撞镜像；碰撞结论来自上述正式合同、源码及明确测试用例复核。没有重跑上游完整 3579 项报告，也没有执行 DG 产品 suite、强杀/断电、性能或 Linux 验收。新 locator 类型仍是设计建议。

本机证据：[程序](../../../.artifacts/headstore-request-review/Program.cs)、[汇总](../../../.artifacts/headstore-request-review/qualification-summary.json)、[writer](../../../.artifacts/headstore-request-review/run-data/seed-receipt.json)、[reader](../../../.artifacts/headstore-request-review/run-data/verify-receipt.json)。这些为本机忽略工件，不随 Git 克隆分发。

复现方式：在该临时项目中使用其 NuGet.Config 和独立 packages restore/build；分别执行 `dotnet bin/Debug/net10.0/Probe.dll seed <新目录>` 与 `dotnet bin/Debug/net10.0/Probe.dll verify <同目录>`，不要复用已有 seed 目录。包 feed 路径见其配置，上游来源资格见[新增验收记录](../../../../atelia-storage/docs/HeadStore/12-versionstore-final-acceptance.md)。

## 6. 本轮停止边界

本轮完成交付核查与后续设计方向；后继概率实验接受开发期诊断目标，原强 scope 建议已退出。DG 固定依赖、格式、公共 API 和运行时没有改动。下一轮按紧凑定位值继续 DG 方案，并单独安排新包/source pin 接入与回归，不将本消费者的成功替代产品验收。
