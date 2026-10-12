namespace Atelia.DurableGraph.Persistence;

internal static class AuditFormat {
    internal static InvalidDataException Unsupported(string message) {
        var error = new InvalidDataException(message);
        error.Data["DurableGraph.UnsupportedFormat"] = true;
        return error;
    }
}
