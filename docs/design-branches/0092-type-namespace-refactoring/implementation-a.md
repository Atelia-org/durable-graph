# DB-092 A：Generator／Build 内部命名实施

> 基线：`d1707f2`；只处理 [方案 §5](README.md#5-generatorbuild-的分析材料)，B／C／D 未包含在本片。
> 验收问题：内部分析名称变化后，实际 generated sources/hints、diagnostics 和 canonical history 文件名／字节是否保持？

## 变更范围

用户通过 IDE 完成六个 Generator 私有类型和一个 Build 内部类型的 Rename。
主线程与执行包补齐 Family 路径的私有名称，保真正 CLR 泛型工具和普通直接生成路径的 Binary*Model。

- 配套私有类型：`GenericDomainField/GenericBaseProjection` → `FamilyDomainField/FamilyBaseProjection`。
- 分析方法：`CreateTypeModel/CreateEnumModel/ToSchemaHistoryFields` → `AnalyzeDurableType/AnalyzeDurableEnum/ToSchemaTemplateFields`。
- 其余 28 个 Family 路径方法收回 Generic 路径标签；例如 `GenerateFamilyStates`、`MakeFamilyLayout`、`UsesFamilyTemplates`，静态操作选择名为 `UsesStateOps`。
- 增加一条 Family 路径不等同 CLR 泛型的说明，更新一处测试注释。文件名、namespace、程序集、访问级别、Generator 入口和 public API 保持。

没有改变 generated helper/hints/FQN、marker 身份核验、SchemaId/FieldId、版本／格式、条件或算法；历史异常文案也保持。
Build 仅采用用户的 `SchemaHistoryTextCodec` Rename，没有修改发布、校验或 canonical rendering。
上游 warning 修正和本库存储 pin 更新已由用户先行提交，本片基于准确新 pin 验证，未改上游工作树。

## 验证

| 验收 | 实际结果与边界 |
|---|---|
| IDE 差异 | 初始 30 份 C# 逐文件逆映射为基线全文；独立复核确认只有约定七型的 411 处替换 |
| 集成源码 | 40 个获准符号；独立复核 30 文件、530 处替换和 3095 个字符串／字符 literal，只有获准 Rename 与一条说明注释。主线程另收齐测试中的方法名注释，最终 31 个 C# 文件；条件、控制流和字面串保持 |
| 实际生成等价 | 独立编译旧 `d1707f2` 与冻结新源码的 Generator/Build，DLL SHA 均不同；14 个固定场景、51 份生成源码（465,345 UTF-8 字节），文本、hints、generator/compile diagnostics 和 SHA 完全相同；两个跨库 provider emitted DLL 相同 |
| Build 文本等价 | 每版本 15 次 publish／再次 publish／verify 全成功，输出和退出码相同；6 个 history 文件共 1,050 字节，路径、文件名和完整字节相同 |
| 根构建与测试 | 准确 package mode 的 Release Rebuild 零警告／错误；DurableGraph.Tests 的 1628 项全部通过，零失败／跳过 |
| 存储来源 | assets 使用 `0.1.3-dev.20261014.g758cebf`；六个测试输出 storage DLL 与准确 feed 包内字节一致，来源 `758cebf8ba82788ba91435d34fd484dff32095fd` |
| 独立复核／文档 | 源码审计无阻断；集成 diff、空白和新增本地引用检查通过；不追改原 DB 正文的历史名称 |

代表输入覆盖普通 class/struct、非泛型 record/enum/Nullable Family、真实泛型、forced definitions、显式 Upgrade、accepted/retained history、
跨程序集 fixed base/inline 与 DG0001/DG0012 反例。生成等价是有限输入证据，不是全体源程序的证明。

主线程执行：

```powershell
dotnet build DurableGraph.slnx -c Release --no-restore -t:Rebuild -m:1 -nr:false -p:UseStorageSources=false
dotnet test tests/DurableGraph.Tests/DurableGraph.Tests.csproj -c Release --no-build --no-restore -m:1 -nr:false -p:UseStorageSources=false
```

独立工件在忽略目录 `.artifacts/db092-a/equivalence/result.json`，SHA256：
`342E7CC5E1836B2C4C57777C606FFBC0694A5C80D1C272C50FCE62321B9F7D44`。
主线程核回 receipt、旧／新 DLL、采样源码与代表 harness 的实际载入路径；源码差异 map 和 storage 字节核验另在同级工件。
隔离旧 Generator 构建有 21 条 RS2008 发布跟踪提示，新隔离构建仅抑制该提示；根构建的零警告结果另独立取得。

本片没有 pack、验证新 G 的 PackageReference 或旧包冷开，未改员工 CLR 名、namespace、Generator 入口或公开 API。
没有新增常驻验证框架或永久测试。下一片从方案 B 的普通内部映射继续，C／D 仍独立验收。
