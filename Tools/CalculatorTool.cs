using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using NCalc;

namespace AtariHackerMCP.Tools;

[McpServerToolType]
public static partial class CalculatorTool
{
    [McpServerTool, Description("Evaluate a mathematical expression with hex literals and bitwise operators.")]
    public static string Calculate([Description("Expression to evaluate.")] string expression)
    {
        try
        {
            var normalized = AtariHexLiteralRegex().Replace(expression, static match => $"0x{match.Groups[1].Value}");
            var result = new Expression(normalized).Evaluate();
            if (result is null)
            {
                return "ERROR: Expression evaluation returned no result.";
            }

            var decimalValue = Convert.ToInt64(result, CultureInfo.InvariantCulture);
            return $"Result: {decimalValue} (${decimalValue:X})";
        }
        catch (Exception ex)
        {
            return $"ERROR: Expression evaluation failed: {ex.Message}";
        }
    }

    [GeneratedRegex("\\$([0-9A-Fa-f]+)")]
    private static partial Regex AtariHexLiteralRegex();
}
