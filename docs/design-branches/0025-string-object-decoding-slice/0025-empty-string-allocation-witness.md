# DB-025 附件：独立空字符串分配见证

> 历史研究附件：用户最终选择空字符串两端统一为 string.Empty，产品不采用本页任何独立分配机制。

2026-09-06，机制子代理执行、主代理独立复跑。环境：`.NET 10.0.5` / `win-x64` / Release。
这是本机机制证据，不是跨 .NET 版本的兼容承诺；是否用于产品见 DB-025 的实施决定。
**后续核验修正**：本页末尾记录了公开 Replace/StringBuilder 路径也能产生独立空串。
因此非公开 FastAllocateString 并非当前已知唯一方案，原来的二选一不完整。

## 可复跑程序

在临时目录创建一个 net10.0 console 项目，将以下内容放入 Program.cs，然后执行
`dotnet run --project <该项目的.csproj> -c Release`。不需要项目依赖或 unsafe 编译开关。

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

Console.WriteLine(RuntimeInformation.FrameworkDescription);
Console.WriteLine(RuntimeInformation.RuntimeIdentifier);
var allocator = typeof(string).GetMethod("FastAllocateString",
    BindingFlags.Static | BindingFlags.NonPublic, new[] { typeof(nint) })!;
Console.WriteLine($"FastAllocateString:Attributes={allocator.Attributes};" +
    $"Assembly={allocator.IsAssembly};Private={allocator.IsPrivate};DeclaringType={allocator.DeclaringType}");
var empties = new HashSet<object>(ReferenceEqualityComparer.Instance);
for (int i = 0; i < 100000; i++) {
    string fresh = i % 2 == 0
        ? StringAccess.Allocate(null!, 0)
        : (string)allocator.Invoke(null, new object[] { (nint)0 })!;
    if (fresh.Length != 0 || fresh != string.Empty || ReferenceEquals(fresh, string.Empty) ||
        fresh.GetHashCode() != string.Empty.GetHashCode() || !empties.Add(fresh) || !fresh.AsSpan().IsEmpty) {
        throw new Exception("Invalid or shared empty string.");
    }
}
Console.WriteLine($"Before GC:Distinct={empties.Count};HalfUnsafeAccessorHalfReflection=True");
GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
if (empties.Count != 100000 || !empties.Cast<string>().All(s =>
    s.Length == 0 && s == string.Empty && s.GetHashCode() == string.Empty.GetHashCode())) {
    throw new Exception("Post-GC validation failed.");
}
Console.WriteLine($"After GC:Distinct={empties.Count};HashAndContentValid=True");
Console.WriteLine("PASS");

static class StringAccess {
    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "FastAllocateString")]
    internal static extern string Allocate(string declaringType, nint length);
}
```

实际两次通过输出：

```text
.NET 10.0.5
win-x64
FastAllocateString:Attributes=Assembly, Static, HideBySig;Assembly=True;Private=False;DeclaringType=System.String
Before GC:Distinct=100000;HalfUnsafeAccessorHalfReflection=True
After GC:Distinct=100000;HashAndContentValid=True
PASS
```

## 依赖与排除项

固定版本 [String.CoreCLR.cs](https://raw.githubusercontent.com/dotnet/runtime/v10.0.5/src/coreclr/System.Private.CoreLib/src/System/String.CoreCLR.cs)
与本机反射一致：入口是 `internal static string FastAllocateString(nint)`，不是公开 API，
也不是 private 修饰符；不要误用 int 签名。UnsafeAccessor 的首个参数指定声明类型，静态调用传 null。
产品若采用，只封装长度 0 分配，不开放任意长度未初始化字符串，不为其他运行时猜测 fallback。

普通空串构造、string.Create(0, ...)、string.Empty.Clone() 的本机对照均复用 Empty。
另一候选是绕过 protected 访问限制调用 object.MemberwiseClone：空串独立进程的 10 万次与压缩 GC 通过，
但非空字符串对照出现内容截断，混合克隆进程随后发生 Internal CLR error (0x80131506)。
因此没有采用 MemberwiseClone；空串子集通过不足以消除该机制的运行时布局风险。
不在此附件保留可误用的非空克隆代码。

## 补充：公开 API 路径实测（同日）

用户追问 Remove 与 Marshal 后，主代理新建独立 net10.0 临时项目，以 Release 在
`.NET 10.0.5 win-x64` 运行。每个工厂调用两次，检查长度、与 Empty 的 ReferenceEquals 和两次结果的 ReferenceEquals。

| 路径 | 与 Empty 同实例 | 两次调用同实例 |
|---|---|---|
| `"A".Remove(0,1)` / Remove(0) / Substring 零长度 / `[1..]` | 是 | 是 |
| `"A".Replace("A", "")` | **否** | **否** |
| `" ".Trim()` | 是 | 是 |
| `new string(char[0])`、char[]/span 的零长度构造、`new string('A',0)` | 是 | 是 |
| `string.Create(0,...)` / `string.Empty.Clone()` | 是 | 是 |
| `string.Copy(string.Empty)`（已 obsolete） | **否** | **否** |
| `new StringBuilder().ToString()` | 是 | 是 |
| `new StringBuilder("A").ToString(0,0)` | **否** | **否** |
| UTF8/Unicode.GetString(empty bytes)，UTF8 的 count=0 overload | 是 | 是 |
| UTF8 无效字节 + 空 DecoderReplacementFallback | 是 | 是 |
| Marshal.PtrToStringUni/Ansi/UTF8/Auto，终止符版及显式长度 0 版 | 是 | 是 |
| Marshal.PtrToStringBSTR，输入 StringToBSTR(Empty) 所得有效 BSTR | 是 | 是 |
| char*/sbyte* 的终止符与显式长度 0 构造，sbyte*+Encoding.UTF8 | 是 | 是 |

所有 Marshal 测试使用有效非零地址与零终止缓冲区，finally 释放；不把 null 指针→null 当空串。

关键结果可用以下 net10.0 console 程序独立复跑（无需非公开调用或 unsafe）：

```csharp
using System;
using System.Runtime.InteropServices;
using System.Text;

