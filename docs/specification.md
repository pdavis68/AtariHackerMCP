# AtariHackerMCP — Design Specification

## Overview

**AtariHackerMCP** is a C# MCP (Model Context Protocol) server that provides a suite of tools for reverse-engineering 8-bit Atari software (XEX binaries, ROM images, cart dumps, ATR disk images). It is intended to be hosted locally and consumed by an LLM client (e.g., Claude Desktop) as a persistent analysis assistant across sessions.

The server is stateful within a session (symbol table, annotations) and persists certain state (symbol table, ZP map, annotations) to a sidecar JSON file alongside the target ROM file.

---

## Technology Stack

| Concern | Choice |
|---|---|
| Language | C# 12 / .NET 8 |
| MCP SDK | `ModelContextProtocol` NuGet package (official C# SDK) |
| Math expressions | `NCalc2` NuGet package |
| Host model | `McpServerHosting` (stdio transport) |
| Persistence | `System.Text.Json` — sidecar `.atarihacker.json` |
| Logging | `Microsoft.Extensions.Logging` to stderr |

---

## Project Structure

```
AtariHackerMCP/
├── AtariHackerMCP.csproj
├── Program.cs                    # Host setup, DI, stdio transport
├── State/
│   ├── RomSession.cs             # Loaded ROM bytes + file path
│   ├── SymbolTable.cs            # Address → label + comment map
│   ├── ZeroPageMap.cs            # ZP address → annotation map
│   └── SessionPersistence.cs     # Load/save sidecar JSON
├── Tools/
│   ├── FileTools.cs              # LoadRom, RomInfo
│   ├── AtrTools.cs               # AtrInfo, LoadAtrFile, LoadAtrBoot
│   ├── HexDumpTool.cs
│   ├── DisassemblerTool.cs       # 6502 disassembler engine
│   ├── CalculatorTool.cs
│   ├── ConversionTools.cs        # HexToDecimal, DecimalToHex
│   ├── SymbolTools.cs            # DefineSymbol, LookupSymbol, ListSymbols
│   ├── XRefTool.cs
│   ├── FindPatternTool.cs
│   ├── StringSearchTool.cs
│   ├── ControlFlowTool.cs
│   └── ZeroPageTool.cs
├── Atari/
│   ├── AtariHardwareMap.cs       # Built-in hardware register labels
│   ├── XexParser.cs              # XEX segment parser
│   ├── AtrParser.cs              # ATR disk image parser + Atari DOS 2.x filesystem
│   └── Opcodes6502.cs            # Full 6502 opcode table
└── AtariHackerMCP.sln
```

---

## State Model

### RomSession

Holds the currently loaded binary in memory. All tools operate against this shared instance. Only one ROM is loaded at a time.

```csharp
public class RomSession
{
    public string FilePath { get; set; }
    public byte[] Data { get; set; }
    public int Length => Data?.Length ?? 0;
    public bool IsLoaded => Data != null;
}
```

### SymbolTable

Maps 16-bit addresses to user-defined or auto-detected labels and optional comments. Initializes with the built-in Atari hardware register map (`AtariHardwareMap`). User-defined symbols override built-ins.

```csharp
public class SymbolEntry
{
    public string Label { get; set; }        // e.g. "sprite_draw"
    public string? Comment { get; set; }     // e.g. "called from main loop"
    public bool IsHardware { get; set; }     // true = from built-in map
    public bool IsUserDefined { get; set; }
}

public class SymbolTable : Dictionary<ushort, SymbolEntry> { }
```

### ZeroPageMap

Same structure as SymbolTable but scoped to `$00–$FF`. Displayed and managed separately.

### SessionPersistence

Sidecar file: `<romfilename>.atarihacker.json`. Saved automatically on any mutation (symbol add/edit/delete, ZP annotation). Loaded automatically when the ROM is loaded if the sidecar exists.

```json
{
  "romPath": "/roms/alibaba.xex",
  "symbols": {
    "0x3F00": { "label": "sprite_draw", "comment": "tile blitter" },
    "0xD000": { "label": "HPOSP0", "isHardware": true }
  },
  "zeroPage": {
    "0x42": { "label": "player_lives", "comment": "" }
  }
}
```

---

## Tool Specifications

All tools are implemented as static methods decorated with `[McpServerTool]` and `[Description(...)]`. They receive injected services (`RomSession`, `SymbolTable`, `ZeroPageMap`) via DI. All tools return `string` (plain text or structured text for LLM consumption).

Tools that require a ROM loaded first check `RomSession.IsLoaded` and return a descriptive error string if not.

---

### 1. `LoadRom`

**Category:** File  
**Description:** Load a ROM or XEX binary file into the session. Automatically loads the sidecar symbol file if present, and runs `RomInfo` internally to return a summary.

**Parameters:**

| Name | Type | Required | Description |
|---|---|---|---|
| `filePath` | string | yes | Absolute or relative path to the binary file |

**Returns:** Summary string including file size, detected format, XEX segments (if applicable), run address, init address, and whether a sidecar was loaded.

**Behavior:**
- Reads all bytes into `RomSession.Data`
- Runs XEX detection (checks for `$FF $FF` header)
- Loads sidecar if present
- Initializes symbol table with `AtariHardwareMap` defaults (if no sidecar)

---

### 2. `RomInfo`

**Category:** File  
**Description:** Display structural information about the currently loaded binary.

**Parameters:** None

**Returns:** Formatted report including:
- File path and size
- Format: Raw binary, XEX, or Unknown
- For XEX: segment list with load address ranges and lengths, init address, run address
- Entry point (run address as both hex and symbol-resolved name if defined)

---

### 3. `HexDump`

**Category:** Inspection  
**Description:** Produce a classic 16-byte-wide hex dump with ASCII representation. Offsets are shown as both file offset and memory address (if a base address can be inferred from XEX segments).

**Parameters:**

| Name | Type | Required | Description |
|---|---|---|---|
| `offset` | string | yes | File offset as decimal or hex (e.g. `0x1A00` or `6656`) |
| `numBytes` | int | yes | Number of bytes to dump |
| `startAddress` | string | no | Override display address (hex). Inferred from XEX if omitted. |

**Returns:** Formatted hex dump. Example:

```
Offset    Address   00 01 02 03 04 05 06 07 08 09 0A 0B 0C 0D 0E 0F  ASCII
--------  --------  -----------------------------------------------  ----------------
00001A00  $3F00     A9 03 85 42 A9 00 85 43 20 F0 3F 4C 00 3F ??     ..B..C .?L.?
```

**Notes:**
- Non-printable bytes shown as `.` in ASCII column
- ATASCII: optionally flag bytes in the $60–$7F range that are ATASCII-specific

---

### 4. `Disassemble`

**Category:** Disassembly  
**Description:** Disassemble 6502 machine code from the loaded ROM starting at a given file offset.

**Parameters:**

| Name | Type | Required | Description |
|---|---|---|---|
| `offset` | string | yes | File offset (decimal or hex) |
| `numBytes` | int | yes | Number of bytes to disassemble |
| `startAddress` | string | no | Memory address for the first byte (hex). Inferred from XEX if omitted. |

**Returns:** Disassembly listing. Example:

```
$3F00  A9 03        LDA #$03
$3F02  85 42        STA $42          ; player_lives
$3F04  A9 00        LDA #$00
$3F06  85 43        STA $43
$3F08  20 F0 3F     JSR $3FF0        ; sprite_draw
$3F0B  4C 00 3F     JMP $3F00        ; main_loop
```

**Notes:**
- All address operands resolved against SymbolTable + ZeroPageMap + AtariHardwareMap
- Resolved symbols appended as inline comments
- Unrecognized opcodes (illegal/undocumented) shown as `.db $XX`
- Full NMOS 6502 opcode set supported (151 official + common illegals flagged)

**Internal engine:** Pure C# implementation in `Opcodes6502.cs`. Each opcode entry carries mnemonic, addressing mode, and byte length. No third-party disassembler library required — the table is small enough to hand-code.

---

### 5. `Calculate`

**Category:** Utility  
**Description:** Evaluate a mathematical expression string using NCalc2. Supports hex literals, bitwise operators, and standard arithmetic.

**Parameters:**

| Name | Type | Required | Description |
|---|---|---|---|
| `expression` | string | yes | Expression string, e.g. `"0x3F00 + 0x80"` or `"(144 * 8) & 0xFF"` |

**Returns:** Result as both decimal and hex. Example: `Result: 16256 ($3F80)`

**Notes:**
- NCalc2 handles operator precedence, bitwise ops (`&`, `|`, `^`, `<<`, `>>`)
- Hex literals accepted as `0x...` prefix (pre-processed before NCalc evaluation if needed)

---

### 6. `HexToDecimal`

**Category:** Utility  
**Description:** Convert a hexadecimal value to decimal.

**Parameters:**

| Name | Type | Required | Description |
|---|---|---|---|
| `hex` | string | yes | Hex value with or without `$` or `0x` prefix |

**Returns:** `$3F00 = 16128`

---

### 7. `DecimalToHex`

**Category:** Utility  
**Description:** Convert a decimal integer to hexadecimal.

**Parameters:**

| Name | Type | Required | Description |
|---|---|---|---|
| `value` | int | yes | Decimal integer |

**Returns:** `16128 = $3F00`

---

### 8. `DefineSymbol`

**Category:** Symbol Table  
**Description:** Add or update a named label for a memory address in the symbol table.

**Parameters:**

| Name | Type | Required | Description |
|---|---|---|---|
| `address` | string | yes | Memory address (hex), e.g. `$3F00` |
| `label` | string | yes | Identifier to use as label. Must be valid identifier characters. |
| `comment` | string | no | Optional annotation comment |

**Returns:** Confirmation string. Sidecar is written immediately.

---

### 9. `RemoveSymbol`

**Category:** Symbol Table  
**Description:** Remove a user-defined symbol. Hardware symbols cannot be removed (only shadowed).

**Parameters:**

| Name | Type | Required | Description |
|---|---|---|---|
| `address` | string | yes | Address of symbol to remove |

**Returns:** Confirmation or error if address not found / is hardware-only.

---

### 10. `LookupSymbol`

**Category:** Symbol Table  
**Description:** Look up the symbol entry for a given address.

**Parameters:**

| Name | Type | Required | Description |
|---|---|---|---|
| `address` | string | yes | Memory address |

**Returns:** Label, comment, and whether it's hardware or user-defined. Returns "No symbol defined" if not found.

---

### 11. `ListSymbols`

**Category:** Symbol Table  
**Description:** List all symbols in the symbol table, optionally filtered.

**Parameters:**

| Name | Type | Required | Description |
|---|---|---|---|
| `includeHardware` | bool | no | Include built-in hardware register labels. Default: `false` |
| `filter` | string | no | Optional substring filter on label name |

**Returns:** Sorted address listing with labels and comments.

---

### 12. `AnnotateZeroPage`

**Category:** Zero Page  
**Description:** Add or update an annotation for a zero page address (`$00–$FF`).

**Parameters:**

| Name | Type | Required | Description |
|---|---|---|---|
| `address` | string | yes | ZP address, e.g. `$42` |
| `label` | string | yes | Label |
| `comment` | string | no | Usage note |

**Returns:** Confirmation. Sidecar written immediately.

---

### 13. `ShowZeroPageMap`

**Category:** Zero Page  
**Description:** Display all annotated zero page addresses in a formatted table.

**Parameters:**

| Name | Type | Required | Description |
|---|---|---|---|
| `showUnannotated` | bool | no | If true, show a hex dump of all 256 ZP bytes alongside annotations. Default: false |

**Returns:** Formatted table of annotated ZP entries. If `showUnannotated`, includes a side-by-side hex dump view.

---

### 14. `XRef`

**Category:** Analysis  
**Description:** Find all locations in the ROM that reference a given address — JSR, JMP, branches, absolute reads/writes (LDA/STA/etc. absolute and zero-page).

**Parameters:**

| Name | Type | Required | Description |
|---|---|---|---|
| `address` | string | yes | Target address to cross-reference |

**Returns:** List of referencing locations with the instruction type at each site. Example:

```
Cross-references to $3FF0 (sprite_draw):
  $3F08  JSR $3FF0
  $4120  JSR $3FF0
  $4388  JMP $3FF0

Cross-references to $42 (player_lives):
  $3F02  STA $42
  $3F20  LDA $42
  $41A0  DEC $42
```

**Notes:**
- Scans entire ROM byte-by-byte looking for address operands
- Two-byte addresses: checks both lo/hi byte pairs as they would appear in little-endian 6502 encoding
- One-byte (ZP) addresses: matches single-byte operands in ZP addressing modes
- Outputs file offset and resolved memory address for each hit

---

### 15. `FindPattern`

**Category:** Analysis  
**Description:** Search the ROM for a byte pattern, with optional wildcard bytes.

**Parameters:**

| Name | Type | Required | Description |
|---|---|---|---|
| `pattern` | string | yes | Space-separated hex bytes. Use `??` for wildcard. E.g. `"A9 ?? 85 ??"` |
| `maxResults` | int | no | Maximum number of matches to return. Default: 50 |

**Returns:** List of file offsets and corresponding memory addresses (from XEX map if available) where the pattern matches. Example:

```
Pattern: A9 ?? 85 ??
Found 7 match(es):

  File offset $1A00  →  Memory $3F00  :  A9 03 85 42
  File offset $1A40  →  Memory $3F40  :  A9 00 85 43
  ...
```

---

### 16. `FindStrings`

**Category:** Analysis  
**Description:** Search the ROM for runs of printable ASCII or ATASCII characters.

**Parameters:**

| Name | Type | Required | Description |
|---|---|---|---|
| `minLength` | int | no | Minimum string length to report. Default: 4 |
| `encoding` | string | no | `"ascii"` or `"atascii"`. Default: `"ascii"` |
| `filter` | string | no | Optional substring filter on found strings |

**Returns:** List of found strings with file offset and memory address. Example:

```
Strings found (ASCII, minLen=4):

  $0200 / $2400  "ALI BABA"
  $0240 / $2440  "GAME OVER"
  $0260 / $2460  "(C)1982 QUALITY"
```

**Notes:**
- ATASCII mode: maps Atari internal screen codes to printable chars before matching
- Inverse-video characters (bit 7 set) optionally included with a `~` prefix indicator

---

### 17. `TraceControlFlow`

**Category:** Analysis  
**Description:** Statically trace execution from a starting address, following JMP, JSR, and branch instructions to build a call/branch graph. Does not emulate — conditional branches are followed both ways.

**Parameters:**

| Name | Type | Required | Description |
|---|---|---|---|
| `address` | string | yes | Starting memory address |
| `maxDepth` | int | no | Maximum call depth to trace. Default: 5 |
| `maxInstructions` | int | no | Instruction budget before halting. Default: 500 |

**Returns:** Indented call/branch graph. Example:

```
$3F00 (main_loop)
  $3F08  JSR → $3FF0 (sprite_draw)
    $3FF0  JSR → $4100 (blit_tile)
    $4010  RTS
  $3F0B  JMP → $3F00 (main_loop)  [loop]
  Branch targets from $3F00:
    BEQ → $3F30
    BNE → $3F40
```

**Notes:**
- Already-visited addresses are noted as `[loop]` or `[visited]` rather than re-expanded
- RTS/RTI terminate a branch
- BRK terminates with a note
- Graph is text-indented for readability; depth-first traversal

---

### 18. `AtrInfo`

**Category:** Disk Image  
**Description:** Display structural information about an ATR disk image file without loading it into the session. Shows geometry and lists the Atari DOS 2.x / MyDOS directory.

**Parameters:**

| Name | Type | Required | Description |
|---|---|---|---|
| `filePath` | string | yes | Path to the `.atr` file |

**Returns:** Formatted report including:
- ATR signature validation
- Disk density: Single (SD, 90KB), Enhanced (ED, 130KB), or Double (DD, 180KB+)
- Sector size: 128 or 256 bytes
- Total sector count
- VTOC free sector count
- Directory listing: filename, extension, sector count, file size, starting sector, flags (locked, DOS 2 binary, etc.)

**Example output:**
```
ATR Disk Image: /roms/alibaba.atr
Density  : Single (SD)
Sectors  : 720 × 128 bytes  =  90,624 bytes
Free     : 402 sectors

Directory:
  #  Filename     Ext  Sectors  Bytes   Start  Flags
  0  ALIBABA      COM      118  15104    0009  [binary]
  1  AUTORUN      SYS        3    384    0127  [binary]
```

---

### 19. `LoadAtrFile`

**Category:** Disk Image  
**Description:** Extract a named file from an ATR disk image, load the reassembled bytes into `RomSession`, and return a `RomInfo`-style summary. After this call all existing analysis tools operate on the extracted file exactly as if `LoadRom` had been used.

**Parameters:**

| Name | Type | Required | Description |
|---|---|---|---|
| `filePath` | string | yes | Path to the `.atr` file |
| `fileName` | string | yes | Atari DOS filename to extract, with or without extension. Case-insensitive. E.g. `"ALIBABA.COM"` or `"ALIBABA"` |

**Returns:** On success, the same summary as `RomInfo` (format detection, XEX segments, run/init address, sidecar status). On failure, an `ERROR:` string.

**Behavior:**
- Reads the ATR directory; resolves `fileName` case-insensitively.
- Follows the Atari DOS 2.x sector-chain to reassemble the file's bytes in order.
- Strips the 3-byte sector-chain header from data sectors before concatenation.
- Loads the resulting byte array into `RomSession` with the synthetic path `<atrPath>/<FILENAME>` for sidecar naming.
- Runs XEX detection and segment parsing on the extracted bytes.
- Loads sidecar if present for the synthetic path.

---

### 20. `LoadAtrBoot`

**Category:** Disk Image  
**Description:** Extract the boot sectors (sectors 1–3, 384 bytes) from an ATR disk image and load them into `RomSession` with base address `$0700`. Useful for analysing disk boot loaders.

**Parameters:**

| Name | Type | Required | Description |
|---|---|---|---|
| `filePath` | string | yes | Path to the `.atr` file |

**Returns:** Confirmation with byte count and base address, followed by a hex dump of the boot sectors.

**Behavior:**
- Reads sectors 1–3 (each 128 bytes regardless of disk density — Atari DOS convention).
- Loads the 384-byte blob into `RomSession` with `startAddress = $0700`.
- Does not attempt XEX detection (boot sector is raw 6502, not XEX format).
- Sets `RomSession.BaseAddress = 0x0700` so disassembler and hex dump tools display correct memory addresses.

---

## ATR Parser (`AtrParser.cs`)

Parses Atari ATR disk image format and the Atari DOS 2.x / MyDOS filesystem it contains.

### ATR Header

| Offset | Length | Field |
|---|---|---|
| `$00` | 2 | Magic: `$96 $02` |
| `$02` | 2 | Image size in 16-byte paragraphs (low word) |
| `$04` | 2 | Sector size: 128 (`$80`) or 256 (`$100`) |
| `$06` | 2 | Image size in paragraphs (high word) |
| `$08` | 1 | Write-protect flag |
| `$09` | 7 | Reserved |

Total header: 16 bytes. Sectors begin at offset 16.

### Density Detection

| Sector count | Sector size | Density |
|---|---|---|
| 720 | 128 | Single (SD, standard) |
| 1040 | 128 | Enhanced / "Medium" (ED) |
| 720 | 256 | Double (DD) |
| > 720 | 256 | MyDOS extended |

**Boot sector quirk:** On 256-byte DD images, sectors 1–3 are always stored as 128-byte sectors. `AtrParser` accounts for this when computing file offsets.

### Filesystem Layout (Atari DOS 2.x)

| Sector | Purpose |
|---|---|
| 1–3 | Boot sectors (boot loader code) |
| 360 | VTOC (Volume Table of Contents) — free sector bitmap |
| 361–368 | Directory (8 sectors × 8 entries = 64 files max) |
| All others | Data sectors |

### Directory Entry (16 bytes each, 8 per sector)

| Offset | Length | Field |
|---|---|---|
| 0 | 1 | Flags byte |
| 1 | 2 | Sector count (little-endian) |
| 3 | 2 | Starting sector number (little-endian) |
| 5 | 8 | Filename (space-padded) |
| 13 | 3 | Extension (space-padded) |

**Flag bits:**
- `$00` — entry never used  
- `$20` — file locked  
- `$42` — DOS 2 binary file  
- `$43` — DOS 2 binary file, locked  
- `$80` — entry deleted  

### Sector Chain Format (data sectors)

Each sector's last 3 bytes are a chain header, not file data:

| Offset from end | Field |
|---|---|
| −3 | File number (bits 6-2) + next sector hi (bits 1-0) |
| −2 | Next sector number (lo byte) |
| −1 | Byte count used in this sector |

A next-sector value of `0` signals the end of the chain.

### Public API

```csharp
public record AtrGeometry(
    int SectorSize,
    int SectorCount,
    string Density  // "SD", "ED", "DD", "Extended"
);

public record AtrDirectoryEntry(
    int Index,
    string FileName,    // "ALIBABA"
    string Extension,   // "COM"
    int StartSector,
    int SectorCount,
    bool IsDeleted,
    bool IsLocked,
    bool IsBinary
);

public static class AtrParser
{
    public static bool IsAtr(byte[] data);
    public static AtrGeometry ParseGeometry(byte[] data);
    public static IReadOnlyList<AtrDirectoryEntry> ReadDirectory(byte[] data);
    public static byte[] ReadSector(byte[] data, AtrGeometry geo, int sectorNumber); // 1-based
    public static byte[] ExtractFile(byte[] data, AtrGeometry geo, AtrDirectoryEntry entry);
    public static byte[] ExtractBootSectors(byte[] data); // always 384 bytes
    public static int FreeSegmentCount(byte[] data, AtrGeometry geo); // from VTOC
}
```

---

## Atari Hardware Map (`AtariHardwareMap.cs`)

Pre-populated with well-known Atari 8-bit hardware register addresses. This list is initialized into the SymbolTable at startup as hardware entries. Key ranges:

| Range | Chip | Examples |
|---|---|---|
| `$D000–$D01F` | GTIA | `HPOSP0`–`HPOSP3`, `SIZEP0`–`SIZEP3`, `GRAFP0`–`GRAFP3`, `COLPM0`–`COLPF3`, `PRIOR`, `GRACTL`, `CONSOL` |
| `$D200–$D21F` | POKEY | `AUDF1`–`AUDF4`, `AUDC1`–`AUDC4`, `AUDCTL`, `KBCODE`, `RANDOM`, `IRQEN`, `IRQST`, `SKCTL` |
| `$D300–$D303` | PIA | `PORTA`, `PORTB`, `PACTL`, `PBCTL` |
| `$D400–$D41F` | ANTIC | `DMACTL`, `CHACTL`, `DLISTL`/`DLISTH`, `HSCROL`, `VSCROL`, `PMBASE`, `CHBASE`, `WSYNC`, `VCOUNT`, `NMIEN`, `NMIST` |
| `$C000–$CFFF` | OS ROM shadow | Key OS vectors annotated |

OS zero page and page-two variables (e.g., `SAVMSC`, `SDLSTL`, `STICK0`–`STICK3`) included as ZP hardware entries.

---

## XEX Parser (`XexParser.cs`)

Parses Atari XEX (executable) format:

- Detects `$FF $FF` header
- Reads segment headers: load address lo/hi, end address lo/hi
- Handles special segments: `$02E0/$02E1` (run address), `$02E2/$02E3` (init address)
- Builds a list of `XexSegment` objects: `{ LoadAddress, EndAddress, FileOffset, Length }`
- Exposes a method `ushort FileOffsetToMemoryAddress(int fileOffset)` for use by other tools

---

## Error Handling

All tools follow a consistent error return pattern:

```
ERROR: No ROM is currently loaded. Use LoadRom first.
ERROR: Offset 0x9000 exceeds ROM size (0x4000 bytes).
ERROR: Invalid hex value: '0xGG'.
ERROR: Pattern parse failed at token 'XY'. Use hex bytes or '??' for wildcard.
```

No exceptions propagate to the MCP client — all errors are caught and returned as descriptive strings.

---

## Persistence Behavior

- **Load:** When `LoadRom` is called, if `<romfile>.atarihacker.json` exists, it is deserialized into the SymbolTable and ZeroPageMap. Hardware symbols not present in the sidecar are re-added from `AtariHardwareMap`.
- **Save:** Any mutating tool (`DefineSymbol`, `RemoveSymbol`, `AnnotateZeroPage`) triggers an immediate synchronous write of the sidecar file.
- **Format:** Pretty-printed JSON for human readability and git-friendliness.

---

## DI Registration (`Program.cs`)

```csharp
var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton<RomSession>();
builder.Services.AddSingleton<SymbolTable>(sp =>
{
    var table = new SymbolTable();
    AtariHardwareMap.Populate(table);
    return table;
});
builder.Services.AddSingleton<ZeroPageMap>(sp =>
{
    var map = new ZeroPageMap();
    AtariHardwareMap.PopulateZeroPage(map);
    return map;
});
builder.Services.AddSingleton<SessionPersistence>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
```

---

## Tool Summary

| # | Tool | Category |
|---|---|---|
| 1 | `LoadRom` | File |
| 2 | `RomInfo` | File |
| 3 | `AtrInfo` | Disk Image |
| 4 | `LoadAtrFile` | Disk Image |
| 5 | `LoadAtrBoot` | Disk Image |
| 6 | `HexDump` | Inspection |
| 7 | `Disassemble` | Disassembly |
| 8 | `Calculate` | Utility |
| 9 | `HexToDecimal` | Utility |
| 10 | `DecimalToHex` | Utility |
| 11 | `DefineSymbol` | Symbol Table |
| 12 | `RemoveSymbol` | Symbol Table |
| 13 | `LookupSymbol` | Symbol Table |
| 14 | `ListSymbols` | Symbol Table |
| 15 | `AnnotateZeroPage` | Zero Page |
| 16 | `ShowZeroPageMap` | Zero Page |
| 17 | `XRef` | Analysis |
| 18 | `FindPattern` | Analysis |
| 19 | `FindStrings` | Analysis |
| 20 | `TraceControlFlow` | Analysis |