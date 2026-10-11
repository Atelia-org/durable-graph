# DB-086：新存储方案简化评审

> 日期：2026-10-11。状态：**独立首论、交叉质询与最终裁决完成；设计结论，非运行验收**。
> 活动方案：[新存储栈重构方案](0086-new-storage-refactoring-plan.md)。本记录保留取舍和证据，不是第二份施工队列。

## 1. 方法与边界

按 dialectical-simplification 使用三位只读审阅者：需求怀疑者、最小架构师、语义守卫；主线程独立核对并裁决。
首轮共享方案全文和需求账本，不互见结论；第二轮交换最强反例；第三轮只处理合帧与 binary cursor 两项实质分歧。
审阅者不编辑目标，不运行产品施工；主线程在裁决后统一修订。

最高优先级是用户当前决定：最终代码简单性优先、撤销全库重开确认/启动审计、旧数据无需保留只验收新库。
其次是源码、可执行测试和当前消费者。DB-085、路线图、方案相互不是独立需求依据。
DG 当前源码 `c900edd`；新上游 `974fd47`。旧pin与新源码身份分开；没有用新底座解释旧package。
没有找到当前外部DramaBoard checkout；本轮消费者证据来自本仓活动PackageConsumerProbe，不冒称已验证外部应用。

## 2. 最终裁决

| 项目 | 初稿 | 最终裁决 | 决定性依据 |
|---|---|---|---|
| 重开屏障/启动审计 | 删除 | delete | 用户撤销其作为日常要求；必要读取/发布检查另保留 |
| Schema存储 | linked batch + frontier | simplify为完整SchemaSnapshot | 消费者需要完整exact目录/稳定ID，无人观察批次链；已有codec支持完整按ID目录 |
| 图持久记录 | Revision帧 + 小commit帧 | merge为Payload/业务TailMeta，一个地址 | 两父关系是真信息；两个物理帧与locator未找到独立需求，窄opaque-meta参数足够 |
| DG binary cursor | 保留两facade及strict输入 | simplify为直接Bare + 组合helpers | Dictionary读值后重编码，不存在冗余输入分裂key身份的反例 |
| nullable string内容编码 | 保留旧内部头 | delete | 仅专用单测调用；SG/Runtime string槽是ObjectId，null不走此头 |
| 根/Schema发布 | 一个root，两次独立接受 | keep/merge | Schema已返回成功后图失败不能撤销；同一最新root防stale覆盖 |
| 第三把repository锁 | 待判 | delete | 两底座实际owner锁已提供排他；固定FS→VS、partial owner不交付 |
| 必要图Parent/链检查 | 泛述按需保留 | keep并指定入口/范围 | 当前ValidateGraph独有Parent对应；完整map的external head只是shallow声明 |
| 大目录/导航优化 | 可能前置实验 | defer | 当前无真实超界/瓶颈，记录限制而不保留双模式 |
| 包/模型升级 | 保留消费者 | simplify矩阵，keep新库模型升级 | 取消旧storage转换不等于取消V1→V2模型历史或NuGet生成资产验收 |

## 3. 实质质询与撤回

### Schema：必须保留接受目录，不必保留登记链

最强反对：完整快照有总目录单帧上限，每次变更重复写旧节点；linked batch可跨帧增长。
这个成本真实，但没有当前目录规模、登记历史查询或写入测量证明必须增量。
[SchemaStore.Prepare](../../src/DurableGraph.Persistence/SchemaStore.cs)已经准备完整候选字典；
[SchemaCatalogWireCodec](../../src/DurableGraph.Persistence/SchemaCatalogWireCodec.cs)的ValidationState可从内建/空集合按ID逐行处理完整目录。
先前依赖、连续ID、exact冲突仍校验，删除的是前驱、visited、逆序批次列表与历史链读取。

需求怀疑者撤回“链为不可删内核”，语义守卫接受快照。方案明确总目录单帧容量，超限正常拒绝，不添加fallback。
已确认登记仍全局单调；graph失败不撤销；未知登记重开查实际root。旧
[AmbiguousWriteOrFlushFaultsInstanceAndColdOpenDecides](../../tests/DurableGraph.Persistence.Tests/SchemaStoreTests.cs)
以完帧可发现决定收养，是旧专用文件的权威机制，不要求新共享allocator收养所有orphan。

### 合帧：保留两种Parent，删除第二个位置

