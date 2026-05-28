using System.ComponentModel;
using System.Text;
using AtariHackerMCP.Helpers;
using AtariHackerMCP.State;
using ModelContextProtocol.Server;

namespace AtariHackerMCP.Tools;

[McpServerToolType]
public static class StringSearchTool
{
    [McpServerTool, Description("Search the ROM for runs of printable ASCII or ATASCII characters.")]
    public static string FindStrings(
        RomSession session,
        [Description("Minimum string length to report.")] int minLength = 4,
        [Description("String encoding: ascii or atascii.")] string encoding = "ascii",
        [Description("Optional substring filter.")] string? filter = null)
    {
        try
        {
            if (!session.IsLoaded || session.Data is null)
            {
                return "ERROR: No ROM is currently loaded. Use LoadRom first.";
            }

            minLength = Math.Max(1, minLength);
            var useAtascii = string.Equals(encoding, "atascii", StringComparison.OrdinalIgnoreCase);
            var results = new List<string>();
            var start = -1;
            var buffer = new StringBuilder();

            for (var i = 0; i < session.Data.Length; i++)
            {
                if (TryDecode(session.Data[i], useAtascii, out var decoded))
                {
                    if (start < 0)
                    {
                        start = i;
                    }

                    buffer.Append(decoded);
                }
                else
                {
                    FlushRun(session, minLength, filter, results, ref start, buffer);
                }
            }

            FlushRun(session, minLength, filter, results, ref start, buffer);

            if (results.Count == 0)
            {
                return $"Strings found ({encoding}, minLen={minLength}):\n\n  <none>";
            }

            return $"Strings found ({encoding}, minLen={minLength}):\n\n" + string.Join('\n', results);
        }
        catch (Exception ex)
        {
            return $"ERROR: {ex.Message}";
        }
    }

    private static void FlushRun(RomSession session, int minLength, string? filter, List<string> results, ref int start, StringBuilder buffer)
    {
        if (start >= 0 && buffer.Length >= minLength)
        {
            var text = buffer.ToString();
            if (string.IsNullOrWhiteSpace(filter) || text.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                var address = XexAddressResolver.ResolveFileOffset(session, start);
                results.Add($"  ${start:X4} / {(address is null ? "(unknown)" : Formatting.HexWord(address.Value))}  \"{text}\"");
            }
        }

        start = -1;
        buffer.Clear();
    }

    private static bool TryDecode(byte value, bool atascii, out string decoded)
    {
        if (!atascii)
        {
            if (value is >= 0x20 and <= 0x7E)
            {
                decoded = ((char)value).ToString();
                return true;
            }

            decoded = string.Empty;
            return false;
        }

        var inverse = (value & 0x80) != 0;
        var plain = (byte)(value & 0x7F);
        if (plain is >= 0x20 and <= 0x5F)
        {
            decoded = (inverse ? "~" : string.Empty) + ((char)plain);
            return true;
        }

        if (plain <= 0x1F)
        {
            var mapped = plain switch
            {
                <= 25 => ((char)('A' + plain)).ToString(),
                <= 35 => ((char)('0' + plain - 26)).ToString(),
                _ => string.Empty
            };
            if (!string.IsNullOrEmpty(mapped))
            {
                decoded = (inverse ? "~" : string.Empty) + mapped;
                return true;
            }
        }

        decoded = string.Empty;
        return false;
    }
}
