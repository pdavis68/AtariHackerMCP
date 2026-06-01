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
        [Description("Optional substring filter.")] string? filter = null,
        [Description("Maximum number of results to return. Default is 50. Set higher for fuller coverage or lower to keep responses brief.")] int maxResults = 50)
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
                    FlushRun(session, minLength, filter, results, ref start, buffer, maxResults);
                }
            }

            FlushRun(session, minLength, filter, results, ref start, buffer, maxResults);

            if (results.Count == 0)
            {
                return $"Strings found ({encoding}, minLen={minLength}):\n\n  <none>";
            }

            var summary = results.Count >= maxResults
                ? $" (showing first {maxResults}; result set truncated)"
                : string.Empty;

            return $"Strings found ({encoding}, minLen={minLength}){summary}:\n\n" + string.Join('\n', results);
        }
        catch (Exception ex)
        {
            return $"ERROR: {ex.Message}";
        }
    }

    private const int MaxDisplayedStringLength = 80;

    private static void FlushRun(RomSession session, int minLength, string? filter, List<string> results, ref int start, StringBuilder buffer, int maxResults = 50)
    {
        if (start >= 0 && buffer.Length >= minLength && results.Count < maxResults)
        {
            var text = buffer.ToString();
            if (string.IsNullOrWhiteSpace(filter) || text.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                var address = XexAddressResolver.ResolveFileOffset(session, start);
                var displayText = text.Length <= MaxDisplayedStringLength
                    ? text
                    : text[..MaxDisplayedStringLength] + "...";
                results.Add($"  ${start:X4} / {(address is null ? "(unknown)" : Formatting.HexWord(address.Value))}  [{buffer.Length} bytes] \"{displayText}\"");
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

        var ch = AtasciiDecoder.DecodeByte(value);
        if (ch != '.')
        {
            // AtasciiDecoder.DecodeByte uses char >= 128 as inverse marker
            if (ch >= 128)
            {
                decoded = "~" + (char)(ch - 128);
            }
            else
            {
                decoded = ch.ToString();
            }
            return true;
        }

        decoded = string.Empty;
        return false;
    }
}
