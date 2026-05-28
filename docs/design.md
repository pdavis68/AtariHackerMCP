# AtariHackerMCP — Design Document

## 1. Introduction

This document describes the detailed software design for **AtariHackerMCP**: a stateful, locally-hosted MCP (Model Context Protocol) server written in C# / .NET 10 that exposes 17 tools for reverse-engineering 8-bit Atari software. An LLM client (e.g., Claude Desktop) connects to the server over stdio and drives analysis sessions interactively.

### 1.1 Goals

- Provide a rich, persistent analysis context for 8-bit Atari binaries (XEX, ROM, cart dumps).
- Expose all analysis capabilities as MCP tools so they are directly callable by an LLM.
- Persist the accumulated reverse-engineering knowledge (symbol table, ZP annotations) automatically in a human-readable sidecar file.
- Require zero external disassembler libraries — all 6502 decoding is self-contained.

### 1.2 Non-Goals

- Emulation or runtime tracing. All analysis is static.
- Support for non-6502 architectures.
- Multi-ROM concurrent sessions.
- GUI or web UI.

---

## 2. Architecture Overview

```
┌──────────────────────────────────────────────────────────┐
│  LLM Client (Claude Desktop / other MCP host)            │
│  Communicates via stdio JSON-RPC (MCP protocol)          │
└───────────────────────────┬──────────────────────────────┘
                            │ stdio (JSON-RPC 2.0)
┌───────────────────────────▼──────────────────────────────┐
│  AtariHackerMCP Process                                   │
│                                                           │
│  ┌─────────────────────────────────────────────────────┐ │
│  │  Program.cs — IHost / DI container / stdio transport │ │
│  └──────────────┬──────────────────────────────────────┘ │
│                 │ injects                                  │
│  ┌──────────────▼──────────────────────────────────────┐ │
│  │  Tool Classes (static methods, [McpServerTool])       │ │
│  │  FileTools · HexDumpTool · DisassemblerTool           │ │
│  │  CalculatorTool · ConversionTools · SymbolTools       │ │
│  │  XRefTool · FindPatternTool · StringSearchTool        │ │
│  │  ControlFlowTool · ZeroPageTool                       │ │
│  └──────────────┬──────────────────────────────────────┘ │
│                 │ reads / writes                           │
│  ┌──────────────▼──────────────────────────────────────┐ │
│  │  Session State (singletons)                           │ │
│  │  RomSession · SymbolTable · ZeroPageMap               │ │
│  │  SessionPersistence                                   │ │
│  └──────────────┬──────────────────────────────────────┘ │
│                 │                                          │
│  ┌──────────────▼──────────────────────────────────────┐ │
│  │  Atari Domain Layer                                   │ │
│  │  Opcodes6502 · XexParser · AtariHardwareMap           │ │
│  └─────────────────────────────────────────────────────┘ │
└──────────────────────────────────────────────────────────┘
                            │
              ┌─────────────▼─────────────┐
              │  File System               │
              │  <rom>.xex                 │
              │  <rom>.xex.atarihacker.json│
              └────────────────────────────┘
```

### 2.1 Transport

The MCP SDK's stdio transport is used exclusively. The server process is launched by the client; stdin/stdout carry JSON-RPC frames. All logging is written to stderr to avoid contaminating the wire protocol.

### 2.2 Concurrency Model

The server is single-session and effectively single-threaded from a state-mutation perspective. The MCP stdio transport processes one request at a time. No locking is required on shared singletons.

---

## 3. Project Structure

```
AtariHackerMCP/
├── AtariHackerMCP.csproj
├── Program.cs
├── State/
│   ├── RomSession.cs
│   ├── SymbolTable.cs
│   ├── ZeroPageMap.cs
│   └── SessionPersistence.cs
├── Tools/
│   ├── FileTools.cs
│   ├── AtrTools.cs
│   ├── HexDumpTool.cs
│   ├── DisassemblerTool.cs
│   ├── CalculatorTool.cs
│   ├── ConversionTools.cs
│   ├── SymbolTools.cs
│   ├── XRefTool.cs
│   ├── FindPatternTool.cs
│   ├── StringSearchTool.cs
│   ├── ControlFlowTool.cs
│   └── ZeroPageTool.cs
├── Atari/
│   ├── AtariHardwareMap.cs
│   ├── XexParser.cs
│   ├── AtrParser.cs
│   └── Opcodes6502.cs
└── docs/
    ├── specification.md
    └── design.md          ← this file
```

