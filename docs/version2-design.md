# AtariHackerMCP — Version 2 Design

## 1. Introduction

This document describes the Version 2 design for **AtariHackerMCP**, driven by findings from a systematic review of the toolset against real-world Atari 8-bit disk images (*Seven Cities of Gold* ATRs). The review identified three categories of work:

- **Bug fixes:** Defects that cause crashes or incorrect results on non-DOS / homebrew ATR images.
- **New tools:** Capabilities the review identified as missing and high-value for disk-image reverse engineering.
- **Robustness improvements:** Hardening existing tools against edge cases uncovered during analysis.

All changes are additive or surgical; the v1 architecture (DI, tool pattern, session state, persistence) remains unchanged.

---

## 2. Root Cause Analysis of v1 Defects

### 2.1 `atr_info` — Crashes on Non-DOS ATR Images

**Symptom (from review):**

```
ERROR: Sector 51358 is outside the image.  (Disk 1)
ERROR: Sector 4529 is outside the image.   (Disk 2)
```

**Root cause chain:**

1. [`AtrInfo()`](Tools/AtrTools.cs:13) calls [`AtrParser.ReadDirectory()`](Atari/AtrParser.cs:82), which iterates sectors 361–368.
2. For a non-DOS disk, sectors 361–368 exist physically but contain **game data, not directory entries**.
3. The flag byte at offset 0 of each 16-byte pseudo-entry is compared against `0x00` to skip empty slots. Random game data bytes are often non-zero, creating **phantom directory entries**.
4. Phantom entries have garbage values for [`StartSector`](Atari/AtrParser.cs:100) — often huge integers derived from sprite data or machine code.
5. [`AtrInfo()`](Tools/AtrTools.cs:39) then calls [`AtrParser.ExtractFile()`](Atari/AtrParser.cs:122) for each phantom entry to compute byte counts for display.
6. [`ExtractFile()`](Atari/AtrParser.cs:122) → [`ReadSector()`](Atari/AtrParser.cs:63) validates `sectorNumber <= geometry.SectorCount` and throws `"Sector 51358 is outside the image."`.

**Why it escapes the `IsDeleted` filter:** The filter on [line 25](Tools/AtrTools.cs:25) of `AtrTools.cs` removes entries with `IsDeleted = true`, but phantom entries have random flag bytes that may not set bit 7 (the deleted flag). The filter passes them through.

**Proposed fix (three-layer defense):**

| Layer | Change | File |
|-------|--------|------|
| 1 | Add DOS filesystem validation heuristic before attempting directory read | [`AtrParser.cs`](Atari/AtrParser.cs) |
| 2 | Wrap `ExtractFile` call in `AtrInfo` with try-catch; emit warning row for unreadable entries instead of crashing | [`AtrTools.cs`](Tools/AtrTools.cs) |
| 3 | Add sector-number sanity bounds in `ReadDirectory`: reject entries whose `StartSector` is zero or exceeds the total sector count | [`AtrParser.cs`](Atari/AtrParser.cs) |

**Layer 1 detail — DOS filesystem heuristic:**

```csharp
public static bool HasDosFilesystem(byte[] data)
{
    var geometry = ParseGeometry(data);
    // VTOC sector must exist and have the DOS 2.x signature byte pattern
    if (geometry.SectorCount < 368) return false;
    var vtoc = ReadSector(data, geometry, 360);
    // DOS 2.x VTOC: byte 0 = directory sector count (usually 8),
    // bytes 1-2 = starting sector count, bytes 3-4 = free sectors
    // A valid DOS 2.x VTOC has reasonable values
    var dirSectors = vtoc[0];
    if (dirSectors == 0 || dirSectors > 16) return false;
    var totalSectors = vtoc[1] | (vtoc[2] << 8);
    if (totalSectors == 0 || totalSectors > geometry.SectorCount) return false;
    return true;
}
```

**Layer 2 detail — graceful extraction failure in AtrInfo:**

```csharp
foreach (var entry in directory)
{
    try
    {
        var extracted = AtrParser.ExtractFile(bytes, geometry, entry);
        // ... format row normally ...
    }
    catch (Exception)
    {
        lines.Add($"  {entry.Index,2}  {entry.FileName,-12} {entry.Extension,-3} {"???",7} {"???",6} {entry.StartSector,6}  [unreadable]");
    }
}
```

**Layer 3 detail — entry validation in ReadDirectory:**

