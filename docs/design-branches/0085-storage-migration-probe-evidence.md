# DB-085：源码基线与隔离机制验证记录

> 日期：2026-10-11。对应[可行性报告](0085-rbf3-framestore-versionstore-migration-feasibility.md)。
> 这是一次限定范围的 Linux/WSL 执行证据，不是产品迁移完成报告。

## 1. 输入与工作区

- DG commit：`c900edd5b3a921b7bf46df2ac0482dc39597e606`，正常包模式。
- 新上游 commit：`e989ba60f8866c60bd86f99388701dfbe9c6125f`；来源为 Windows `E:\repos\Atelia-org\atelia-storage`，本机映射 `/mnt/e/repos/Atelia-org/atelia-storage`。
- 新源由该 commit 的 `git archive` 导出，不复用上游 bin/obj，也不写入指定 Windows 工作树。
- 工具：本轮 `dotnet --version` 为 `10.0.201`。
- 临时工作区：`/tmp/dg-storage-feasibility-20261011`；probe 仅在它自己的 fresh 目录写测试数据。
- 本仓最终修改范围只有报告及导航文档，产品代码、pin、现存存档未变。

## 2. 实际结果

| 执行范围 | 结果 |
|---|---|
| DG Release solution build | 通过；0 warning / 0 error |
| DG Persistence | 959 passed |
| DG Serialization | 163 passed |
| DG Storage | 202 passed |
| DG Runtime / Generator tests | 1652 passed |
| DG 合计 | **2976 passed，0 failed，0 skipped** |
| 新上游 Binary | 214 passed，0 skipped |
| 新上游 FrameStore | 424 passed，1 skipped |
| 新上游 VersionStore | 1340 passed，1 skipped |
| 三个上游测试项目合计 | **1978 passed，0 failed，2 skipped** |
| 独立 public ProjectReference consumer | 通过两个输出断言组；见下文源码 |

两项 skip 都为 `WindowsCallerRootAndParentSpellingIsNotPersistentCanonicalName`，
分别位于 FrameStore/VersionStore PlatformTests。没有把 Linux 通过写成 Windows 本轮资格。
这些 test 项目重跑包括其定义的 public/内部故障/进程组合，但没有额外重跑完整上游 solution、
Rbf/Data/Primitives 的独立全部测试或 NuGet package smoke。
consumer 首次编译显示 RBF 现有 XML 文档 CS1572/CS1573 warning；运行没有失败，不能称新栈编译零 warning。

实际命令：

```bash
# DG cwd=/repos/Atelia-org/durable-graph
dotnet build DurableGraph.slnx -c Release -m:1 -nr:false
dotnet test DurableGraph.slnx -c Release --no-build --no-restore -m:1 -nr:false

# 新上游 cwd=/tmp/dg-storage-feasibility-20261011/upstream，以下逐项执行
dotnet test tests/Binary.Tests/Binary.Tests.csproj -c Release -m:1 -nr:false
dotnet test tests/FrameStore.Tests/FrameStore.Tests.csproj -c Release -m:1 -nr:false
dotnet test tests/VersionStore.Tests/VersionStore.Tests.csproj -c Release -m:1 -nr:false

# 独立 consumer cwd=/tmp/dg-storage-feasibility-20261011/probe
dotnet run --project Probe.csproj -c Release -- /tmp/dg-storage-feasibility-20261011/probe-store
```

临时原始日志：`dg-build.log`、`dg-tests.log`、`upstream-{Binary,FrameStore,VersionStore}-tests.log`、
`probe.log`，均在上述临时工作区。日志不是永久验收资产；这里保留结果、输入与重跑源码。

## 3. Consumer 到底证明了什么

输出：

```text
PASS primitive bytes; reader canonical difference
PASS shared frames; root publication; rewind logical parent; named fork; immutable tag; cold RO audits
```

第一组比较 canonical UInt32 边界、long.MinValue ZigZag、double negative zero 的输出 bytes，
并实际证明新 reader 接受 `80 00` 的零值而 DG reader 拒绝且 cursor 不动。
这不是全 scalar/string 等价证明；更完整候选必须运行既有 goldens/malformed 语料。

第二组在一个 FrameStore 写入 Schema 占位、State 占位和三个 DG commit 示意帧，
借入该 data 创建 VS，并用 reserved ref 的 RootMap 发布分支头。
它发布 c1、c2、rewind 到 c1、再发布 parent=c1 的 c3，同时加入第二个命名根并冻结 c2 的 tag。
观察 VS 历史为 c3,c1,c2,c1，DG 示意 parent 链为 c3→c1。
两层关闭后全新只读打开，核对 roots/tag/parent，并分别运行 data Audit 和 VS Audit。
共 5 个 data 用户帧、1 ref/4 snapshots/1 tag。
这证明共享基础设施的组合，不包含真实 Schema 批次、State wire、领域 Checkout 或 mutable fork 恢复。