---

## 4. Dependency Injection & Host Setup (`Program.cs`)

```csharp
var builder = Host.CreateApplicationBuilder(args);

// Session state singletons
builder.Services.AddSingleton<RomSession>();

builder.Services.AddSingleton<SymbolTable>(sp =>
{
    var table = new SymbolTable();
    AtariHardwareMap.Populate(table);      // seed hardware registers
    return table;
});

builder.Services.AddSingleton<ZeroPageMap>(sp =>
{
    var map = new ZeroPageMap();
    AtariHardwareMap.PopulateZeroPage(map); // seed ZP OS vars
    return map;
});

builder.Services.AddSingleton<SessionPersistence>();

// MCP server wiring
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();   // discovers all [McpServerTool] methods

await builder.Build().RunAsync();
```

`WithToolsFromAssembly()` scans the assembly for static methods carrying `[McpServerTool]`. The MCP SDK reflects parameter types that match registered services and performs constructor/parameter injection automatically.

---

## 5. State Layer

### 5.1 `RomSession`

A simple mutable value object that holds the currently-loaded binary.

```csharp
public class RomSession
{
    public string? FilePath { get; set; }
    public byte[]? Data { get; set; }
    public int Length => Data?.Length ?? 0;
    public bool IsLoaded => Data is not null;
}
```

**Design notes:**
- Only one binary can be loaded at a time. Loading a new ROM replaces the previous state entirely.
- `IsLoaded` is false until `LoadRom` succeeds. All analysis tools must check this guard first.

### 5.2 `SymbolTable`

```csharp
public record SymbolEntry(
    string Label,
    string? Comment,
    bool IsHardware,
    bool IsUserDefined
);

public class SymbolTable : Dictionary<ushort, SymbolEntry> { }
```

**Design notes:**
- Key: 16-bit memory address (`ushort`).
- Hardware entries are seeded at startup by `AtariHardwareMap.Populate()`.
- When a user defines a symbol at an address that already has a hardware entry, the user entry **replaces** the hardware entry in the dictionary. The displacement is recorded by `IsUserDefined = true` / `IsHardware = false`.
- Removing a user-defined symbol at a hardware address restores the hardware entry rather than leaving it empty.
- The symbol table is consulted by `Disassemble`, `XRef`, and `TraceControlFlow` to emit inline comments.

### 5.3 `ZeroPageMap`

Identical in structure to `SymbolTable` but always-restricted to address range `$00–$FF`. Managed and displayed independently of the main symbol table.

```csharp
public class ZeroPageMap : Dictionary<byte, SymbolEntry> { }
```

Seeded by `AtariHardwareMap.PopulateZeroPage()` with well-known Atari OS zero-page variables (`SAVMSC`, `SDLSTL`, `STICK0`–`STICK3`, etc.).

### 5.4 `SessionPersistence`

Responsible for serialization and deserialization of the sidecar file.

**Sidecar path:** `<romFilePath>.atarihacker.json`

```csharp
public class SessionPersistence
{
    private readonly RomSession _session;
    private readonly SymbolTable _symbols;
    private readonly ZeroPageMap _zpMap;

    public void Save();   // called by every mutating tool
    public bool TryLoad(string romPath);  // called by LoadRom
}
```

**Save format (pretty-printed JSON):**

```json
{
  "romPath": "/roms/alibaba.xex",
  "symbols": {
    "0x3F00": { "label": "sprite_draw", "comment": "tile blitter", "isHardware": false, "isUserDefined": true },
    "0xD000": { "label": "HPOSP0", "comment": null, "isHardware": true, "isUserDefined": false }
  },
  "zeroPage": {
    "0x42": { "label": "player_lives", "comment": "", "isHardware": false, "isUserDefined": true }
  }
}
```

**Design notes:**
- Addresses are serialized as `"0xNNNN"` hex strings for readability.
- On load, hardware symbols not present in the sidecar are re-added from `AtariHardwareMap` so the table is always fully populated from the hardware map plus whatever overrides/additions the user saved.
- Writes are synchronous and take place on the calling thread; the file is small enough that async I/O adds no value.

