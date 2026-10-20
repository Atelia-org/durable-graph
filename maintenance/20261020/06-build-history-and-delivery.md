# 06 — Build、Schema history 与候选交付质量体检

审查日期：2026-10-20。基线：`1beca7cf2477d369010b3577b7a795c41dbc45f1`。报告主要读者为后续 Coding Agent。

## 结论与证据边界

本包发现 1 项打包输出路径风险、1 项普通包门禁证据债和 1 项准备脚本并发边界债。没有找到足以推翻当前 Schema-history 发布保护合同的证据，也没有据此修改持久格式、公开 API 或依赖 pin。下列 P2 表示值得在下一次相关交付前处理，P3 表示由相应使用场景触发；均不是已经观察到的线上损坏。

已读根 AGENTS、完整 `src/PROJECT-STATE.md`、存储依赖指南，审查 `src/DurableGraph.Build/`、包 props/targets、根工程配置、`eng/`、PackageProbeSupport、基础 Run-Probe，并对照较新的 ApplicationRecipes runner。未逐一重审全部领域 runner、Generator 算法或上游 storage 实现。

本轮执行的是源码审查和 PowerShell 静态解析：五个相关脚本解析错误均为 0；执行了不写盘的配置路径冲突计算。按主线程协调，没有执行 dotnet、pack、restore、runner，也没有以历史 receipt 或已有 DLL 宣称当前包资格。产品行为推断尚需下列定向实验确认；历史测试通过记录只作为导航。

## F06-1 / P2：Runtime 包从硬编码 bin 路径收集 SG/Build，绕过当前输出路径

