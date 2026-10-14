# DB-018 附件：运行时闭合泛型与 typed ref 读入见证

2026-09-05 在 PowerShell 的内存编译中运行，主审独立复跑通过：

```text
Runtime=10.0.9; closed generic array=[17,29], bytes=8
```

这里只验证 MakeGenericType/MakeGenericMethod/CreateDelegate 能把开放泛型 body 与数组模板组合，
delegate 可以接受 ref struct Reader 和真实 struct 元素的 ref；没有使用 Reflection.Emit。
不是产品 codec、Schema binding、并发缓存或完整反序列化实现。
示例使用 BitConverter 和两种固定受支持类型，仅为机制见证，不定义 DurableGraph 字节格式。

从 PowerShell 新进程运行下面代码即可；只在内存中 Add-Type，不写产品项目或旧备份。
同一进程重复运行若类型已加载，可跳过 Add-Type，直接调用 GFactory.Run。

```powershell
$codecEvidenceSource = @'
using System;
using System.Reflection;
public ref struct GReader {
    public ReadOnlySpan<byte> Bytes;
    public int Position;
    public int ReadInt32() {
        int value = BitConverter.ToInt32(Bytes.Slice(Position, 4));
        Position += 4;
        return value;
    }
}
public struct GCell<T> { public T Value; }
public delegate void GRead<T>(ref GReader reader, ref T value);
public interface IGReader { void Read(ref GReader reader, object target); }
public sealed class GVectorReader<T> : IGReader {
    public void Read(ref GReader reader, object target) {
        var values = (T[])target;
        var body = GSlots<T>.Read;
        for (int i = 0; i < values.Length; i++) {
            body(ref reader, ref values[i]);
        }
    }
}
public static class GSlots<T> {
    public static readonly GRead<T> Read = (GRead<T>)GFactory.Bind(typeof(T));
}
public static class GFactory {
    public static void ReadInt32(ref GReader reader, ref int value) {
        value = reader.ReadInt32();
    }
    public static void ReadCell<T>(ref GReader reader, ref GCell<T> value) {
        GSlots<T>.Read(ref reader, ref value.Value);
    }
    public static Delegate Bind(Type type) {
        Type delegateType = typeof(GRead<>).MakeGenericType(type);
        MethodInfo method = type == typeof(int)
            ? typeof(GFactory).GetMethod(nameof(ReadInt32))
            : typeof(GFactory).GetMethod(nameof(ReadCell)).MakeGenericMethod(type.GenericTypeArguments);
        return method.CreateDelegate(delegateType);
    }
    public static string Run() {
        Type runtimeCellType = typeof(GCell<>).MakeGenericType(typeof(int));
        Array target = Array.CreateInstance(runtimeCellType, 2);
        var codec = (IGReader)Activator.CreateInstance(typeof(GVectorReader<>).MakeGenericType(runtimeCellType));
        byte[] bytes = new byte[8];
        BitConverter.GetBytes(17).CopyTo(bytes, 0);
        BitConverter.GetBytes(29).CopyTo(bytes, 4);
        var reader = new GReader { Bytes = bytes };
        codec.Read(ref reader, target);
        var typed = (GCell<int>[])target;
        return $"Runtime={Environment.Version}; closed generic array=[{typed[0].Value},{typed[1].Value}], bytes={reader.Position}";
    }
}
'@
Add-Type -TypeDefinition $codecEvidenceSource
[GFactory]::Run()
```

读入循环没有 GetValue/SetValue 或逐元素装箱；object 只用于对象级调度边界。
其他类型的拒绝、完整输入校验、binding 注册与失败缓存尚未设计进此见证。
该例不验证任意 MD rank、继承升级、string 引用身份或 BCL 集合。

设计结论及后续边界见 [DB-018](0018-generated-graph-codec-shape.md)。