---

## 6. Atari Domain Layer

### 6.1 `Opcodes6502`

A static data table encoding the complete NMOS 6502 instruction set plus common illegal/undocumented opcodes.

```csharp
public enum AddressingMode
{
    Implied, Accumulator,
    Immediate, ZeroPage, ZeroPageX, ZeroPageY,
    Absolute, AbsoluteX, AbsoluteY,
    Indirect, IndirectX, IndirectY,
    Relative
}

public record OpcodeEntry(
    byte OpCode,
    string Mnemonic,
    AddressingMode Mode,
    int Bytes          // 1, 2, or 3
);

public static class Opcodes6502
{
    public static readonly IReadOnlyDictionary<byte, OpcodeEntry> Table;
}
```

**Design notes:**
- 151 official opcodes fully covered.
- Illegal opcodes present in the table but flagged; disassembler emits `.db $XX` for them instead of the mnemonic.
- `Bytes` is used by both the disassembler (to advance the program counter) and the `XRef` scanner (to step to the next instruction boundary).

### 6.2 `XexParser`

Parses Atari XEX (load-format executable) binary structure.

```csharp
public record XexSegment(
    ushort LoadAddress,
    ushort EndAddress,
    int FileOffset,
    int Length
);

public class XexParser
{
    public static bool IsXex(byte[] data);

    public static IReadOnlyList<XexSegment> ParseSegments(byte[] data);

    /// <summary>
    /// Translates a file byte offset to its corresponding memory address
    /// using the XEX segment map. Returns null if no segment covers the offset.
    /// </summary>
    public static ushort? FileOffsetToMemoryAddress(
        IReadOnlyList<XexSegment> segments, int fileOffset);

    /// <summary>
    /// The inverse: translates a memory address to its file offset.
    /// Returns null if the address is not covered by any segment.
    /// </summary>
    public static int? MemoryAddressToFileOffset(
        IReadOnlyList<XexSegment> segments, ushort memAddr);
}
```

**XEX format rules implemented:**
- Header: `$FF $FF` at file offset 0.
- Segment header: 4 bytes — lo-byte of load address, hi-byte of load address, lo-byte of end address, hi-byte of end address. End address is inclusive.
- Special segment addresses: `$02E0/$02E1` → run address (stored, not loaded); `$02E2/$02E3` → init address.
- Segments may be `$FF $FF`-prefixed themselves or appear back-to-back after a single `$FF $FF` header.

### 6.3 `AtrParser`

Parses ATR disk images and the Atari DOS 2.x / MyDOS filesystem encoded within them.

#### ATR Header Layout

```
Offset  Len  Field
$00      2   Magic: $96 $02
$02      2   Image size in 16-byte paragraphs (lo word)
$04      2   Sector size: 128 or 256
$06      2   Image size in paragraphs (hi word)
$08      1   Write-protect flag
$09      7   Reserved
```

Header is always 16 bytes. Sector data begins at offset 16.

#### Density / Geometry Detection

```csharp
public record AtrGeometry(
    int SectorSize,
    int SectorCount,
    string Density  // "SD" | "ED" | "DD" | "Extended"
);
```

| SectorCount | SectorSize | Density |
|---|---|---|
| 720 | 128 | SD (Single) |
| 1040 | 128 | ED (Enhanced/Medium) |
| 720 | 256 | DD (Double) |
| > 720 | 256 | Extended (MyDOS) |

**Boot quirk:** On DD images, sectors 1–3 are stored as 128-byte sectors regardless of the stated sector size. `ReadSector` handles this transparently.

#### Sector Addressing

```csharp
private static int SectorFileOffset(AtrGeometry geo, int sectorNumber)
{
    // 1-based sector numbers; sectors 1-3 on DD are always 128 bytes
    bool isBootSector = sectorNumber <= 3 && geo.SectorSize == 256;
    int bootBytes = isBootSector ? 0 : 3 * 128;  // first 3 sectors always 128
    int dataOffset = sectorNumber <= 3
        ? (sectorNumber - 1) * 128
        : bootBytes + (sectorNumber - 4) * geo.SectorSize;
    return 16 + dataOffset;  // 16-byte ATR header
}
```

