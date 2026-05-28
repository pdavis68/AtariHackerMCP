using System.ComponentModel;
using System.Text;
using AtariHackerMCP.Helpers;
using AtariHackerMCP.State;
using ModelContextProtocol.Server;

namespace AtariHackerMCP.Tools;

[McpServerToolType]
public static class HexDumpTool
{
    [McpServerTool, Description("Produce a hex dump with file offsets, memory addresses, and ASCII.")]
    public static string HexDump(
        RomSession session,
        [Description("File offset as decimal or hex.")] string offset,
        [Description("Number of bytes to dump.")] int numBytes,
        [Description("Optional override memory start address.")] string? startAddress = null)
    {
        try
        {
            if (!session.IsLoaded)
            {
                return "ERROR: No ROM is currently loaded. Use LoadRom first.";
            }

            var fileOffset = AddressParser.ParseOffset(offset);
            var addressOverride = string.IsNullOrWhiteSpace(startAddress) ? (ushort?)null : AddressParser.ParseAddress(startAddress);
            return GenerateHexDump(session, fileOffset, numBytes, addressOverride);
        }
        catch (Exception ex)
        {
            return $"ERROR: {ex.Message}";
        }
    }

    internal static string GenerateHexDump(RomSession session, int offset, int numBytes, ushort? startAddress = null)
    {
        if (!session.IsLoaded || session.Data is null)
        {
            return "ERROR: No ROM is currently loaded. Use LoadRom first.";
        }

        if (offset < 0 || offset >= session.Length)
        {
            return $"ERROR: Offset 0x{offset:X} exceeds ROM size (0x{session.Length:X} bytes).";
        }

        if (numBytes <= 0)
        {
            return "ERROR: Number of bytes must be greater than zero.";
        }

        var count = Math.Min(numBytes, session.Length - offset);
        var lines = new List<string>
        {
            "Offset    Address   00 01 02 03 04 05 06 07 08 09 0A 0B 0C 0D 0E 0F  ASCII",
            "--------  --------  -----------------------------------------------  ----------------"
        };

        for (var rowStart = 0; rowStart < count; rowStart += 16)
        {
            var currentOffset = offset + rowStart;
            var rowCount = Math.Min(16, count - rowStart);
            var address = startAddress is null
                ? XexAddressResolver.ResolveFileOffset(session, currentOffset)
                : (ushort)(startAddress.Value + rowStart);

            var hex = new StringBuilder();
            var ascii = new StringBuilder();
            for (var i = 0; i < 16; i++)
            {
                if (i < rowCount)
                {
                    var value = session.Data[currentOffset + i];
                    hex.Append(value.ToString("X2")).Append(' ');
                    ascii.Append(value is >= 0x20 and <= 0x7E ? (char)value : '.');
                }
                else
                {
                    hex.Append("   ");
                    ascii.Append(' ');
                }
            }

            lines.Add($"{Formatting.HexOffset(currentOffset)}  {Formatting.DisplayAddress(address),-8}  {hex.ToString().TrimEnd(),-47}  {ascii}");
        }

        return string.Join('\n', lines);
    }
}
