using AtariHackerMCP.State;

namespace AtariHackerMCP.Helpers;

internal static class SymbolResolver
{
    public static string? Resolve(ushort address, SymbolTable symbols, ZeroPageMap zeroPageMap)
    {
        if (symbols.TryGetValue(address, out var symbol))
        {
            return symbol.Label;
        }

        if (address <= 0xFF && zeroPageMap.TryGetValue((byte)address, out var zpSymbol))
        {
            return zpSymbol.Label;
        }

        return null;
    }

    public static SymbolEntry? ResolveEntry(ushort address, SymbolTable symbols, ZeroPageMap zeroPageMap)
    {
        if (symbols.TryGetValue(address, out var symbol))
        {
            return symbol;
        }

        if (address <= 0xFF && zeroPageMap.TryGetValue((byte)address, out var zpSymbol))
        {
            return zpSymbol;
        }

        return null;
    }
}