#### Atari DOS 2.x Filesystem

**VTOC** (sector 360): byte offset 10 contains the free-sector bitmap starting at offset 10; total free count bytes at offsets 3–4 (little-endian).

**Directory** (sectors 361–368): Each sector holds 8 directory entries of 16 bytes each (64 files maximum).

**Directory entry:**

```csharp
public record AtrDirectoryEntry(
    int Index,           // 0-63
    string FileName,     // trimmed, e.g. "ALIBABA"
    string Extension,    // trimmed, e.g. "COM"
    int StartSector,
    int SectorCount,
    bool IsDeleted,
    bool IsLocked,
    bool IsBinary
);
```

Flag byte values: `$00` = never used, `$20` = locked, `$42` = DOS 2 binary, `$80` = deleted.

#### Sector Chain & File Extraction

Each data sector's last 3 bytes carry chain metadata, not file content:

```
byte[-3]  bits 7-2: file number, bits 1-0: next sector (hi)
byte[-2]  next sector number (lo)
byte[-1]  count of valid data bytes in this sector
```

Next sector == 0 marks end-of-chain.

```csharp
public static byte[] ExtractFile(byte[] data, AtrGeometry geo, AtrDirectoryEntry entry)
{
    var result = new List<byte>();
    int sector = entry.StartSector;
    while (sector != 0)
    {
        byte[] sec = ReadSector(data, geo, sector);
        int dataBytes = sec[sec.Length - 1];      // last byte = valid byte count
        int nextHi    = sec[sec.Length - 3] & 0x03;
        int nextLo    = sec[sec.Length - 2];
        int next      = (nextHi << 8) | nextLo;
        result.AddRange(sec[..(sec.Length - 3)][..dataBytes]);
        sector = next;
    }
    return result.ToArray();
}
```

#### Public API Summary

```csharp
public static class AtrParser
{
    public static bool IsAtr(byte[] data);
    public static AtrGeometry ParseGeometry(byte[] data);
    public static byte[] ReadSector(byte[] data, AtrGeometry geo, int sectorNumber);
    public static IReadOnlyList<AtrDirectoryEntry> ReadDirectory(byte[] data);
    public static byte[] ExtractFile(byte[] data, AtrGeometry geo, AtrDirectoryEntry entry);
    public static byte[] ExtractBootSectors(byte[] data);   // sectors 1-3 → 384 bytes
    public static int FreeSegmentCount(byte[] data, AtrGeometry geo);
}
```

---

### 6.4 `AtariHardwareMap`

Hand-coded static class. Provides two static methods:

```csharp
public static class AtariHardwareMap
{
    public static void Populate(SymbolTable table);
    public static void PopulateZeroPage(ZeroPageMap map);
}
```

Coverage:

| Range | Chip |
|---|---|
| `$D000–$D01F` | GTIA (read and write shadows separately named) |
| `$D200–$D21F` | POKEY |
| `$D300–$D303` | PIA |
| `$D400–$D41F` | ANTIC |
| `$C000–$CFFF` | Selected OS ROM entry points and vectors |
| `$0000–$00FF` (ZP) | OS zero-page variables: `SAVMSC`, `SDLSTL`, `STICK0`–`STICK3`, `STRIG0`–`STRIG3`, `COLOR0`–`COLOR4`, etc. |

---

## 7. Tool Layer

### 7.1 Tool Pattern

Every tool is a static method in a tool class. The method signature receives injected services as parameters (the MCP SDK performs DI resolution), plus any declared JSON parameters for the LLM:

```csharp
[McpServerTool, Description("Load a ROM or XEX binary file.")]
public static string LoadRom(
    RomSession session,
    SymbolTable symbols,
    ZeroPageMap zpMap,
    SessionPersistence persistence,
    [Description("Absolute or relative path to the binary.")] string filePath)
{ ... }
```

**Guard pattern (used by all analysis tools):**

```csharp
if (!session.IsLoaded)
    return "ERROR: No ROM is currently loaded. Use LoadRom first.";
```

**Error return contract:** All exceptions caught at tool level; returned as `"ERROR: <message>"` strings. Nothing propagates to the MCP layer.

