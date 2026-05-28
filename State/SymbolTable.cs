namespace AtariHackerMCP.State;

public sealed record SymbolEntry(
    string Label,
    string? Comment = null,
    bool IsHardware = false,
    bool IsUserDefined = false);

public sealed class SymbolTable : Dictionary<ushort, SymbolEntry>
{
}
