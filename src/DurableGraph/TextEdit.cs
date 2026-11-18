namespace Atelia.DurableGraph;

/// <summary>A replacement in the UTF-16 coordinates of the text before an edit batch.</summary>
public readonly record struct TextEdit(int Start, int Length, string Replacement);