### 7.2 Tool Catalogue

#### 7.2.1 `AtrInfo` (`AtrTools.cs`)

**Steps:**
1. `File.ReadAllBytes(filePath)` into a local buffer (not into `RomSession`).
2. `AtrParser.IsAtr()` — return an error if false.
3. `AtrParser.ParseGeometry()` → format density, sector size, total sectors, total image bytes.
4. `AtrParser.ReadDirectory()` → format each non-deleted entry as a table row.
5. `AtrParser.FreeSegmentCount()` → append free-sector line.

**Design note:** `AtrInfo` is read-only and never touches `RomSession`. It can safely be called at any time, even without a ROM loaded.

---

#### 7.2.2 `LoadAtrFile` (`AtrTools.cs`)

**Steps:**
1. `File.ReadAllBytes(filePath)` into local buffer.
2. `AtrParser.IsAtr()` guard.
3. `AtrParser.ReadDirectory()` → case-insensitive match on `"FILENAME"` or `"FILENAME.EXT"`.
4. `AtrParser.ExtractFile()` → reassembled byte array.
5. Build synthetic path: `"<atrPath>/<FILENAME>.<EXT>"` for sidecar naming.
6. Assign extracted bytes to `RomSession.Data`, synthetic path to `RomSession.FilePath`.
7. Run `XexParser` detection → populate `RomSession.Segments`, `RunAddress`, `InitAddress`.
8. `persistence.TryLoad(syntheticPath)`.
9. Run `RomInfo` internally and return its output prefixed with extraction confirmation.

**Error cases:**
- File not found in directory → `ERROR: File "NOTHERE.COM" not found in ATR directory.`
- Deleted entry matched → `ERROR: File "OLD.COM" exists but is deleted.`
- ATR validation fails → `ERROR: Not a valid ATR image.`

---

#### 7.2.3 `LoadAtrBoot` (`AtrTools.cs`)

**Steps:**
1. `File.ReadAllBytes(filePath)` → `AtrParser.IsAtr()` guard.
2. `AtrParser.ExtractBootSectors()` → 384-byte array.
3. Set `RomSession.Data`, `RomSession.FilePath` (synthetic: `"<atrPath>/BOOT"`), `RomSession.BaseAddress = 0x0700`.
4. Skip XEX detection (boot sectors are raw 6502).
5. `persistence.TryLoad(syntheticPath)`.
6. Return confirmation + 384-byte hex dump via `HexDump` (offset 0, 384 bytes, startAddress `$0700`).

**`RomSession` extension for base address:**
```csharp
public ushort? BaseAddress { get; set; }  // used when no XEX segment map is available
```
When `BaseAddress` is set and no XEX segments exist, `XexAddressResolver` uses `memAddr = fileOffset + BaseAddress` for all address display in `HexDump` and `Disassemble`.

---

#### 7.2.4 `LoadRom` (`FileTools.cs`)

**Steps:**
1. `File.ReadAllBytes(filePath)` → assign to `RomSession.Data` + `FilePath`.
2. Run `XexParser.IsXex()` and, if true, `XexParser.ParseSegments()` → cache segment list on `RomSession`.
3. Call `persistence.TryLoad(filePath)`:
   - If sidecar found: merge symbols/ZP into tables (user entries override hardware).
   - If no sidecar: tables remain at hardware-seeded defaults.
4. Call `RomInfo` internally and return its output prefixed with load confirmation.

**`RomSession` extension** (to cache segment list):
```csharp
public IReadOnlyList<XexSegment>? Segments { get; set; }
public ushort? RunAddress { get; set; }
public ushort? InitAddress { get; set; }
```

#### 7.2.5 `RomInfo` (`FileTools.cs`)

Returns a multi-line formatted report. For XEX files, iterates `RomSession.Segments` and formats each as:

```
Segment 1: $3F00 – $3FFF  (256 bytes, file offset $0004)
Segment 2: $4000 – $4FFF  (4096 bytes, file offset $0106)
Run address : $3F00  (main_loop)
Init address: --
```

Symbol resolution for the run address uses `SymbolTable`.

#### 7.2.6 `HexDump` (`HexDumpTool.cs`)

