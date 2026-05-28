using System.ComponentModel;
using AtariHackerMCP.Atari;
using AtariHackerMCP.Helpers;
using AtariHackerMCP.State;
using ModelContextProtocol.Server;

namespace AtariHackerMCP.Tools;

[McpServerToolType]
public static class DisassemblerTool
{
    [McpServerTool, Description("Disassemble 6502 machine code from the loaded ROM.")]
    public static string Disassemble(
        RomSession session,
        SymbolTable symbols,
        ZeroPageMap zeroPageMap,
        [Description("File offset as decimal or hex.")] string offset,
        [Description("Number of bytes to disassemble.")] int numBytes,
        [Description("Optional override start address.")] string? startAddress = null)
    {
        try
        {
            if (!session.IsLoaded || session.Data is null)
            {
                return "ERROR: No ROM is currently loaded. Use LoadRom first.";
            }

            var fileOffset = AddressParser.ParseOffset(offset);
            if (fileOffset < 0 || fileOffset >= session.Length)
            {
                return $"ERROR: Offset 0x{fileOffset:X} exceeds ROM size (0x{session.Length:X} bytes).";
            }

            var addressOverride = string.IsNullOrWhiteSpace(startAddress) ? (ushort?)null : AddressParser.ParseAddress(startAddress);
            var end = Math.Min(session.Length, fileOffset + Math.Max(numBytes, 0));
            var lines = new List<string>();
            var position = fileOffset;

            while (position < end)
            {
                var opcode = session.Data[position];
                if (!Opcodes6502.Table.TryGetValue(opcode, out var entry) || !entry.IsOfficial || position + entry.Bytes > session.Length)
                {
                    var memoryAddress = addressOverride is null
                        ? XexAddressResolver.ResolveFileOffset(session, position)
                        : (ushort)(addressOverride.Value + (position - fileOffset));
                    lines.Add($"{FormatMemoryAddress(memoryAddress)}  {opcode:X2}           .db ${opcode:X2}");
                    position++;
                    continue;
                }

                var currentAddress = addressOverride is null
                    ? XexAddressResolver.ResolveFileOffset(session, position)
                    : (ushort)(addressOverride.Value + (position - fileOffset));
                var bytes = session.Data.Skip(position).Take(entry.Bytes).ToArray();
                var operand = FormatOperand(entry, session.Data, position, currentAddress ?? 0, symbols, zeroPageMap);
                var comments = BuildComments(entry, session.Data, position, currentAddress, symbols, zeroPageMap);
                var byteText = string.Join(' ', bytes.Select(value => value.ToString("X2"))).PadRight(9);
                var mnemonicText = string.IsNullOrWhiteSpace(operand) ? entry.Mnemonic : $"{entry.Mnemonic} {operand}";
                var commentText = comments.Count == 0 ? string.Empty : $"  ; {string.Join(" | ", comments)}";
                lines.Add($"{FormatMemoryAddress(currentAddress)}  {byteText}  {mnemonicText}{commentText}");
                position += entry.Bytes;
            }

            return string.Join('\n', lines);
        }
        catch (Exception ex)
        {
            return $"ERROR: {ex.Message}";
        }
    }

    internal static int GetStepLength(byte opcode)
    {
        return Opcodes6502.Table.TryGetValue(opcode, out var entry) ? Math.Max(entry.Bytes, 1) : 1;
    }

    internal static bool TryGetOfficialEntry(byte opcode, out OpcodeEntry entry)
    {
        if (Opcodes6502.Table.TryGetValue(opcode, out entry!) && entry.IsOfficial)
        {
            return true;
        }

        entry = null!;
        return false;
    }

    internal static ushort? ResolveOperandAddress(OpcodeEntry entry, byte[] data, int position, ushort memoryAddress)
    {
        return entry.Mode switch
        {
            AddressingMode.ZeroPage or AddressingMode.ZeroPageX or AddressingMode.ZeroPageY or AddressingMode.IndirectX or AddressingMode.IndirectY => data[position + 1],
            AddressingMode.Absolute or AddressingMode.AbsoluteX or AddressingMode.AbsoluteY or AddressingMode.Indirect => ReadWord(data, position + 1),
            AddressingMode.Relative => (ushort)(memoryAddress + entry.Bytes + unchecked((sbyte)data[position + 1])),
            _ => null
        };
    }

    internal static string FormatOperand(OpcodeEntry entry, byte[] data, int position, ushort memoryAddress, SymbolTable symbols, ZeroPageMap zeroPageMap)
    {
        return entry.Mode switch
        {
            AddressingMode.Implied => string.Empty,
            AddressingMode.Accumulator => "A",
            AddressingMode.Immediate => $"#{Formatting.HexByte(data[position + 1])}",
            AddressingMode.ZeroPage => Formatting.HexByte(data[position + 1]),
            AddressingMode.ZeroPageX => $"{Formatting.HexByte(data[position + 1])},X",
            AddressingMode.ZeroPageY => $"{Formatting.HexByte(data[position + 1])},Y",
            AddressingMode.Absolute => Formatting.HexWord(ReadWord(data, position + 1)),
            AddressingMode.AbsoluteX => $"{Formatting.HexWord(ReadWord(data, position + 1))},X",
            AddressingMode.AbsoluteY => $"{Formatting.HexWord(ReadWord(data, position + 1))},Y",
            AddressingMode.Indirect => $"({Formatting.HexWord(ReadWord(data, position + 1))})",
            AddressingMode.IndirectX => $"({Formatting.HexByte(data[position + 1])},X)",
            AddressingMode.IndirectY => $"({Formatting.HexByte(data[position + 1])}),Y",
            AddressingMode.Relative => Formatting.HexWord((ushort)(memoryAddress + entry.Bytes + unchecked((sbyte)data[position + 1]))),
            _ => string.Empty
        };
    }

    internal static ushort ReadWord(byte[] data, int position)
    {
        return (ushort)(data[position] | (data[position + 1] << 8));
    }

    private static List<string> BuildComments(OpcodeEntry entry, byte[] data, int position, ushort? memoryAddress, SymbolTable symbols, ZeroPageMap zeroPageMap)
    {
        var comments = new List<string>();
        if (memoryAddress is not null)
        {
            var currentEntry = SymbolResolver.ResolveEntry(memoryAddress.Value, symbols, zeroPageMap);
            if (!string.IsNullOrWhiteSpace(currentEntry?.Comment))
            {
                comments.Add(currentEntry.Comment!);
            }
        }

        if (memoryAddress is null)
        {
            return comments;
        }

        var operandAddress = ResolveOperandAddress(entry, data, position, memoryAddress.Value);
        if (operandAddress is not null)
        {
            var symbol = SymbolResolver.ResolveEntry(operandAddress.Value, symbols, zeroPageMap);
            if (symbol is not null)
            {
                comments.Insert(0, symbol.Label);
            }
        }

        return comments;
    }

    private static string FormatMemoryAddress(ushort? address) => address is null ? "$????" : Formatting.HexWord(address.Value);
}