最小架构师第一轮提出合帧，第二轮因FS只有全文CRC读取而撤回，第三轮核对真实导航合同后再修订。
最强反对：TailMeta切片不是metadata-only I/O；每跳读完整Revision，坏Payload CRC也使metadata查询报错。
[FrameRead](../../../atelia-storage/src/FrameStore/FrameRead.cs)和[FrameStore.ReadFrame](../../../atelia-storage/src/FrameStore/FrameStore.cs)支持此事实。

但[DB078EventQueryTests](../../tests/DurableGraph.Persistence.Tests/DB078EventQueryTests.cs)验证的是空model导航、
不执行Allocate/typed解码；[DB078CheckpointTests](../../tests/DurableGraph.Persistence.Tests/DB078CheckpointTests.cs)
验证缺图capability不妨碍EnumerateEvents。没有小字节I/O或坏CRC仍导航的承诺；旧打开审计本来也先拒绝CRC坏内容。
CRC读帧、解业务meta、准备所选图是三个层次，不能混为“导航已完整解图”。

因此采用窄`Append(revision, opaqueTailMeta)`，低层Read只解Payload，Repository解业务头；不引入泛型callback、backend或新cache。
同一地址支持[ListConsumer](../../experiments/PackageConsumerProbe/ListConsumer/Program.cs)等真实低层RevisionDecoder消费。
删除一个独立commit帧、一条持久Revision locator、一次append及其间失败窗口；没有删除业务Parent或最近State Parent。
整帧导航成本和同帧CRC耦合明确写入方案；真实大快照导航瓶颈出现再重访。

### Binary：规范输出不要求全局拒绝冗余输入

首轮部分审阅者声称overlong primitive会使Dictionary键的bytes身份分裂；主线程及对立审阅核对源码后否证。
[DictionaryStateReader.ReadKey/Index](../../src/DurableGraph/Runtime/Containers/DictionaryStateReader.cs)从已解码key重新EncodeKey，
去重与Delta寻址不直接使用输入片段。这个错误反例被明确撤回。

保留facade的最强理由改为“集中DG grammar”，但直接Bare仍能保存相同值、上游检查溢出/截短/bool/UTF8。
少数组合helper足以处理Guid/decimal/Temporal；没找到必须拒绝合法有界冗余才能避免错内容的当前规律。
新格式选择上游接受域，删除两个cursor、primitive命名转发及全局最短输入检查。施工要改SG/StateOps/反射类型查验并重编，不能以此反推旧壳必要。

严格值检查仍保留：decimal flags、日期范围、完整bits、ObjectId/null、结构count/ID/角色和exact consume。
规范writer继续生成Dictionary key身份。旧strict专用测试改成新grammar的合法/非法边界测试，不按删除测试数量声称正确性改善。
无生产caller的nullable-string内部路径删除；SG/Runtime的ObjectId string槽及nullable值槽不变。

## 4. 语义守卫补出的具体保留项

- [Repository.ValidateGraph](../../src/DurableGraph.Persistence/Repository.cs)独有Revision.Parent与业务祖先最近State比较。
  移至每个实际选图准备入口；CRC合格且完整Base也可能Parent不符。metadata导航不遍历所有对象。
- Delta prior必须等于exact Parent map选中的head；完整map可重新选择更老head，不能把shallow声明强化为全局实体provenance。
  visited放在真正间接遍历路径，ObjectId图循环仍允许。
- Schema发布与graph业务发布两次接受分开：同一root权威不等于同一次transaction。graph更新必须读取最新schema项；
  Schema Unknown不能机械映射成graph Unknown，NotPublished也不代表owner健康。
- cached读取先做actual backing/owner guard；fault/Dispose不允许热cache绕过；owned已交付结果寿命与存储句柄分开。
- DG不主动检查无关orphan，不承诺上游Open容忍所有orphan损坏；必要EOF/header错误继续传播。
- root采用magic/version+RootMap envelope，容量包括头；未知新底座应用root只能在上游打开后判定，不重新发明strict preflight。

## 5. 简化规模与验证限度

相对初稿，确定少了：一条Schema持久前驱链及其重放状态；一种独立commit物理帧、一条locator与一次append；
两个DG binary cursor及对应strict协议；一条nullable-string死路径；一把重复repository锁。
没有估算LOC或工期，也没有把共同的prepare/install、故障结果、两个因果Parent、Schema目录本身算作已删除。

本轮实际执行的是源码/调用方/测试内容审查、三轮交叉质询与文档修订；没有实现候选、运行新DG build/test或测量性能。
该记录证明方案裁决经过挑战，不能证明合帧、完整快照或Bare签名切换已经运行成功。
R1必须修改最终产品source并由真生成图完成保存→冷重开→续写；临时host不能另实现一套最小DG。
