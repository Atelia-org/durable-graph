using System.Text;

namespace ApplicationRecipes;

public static class PreviewSummaryChecks {
    public static void Run() {
        var state = new GardenState { Day = 7, Flowers = 3, Won = true };
        GardenPreview preview = SummaryCodec.FromState(state);
        Require(preview == new GardenPreview(7, 3, true), "State projection");
        Require(state.Day == 7 && state.Flowers == 3 && state.Won, "Projection changed State");
        Require(SummaryCodec.Read(SummaryCodec.Encode(preview)) == new SummaryReadResult(SummaryStatus.Available, preview),
            "UTF-8 JSON roundtrip");
        state.Day = 6;
        state.Won = false;
        Require(preview == new GardenPreview(7, 3, true), "Preview retained mutable State");
        Require(SummaryCodec.Read(default).Status == SummaryStatus.None, "Empty means no summary");
        Require(Read("""{"schemaVersion":1,"day":0,"flowers":0,"won":false}""").Preview == new GardenPreview(0, 0, false),
            "Explicit zero values are valid");
        Require(Read("""{"schemaVersion":1,"day":7,"flowers":3,"won":true,"note":"花园"}""").Preview == preview,
            "Well-formed additional fields are allowed");
        Require(Read("""{"schemaVersion":2}""").Status == SummaryStatus.Unsupported, "Unknown version needs no V1 fields");
        Require(Read("""{"schemaVersion":2,"day":"different protocol","won":null}""").Status == SummaryStatus.Unsupported,
            "Unknown version must not use V1 field rules");

        string[] invalid = [
            "null", "[]", "{", " ", "{}",
            """{"day":0,"flowers":0,"won":false}""",
            """{"schemaVersion":"1","day":0,"flowers":0,"won":false}""",
            """{"schemaVersion":0}""", """{"schemaVersion":-1}""",
            """{"schemaVersion":1.5}""", """{"schemaVersion":2147483648}""",
            """{"schemaVersion":1,"flowers":0,"won":false}""",
            """{"schemaVersion":1,"day":0,"won":false}""",
            """{"schemaVersion":1,"day":0,"flowers":0}""",
            """{"schemaVersion":1,"day":null,"flowers":0,"won":false}""",
            """{"schemaVersion":1,"day":"0","flowers":0,"won":false}""",
            """{"schemaVersion":1,"day":0.5,"flowers":0,"won":false}""",
            """{"schemaVersion":1,"day":0,"flowers":"0","won":false}""",
            """{"schemaVersion":1,"day":0,"flowers":0,"won":"false"}""",
            """{"schemaVersion":1,"day":0,"flowers":0,"won":null}""",
            """{"schemaVersion":1,"day":-1,"flowers":0,"won":false}""",
            """{"schemaVersion":1,"day":8,"flowers":0,"won":false}""",
            """{"schemaVersion":1,"day":0,"flowers":-1,"won":false}""",
            """{"schemaVersion":1,"day":0,"flowers":4,"won":false}""",
            """{"schemaVersion":1,"day":6,"flowers":3,"won":true}""",
            """{"schemaVersion":1,"day":7,"flowers":2,"won":true}""",
            """{"schemaVersion":1,"schemaVersion":2}""",
            """{"schemaVersion":1,"day":0,"day":1,"flowers":0,"won":false}""",
        ];
        foreach (string json in invalid) {
            Require(Read(json) == new SummaryReadResult(SummaryStatus.Invalid, null), $"Invalid preview accepted: {json}");
        }

        byte[] malformedUtf8 = [
            .. """{"schemaVersion":1,"day":0,"flowers":0,"won":false,"extra":"""u8,
            (byte)'"', 0xFF, (byte)'"', (byte)'}',
        ];
        Require(SummaryCodec.Read(malformedUtf8).Status == SummaryStatus.Invalid, "Invalid UTF-8 in unknown field");
        byte[] oversized = Encoding.UTF8.GetBytes(
            "{\"schemaVersion\":2,\"padding\":\"" + new string('x', SummaryCodec.MaxBytes) + "\"}");
        Require(SummaryCodec.Read(oversized).Status == SummaryStatus.Invalid, "Size budget precedes version dispatch");
        byte[] atLimit = new byte[SummaryCodec.MaxBytes];
        Array.Fill(atLimit, (byte)' ');
        SummaryCodec.Encode(preview).CopyTo(atLimit, 0);
        Require(SummaryCodec.Read(atLimit).Status == SummaryStatus.Available, "Exact byte budget is allowed");
        RequireThrows<ArgumentOutOfRangeException>(() => SummaryCodec.Encode(new GardenPreview(8, 0, false)),
            "Encoding must reject invalid fields");
        RequireThrows<ArgumentOutOfRangeException>(() => SummaryCodec.FromState(new GardenState { Day = -1 }),
            "Projection must reject invalid State");
    }

    private static SummaryReadResult Read(string json) => SummaryCodec.Read(Encoding.UTF8.GetBytes(json));
    private static void Require(bool condition, string message) {
        if (!condition) { throw new InvalidOperationException(message); }
    }
    private static void RequireThrows<T>(Action action, string message) where T : Exception {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException(message);
    }
}