```csharp
// After reading StartSector:
if (startSector == 0 || startSector > geometry.SectorCount)
{
    continue; // skip phantom entries with impossible sector numbers
}
```

---

### 2.2 `load_atr_file` — No Directory Listing Capability

**Symptom:** The tool requires knowing the exact filename ahead of time. There is no way to discover what files exist on a DOS-formatted disk.

**Root cause:** This is a missing feature, not a bug. The tool was designed for the workflow "I know the filename, extract it." The review identifies the complementary need: "What files are on this disk?"

**Proposed fix:** Add a new [`list_atr_directory`](#35-list_atr_directory) tool (see §3.5).

---

### 2.3 `disassemble` — Intermittent Parameter Alignment Issues

**Symptom (from review):**

```
disassemble(offset="6", numBytes=128)  →  error
```

**Analysis:** The current code path for this call should work. With `RomSession.BaseAddress = 0x0700` set by [`LoadAtrBoot`](Tools/AtrTools.cs:117), the `XexAddressResolver` returns `(ushort)(0x0700 + 6) = 0x0706`. The hex digit regex in [`AddressParser`](Helpers/AddressParser.cs:59) treats `"6"` as decimal (no `$`/`0x` prefix and '6' ∉ `[A-Fa-f]`), so `ParseOffset("6")` → `6`. This should succeed.

**Likely explanation:** The error was encountered on a version of the codebase before [`RomSession.BaseAddress`](State/RomSession.cs:17) was implemented, or the user omitted the `startAddress` override when using a raw ROM load (no XEX segments, no BaseAddress). In that case, `XexAddressResolver.ResolveFileOffset` falls through to identity mapping (`memAddr = fileOffset`), producing address `$0006` — which is technically valid but may not be what the user intended.

**Proposed fix (hardening, not a logic change):**

| Change | Rationale |
|--------|-----------|
| Improve error messages when `startAddress` is not provided and no segment map / base address exists | The current code silently uses identity mapping; it should warn |
| Document the `startAddress` parameter more prominently in the tool description | Currently: `"Optional override start address."` — too terse |

**Revised tool description:**

```
Disassemble 6502 machine code from the loaded ROM. Provide startAddress
when the ROM has no XEX segment map (e.g., boot sectors loaded via
LoadAtrBoot should use startAddress=$0700).
```

---

### 2.4 `trace_control_flow` — BRK at Boot Sector Entry Point

**Symptom (from review):**

```
$0700  BRK
    [BRK]
```

**Root cause:** When [`LoadAtrBoot`](Tools/AtrTools.cs:99) loads boot sectors, the 384-byte blob is loaded at `BaseAddress = 0x0700`. The first byte at offset 0 is the boot flag byte (typically `$00`), not executable code. The opcode `$00` is [`BRK`](Atari/Opcodes6502.cs). The tool correctly disassembles it but the user wanted to trace the *actual* boot loader code, which starts after the 6-byte boot header.

**The Atari boot sector header (standard layout):**

```
Offset  Field
$00     Boot flag (0 = continue loading)
$01     Number of sectors to load
$02-$03 Load address (little-endian)
$04-$05 Init address (little-endian)
$06+    First executable instruction
```

**Proposed fix:** Add an [`analyze_boot_sector`](#36-analyze_boot_sector) tool that decodes this header and reports the actual entry point. The user can then pass that address to [`trace_control_flow`](Tools/ControlFlowTool.cs:13).

---

## 3. New Tools

### 3.1 `atr_header`

**Category:** Disk Image  
**Description:** Parse and display the 16-byte ATR header in structured form. Read-only; does not touch `RomSession`.

**Parameters:**

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `filePath` | string | yes | Path to the `.atr` file |

**Returns:** Formatted report:

```
ATR Header: /roms/alibaba.atr
  Magic:         $0296
  Image size:    92160 bytes (5760 paragraphs)
  Sector size:   128 bytes
  Sector count:  720
  Density:       Single (SD)
  Write protect: No
```

**Implementation:**

```csharp
[McpServerTool, Description("Display the ATR header fields for a disk image.")]
public static string AtrHeader(
    [Description("Path to the ATR file.")] string filePath)
{
    var bytes = File.ReadAllBytes(Path.GetFullPath(filePath));
    if (!AtrParser.IsAtr(bytes))
        return "ERROR: Not a valid ATR image.";

    var geo = AtrParser.ParseGeometry(bytes);
    var paragraphsLow  = bytes[2] | (bytes[3] << 8);
    var paragraphsHigh = bytes[6] | (bytes[7] << 8);
    var totalParagraphs = ((uint)paragraphsHigh << 16) | (uint)paragraphsLow;
    var imageBytes = (int)(totalParagraphs * 16u);
    var writeProtect = bytes[8] != 0;

    return string.Join('\n',
        $"ATR Header: {Path.GetFullPath(filePath)}",
        $"  Magic:         $0296",
        $"  Image size:    {imageBytes} bytes ({totalParagraphs} paragraphs)",
        $"  Sector size:   {geo.SectorSize} bytes",
        $"  Sector count:  {geo.SectorCount}",
        $"  Density:       {DescribeDensity(geo)}",
        $"  Write protect: {(writeProtect ? "Yes" : "No")}"
    );
}
```

**Design note:** This is a thin wrapper over [`AtrParser.ParseGeometry()`](Atari/AtrParser.cs:23) with additional header byte decoding. The [`atr_info`](#21-atr_info--crashes-on-non-dos-atr-images) tool already displays density and sector info, but buried inside a larger report. This tool is laser-focused on the header alone — fast, always safe, and useful as a first-look command before deeper analysis.

---

### 3.2 `sector_dump`

**Category:** Disk Image / Inspection  
**Description:** Hex dump one or more sectors from an ATR disk image by logical sector number (1-based). Eliminates the mental arithmetic of converting sector numbers to file offsets. Read-only; does not touch `RomSession`.

**Parameters:**

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `filePath` | string | yes | Path to the `.atr` file |
| `sector` | string | yes | Starting sector number (1-based, decimal or hex) |
| `count` | int | no | Number of consecutive sectors to dump. Default: 1 |

**Returns:** Standard hex dump format (16 bytes per row, file offset + memory address columns + ASCII). For sector-based dumps, the memory address column shows the sector-relative offset:

```
Sector 361 (file offset $02D10), 128 bytes:
Offset    Sector:Off  00 01 02 03 04 05 06 07 08 09 0A 0B 0C 0D 0E 0F  ASCII
--------  ----------  -----------------------------------------------  ----------------
00002D10  361:$0000   00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00  ................
00002D20  361:$0010   42 10 00 09 00 41 4C 49 42 41 42 41 20 20 43 4F  B....ALIBABA  CO
```

**Implementation:**

```csharp
[McpServerTool, Description("Hex dump sectors from an ATR disk image by logical sector number.")]
public static string SectorDump(
    [Description("Path to the ATR file.")] string filePath,
    [Description("Starting sector number (1-based).")] string sector,
    [Description("Number of consecutive sectors to dump.")] int count = 1)
{
    var bytes = File.ReadAllBytes(Path.GetFullPath(filePath));
    if (!AtrParser.IsAtr(bytes))
        return "ERROR: Not a valid ATR image.";

    var geo = AtrParser.ParseGeometry(bytes);
    var sectorNum = AddressParser.ParseAddress(sector); // reuse address parser
    if (sectorNum < 1 || sectorNum > geo.SectorCount)
        return $"ERROR: Sector {sectorNum} is outside the image (1-{geo.SectorCount}).";

    count = Math.Max(1, Math.Min(count, geo.SectorCount - sectorNum + 1));
    // Build a contiguous byte buffer from the requested sectors
    using var ms = new MemoryStream();
    for (int i = 0; i < count; i++)
    {
        var sec = AtrParser.ReadSector(bytes, geo, sectorNum + i);
        ms.Write(sec, 0, sec.Length);
    }
    var combined = ms.ToArray();
    var fileOffset = AtrParser.SectorFileOffset(geo, sectorNum); // needs to be made internal/public

    return HexDumpTool.GenerateHexDump(combined, 0, combined.Length, fileOffset,
        addressLabel: row => $"{(sectorNum + (row - fileOffset) / geo.SectorSize)}:${(row - fileOffset) % geo.SectorSize:X4}");
}
```

**Design note:** Requires exposing [`SectorFileOffset`](Atari/AtrParser.cs:203) as `internal` (or `public`) and adding an overload of `GenerateHexDump` that accepts a custom address label formatter. The sector-relative address column makes it trivial to cross-reference sector-level documentation (e.g., "the directory starts at byte offset 0 of sector 361").

---

### 3.3 `analyze_boot_sector`

**Category:** Disk Image / Analysis  
**Description:** Decode the standard Atari boot sector header from the boot sectors (sectors 1–3) of an ATR disk image. Reports the boot flag, load address, init address, sector count, and computed entry point. Read-only; does not touch `RomSession`.

**Parameters:**

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `filePath` | string | yes | Path to the `.atr` file |

**Returns:** Formatted report:

```
Boot Sector Analysis: /roms/alibaba.atr
  Boot flag:       $00  (continue loading)
  Sectors to load: 70
  Load address:    $3C07
  Init address:    $4C1A
  Entry point:     $0706  (first instruction after boot header)
  Header bytes:    00 46 07 3C 1A 4C
  DOS boot:        No  (custom loader detected; DOS boots use init=$x7xx)
```

**Implementation:**

```csharp
[McpServerTool, Description("Decode the boot sector header from an ATR disk image.")]
public static string AnalyzeBootSector(
    [Description("Path to the ATR file.")] string filePath)
{
    var bytes = File.ReadAllBytes(Path.GetFullPath(filePath));
    if (!AtrParser.IsAtr(bytes))
        return "ERROR: Not a valid ATR image.";

    var boot = AtrParser.ExtractBootSectors(bytes); // 384 bytes
    var flag        = boot[0];
    var sectorCount = boot[1];
    var loadAddr    = (ushort)(boot[2] | (boot[3] << 8));
    var initAddr    = (ushort)(boot[4] | (boot[5] << 8));

    var isDosBoot = initAddr is >= 0x0700 and <= 0x07FF;
    var bootType = isDosBoot ? "DOS boot" : "Custom loader";

    return string.Join('\n',
        $"Boot Sector Analysis: {Path.GetFullPath(filePath)}",
        $"  Boot flag:       ${flag:X2}  ({(flag == 0 ? "continue loading" : "stop / run")})",
        $"  Sectors to load: {sectorCount}",
        $"  Load address:    ${loadAddr:X4}",
        $"  Init address:    ${initAddr:X4}",
        $"  Entry point:     $0706  (first instruction after boot header)",
        $"  Header bytes:    {boot[0]:X2} {boot[1]:X2} {boot[2]:X2} {boot[3]:X2} {boot[4]:X2} {boot[5]:X2}",
        $"  DOS boot:        {(isDosBoot ? "Yes" : "No")}  ({bootType})"
    );
}
```

**Design note:** This tool answers the single most common question when analyzing an Atari disk for the first time: "How does this disk boot?" The `isDosBoot` heuristic (init address in `$0700–$07FF` range) is a reliable differentiator — DOS boot loaders reinitialize at a DOS entry point, while custom/game loaders jump to game code elsewhere.

**Synergy with `trace_control_flow`:** The user can take the reported entry point (typically `$0706`) and pass it directly to [`trace_control_flow(address="$0706")`](Tools/ControlFlowTool.cs:13) for meaningful control-flow analysis, avoiding the v1 pitfall of tracing from `$0700` and hitting `BRK`.

---

### 3.4 `search_boot_sector`

**Category:** Disk Image / Analysis  
**Description:** Scan one or more ATR disk images for boot sectors matching a byte pattern (e.g., a known boot loader fingerprint). Useful for identifying which disks in a multi-disk game set share a common boot loader.

**Parameters:**

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `filePaths` | string[] | yes | One or more paths to `.atr` files |
| `pattern` | string | no | Hex byte pattern to match in the boot sectors. Default: none (report all) |
| `compareMode` | string | no | `"pattern"` (match a byte pattern) or `"diff"` (compare boot sectors pairwise). Default: `"pattern"` |

**Returns (pattern mode):**

```
Boot sector search: pattern "A9 ?? 85 ??"
  /roms/disk1.atr  —  Match at sector offset $0010 (boot flag $00, loads 70 sectors to $3C07)
  /roms/disk3.atr  —  Match at sector offset $0010 (boot flag $00, loads 70 sectors to $3C07)
  /roms/disk2.atr  —  No match
```

**Returns (diff mode):**

```
Boot sector comparison:
  disk1.atr vs disk2.atr  —  342 / 384 bytes identical (89%)
  disk1.atr vs disk3.atr  —  380 / 384 bytes identical (99%)
```

**Implementation sketch:**

```csharp
[McpServerTool, Description("Scan boot sectors across multiple ATR images for patterns or differences.")]
public static string SearchBootSector(
    [Description("Paths to ATR files to scan.")] string[] filePaths,
    [Description("Hex byte pattern with ?? wildcards.")] string? pattern = null,
    [Description("Search mode: pattern or diff.")] string compareMode = "pattern")
{
    // 1. Validate all paths are ATR; skip non-ATR files with warnings
    // 2. Extract boot sectors from each
    // 3. If mode == "pattern": run FindPattern on each, report matches with boot header summary
    // 4. If mode == "diff": compare each pair, report byte-identical percentage
    // 5. Return formatted results
}
```

**Design note:** This tool reads multiple ATR files and extracts boot sectors from each, but never loads into `RomSession`. It operates on local buffers only. The `compareMode = "diff"` is especially valuable for multi-disk game sets where disks often share an identical boot loader with only a few bytes changed (e.g., the number of sectors to load).

---

### 3.5 `list_atr_directory`

**Category:** Disk Image  
**Description:** List all files on a DOS-formatted ATR disk image without extracting any. Provides a directory listing so the user knows what filenames are available to pass to [`load_atr_file`](Tools/AtrTools.cs:56). Read-only; does not touch `RomSession`.

**Parameters:**

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `filePath` | string | yes | Path to the `.atr` file |

**Returns:** Formatted directory listing:

```
ATR Directory: /roms/game.atr
  #  Filename     Ext  Sectors  Start   Flags
  0  DOS          SYS      39    0004   [system]
  1  DUP          SYS      42    0043   [system]
  2  GAME         EXE     118    0085   [binary]
  3  DATA         DAT      80    0203   []
  4  AUTORUN      SYS       3    0283   [binary]

4 files (2 deleted hidden), 282 sectors used, 438 sectors free
```

**Implementation:**

```csharp
[McpServerTool, Description("List the directory of a DOS-formatted ATR disk image.")]
public static string ListAtrDirectory(
    [Description("Path to the ATR file.")] string filePath)
{
    var bytes = File.ReadAllBytes(Path.GetFullPath(filePath));
    if (!AtrParser.IsAtr(bytes))
        return "ERROR: Not a valid ATR image.";

    if (!AtrParser.HasDosFilesystem(bytes))
        return "ERROR: No DOS 2.x filesystem detected on this disk image. " +
               "This disk may use a custom/non-DOS layout. " +
               "Use load_rom to load it as a raw binary, or load_atr_boot to inspect the boot loader.";

    var geo = AtrParser.ParseGeometry(bytes);
    var allEntries = AtrParser.ReadDirectory(bytes);
    var active = allEntries.Where(e => !e.IsDeleted).ToList();
    var deleted = allEntries.Where(e => e.IsDeleted).ToList();

    var lines = new List<string>
    {
        $"ATR Directory: {Path.GetFullPath(filePath)}",
        "  #  Filename     Ext  Sectors  Start   Flags"
    };

    foreach (var entry in active)
    {
        var flags = new List<string>();
        if (entry.IsBinary) flags.Add("binary");
        if (entry.IsLocked) flags.Add("locked");
        lines.Add($"  {entry.Index,2}  {entry.FileName,-12} {entry.Extension,-3} {entry.SectorCount,7} {entry.StartSector,6}  [{(flags.Count == 0 ? "" : string.Join(',', flags))}]");
    }

    var free = AtrParser.FreeSegmentCount(bytes, geo);
    var used = active.Sum(e => e.SectorCount);

    lines.Add("");
    lines.Add($"{active.Count} files{(deleted.Count > 0 ? $" ({deleted.Count} deleted hidden)" : "")}, {used} sectors used, {free} sectors free");

    return string.Join('\n', lines);
}
```

**Design note:** This tool is the "missing half" of [`load_atr_file`](Tools/AtrTools.cs:56). Together they form the standard two-step disk exploration workflow: **list → extract**. The tool also calls [`HasDosFilesystem()`](#21-atr_info--crashes-on-non-dos-atr-images) as a guard and returns a helpful error message suggesting alternative tools for non-DOS disks.

---

## 4. ATR Parser Enhancements

### 4.1 New Public Members

| Member | Kind | Purpose |
|--------|------|---------|
| [`HasDosFilesystem(byte[])`](#21-atr_info--crashes-on-non-dos-atr-images) | static method | VTOC-based heuristic to detect DOS 2.x formatting |
| [`SectorFileOffset(AtrGeometry, int)`](Atari/AtrParser.cs:203) | visibility change | Change from `private` to `internal` so [`sector_dump`](#32-sector_dump) can compute the file offset for its offset column |

### 4.2 Directory Entry Validation

Add two guard clauses to [`ReadDirectory()`](Atari/AtrParser.cs:82):

```csharp
// After reading startSector on line 100:
if (startSector == 0 || startSector > geometry.SectorCount)
{
    continue; // skip phantom entries with impossible sector numbers
}

// After reading sectorCount on line 99:
if (sectorCount == 0 || sectorCount > geometry.SectorCount)
{
    continue; // skip phantom entries with impossible sector counts
}
```

### 4.3 String Encoding for Filenames

Current code uses [`Encoding.ASCII.GetString`](Atari/AtrParser.cs:215) for filename extraction. For ATASCII-encoded filenames (common in homebrew and some commercial software), this produces garbled output. Add an optional `encoding` parameter to [`ReadPaddedString`](Atari/AtrParser.cs:213) with `"atascii"` support, reusing the ATASCII decoding logic from [`StringSearchTool`](Tools/StringSearchTool.cs).

---

## 5. Session State Enhancements

### 5.1 `RomSession` — ATR Context Tracking

Add optional ATR context fields so tools can report richer information when the loaded binary was extracted from a disk image:

```csharp
public sealed class RomSession
{
    // ... existing fields ...

    /// <summary>
    /// When the loaded data was extracted from an ATR, the path to that ATR.
    /// </summary>
    public string? SourceAtrPath { get; set; }

    /// <summary>
    /// When loading boot sectors, the decoded boot header fields (if standard).
    /// </summary>
    public BootHeader? BootHeader { get; set; }
}

public record BootHeader(
    byte Flag,
    byte SectorCount,
    ushort LoadAddress,
    ushort InitAddress
);
```

**Design note:** This is a lightweight extension. [`LoadAtrFile`](Tools/AtrTools.cs:56) sets `SourceAtrPath`; [`LoadAtrBoot`](Tools/AtrTools.cs:99) sets both `SourceAtrPath` and `BootHeader`. Other tools (e.g., [`RomInfo`](specification.md#L142)) can optionally display this context. The fields are cleared by [`ClearMetadata()`](State/RomSession.cs:33) / [`Load()`](State/RomSession.cs:23) when a new binary is loaded.

---

## 6. Robustness Improvements

### 6.1 `Disassemble` — Clearer `startAddress` Guidance

Update the tool description and add a guard that emits a **non-fatal advisory** when no address mapping is available:

```
Disassemble 6502 machine code from the loaded ROM. Provide startAddress
when the ROM lacks a built-in address map (e.g., boot sectors loaded via
LoadAtrBoot should use startAddress=$0706, the entry point after the
6-byte boot header).
```

If `startAddress` is not provided and no XEX segments or BaseAddress exist, prepend the output with:

```
NOTE: No address mapping available. Memory addresses shown as file offsets.
      Use the startAddress parameter to set a base address (e.g., startAddress=$0700).
```

### 6.2 `trace_control_flow` — Better Error for Ambiguous Entry Points

When tracing from an address whose first byte is `$00` (BRK), add a hint:

```
$0700  BRK
    [BRK]

NOTE: $0700 disassembles as BRK. If this is a boot sector, the actual
      code starts at $0706 (after the 6-byte boot header). Use
      analyze_boot_sector to confirm, then re-run with address=$0706.
```

### 6.3 `FindStrings` — ATASCII Decoding Parity

The review noted that ASCII-mode search on Disk 1 found `"copyright"` and ATASCII mode on Disk 2 found font glyph hints. The ATASCII decoding in [`StringSearchTool`](Tools/StringSearchTool.cs) should be extracted into a shared helper so it can be reused by:

- [`ReadPaddedString`](Atari/AtrParser.cs:213) (ATR filename decoding, §4.3)
- Future tools that display ATASCII-encoded data

```csharp
// New helper in Helpers/AtasciiDecoder.cs
internal static class AtasciiDecoder
{
    /// <summary>Decode a byte as an ATASCII screen code.</summary>
    public static char DecodeByte(byte b);

    /// <summary>Decode a span of bytes as ATASCII text.</summary>
    public static string Decode(ReadOnlySpan<byte> bytes);
}
```

---

## 7. Updated Tool Catalog

Tools marked **NEW** are introduced in v2. Tools marked **FIXED** had bugs resolved.

| # | Tool | Category | Status | Change Summary |
|---|------|----------|--------|----------------|
| 1 | [`load_rom`](specification.md#L121) | File | — | No change |
| 2 | [`rom_info`](specification.md#L142) | File | Enhanced | Optionally displays ATR source context |
| 3 | [`atr_info`](specification.md#L483) | Disk Image | **FIXED** | Graceful handling of non-DOS disks |
| 4 | [`load_atr_file`](specification.md#L517) | Disk Image | — | No change |
| 5 | [`load_atr_boot`](specification.md#L541) | Disk Image | Enhanced | Sets `BootHeader` on session |
| 6 | [`atr_header`](#31-atr_header) | Disk Image | **NEW** | Structured ATR header display |
| 7 | [`list_atr_directory`](#35-list_atr_directory) | Disk Image | **NEW** | Directory enumeration for DOS disks |
| 8 | [`sector_dump`](#32-sector_dump) | Disk Image | **NEW** | Hex dump by sector number |
| 9 | [`analyze_boot_sector`](#33-analyze_boot_sector) | Disk Image | **NEW** | Decode boot sector header |
| 10 | [`search_boot_sector`](#34-search_boot_sector) | Disk Image | **NEW** | Scan ATRs for boot patterns |
| 11 | [`hex_dump`](specification.md#L157) | Inspection | — | No change |
| 12 | [`disassemble`](specification.md#L184) | Disassembly | Enhanced | Better `startAddress` guidance |
| 13 | [`calculate`](specification.md#L218) | Utility | — | No change |
| 14 | [`hex_to_decimal`](specification.md#L237) | Utility | — | No change |
| 15 | [`decimal_to_hex`](specification.md#L252) | Utility | — | No change |
| 16 | [`define_symbol`](specification.md#L267) | Symbol Table | — | No change |
| 17 | [`remove_symbol`](specification.md#L284) | Symbol Table | — | No change |
| 18 | [`lookup_symbol`](specification.md#L299) | Symbol Table | — | No change |
| 19 | [`list_symbols`](specification.md#L314) | Symbol Table | — | No change |
| 20 | [`annotate_zero_page`](specification.md#L330) | Zero Page | — | No change |
| 21 | [`show_zero_page_map`](specification.md#L347) | Zero Page | — | No change |
| 22 | [`x_ref`](specification.md#L362) | Analysis | — | No change |
| 23 | [`find_pattern`](specification.md#L395) | Analysis | — | No change |
| 24 | [`find_strings`](specification.md#L420) | Analysis | Refactored | ATASCII decoder extracted to shared helper |
| 25 | [`trace_control_flow`](specification.md#L449) | Analysis | Enhanced | Boot-sector-aware hints |

---

## 8. Implementation Order

The changes are ordered to deliver value incrementally and minimize risk:

| Phase | Items | Rationale |
|-------|-------|-----------|
| **Phase 1: Bug fixes** | `atr_info` crash fix, directory entry validation, `HasDosFilesystem` | Unblocks ATR analysis for non-DOS disks; highest user pain |
| **Phase 2: Quick wins** | `atr_header`, `list_atr_directory`, `analyze_boot_sector` | Three standalone tools with zero dependencies on other v2 changes |
| **Phase 3: Deeper tools** | `sector_dump`, `search_boot_sector` | Depend on `SectorFileOffset` visibility change and pattern-matching refactor |
| **Phase 4: Polish** | `Disassemble` hints, `TraceControlFlow` hints, ATASCII helper extraction, `RomSession` ATR context fields | UX improvements with no functional risk |

---

## 9. Migration Notes

### Backward Compatibility

- All v1 tools retain their existing signatures. No parameters are removed or renamed.
- The [`atr_info`](#21-atr_info--crashes-on-non-dos-atr-images) behavior change (returning a graceful message instead of crashing) is the only observable difference — strictly an improvement.
- The sidecar file format (`.atarihacker.json`) is unchanged. No migration needed.

### New Dependencies

None. All new functionality uses existing NuGet packages and the .NET 10 BCL.

### Files Changed

| File | Change Type |
|------|-------------|
| [`Atari/AtrParser.cs`](Atari/AtrParser.cs) | Add `HasDosFilesystem`, expose `SectorFileOffset`, add entry validation, add ATASCII filename support |
| [`Tools/AtrTools.cs`](Tools/AtrTools.cs) | Add 5 new tool methods (`AtrHeader`, `SectorDump`, `AnalyzeBootSector`, `SearchBootSector`, `ListAtrDirectory`); fix `AtrInfo` crash |
| [`State/RomSession.cs`](State/RomSession.cs) | Add `SourceAtrPath`, `BootHeader` fields + `BootHeader` record |
| [`Tools/DisassemblerTool.cs`](Tools/DisassemblerTool.cs) | Update tool description; add advisory when no address mapping exists |
| [`Tools/ControlFlowTool.cs`](Tools/ControlFlowTool.cs) | Add BRK-at-entry-point hint |
| [`Tools/StringSearchTool.cs`](Tools/StringSearchTool.cs) | Extract ATASCII decoding to shared helper |
| `Helpers/AtasciiDecoder.cs` | **New file** — shared ATASCII decode helper |
| [`docs/version2-design.md`](docs/version2-design.md) | **New file** — this document |

---

## 10. Architecture Diagram (v2)

```
┌──────────────────────────────────────────────────────────────────┐
│  LLM Client (Claude Desktop / other MCP host)                    │
│  Communicates via stdio JSON-RPC (MCP protocol)                  │
└───────────────────────────┬──────────────────────────────────────┘
                            │ stdio (JSON-RPC 2.0)
┌───────────────────────────▼──────────────────────────────────────┐
│  AtariHackerMCP Process (v2)                                      │
│                                                                   │
│  ┌─────────────────────────────────────────────────────────────┐ │
│  │  Program.cs — IHost / DI container / stdio transport         │ │
│  └──────────────┬──────────────────────────────────────────────┘ │
│                 │ injects                                          │
│  ┌──────────────▼──────────────────────────────────────────────┐ │
│  │  Tool Classes (static methods, [McpServerTool])               │ │
│  │                                                               │ │
│  │  ┌─ FileTools ────────────────────────────────────────────┐ │ │
│  │  │  LoadRom  RomInfo                                       │ │ │
│  │  └────────────────────────────────────────────────────────┘ │ │
│  │  ┌─ AtrTools (v2 expanded) ───────────────────────────────┐ │ │
│  │  │  AtrInfo [FIXED]  LoadAtrFile  LoadAtrBoot              │ │ │
│  │  │  AtrHeader [NEW]   ListAtrDirectory [NEW]              │ │ │
│  │  │  SectorDump [NEW]  AnalyzeBootSector [NEW]             │ │ │
│  │  │  SearchBootSector [NEW]                                │ │ │
│  │  └────────────────────────────────────────────────────────┘ │ │
│  │  ┌─ HexDumpTool  DisassemblerTool [ENHANCED] ─────────────┐ │ │
│  │  │  CalculatorTool  ConversionTools                        │ │ │
│  │  │  SymbolTools  XRefTool  FindPatternTool                │ │ │
│  │  │  StringSearchTool [REFACTORED]                          │ │ │
│  │  │  ControlFlowTool [ENHANCED]  ZeroPageTool              │ │ │
│  │  └────────────────────────────────────────────────────────┘ │ │
│  └──────────────┬──────────────────────────────────────────────┘ │
│                 │ reads / writes                                   │
│  ┌──────────────▼──────────────────────────────────────────────┐ │
│  │  Session State (singletons)                                   │ │
│  │  RomSession [+SourceAtrPath, +BootHeader]                     │ │
│  │  SymbolTable  ·  ZeroPageMap  ·  SessionPersistence           │ │
│  └──────────────┬──────────────────────────────────────────────┘ │
│                 │                                                  │
│  ┌──────────────▼──────────────────────────────────────────────┐ │
│  │  Atari Domain Layer (v2)                                      │ │
│  │  Opcodes6502  ·  XexParser  ·  AtariHardwareMap              │ │
│  │  AtrParser [+HasDosFilesystem, +SectorFileOffset(internal)]  │ │
│  │  AtasciiDecoder [NEW shared helper]                           │ │
│  └─────────────────────────────────────────────────────────────┘ │
└──────────────────────────────────────────────────────────────────┘
```

---

## 11. Summary of Changes from v1

| Area | v1 | v2 |
|------|----|----|
| ATR tools | 3 tools (`atr_info`, `load_atr_file`, `load_atr_boot`) | 8 tools (+5 new) |
| Non-DOS disks | Crashes with opaque errors | Graceful fallback with actionable guidance |
| Boot sector analysis | Manual hex inspection required | Dedicated `analyze_boot_sector` tool |
| Directory discovery | Must know filename in advance | `list_atr_directory` provides enumeration |
| Sector access | File-offset-only via `hex_dump` | Logical sector numbers via `sector_dump` |
| Disassembly UX | Silent identity mapping when no address context | Advisory hints + better `startAddress` guidance |
| Trace control flow | BRK at `$0700` with no explanation | Hint about boot header and actual entry point |
| ATASCII support | Embedded in `find_strings` only | Shared helper reusable across tools |