**Address argument parsing** (shared helper, see §7.3):
- Strips `$` or `0x` prefix, parses as hex.

**Rendering algorithm:**
```
for each row of 16 bytes:
    col1: file offset (8 hex digits)
    col2: memory address (resolved via XexParser or startAddress override)
    col3: hex bytes, space-separated, padded to 47 chars
    col4: ASCII — bytes $20–$7E as-is, all others as '.'
```

Memory address column shows `--------` when the offset falls outside all XEX segments and no `startAddress` was given.

#### 7.2.7 `Disassemble` (`DisassemblerTool.cs`)

**Main loop:**

```csharp
int pos = fileOffset;
int end = fileOffset + numBytes;
while (pos < end)
{
    byte op = data[pos];
    if (!Opcodes6502.Table.TryGetValue(op, out var entry))
    {
        // illegal — emit .db $XX, advance 1
    }
    else
    {
        ushort memAddr = Resolve(pos);
        string operand = FormatOperand(entry, data, pos, memAddr, symbols, zpMap);
        string comment = ResolveComment(entry, data, pos, symbols, zpMap);
        EmitLine(memAddr, data[pos..pos+entry.Bytes], entry.Mnemonic, operand, comment);
        pos += entry.Bytes;
    }
}
```

**Operand formatting by addressing mode:**

| Mode | Format |
|---|---|
| Implied / Accumulator | (none) |
| Immediate | `#$XX` |
| ZeroPage | `$XX` (+ symbol if in ZeroPageMap) |
| ZeroPageX/Y | `$XX,X` / `$XX,Y` |
| Absolute | `$XXXX` (+ symbol if in SymbolTable) |
| AbsoluteX/Y | `$XXXX,X` / `$XXXX,Y` |
| Indirect | `($XXXX)` |
| IndirectX/Y | `($XX,X)` / `($XX),Y` |
| Relative | target = PC + 2 + signed offset → `$XXXX` + symbol |

**Symbol comment injection:** If an operand address resolves to a symbol, append `; <label>` as a comment. If the instruction already has a programmer comment (from `SymbolTable.Comment` on the instruction address itself), append that too.

#### 7.2.8 `Calculate` (`CalculatorTool.cs`)

Pre-processes the expression:
1. Replace `$NNNN` Atari-style hex literals with `0xNNNN` (NCalc2 understands `0x` prefix).
2. Pass to `new Expression(expr).Evaluate()`.
3. Format result as `Result: <decimal> ($<HEX>)`.

Error: if NCalc throws, return `ERROR: Expression evaluation failed: <exception message>`.

#### 7.2.9 `HexToDecimal` / `DecimalToHex` (`ConversionTools.cs`)

Trivial converters. `HexToDecimal` strips `$`/`0x` prefix then calls `Convert.ToInt64(s, 16)`.

#### 7.2.10 `DefineSymbol` / `RemoveSymbol` / `LookupSymbol` / `ListSymbols` (`SymbolTools.cs`)

**`DefineSymbol`:**
1. Parse address (shared helper).
2. Validate label: `Regex.IsMatch(label, @"^[A-Za-z_][A-Za-z0-9_]*$")`.
3. Upsert into `SymbolTable` with `IsUserDefined = true`, `IsHardware = false`.
4. Call `persistence.Save()`.

**`RemoveSymbol`:**
1. Look up address in `SymbolTable`.
2. If entry is `IsHardware = true` and `IsUserDefined = false` → return error (hardware-only, cannot remove).
3. If user-defined override exists: remove user entry, re-insert the hardware entry from `AtariHardwareMap` if one exists for that address.
4. Call `persistence.Save()`.

**`ListSymbols`:**
- Filter: `includeHardware` controls whether `IsHardware=true, IsUserDefined=false` entries appear.
- Optional `filter` substring match on `Label` (case-insensitive).
- Sort by address ascending.
- Column format: `$XXXX  label  ; comment`

#### 7.2.11 `AnnotateZeroPage` / `ShowZeroPageMap` (`ZeroPageTool.cs`)

`AnnotateZeroPage` mirrors `DefineSymbol` but targets `ZeroPageMap` with `byte` keys.

