using System.ComponentModel;
using AtariHackerMCP.Atari;
using AtariHackerMCP.Helpers;
using AtariHackerMCP.State;
using ModelContextProtocol.Server;

namespace AtariHackerMCP.Tools;

[McpServerToolType]
public static class ZeroPageTool
{
    [McpServerTool, Description("Add or update an annotation for a zero page address.")]
    public static string AnnotateZeroPage(
        RomSession session,
        ZeroPageMap zeroPageMap,
        SessionPersistence persistence,
        [Description("Zero page address.")] string address,
        [Description("Label to assign.")] string label,
        [Description("Optional comment.")] string? comment = null)
    {
        try
        {
            if (!session.IsLoaded)
            {
                return "ERROR: No ROM is currently loaded. Use LoadRom first.";
            }

            var parsedAddress = AddressParser.ParseZeroPageAddress(address);
            zeroPageMap[parsedAddress] = new SymbolEntry(label, comment, false, true);
            persistence.Save();
            return $"Annotated zero page {Formatting.HexByte(parsedAddress)} as {label}.";
        }
        catch (Exception ex)
        {
            return $"ERROR: {ex.Message}";
        }
    }

    [McpServerTool, Description("Display the current zero page annotations.")]
    public static string ShowZeroPageMap(RomSession session, ZeroPageMap zeroPageMap, [Description("Show all 256 bytes of zero page.")] bool showUnannotated = false)
    {
        try
        {
            if (!session.IsLoaded)
            {
                return "ERROR: No ROM is currently loaded. Use LoadRom first.";
            }

            var lines = zeroPageMap
                .OrderBy(pair => pair.Key)
                .Select(pair => $"{Formatting.HexByte(pair.Key)}  {pair.Value.Label}{(string.IsNullOrWhiteSpace(pair.Value.Comment) ? string.Empty : $"  ; {pair.Value.Comment}")}")
                .ToList();

            if (!showUnannotated)
            {
                return lines.Count == 0 ? "No zero page annotations defined." : string.Join('\n', lines);
            }

            var dumpLines = new List<string> { "Zero page bytes:" };
            var data = session.Data ?? Array.Empty<byte>();
            for (var row = 0; row < 16; row++)
            {
                var rowBase = row * 16;
                var bytes = Enumerable.Range(0, 16)
                    .Select(column => rowBase + column < data.Length ? data[rowBase + column].ToString("X2") : "??")
                    .ToArray();
                var annotations = zeroPageMap
                    .Where(pair => pair.Key >= rowBase && pair.Key < rowBase + 16)
                    .Select(pair => $"{Formatting.HexByte(pair.Key)}={pair.Value.Label}");
                dumpLines.Add($"{rowBase:X2}: {string.Join(' ', bytes)}    {string.Join(", ", annotations)}");
            }

            if (lines.Count > 0)
            {
                dumpLines.Add(string.Empty);
                dumpLines.Add("Annotations:");
                dumpLines.AddRange(lines);
            }

            return string.Join('\n', dumpLines);
        }
        catch (Exception ex)
        {
            return $"ERROR: {ex.Message}";
        }
    }
}
