using System.ComponentModel;
using System.Text.RegularExpressions;
using AtariHackerMCP.Atari;
using AtariHackerMCP.Helpers;
using AtariHackerMCP.State;
using ModelContextProtocol.Server;

namespace AtariHackerMCP.Tools;

[McpServerToolType]
public static partial class SymbolTools
{
    [McpServerTool, Description("Add or update a named label for a memory address.")]
    public static string DefineSymbol(
        RomSession session,
        SymbolTable symbols,
        SessionPersistence persistence,
        [Description("Memory address.")] string address,
        [Description("Label to define.")] string label,
        [Description("Optional comment.")] string? comment = null)
    {
        try
        {
            if (!session.IsLoaded)
            {
                return "ERROR: No ROM is currently loaded. Use LoadRom first.";
            }

            if (!LabelRegex().IsMatch(label))
            {
                return $"ERROR: Invalid label '{label}'. Use identifier characters only.";
            }

            var parsedAddress = AddressParser.ParseAddress(address);
            symbols[parsedAddress] = new SymbolEntry(label, comment, false, true);
            persistence.Save();
            return $"Defined symbol {label} at {Formatting.HexWord(parsedAddress)}.";
        }
        catch (Exception ex)
        {
            return $"ERROR: {ex.Message}";
        }
    }

    [McpServerTool, Description("Remove a user-defined symbol.")]
    public static string RemoveSymbol(
        RomSession session,
        SymbolTable symbols,
        SessionPersistence persistence,
        [Description("Address of the symbol to remove.")] string address)
    {
        try
        {
            if (!session.IsLoaded)
            {
                return "ERROR: No ROM is currently loaded. Use LoadRom first.";
            }

            var parsedAddress = AddressParser.ParseAddress(address);
            if (!symbols.TryGetValue(parsedAddress, out var existing))
            {
                return $"ERROR: No symbol defined at {Formatting.HexWord(parsedAddress)}.";
            }

            if (existing.IsHardware && !existing.IsUserDefined)
            {
                return $"ERROR: Cannot remove hardware symbol at {Formatting.HexWord(parsedAddress)}.";
            }

            symbols.Remove(parsedAddress);
            if (AtariHardwareMap.TryGetHardwareSymbol(parsedAddress, out var hardware))
            {
                symbols[parsedAddress] = hardware;
            }

            persistence.Save();
            return $"Removed symbol at {Formatting.HexWord(parsedAddress)}.";
        }
        catch (Exception ex)
        {
            return $"ERROR: {ex.Message}";
        }
    }

    [McpServerTool, Description("Look up the symbol entry for a given address.")]
    public static string LookupSymbol(RomSession session, SymbolTable symbols, [Description("Memory address.")] string address)
    {
        try
        {
            if (!session.IsLoaded)
            {
                return "ERROR: No ROM is currently loaded. Use LoadRom first.";
            }

            var parsedAddress = AddressParser.ParseAddress(address);
            if (!symbols.TryGetValue(parsedAddress, out var symbol))
            {
                return $"No symbol defined at {Formatting.HexWord(parsedAddress)}.";
            }

            return string.Join('\n',
                $"Address      : {Formatting.HexWord(parsedAddress)}",
                $"Label        : {symbol.Label}",
                $"Comment      : {symbol.Comment ?? "--"}",
                $"Hardware     : {symbol.IsHardware}",
                $"User-defined : {symbol.IsUserDefined}");
        }
        catch (Exception ex)
        {
            return $"ERROR: {ex.Message}";
        }
    }

    [McpServerTool, Description("List symbols in the symbol table.")]
    public static string ListSymbols(
        RomSession session,
        SymbolTable symbols,
        [Description("Include built-in hardware symbols.")] bool includeHardware = false,
        [Description("Optional substring filter.")] string? filter = null)
    {
        try
        {
            if (!session.IsLoaded)
            {
                return "ERROR: No ROM is currently loaded. Use LoadRom first.";
            }

            var query = symbols
                .Where(pair => includeHardware || pair.Value.IsUserDefined)
                .Where(pair => string.IsNullOrWhiteSpace(filter) || pair.Value.Label.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .OrderBy(pair => pair.Key)
                .ToList();

            if (query.Count == 0)
            {
                return "No symbols matched the current filter.";
            }

            return string.Join('\n', query.Select(pair =>
            {
                var comment = string.IsNullOrWhiteSpace(pair.Value.Comment) ? string.Empty : $"  ; {pair.Value.Comment}";
                return $"{Formatting.HexWord(pair.Key)}  {pair.Value.Label}{comment}";
            }));
        }
        catch (Exception ex)
        {
            return $"ERROR: {ex.Message}";
        }
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex LabelRegex();
}