`ShowZeroPageMap`:
- Default: table of annotated entries only.
- `showUnannotated = true`: 16×16 grid of all 256 ZP byte values from ROM (first 256 bytes), with annotation labels shown in a right-hand column for annotated addresses.

#### 7.2.9 `XRef` (`XRefTool.cs`)

**Scan algorithm:**

```
Walk all instruction boundaries (use Opcodes6502 to step correctly):
  For each instruction:
    If mode is Absolute or AbsoluteX/Y/Indirect:
      operand = little-endian ushort at pos+1
      if operand == targetAddress → record hit
    If mode is ZeroPage or ZP variants:
      operand = byte at pos+1
      if operand == (byte)targetAddress → record hit (ZP scan only valid if target < $100)
    If mode is Relative:
      target = PC + 2 + (sbyte)data[pos+1]
      if target == targetAddress → record hit
```

Output groups hits by instruction type (JSR, JMP, branch, read, write, etc.).

**Design note:** The scanner walks instruction boundaries rather than scanning raw bytes, to avoid false positives where an address appears as data inside an operand of a different instruction. To walk correctly, it must start from a known entry point or the beginning of each XEX segment.

#### 7.2.13 `FindPattern` (`FindPatternTool.cs`)

**Pattern parsing:** Split on whitespace; each token is a 2-char hex byte or `??`. Build a `(byte value, bool isWildcard)[]`.

**Search:** Simple sliding window; at each byte position compare pattern. Wildcards always match.

**Address resolution:** `XexParser.FileOffsetToMemoryAddress()` for each hit — show as `Memory $XXXX` or `Memory (unknown)` if outside all segments.

#### 7.2.14 `FindStrings` (`StringSearchTool.cs`)

**ASCII mode:**
- Scan for runs of bytes in `$20–$7E`.
- Collect runs ≥ `minLength`.

**ATASCII mode:**
- Atari internal screen codes: map character codes $00–$3F → uppercase letters/digits/special as per the ATASCII table.
- Bytes with bit 7 set are inverse-video; collect but prefix with `~` in output.
- Standard printable ASCII bytes ($20–$7E) also match.

**Filter:** Post-match substring filter (case-insensitive) on the decoded string value.

#### 7.2.15 `TraceControlFlow` (`ControlFlowTool.cs`)

**Data structures:**

```csharp
record TraceNode(ushort Address, string? Label, List<TraceEdge> Edges);
record TraceEdge(ushort From, ushort Target, string InstructionType); // "JSR", "JMP", "BEQ", etc.
```

**DFS algorithm:**

```
Queue<(ushort addr, int depth)> worklist;
HashSet<ushort> visited;

Push startAddress at depth 0.
While worklist not empty and instructionBudget > 0:
  Resolve file offset from addr (XEX map or raw).
  Disassemble instructions at addr until:
    - JSR: recurse into target (if depth < maxDepth and not visited), mark JMP edge
    - JMP absolute: follow target, mark JMP edge, stop current path (unconditional)
    - JMP indirect: emit [indirect, cannot trace statically], stop
    - Bxx: push both fall-through and branch target as separate paths
    - RTS/RTI: stop current path
    - BRK: stop, emit [BRK]
    - visited target: emit [loop] or [visited] annotation, stop
```

**Output formatting:** Depth-first indented text. Each nesting level adds 2 spaces. JSR-descended nodes are indented under their call site.

---

## 8. Shared Helpers

These are private or internal utilities used by multiple tools.

### 8.1 `AddressParser`

```csharp
internal static class AddressParser
{
    /// Parses hex (with $, 0x, or bare) or decimal. Returns ushort.
    public static ushort ParseAddress(string input);

    /// Same but returns int (for file offsets which may exceed 16-bit).
    public static int ParseOffset(string input);
}
```

Throws `FormatException` with a descriptive message on bad input; caller catches and returns `"ERROR: ..."`.

### 8.2 `SymbolResolver`

```csharp
internal static class SymbolResolver
{
    /// Returns label string for a 16-bit address, checking SymbolTable then ZeroPageMap.
    public static string? Resolve(ushort address, SymbolTable symbols, ZeroPageMap zpMap);
}
```

### 8.3 `XexAddressResolver`