Console.WriteLine(RuntimeInformation.FrameworkDescription);
void Check(string name, Func<string?> make) {
    string? a = make(), b = make();
    Console.WriteLine($"{name}: length={a?.Length}; empty={ReferenceEquals(a, string.Empty)}; repeated={ReferenceEquals(a,b)}");
}
Check("Remove", () => "A".Remove(0,1));
Check("Replace", () => "A".Replace("A", ""));
Check("StringBuilder whole", () => new StringBuilder().ToString());
Check("StringBuilder range", () => new StringBuilder("A").ToString(0,0));
nint buffer = Marshal.AllocHGlobal(16);
nint bstr = Marshal.StringToBSTR(string.Empty);
try {
    for (int i = 0; i < 16; i++) Marshal.WriteByte(buffer, i, 0);
    Check("Uni", () => Marshal.PtrToStringUni(buffer));
    Check("Uni length 0", () => Marshal.PtrToStringUni(buffer,0));
    Check("Ansi", () => Marshal.PtrToStringAnsi(buffer));
    Check("Ansi length 0", () => Marshal.PtrToStringAnsi(buffer,0));
    Check("UTF8", () => Marshal.PtrToStringUTF8(buffer));
    Check("UTF8 length 0", () => Marshal.PtrToStringUTF8(buffer,0));
    Check("BSTR", () => Marshal.PtrToStringBSTR(bstr));
} finally {
    Marshal.FreeBSTR(bstr);
    Marshal.FreeHGlobal(buffer);
}
```

固定版本源码解释：
[Remove 与 ReplaceHelper](https://raw.githubusercontent.com/dotnet/runtime/v10.0.5/src/libraries/System.Private.CoreLib/src/System/String.Manipulation.cs)
中 Remove 特判零结果返回 Empty，ReplaceHelper 则继续分配长度为零的新串；
[StringBuilder.ToString(startIndex,length)](https://raw.githubusercontent.com/dotnet/runtime/v10.0.5/src/libraries/System.Private.CoreLib/src/System/Text/StringBuilder.cs)
同样直接分配；[Marshal](https://raw.githubusercontent.com/dotnet/runtime/v10.0.5/src/libraries/System.Private.CoreLib/src/System/Runtime/InteropServices/Marshal.cs)
的 BSTR 路径最终调用 PtrToStringUni(ptr,length)，落到零长度构造。

这些是具体版本的行为，不构成“以后必须返回新实例”的 API 保证。公开 API 候选避免绑定内部签名，
但若依靠它分配独立空串，仍需身份后置检查与升级测试；不得失败时静默合并。
“独立空串只会由特殊非公开代码产生”的暗示已被普通 Replace 路径反例否定。