**事实。** [CollectDurableGraphPackageAssets](../../src/DurableGraph/DurableGraph.csproj#L37) 的 Generator DLL 来源固定为 `../DurableGraph.Generator/bin/$(Configuration)/netstandard2.0/...`，Build 的 DLL/deps/runtimeconfig 来源固定为 `../DurableGraph.Build/bin/$(Configuration)/net10.0/...`。这些路径没有消费被引用项目实际的 `TargetPath` 或输出目录。项目确实有这两个 ProjectReference，但收包路径是另算的。

**触发与影响推断。** 使用 SDK `--artifacts-path`、自定义 `BaseOutputPath` 等方式隔离构建输出时，引用项目可以在新目录完成构建，收包 target 却仍读取默认 bin。默认 bin 不存在时应会打包失败；存在旧文件时可能把旧 Generator/Build 放进声明新版本 G 的 Runtime nupkg。后者的确切触发条件本轮未运行验证。风险涉及生成/发布工具和 Runtime 的版本组合，不能由 runtime DLL 构建成功消除。

**最小改进。** 通过被引用项目返回的实际输出元数据收集包资产；Build 的 deps/runtimeconfig 从其实际 TargetDir 派生。先验证该元数据在 `pack --no-build` 下的行为，不为这一处建立通用打包框架。如果当前有意只支持默认输出，至少在非默认输出模式下明确拒绝，避免静默读取旧 bin。此项超出安全小修范围，本轮只报告。

**可执行验收。** 在独立 checkout 中，以准确 storage feed 和新 G：

1. 先不创建默认 bin，使用 `dotnet pack src/DurableGraph/DurableGraph.csproj -c Release --artifacts-path <fresh> -p:UseStorageSources=false -p:RestoreConfigFile=<task-config> -p:PackageVersion=<new-G>`，检查四项工具资产齐全且 SHA256 等于此次实际输出。
2. 再在默认 bin 放置可明确区分的旧工具输出，在隔离输出中重新构建另一新 G；包中工具必须来自本次输出，或操作明确拒绝。
3. 用新普通包在仓外空 cache 消费，真实执行 SG、Publish 与 Verify。源码测试不能替代该收包验收。

## F06-2 / P2：基础 Run-Probe 的成功结论缺少实际解析/运行字节闭环

**事实。** [Run-Probe](../../experiments/PackageConsumerProbe/Run-Probe.ps1#L111) 对输入 feed 做十个 nupkg 计数，并检查选定 Runtime 包的若干 ZIP entry；[restore 调用](../../experiments/PackageConsumerProbe/Run-Probe.ps1#L146) 使用新 `--packages` 目录。脚本没有读取 `project.assets.json` 校验包版本/包目录，也没有将实际缓存原包、输出 DLL、SG/Build/targets 与输入 nupkg 做字节比较。共享 [Invoke-DotNet](../../experiments/PackageConsumerProbe/PackageProbeSupport.ps1#L19) 清理 sources/mapping，但没有清理 fallback folders。

对照 [ApplicationRecipes runner](../../experiments/PackageConsumerProbe/Run-ApplicationRecipesProbe.ps1#L162)：它已经核对纯包 assets、唯一 cache、十个实际 Atelia ID/version、缓存原包与 feed hash，并在后文比较输出 DLL 与包内 DLL。因而这不是需要新创一套验收理论，而是基础门禁的证据强度落后于现有实现。

**影响与限度。** 当前 Run-Probe 成功能证明当次生成/Schema/history/DTO/body/Capture 故事运行，不足以单独证明全部实际 Atelia 输入来自所指定 feed，也不能证明其来源是本次基线。这里没有观察到实际消费错包；已经另有独立字节复核的历史资格不因此失效。问题是未来 Agent 容易把一个成功日志扩大解释为完整普通包资格。

**最小改进。** 保留现有领域断言，补上恢复后的 assets 身份/包目录检查和缓存原包摘要核对；只为真正执行到的 runtime/SG/Build/targets 补字节核对。消费配置清空 fallback folders。可以先在这个 runner 局部实现；证据稳定后才考虑抽出少量公共读取函数，不统一所有 runner 协议。

**可执行验收。** 以全新 G/cache 执行现有全部正反例；确认 assets 仅有 package 输入且版本对应选定 feed。另设有意污染的 fallback 输入或同版本不同字节测试夹具，要求门禁拒绝消费错字节。保留本地 fixture、命令和摘要，不触碰全局 cache，不覆盖真实候选。

## F06-3 / P3：不同 feed 目录不意味着 producer 构建隔离

**事实。** [Prepare-DurableGraphProbeFeed](../../experiments/PackageConsumerProbe/PackageProbeSupport.ps1#L61) 把 `pack.nuget.config` 写到输出目录的父目录。传入 `<root>/A` 与 `<root>/B` 时，两次调用均使用 `<root>/pack.nuget.config`；本轮用 PowerShell 执行同一 Join-Path/Split-Path 计算，确认二者相等。后续 [dotnet pack](../../experiments/PackageConsumerProbe/PackageProbeSupport.ps1#L72) 仍构建同一仓内项目、使用默认 bin/obj；`-m:1` 只限制单次 MSBuild 的并行度，不协调两次脚本调用。

**触发与影响推断。** 多 Agent 各选一个新 feed 后并行准备，仍可互相覆盖 producer 配置，并同时写同一组项目输出。可能导致 restore 读取另一 feed、构建失败或收包输入失配；具体交错本轮未实际触发。不同 G 和不同 feed 仅隔离包目的地。候选 exporter 已使用唯一 preparation 子目录，减少了配置碰撞，但仍未隔离仓内 producer bin/obj。

**最小改进。** 当前先把“同一 checkout 的准备/pack 必须串行”作为明确调用约束，主线程统一准备一个十包 feed，再并行运行具有各自 cache/obj/bin 的只读消费者。若现实确需并发 producer，优先独立 checkout；本次不建议为了假想负载引入跨脚本事务或全局调度器。把 config 放入本次独占工作目录可消除一个局部碰撞，但不能据此宣称 producer 已可并发。

**可执行验收。** 若选择只承诺串行，增加帮助/入口说明并验证文档不再暗示新 feed 即隔离构建。若选择支持并行，必须用两个 G、独立配置和构建输出运行交叠准备，最终核对四个 DG 包及其工具的来源和摘要；只测目录名称不同不够。

## 经核对后不列为新缺陷的边界

- [Publish/Verify](../../src/DurableGraph.Build/SchemaHistoryTool.cs#L15) 在目录 lease 内重读 accepted history、验证完整闭包；新文件使用 CreateNew 临时文件、flush 和不覆盖 move。已有并发测试覆盖等待后的重读、同形幂等、冲突和 timeout；建议主线程使用 `SchemaHistoryToolTests|SchemaHistoryPublicationConcurrencyTests|CrossAssemblyInlineBuildTests|CrossAssemblyInheritanceBuildTests` 的 FullyQualifiedName 过滤器运行定向回归。
- [Build README](../../src/DurableGraph.Build/README.md#L72) 明确排除了物理目录 alias、跨用户/机器协调和多文件 batch 崩溃原子性；完整构建仍须串行。不能把这些已选范围重新当成遗漏，进而重开稳定输入快照或 batch 事务方案。
- [Prepare-Storage](../../eng/Prepare-Storage.ps1) 的本地模式核对 pin、上游 manifest、六包 nuspec 的 ID/version/repository/commit 和 SHA256；没有 clone/build/升级 fallback。source-mode pack 拒绝及 G/S 独立约束仍在根 targets。
- 候选导出从 README 提取例子与相对 NuGet 配置，原始第三方包闭包有显式范围；dirty source 的 commit 元数据不等于干净来源，脚本已有文字提示。签名条目检查没有被冒称为信任链验证。

## 后续执行顺序与本轮改动

先用独立 checkout 证实并修复 F06-1；下一次运行基础包门禁时闭合 F06-2；F06-3 先明确串行约束，只有实际并发 producer 需求才扩大工程实现。没有证据支持为此拆分程序集或引入通用构建框架。

本轮只新增本报告，没有改源码、工程配置、pin、共享状态文档或已有维护文件。源码 build/test 与普通包资格由主线程统一执行和记录；本报告不声称这些验证已完成。
