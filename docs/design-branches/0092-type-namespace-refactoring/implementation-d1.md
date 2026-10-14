# DB-092 D1：公开内容操作名称迁移

> 2026-10-14；源码基线 `f06221d`，源码提交 `bb91852`。源码、根测试、API 清单和准确新包消费均已限定验收。
> 问题：公开能力绑定与裸 string body 的名称能否准确表达职责，同时保持原有执行和字节行为？

## 当片合同

本片只采用提案 §6 中两项明确误称；内部接口随公开绑定改名。

| 旧全名 | 新全名 |
|---|---|
| `Atelia.DurableGraph.Runtime.CapturedStatePreparation<TState>` | `Atelia.DurableGraph.Runtime.StatePreparationBinding<TState>` |
| `Atelia.DurableGraph.Runtime.ICapturedStatePreparation`（internal） | `Atelia.DurableGraph.Runtime.IStatePreparationBinding`（internal） |
| `Atelia.DurableGraph.Serialization.StringPayloadCodec` | `Atelia.DurableGraph.Serialization.StringBodyPreparer` |

前者绑定 exact Schema/DTO、Base/Delta 操作与可选状态相等证明，Capture、Normalize 和 Pair 都使用；它不是一次准备结果。
后者唯一操作返回 owned raw `PreparedBaseBody`，没有 typed 头或 ObjectVersion payload。
公开构造器和 `CaptureContext`／`StateModelBinding` 中的类型引用直接更新；成员名、参数名和委托形状保持。