`probe/obj/project.assets.json` 实际 libraries：

- project：Atelia.Primitives、Data、Rbf、Binary、FrameStore、VersionStore；
- project：Atelia.DurableGraph.Serialization；
- package：K4os.Compression.LZ4/1.3.8；
- 没有 Atelia.EventJournal 或 Atelia.RbfSegmentStore。

项目 entries 默认版本显示 1.0.0，不是公开发布版本。来源由 archive commit 限定。
未用旧栈+新栈同 runtime 联调冒充 migration。

两层 Audit 不验证 DG 应用闭包；上游 `tests/VersionStore.Tests/AuditPublic/PublicAuditBehaviorTests.cs:15-51`
还明确见证 opaque roots/tag 指向不存在的 data 时 VS Audit 可以成功。
后续 DG semantic audit 必须独立覆盖全部 commit（含 orphan）、branch/tag 目标、root/type 与最近 State Parent。

## 4. 重跑输入

以下保留本轮 consumer 的完整源码。临时路径可能清理；重跑时新建临时目录，
从准确上游 commit 导出源码为 `upstream/`，把两个代码块保存为 `probe/Probe.csproj` 与
`probe/Program.cs`，更新 DG Serialization 的绝对路径；选择不存在的 probe-store 路径。
建议重跑先串行 `dotnet build probe/Probe.csproj -c Release -m:1 -nr:false`，
再 `dotnet run --project probe/Probe.csproj -c Release --no-build -- <fresh-path>`。
不在 product source-mode assets 中混用旧包/新源。

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../upstream/src/Binary/Binary.csproj" />
    <ProjectReference Include="../upstream/src/FrameStore/FrameStore.csproj" />
    <ProjectReference Include="../upstream/src/VersionStore/VersionStore.csproj" />
    <ProjectReference Include="/repos/Atelia-org/durable-graph/src/DurableGraph.Serialization/DurableGraph.Serialization.csproj" />
  </ItemGroup>
</Project>
```

```csharp
using System.Buffers;
using Atelia.Binary;
using Atelia.DurableGraph.Serialization;
using Atelia.FrameStore;
using Atelia.VersionStore;
using Frames = Atelia.FrameStore.FrameStore;
using Versions = Atelia.VersionStore.VersionStore;

static void Require(bool value, string message) {
    if (!value) { throw new InvalidDataException(message); }
}
static byte[] Commit(byte kind, FrameAddress graph, FrameAddress? parent) {
    byte[] bytes = new byte[parent is null ? 14 : 26];
    bytes[0] = kind;
    Require(graph.TryWrite(bytes.AsSpan(1, 12)), "graph encode");
    bytes[13] = parent is null ? (byte)0 : (byte)1;
    if (parent is { } p) { Require(p.TryWrite(bytes.AsSpan(14, 12)), "parent encode"); }
    return bytes;
}
static FrameAddress? Parent(Frames data, FrameAddress address) {
    using var frame = data.ReadFrame(address).Unwrap();
    var bytes = frame.PayloadAndMeta;
    Require(frame.Tag == 3 && bytes.Length is 14 or 26, "commit frame");
    if (bytes[13] == 0) { return null; }
    Require(FrameAddress.TryRead(bytes[14..], out var parent), "parent decode");
    return parent;
}
static byte[] Roots(FrameAddress head, FrameAddress? fork = null) {
    var roots = new Dictionary<string, FrameAddress>(StringComparer.Ordinal) { ["main"] = head };
    if (fork is { } f) { roots.Add("fork/中文", f); }
    return RootMapCodec.Encode(roots).Unwrap();
}