Wraps `XexParser.FileOffsetToMemoryAddress` and `MemoryAddressToFileOffset` with fallback for raw binary. Fallback priority:
1. XEX segment map (if `RomSession.Segments` is populated)
2. `RomSession.BaseAddress` (set by `LoadAtrBoot` or explicit `startAddress` parameter)
3. Identity (memory address = file offset) if neither is available

---

## 9. Error Handling Strategy

All tool methods follow this pattern:

```csharp
try
{
    // guard check
    if (!session.IsLoaded) return "ERROR: No ROM loaded. Use LoadRom first.";

    // parse inputs
    var offset = AddressParser.ParseOffset(offsetStr);  // may throw FormatException

    // validation
    if (offset >= session.Length)
        return $"ERROR: Offset 0x{offset:X} exceeds ROM size (0x{session.Length:X} bytes).";

    // ... do work ...
    return result;
}
catch (FormatException ex)
{
    return $"ERROR: {ex.Message}";
}
catch (Exception ex)
{
    return $"ERROR: Unexpected error: {ex.Message}";
}
```

Rationale: The MCP SDK will propagate uncaught exceptions as JSON-RPC error responses to the client, which degrades the LLM experience. Descriptive string errors are far more useful for an LLM to reason about and route to the user.

---

## 10. Output Formatting Conventions

All tools return plain text strings. Conventions:

| Construct | Format |
|---|---|
| Hex address (16-bit) | `$XXXX` (uppercase, 4 digits) |
| Hex byte | `$XX` (uppercase, 2 digits) |
| File offset column | 8-digit hex, no prefix, padded with zeros |
| Decimal fallback | shown in parentheses after hex where relevant |
| Separator lines | `────────` or `--------` (depends on context) |
| Error prefix | `ERROR: ` |
| Multi-line results | `\n`-separated lines; no trailing newline |

---

## 11. NuGet Dependencies

| Package | Version | Purpose |
|---|---|---|
| `ModelContextProtocol` | latest stable | MCP server SDK, tool discovery, stdio transport |
| `Microsoft.Extensions.Hosting` | .NET 10 built-in | IHost, DI container |
| `Microsoft.Extensions.Logging` | .NET 10 built-in | stderr logging |
| `NCalc2` | latest stable | Mathematical expression evaluation |
| `System.Text.Json` | .NET 10 built-in | Sidecar JSON serialization |

No third-party disassembler library is used. `Opcodes6502.cs` is self-contained.

---

## 12. Testing Considerations

While a full test suite is out of scope for this document, the following units are most important to test in isolation:

| Component | Test Focus |
|---|---|
| `XexParser` | Segment parsing, run/init address detection, `FileOffsetToMemoryAddress` boundary cases |
| `Opcodes6502` | Opcode table completeness, byte lengths for all modes |
| `DisassemblerTool` | Correct advance through multi-byte instructions, symbol injection |
| `XRefTool` | No false positives from data bytes inside multi-byte operands |
| `FindPatternTool` | Wildcard matching, edge cases at end-of-rom |
| `SessionPersistence` | Round-trip save/load, hardware symbol restoration on partial sidecar |
| `AtrParser` | Header detection, SD/ED/DD geometry, DD boot-sector quirk, sector chain extraction, deleted/locked flag parsing |
| `AddressParser` | All prefix variants (`$`, `0x`, bare decimal), bad input returns |

---

## 13. Future Considerations

The following are explicitly out of scope v1 but noted for future design:

- **Bookmarks / regions:** Named address ranges (e.g., "sprite routines: $3F00–$3FFF").
- **Data type annotations:** Mark regions as lookup tables, sprite data, font data.
- **Batch export:** Dump full annotated disassembly as `.asm` file.
- **CTIA/GTIA palette preview:** Map colour register values to human-readable palette names.
- **65C02 / 65816 support:** Extend `Opcodes6502` with an extended table and addressing mode variants.
- **Second ROM slot:** Support simultaneous comparison of two binaries.
- **SpartaDOS X filesystem:** Extend `AtrParser` to support SDX directory structures and subdirectories.
- **ATR write-back:** Allow saving modified bytes back into a sector chain (enables patching games on disk images).
- **MyDOS extended disks:** Full support for sector counts beyond 720 with the MyDOS VTOC2 extension.
