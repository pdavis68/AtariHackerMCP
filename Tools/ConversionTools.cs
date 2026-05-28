using System.ComponentModel;
using AtariHackerMCP.Helpers;
using ModelContextProtocol.Server;

namespace AtariHackerMCP.Tools;

[McpServerToolType]
public static class ConversionTools
{
    [McpServerTool, Description("Convert a hexadecimal value to decimal.")]
    public static string HexToDecimal([Description("Hex value with or without $ or 0x prefix.")] string hex)
    {
        try
        {
            var value = AddressParser.ParseOffset(hex);
            return $"{Formatting.HexWord((ushort)Math.Min(value, 0xFFFF))} = {value}";
        }
        catch (Exception ex)
        {
            return $"ERROR: {ex.Message}";
        }
    }

    [McpServerTool, Description("Convert a decimal integer to hexadecimal.")]
    public static string DecimalToHex([Description("Decimal integer to convert.")] int value)
    {
        try
        {
            if (value < 0)
            {
                return "ERROR: Decimal value must be non-negative.";
            }

            return $"{value} = ${value:X}";
        }
        catch (Exception ex)
        {
            return $"ERROR: {ex.Message}";
        }
    }
}