同步运行时、容器、Persistence、普通与 Family SG 的 emitted FQN、手写绑定、测试及活动消费者。
用户已明确没有兼容负担：调用方迁移后重新编译，不增加旧类型、转发壳或 overload；实验存档可 reset 后重生成。
该前提不改变当前包的 Schema 升级、exact history 和打开 Store 的完整帧纪律，详见[原型约束](../../DurableGraph-target-design-v0.md#原型迭代与数据兼容)。

Namespace、文件路径、程序集、包身份、访问级别和现有校验保持。
Generator 入口另作 D2；§6 其余可选名称与槽职责问题仍待独立判断，E 组织讨论不随本片启动。

## 最小验收

- 源码差异只有批准 identifier 与对应 emitted FQN／编译 fixture／名称注释；SchemaId、FieldId、wire、accepted history 和执行逻辑保持。
- 根 solution build 和相关 tests 通过；完整相等证明、缺 proof、错误传播、exact Schema/DTO/binding 与 string 编码回归保持。
- 公开 API inventory 只出现两项映射及其构造器／签名传播，无额外形状变化；不承诺旧 DLL 可直接加载。
- 准确新 G 的 runtime/SG PackageReference 编译与执行，覆盖普通生成、Family、手写绑定与外部模型库；从空库保存、冷开和续写，并验证当前包内部升版／Pair。
- 现有有效 package runner 与文档链接验证通过，不引入旧包数据兼容门禁。

## 源码与根验证

62 份 C# 完成 179 次 identifier 替换（含配套测试类名）；仅三处 literal 变化：普通 SG emitted FQN、Family SG 类型片段和生成文本断言。
两项公开名称、内部接口与现有 `StringPayloadCodecTests` → `StringBodyPreparerTests` 的测试类名是完整映射，文件路径保留。
实施者 Roslyn token／literal 审计、独立复核及主线程直接 Git 全文比对均通过：62 份源码只有批准替换，namespace、成员、逻辑、校验、history 与配置保持。
主线程证据 `.artifacts/db092-d1/root-source-acceptance.json`；实施冻结清单及逐文件 SHA 位于 `.artifacts/db092-d1/source/`，没有引入长期工具或测试。

主线程以准确 S 的 package mode 执行：

```powershell
dotnet build DurableGraph.slnx -c Release --no-restore -m:1 -nr:false -p:UseStorageSources=false
dotnet test DurableGraph.slnx -c Release --no-build --no-restore -m:1 -nr:false -p:UseStorageSources=false
```

构建零警告／错误；2690 项 tests 全通过，失败／跳过为零（Persistence 893、Serialization 133、Storage 36、Runtime/SG 1628）。
日志 `.artifacts/db092-d1/build.log`、`tests.log`。
既有 exact Schema/DTO/binding 身份、缺 proof／完整比较／错误传播、容器与 string code-unit／golden bytes 回归保持。

对四程序集 exported 类型和 public/protected 成员的 1359 行 inventory，仅按当片三个类型／接口映射逆替换后，与 C 真包基线全文相等；无额外 API 形状差异。
当前 SHA256 `4B31C66D688E85129DB991724A53516C592B46A411B08AA432617A6DB47A98DB`，根验收 `.artifacts/db092-d1/root-public-api.acceptance.json`。
该工具也记录 exported 类型实现的 internal interface；映射包含它，不把接口改名误算为额外公开变更。
inventory 不穷举 custom attributes／nullable annotations；源码全文核对另保其边界。

## 当前包消费

原 `Run-SplitRepositoryProbe.ps1` 单次 Prepare 产生完整十包 feed，随后所有故事复用该 G，不重复打包 DG：

| 输入 | 准确值 |
|---|---|
| G／producer | `0.0.0-public-history-e2e.20261014094057.27508`／`bb918527aa22cd40f55b0dbdfdc1fa9455915ba3` |
| S／revision | `0.1.3-dev.20261014.g758cebf`／`758cebf8ba82788ba91435d34fd484dff32095fd` |
| feed | `W:/TEMP/dg-db092-d1-split-20261014T094054596Z-14948/feed` |

原生 provenance 记录并核对 188 个生产输入在受控 pack 前后对应已提交 HEAD；打包时只有本片文档维护尚未提交，不属于生产输入闭包。
主线程另核当前输入／Git 状态、十包 nuspec 来源和 DLL 字节，六个 S 包与原 feed 相等；实际新包 API inventory 与根构建同 SHA、同限定映射。

- **Split PASS**：原门禁五个独立进程，覆盖 shared/private/meta／目录、当前包内部升版、Pair／Fork、冷开与增量续写；3 份 accepted history 保持。原准确 CS0122 反例与派生正控保留。
- **普通生成 PASS**：复制现行 Consumer，`ProbeVersion=3` 的 Publish／CI Verify／run 全通过，保留原完整输出断言，7 份 history 字节保持；实际 `__DurableState` 使用新 FQN。
- **外部模型与手写绑定 PASS**：以同一 G 重编译 DomainLibrary V1 与 AppModel，分别 Publish／Verify／pack，2＋1 份 history 字节保持；独立 PackageReference host 从空库经三进程保存／冷续写／冷验证，保 Node alias／自环、当前 Pair 共享与只读文件不变。手写绑定验证 frozen Capture、Base、变化／无变化 Delta；string 验证 A／empty／null 与 owned body。

六份 assets 均无 ProjectReference；普通与外部 Family 两条生成路径实际引用 `StatePreparationBinding`。
主线程直接审阅有限 host／控制器，另核 50 次 Split 与 36 次最终外部 runtime／模型 DLL 加载、3 次普通 runtime 加载、实际 package SG／Build 调用日志与 cache 字节，以及合计 13 份故事 history。
外部模型只取得本次有限新故事资格，未恢复退休 CrossAssembly runner。

外部 host 首轮 `FrameAddress.ToString()` 不提供准确地址，原 source／DLL／receipt／日志保存在 `first-address-observation/`，不采用该地址字段。
仅补官方 `TryWrite` 的完整 12B 地址观察，在另一空库重跑三进程；续写地址变化、再冷开保持。该观察不独立证明物理帧，也不授旧数据资格。

结果工件集中于 `.artifacts/db092-d1/qualification/`；主线程验收为 `.artifacts/db092-d1/root-{pack,split,witness}-acceptance.json` 与 `package-public-api.acceptance.json`。

| 终态 receipt | SHA256 |
|---|---|
| `qualification-result.json` | `15814464B218C5373C73A226CAC97651DC5B1CD2942C1D33E4D0A0369AD0868B` |
| 原 Split `result.json`（外部 runroot） | `ECC71E2243D76047EF75E28DC364979A192A2BB973268A8F50755665C7C57893` |
| `witness-result.json` | `826B2B82B8C996921578A1B7AC99CCA6575531997907AB13E45268107DE3CF37` |
| `address-observation-result.json` | `18C852697594AE5004165E0B7FFE8D4957E05BE26210BCC19EF5C587F79109CF` |

这是当前包的有限命名迁移与新建库资格；未测试旧 DLL／旧包存档、crash／性能或远程发布，没有新增兼容层。
集成 diff 与文档链接另核无新增缺口；原有历史缺口不在本片修补。

## 续工

D1 完成后，优先选择 Generator 入口 D2，静态风险与最小验收维护在 [README §5](README.md)，其余候选与 E 组织判断见[路线图](../../DurableGraph-research-roadmap.md)。
公开调用方按上表直接改名并重新编译；不要求保留旧模型 DLL 或旧实验数据。
