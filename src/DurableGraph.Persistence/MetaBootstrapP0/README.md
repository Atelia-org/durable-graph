# DB-087 P0：持久 meta 自举实验

本目录是内部纵向实验，不是公开 Repository 的第二种生产模式。问题与分片边界见
[DB-087 §6](../../../docs/design-branches/0087-meta-bootstrap-refactoring-route.md#6-建议的重构任务顺序)。

实验只把两种变化来源汇入共同保存：业务 Capture/比较；meta 按逻辑 key 归约修改。
图 writer 接收来源、根、owned ObjectVersion records 与删除集合；取得完成对象 batch 地址后，
用真实 Dictionary body 保存存活目录，再追加固定图入口。目录自身没有领域 ObjectId。
descriptor 使用其 meta ObjectId；每个动态 Base 的解释仍受其 batch 的准确 Schema 根约束。

真实 FS/VS 拥有文件和两个 refs。共同确认后才接受候选：Event 接受 meta、推进业务 Head，
但保留最近 State 的业务保存基线。关闭全部资源后重新打开，必须只从实际发布根恢复。
P0 wire 和固定员工闭包允许调整；不提供旧格式兼容、私有分支策略或公开入口切换。

从 [P0Session](P0Session.cs) 看两侧准备与安装；
[P0GraphStore](P0GraphStore.cs) 保存共同 batch／目录／图并校验来源，
[P0Repository](P0Repository.cs) 是唯一发布 owner。
[P0CatalogWorkspace](P0CatalogWorkspace.cs) 只保留逻辑脏集合，
[P0CatalogView](P0CatalogView.cs) 从员工 DTO 调用 Schema 工厂；均不调用 SchemaStore。
员工模型在本程序集内经真实 Source Generator 编译。

P0 接受无继承、无泛型的 ReferenceObject Schema，基元字段、简单命名引用及 List<int>。
私有模式、完整 Schema variants、历史升级恢复与公开缓存／包交付均未获得此实验的资格。
目录冻结、Base 候选、Schema 投影和业务 Capture／预算仍可全量工作；这里只减少受控变更的比较与发射。

验证命令从仓库根执行，准确上游 pin 及 source/package 模式见[依赖指南](../../../docs/storage-dependency.md)：

```powershell
dotnet build DurableGraph.slnx -c Release -m:1 -nr:false -p:UseStorageSources=true -p:StorageSourceRoot=E:/repos/Atelia-org/atelia-storage
dotnet test tests/DurableGraph.Persistence.Tests/DurableGraph.Persistence.Tests.csproj -c Release --no-build --no-restore -p:UseStorageSources=true -p:StorageSourceRoot=E:/repos/Atelia-org/atelia-storage --filter FullyQualifiedName~MetaBootstrapP0
dotnet test DurableGraph.slnx -c Release --no-build --no-restore -m:1 -nr:false -p:UseStorageSources=true -p:StorageSourceRoot=E:/repos/Atelia-org/atelia-storage
```

本次结果和未验证项统一记录于 DB-087；本目录不另维护产品能力清单或长期路线图。