var oldBuffer = new ArrayBufferWriter<byte>();
var newBuffer = new ArrayBufferWriter<byte>();
var dg = new BinaryPayloadWriter(oldBuffer);
var upstream = new BareValueWriter(newBuffer);
foreach (uint n in new uint[] { 0, 1, 127, 128, uint.MaxValue }) {
    dg.WriteUInt32(n); upstream.WriteVarUInt32(n);
}
dg.WriteInt64(long.MinValue); upstream.WriteVarInt64(long.MinValue);
dg.WriteDouble(-0.0); upstream.WriteDoubleLE(-0.0);
Require(oldBuffer.WrittenSpan.SequenceEqual(newBuffer.WrittenSpan), "primitive writer bytes differ");
var permissive = new BareValueReader(new byte[] { 0x80, 0x00 });
Require(permissive.ReadVarUInt32() == 0 && permissive.ConsumedCount == 2, "upstream redundant varint");
var strict = new BinaryPayloadReader(new byte[] { 0x80, 0x00 });
bool refused = false;
try { strict.ReadUInt32(); } catch (InvalidDataException) { refused = true; }
Require(refused && strict.ConsumedCount == 0, "DG canonical rejection");
Console.WriteLine("PASS primitive bytes; reader canonical difference");

string root = Path.GetFullPath(args.Single());
Require(!Directory.Exists(root), "fresh probe root required");
Directory.CreateDirectory(root);
string dataPath = Path.Combine(root, "data"), versionsPath = Path.Combine(root, "versions");
FrameAddress c1, c2, c3;
RefId refId;
using (var data = Frames.Create(dataPath, rotationThresholdBytes: 128)) {
    var schema = data.Append(1, "schema-placeholder"u8).Unwrap();
    var graph = data.Append(2, "state-placeholder"u8).Unwrap();
    c1 = data.Append(3, Commit(1, graph, null)).Unwrap();
    c2 = data.Append(3, Commit(2, graph, c1)).Unwrap();
    using (var versions = Versions.Create(versionsPath, data, reservedRefIdUpperBound: 1)) {
        refId = versions.CreateRef(1, Roots(c1), out var outcome).Unwrap().RefId;
        Require(outcome == PublicationOutcome.Confirmed, "create outcome");
        versions.PublishRef(refId, Roots(c2), out outcome).Unwrap();
        versions.CreateTag("fixed", Roots(c2), out outcome).Unwrap();
        versions.PublishRef(refId, Roots(c1), out outcome).Unwrap(); // rewind
        c3 = data.Append(3, Commit(2, graph, c1)).Unwrap();
        versions.PublishRef(refId, Roots(c3, c2), out outcome).Unwrap();
        Require(outcome == PublicationOutcome.Confirmed && Parent(data, c3) == c1, "logical parent after rewind");
        var physical = new List<FrameAddress>();
        versions.ReadRefHistory(refId, new HistoryBudget(16, 256), snapshot => {
            physical.Add(RootMapCodec.Decode(snapshot.Payload).Unwrap()["main"]); return true;
        }).Unwrap();
        Require(physical.SequenceEqual(new[] { c3, c1, c2, c1 }), "publication history");
        using var storedSchema = data.ReadFrame(schema).Unwrap();
        Require(storedSchema.PayloadAndMeta.SequenceEqual("schema-placeholder"u8), "shared schema frame");
    }
    data.EnsureUsable(); // VS borrows data
}
using (var data = Frames.OpenReadOnly(dataPath)) {
    using var versions = Versions.OpenReadOnly(versionsPath, data);
    var current = RootMapCodec.Decode(versions.ReadRef(refId).Unwrap().Payload).Unwrap();
    Require(current["main"] == c3 && current["fork/中文"] == c2, "cold branch roots");
    Require(RootMapCodec.Decode(versions.ResolveTag("fixed").Unwrap().Bytes).Unwrap()["main"] == c2, "cold tag");
    Require(Parent(data, c3) == c1 && Parent(data, c1) is null, "cold logical chain");
    Require(data.Audit(_ => { }).Unwrap() == 5, "data audit count");
    var audit = versions.Audit().Unwrap();
    Require(audit.RefCount == 1 && audit.SnapshotCount == 4 && audit.TagCount == 1, "versions audit count");
}
Console.WriteLine("PASS shared frames; root publication; rewind logical parent; named fork; immutable tag; cold RO audits");
```

## 5. 未覆盖和停止门

- 没有实现/运行完整 DG 新栈候选，未改变当前产品引用。
- 没有证明 opaque 地址下的真实 parent/prior 资格、防环或热冷 H 等价。
- 没有实现共享 Schema 重放/逻辑 frontier。
- 没有证明 RO preflight→WO 的严格无恢复合同或旧全库重开确认等价。
- 没有本轮 Windows 测试、旧 DG 存档迁移、包模式消费/公开发布、性能比较或 OS crash/power-loss 保证。

这些未覆盖项对应[报告 T0–T7 路标](0085-rbf3-framestore-versionstore-migration-feasibility.md#6-后续形成工单的路标)，
不因现有测试和示意 consumer 通过而删除。
