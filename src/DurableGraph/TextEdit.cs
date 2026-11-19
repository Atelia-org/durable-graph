namespace Atelia.DurableGraph;

/// <summary>A replacement in the UTF-16 coordinates of the text before an edit batch.</summary>
/// <param name="Start">Zero-based offset in the original text.</param>
/// <param name="Length">Number of original UTF-16 code units to remove, or zero for an insertion.</param>
/// <param name="Replacement">Text to insert at Start. An empty string deletes the range.</param>
public readonly record struct TextEdit(int Start, int Length, string Replacement);
