# AtariHackerMCP — Version 3 Design

## 1. Introduction

This document describes the Version 3 design for **AtariHackerMCP**, focused on deep disk-image analysis for reverse-engineering and preservation. Version 2 established a solid foundation of ATR tools (header parsing, sector dumping, boot sector analysis, directory listing). Version 3 builds on that foundation to add the kind of detailed forensic analysis that preservationists, reverse engineers, and Atari archaeologists need.

The driving principle: **surface everything interesting about a disk image, whether it uses a standard DOS filesystem or not.**

---

## 2. DOS Flavor Detection — Beyond Binary Yes/No

### 2.1 Current State (v2)

[`AtrParser.HasDosFilesystem()`](Atari/AtrParser.cs:22) returns a boolean — yes, the disk has a DOS 2.x VTOC, or no, it doesn't. This is too coarse. The Atari 8-bit world had multiple DOS families with different structures:

### 2.2 DOS Families to Detect

| DOS | VTOC Location | Directory Entry Size | Key Fingerprints |
|-----|--------------|---------------------|------------------|
| **Atari DOS 2.0s** | Sector 360 | 16 bytes, 8/sector | VTOC byte 0 usually 8; total sector count in bytes 1-2 |
| **Atari DOS 2.5** | Sector 360 | 16 bytes | Same structure; supports enhanced density (1040 sectors, 128-byte) |
| **Atari DOS 3** | Sector 360 | 16 bytes (different flag semantics) | VTOC byte 0 = 8; flag byte 0x80 = FMS file; completely different bitmap layout |
| **MyDOS** | Sector 360 (359 on large disks) | 32 bytes extended | VTOC byte 0 ≤ 16; supports subdirectories; 256-byte sectors; 2-byte flag field; sector count bytes 1-2 use full 16-bit; bytes 3-4 free count; extended VTOC at byte 10+ with different bitmap layout |
| **SpartaDOS** | Sector 360 (or boot-configurable) | 23 bytes | Completely different VTOC structure; hierarchical directories; bitmap sectors separate from VTOC; volume name stored; boot sector starts with "SPARTA" or code that sets up the SpartaDOS kernel |
| **DOS XL** | Sector 360 | 16 bytes (Atari DOS-compatible) | VTOC byte 0 up to 16; extended directory; supports DD; often has "DOS XL" or "OSS" strings in boot area |
| **Turbo-DOS / XE DOS** | Sector 360 | 16 bytes | Extended MyDOS variant; faster sector I/O; VTOC format similar to MyDOS |
| **Bibo-DOS** | Sector 360 | 16 bytes | German DOS variant; Atari DOS compatible layout |
| **SmartDOS** | Sector 360 | 16 bytes | US Doubler compatible; similar to Atari DOS 2.x |

### 2.3 Detection Strategy

Rather than trying to distinguish every variant perfectly (which is impossible by VTOC alone since MyDOS, Turbo-DOS, and DOS 2.x use the same VTOC sector), use a **confidence-based, multi-signal approach**:

```
DosFlavor enum:
  Unknown
  NoDosFilesystem
  AtariDos2_0s      // DOS 2.0s / 2.5 compatible
  AtariDos3         // Atari DOS 3 (different bitmap)
  MyDos             // MyDOS (32-byte dir entries, subdirectories)
  SpartaDos         // SpartaDOS (completely different structure)
  DosXl             // DOS XL
  TurboDos          // Turbo-DOS / XE DOS
```

**Signal 1 — VTOC Format Byte (offset 0 of sector 360)**

| VTOC[0] | Likely Meaning |
|---------|---------------|
| 8 | Standard Atari DOS 2.x, DOS XL (8 directory sectors) |
| 4 | Atari DOS 2.x single-density (4 directory sectors — very early disks) |
| ≤ 16 | MyDOS, DOS XL, or Atari DOS 2.x with non-standard dir size |
| ≥ 128 | Probably not DOS 2.x; could be SpartaDOS or non-DOS data |

**Signal 2 — Directory Entry Size Check**

Read sector 361. If `HasDosFilesystem()` passes, check the entry size:

```
Entry 0 starts at byte 0:
  flag = byte 0

If flag is 0:
  Look for first non-zero entry. Count bytes from sector start.

MyDOS uses 32-byte entries. At offset 0 of the second 32-byte block
(byte 32), the flags byte should show a similar structure. With 16-byte
entries, byte 16 would be the second entry's flags — but byte 0 and
byte 16 will have different-looking flag patterns.

Heuristic: look for repeating structure every 16 vs every 32 bytes.
```

**Signal 3 — SpartaDOS Detection**

SpartaDOS has a radically different VTOC:
- Boot sector (sectors 1-3) contains initialization code that loads `SPARTA.SYS` or a kernel
- VTOC at sector 360: byte 0 = bitmap sector count (not dir sector count), completely different layout
- Boot code typically starts by setting up the filesystem driver
- Signature: boot code often contains references to sector numbers used for bitmap sectors

Check: if VTOC[0] is unusually large (≥128) AND the disk has boot sectors with code that accesses sector numbers outside the standard DOS 2.x range, flag as SpartaDOS.

**Signal 4 — Boot Sector Code Fingerprints**

| Fingerprint | DOS |
|-------------|-----|
| Loads 3 sectors to `$0700`, init at `$0700-$07FF` | Atari DOS 2.x / MyDOS / DOS XL |
| Loads N sectors, init at `$1D00-$1E00` range | SpartaDOS |
| "SPARTA" or "DOSXL" string in boot | SpartaDOS / DOS XL |
| Loads >3 sectors to non-$0700 address | Game loader, custom boot |
| Loads 0 sectors, init = load address | Possible boot-game (no DOS) |

**Signal 5 — VTOC Total Sectors vs Geometry**

```
var totalFromVtoc = vtoc[1] | (vtoc[2] << 8);
var actualSectors = geometry.SectorCount;

If totalFromVtoc == actualSectors → normal DOS 2.x
If totalFromVtoc == 65535 → SpartaDOS (uses $FFFF sentinel)
If totalFromVtoc < actualSectors → possibly DOS 2.x with unused sectors
If totalFromVtoc > actualSectors → corrupt or non-standard
```

### 2.4 Proposed API Changes

```csharp
// New enum in AtrParser.cs
public enum DosFlavor
{
    Unknown,
    NoDosFilesystem,
    AtariDos2,        // Atari DOS 2.0s / 2.5 / compatible
    AtariDos3,        // Atari DOS 3
    MyDos,            // MyDOS
    SpartaDos,        // SpartaDOS
    DosXl,            // DOS XL
    TurboDos,         // Turbo-DOS / XE DOS
}

// New record for detailed DOS info
public sealed record DosInfo(
    DosFlavor Flavor,
    string Description,
    int VtocSector,
    int VtocFormatByte,       // Raw VTOC[0] value
    int TotalSectorsFromVtoc, // From VTOC bytes 1-2
    int FreeSectorsFromVtoc,  // From VTOC bytes 3-4
    int DirectorySectorCount, // Number of dir sectors
    int DirectoryEntrySize,   // 16 or 32
    int MaxDirectoryEntries,  // Total possible entries
    string? VolumeLabel,      // SpartaDOS / MyDOS subdirectory name
    bool SupportsSubdirectories
);

// Replace HasDosFilesystem with:
public static DosInfo IdentifyDos(byte[] data);

// Keep HasDosFilesystem as a convenience wrapper:
public static bool HasDosFilesystem(byte[] data) =>
    IdentifyDos(data).Flavor != DosFlavor.NoDosFilesystem &&
    IdentifyDos(data).Flavor != DosFlavor.Unknown;
```

### 2.5 MyDOS-Specific Directory Parsing

When `DosInfo.DirectoryEntrySize == 32`, the directory entry format changes:

```
MyDOS 32-byte directory entry:
Offset  Size  Field
$00     1     Flags (same bit layout as Atari DOS)
$01     2     Sector count (little-endian, 16-bit)
$03     2     Start sector (little-endian, 16-bit)
$05     8     Filename (padded with spaces)
$0D     3     Extension (padded with spaces)
$10     8     Optional: date/time stamp or subdirectory marker
$18     2     Optional: file length in bytes
$1A     2     Optional: directory cluster for subdirs
$1C     4     Reserved / unused
```

MyDOS subdirectory flag: high bit of the extension or a specific byte at offset `$10`.

### 2.6 SpartaDOS-Specific Parsing

SpartaDOS uses a completely different hierarchy. For v3, detection and reporting are in scope. Full SpartaDOS directory reading and file extraction is deferred to a future version, since SpartaDOS has:
- Hierarchical directories (tree structure)
- 23-byte directory entries
- Separate bitmap sectors (not embedded in VTOC)
- Volume names and timestamps
- Subdirectory sector chains

For v3, the goal is: detect SpartaDOS, report it, and show what we can (volume name, bitmap sector count, first directory sector) without attempting deep traversal.

---

## 3. Boot Sector Fingerprinting

### 3.1 Current State (v2)

[`AnalyzeBootSector()`](Tools/AtrTools.cs:245) decodes the 6-byte boot header (flag, sector count, load address, init address) and reports whether the init address suggests a DOS boot (`$07xx`) or a custom loader. This is a good start but surface-level.

### 3.2 Boot Type Fingerprints

The boot sectors (sectors 1-3, 384 bytes) contain the boot loader. The first 6 bytes are the header; the remaining 378 bytes are executable code. Different DOS flavors and game loaders have distinctive code patterns. We can fingerprint these.

**Atari DOS 2.0s boot** (signature bytes at `$0706`):
```
$0706  SEI         (78)
$0707  CLD         (D8)
$0708  LDX #$00    (A2 00)
$070A  TXS         (9A)
$070B  LDA #$00    (A9 00)
...sets up stack, zero page, then loads DOS.SYS
Known pattern at $0706: 78 D8 A2 00 9A A9 00
```

**Atari DOS 2.5 boot** — very similar, minor differences in sector counts loaded.

**MyDOS boot** — similar to DOS 2.x but typically includes MyDOS-specific initialization for double-density and subdirectory support. Signature: also starts at `$0706` with `SEI / CLD / LDX #$00 / TXS` but subsequent code differs.

**SpartaDOS boot** — completely different. Often loads at `$1D00` or similar. Signature: does NOT use the `$0700` load address; code typically includes setup for the SpartaDOS kernel and filesystem driver.

**DOS XL boot** — similar to DOS 2.x but includes DD support code. Often contains "DOS XL" or "OSS" text strings.

**Game loaders (custom boot)**:
- **Standard game loader**: Loads >3 sectors, init address is in game RAM (not `$07xx`)
- **Boot-game (no DOS)**: zero-byte boot header flag is 0, but init address = load address (the boot code IS the game start)
- **Multi-stage loader**: Loads a small first stage to `$0700`, which then loads more sectors to game RAM
- **Protected boot**: Contains deliberate obfuscation, checksum checks, or anti-copy code

### 3.3 Fingerprint Database

```csharp
// In a new file: Atari/BootFingerprints.cs

public sealed record BootSignature(
    string Name,           // Human-readable name
    string Category,       // "DOS", "Game Loader", "Multi-Stage", etc.
    byte[] Pattern,         // Byte pattern to match at a given offset
    int MatchOffset,        // Where to look for the pattern
    string? Mask,           // Optional wildcard mask (same length as Pattern)
    string Description
);

public static class BootFingerprints
{
    public static readonly BootSignature[] Signatures = new[]
    {
        new BootSignature(
            "Atari DOS 2.0s Boot",
            "DOS",
            new byte[] { 0x78, 0xD8, 0xA2, 0x00, 0x9A, 0xA9, 0x00 },
            6, // offset $0706
            null,
            "Standard Atari DOS 2.0s boot loader entry point"
        ),
        new BootSignature(
            "Atari DOS 2.5 Boot",
            "DOS",
            new byte[] { 0x78, 0xD8, 0xA2, 0x00, 0x9A },
            6,
            null,
            "Atari DOS 2.5 boot loader (SEI/CLD/LDX#$00/TXS)"
        ),
        new BootSignature(
            "MyDOS Boot",
            "DOS",
            new byte[] { 0x78, 0xD8, 0xA2, 0x00, 0x9A, 0x8E },
            6,
            null,
            "MyDOS boot loader — similar to DOS 2.x but with DD init"
        ),
        // ... more signatures for DOS XL, SpartaDOS, common game loaders, etc.
    };
}
```

### 3.4 Enhanced `AnalyzeBootSector` Output (v3)

```
Boot Sector Analysis: /roms/game.atr
  Boot flag:       $00  (continue loading)
  Sectors to load: 70
  Load address:    $3C07
  Init address:    $4C1A
  Entry point:     $0706
  Header bytes:    00 46 07 3C 1A 4C
  ---
  DOS boot:        No  (custom loader — init at $4C1A)
  Boot type:       Custom game loader (high-memory init)
  Matched fingerprint: None
  Contains executable code in sector 1: Yes (code density at $0706)
  ---
  Boot code preview at $0706:
  0706  A9 00        LDA #$00
  0708  85 4A        STA $4A
  070A  A2 FF        LDX #$FF
  070C  9A           TXS
  ...
```

Key additions:
- **Fingerprint match**: Report the best-matching known boot signature
- **Code density in sector 1**: Is sector 1 mostly executable code (>50% valid opcodes), or mostly data?
- **Boot code preview**: First N instructions at the entry point, giving immediate insight into what the boot loader does

### 3.5 New `BootInfo` Record

```csharp
public sealed record BootAnalysis(
    // Header fields (from v2)
    byte Flag,
    int SectorCount,
    ushort LoadAddress,
    ushort InitAddress,
    bool IsDosBoot,
    string BootType,

    // v3 additions
    string? MatchedFingerprint,
    bool HasExecutableCode,
    double CodeDensity,         // 0.0 — 1.0 fraction of valid 6502 opcodes
    ushort EntryPoint,          // Computed: LoadAddress + 6
    byte[] HeaderBytes,         // First 6 bytes of sector 1
    string? Note                // Any anomalies (e.g., "Init address equals load address — boot-game?")
);
```

---

## 4. Sector Map Visualization

### 4.1 Purpose

A single visual overview that shows, for every sector on the disk, what kind of content it contains. This is the fastest way for a human (or LLM) to understand disk layout at a glance.

### 4.2 Map Characters

| Char | Meaning |
|------|---------|
| `.`  | Empty sector (all zeroes) |
| `*`  | Sector contains non-zero data |
| `B`  | Boot sector (sectors 1-3) |
| `V`  | VTOC sector (360, or SpartaDOS VTOC) |
| `D`  | Directory sector (DOS 2.x: 361-368; MyDOS: 361-376; SpartaDOS: configured) |
| `F`  | File data sector (reachable from a directory entry chain) |
| `H`  | Hidden sector — contains data but not in any directory chain and not in VTOC free map |
| `O`  | Orphaned sector — marked free in VTOC but contains non-zero data (deleted file remnants) |
| `?`  | Unknown / could not classify |

### 4.3 Layout Rules

- 80 characters per line
- Each character represents one sector
- Sectors numbered 1 to N, left to right, top to bottom
- For a 720-sector SD disk: 9 lines (720 / 80 = 9)
- For a 720-sector DD disk: 9 lines
- For a 1440-sector DS,DD disk: 18 lines
- **Maximum 40 lines** (3200 sectors). Beyond this, truncate and note: `(showing first 3200 of N sectors)`

### 4.4 Legend

The map is followed by a compact legend:
```
Sector Map Legend:
 .  Empty      B  Boot      V  VTOC      D  Directory
 F  File data  H  Hidden    O  Orphaned  *  Other data
 ?  Unknown
```

### 4.5 Example Output — DOS 2.x SD Disk

```
Sector Map (720 sectors, 9 lines × 80):
BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB
BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB
BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB
**...FFFVDDDDDDDD**********************************************************......
................................................................................
................................................................................
................................................................................
................................................................................
.............................................................******************
```

Wait — sectors 1-3 are 128 bytes on DD. Let me reconsider: the map displays at the *logical sector* level. On a DD disk with 256-byte sectors, sectors 1-3 are stored as 128 bytes in the ATR file, but they are still sectors 1, 2, 3 logically. The map shows logical sectors.

For a DOS 2.x SD disk with 720 sectors:
- Sectors 1-3: boot (B)
- Sector 360: VTOC (V)
- Sectors 361-368: directory (D)
- DOS.SYS: typically occupies some early sectors (F)
- DUP.SYS: occupies next sectors (F)
- User files: distributed through remaining space (F)
- Free sectors: empty (.)

### 4.6 Implementation

```csharp
// In AtrParser.cs or a new helper:
public static string GenerateSectorMap(byte[] data, DosInfo dosInfo)
{
    var geo = ParseGeometry(data);
    var sectorCount = geo.SectorCount;
    var displaySectors = Math.Min(sectorCount, 3200); // 40 lines × 80
    var lines = (displaySectors + 79) / 80;

    // Build classification for each sector
    var classification = new char[sectorCount + 1]; // 1-based
    // Default: unknown
    for (int s = 1; s <= sectorCount; s++) classification[s] = '?';

    // Phase 1: Mark known structural sectors
    classification[1] = classification[2] = classification[3] = 'B';
    if (dosInfo.Flavor != DosFlavor.NoDosFilesystem)
    {
        classification[dosInfo.VtocSector] = 'V';
        for (int dirSector = dosInfo.VtocSector + 1;
             dirSector <= dosInfo.VtocSector + dosInfo.DirectorySectorCount;
             dirSector++)
        {
            classification[dirSector] = 'D';
        }
    }

    // Phase 2: Classify all sectors as empty or non-empty
    for (int s = 1; s <= sectorCount; s++)
    {
        if (classification[s] != '?') continue; // already classified
        var sectorData = ReadSector(data, geo, s);
        var isEmpty = sectorData.All(b => b == 0);
        classification[s] = isEmpty ? '.' : '*';
    }

    // Phase 3: For DOS disks, mark file data sectors (F) and detect anomalies
    if (dosInfo.Flavor != DosFlavor.NoDosFilesystem)
    {
        var entries = ReadDirectory(data);
        var fileSectors = new HashSet<int>();
        foreach (var entry in entries.Where(e => !e.IsDeleted))
        {
            // Walk the sector chain and collect all sectors
            var sector = entry.StartSector;
            var seen = new HashSet<int>();
            while (sector != 0 && seen.Add(sector) && sector <= sectorCount)
            {
                fileSectors.Add(sector);
                var rawSector = ReadSector(data, geo, sector);
                var nextHi = rawSector[^3] & 0x03;
                var nextLo = rawSector[^2];
                sector = (nextHi << 8) | nextLo;
            }
        }

        // Build VTOC free set
        var vtoc = ReadSector(data, geo, 360);
        var freeSet = new HashSet<int>();
        var vtocTotal = vtoc[1] | (vtoc[2] << 8);
        for (int s = 1; s <= vtocTotal && s <= sectorCount; s++)
        {
            // Skip reserved sectors (boot, VTOC, dir)
            if (s <= 3 || s == 360 || (s >= 361 && s <= 368)) continue;
            var byteIdx = 10 + ((s - 1) / 8);
            var bitIdx = (s - 1) % 8;
            if (byteIdx < vtoc.Length && (vtoc[byteIdx] & (1 << bitIdx)) != 0)
                freeSet.Add(s);
        }

        foreach (var s in fileSectors)
        {
            if (classification[s] == '*' || classification[s] == '.')
                classification[s] = 'F';
        }

        // Detect hidden sectors (data but not in any file chain and not marked free)
        foreach (var s in freeSet)
        {
            if (classification[s] == '*')
                classification[s] = 'O'; // orphaned — free but has data
        }
        for (int s = 1; s <= sectorCount; s++)
        {
            if (classification[s] == '*' && !fileSectors.Contains(s) && !freeSet.Contains(s)
                && s != 360 && (s < 361 || s > 368) && s > 3)
            {
                classification[s] = 'H'; // hidden — data, not in any file, not marked free
            }
        }
    }

    // Build map lines
    var sb = new StringBuilder();
    sb.AppendLine($"Sector Map ({displaySectors} of {sectorCount} sectors, {lines} lines x 80):");
    for (int line = 0; line < lines; line++)
    {
        for (int col = 0; col < 80; col++)
        {
            var sectorNum = line * 80 + col + 1;
            if (sectorNum > sectorCount) break;
            sb.Append(classification[sectorNum]);
        }
        sb.AppendLine();
    }

    if (sectorCount > 3200)
        sb.AppendLine($"(truncated — {sectorCount - 3200} additional sectors not shown)");

    sb.AppendLine();
    sb.AppendLine("Sector Map Legend:");
    sb.AppendLine(" .  Empty      B  Boot      V  VTOC      D  Directory");
    sb.AppendLine(" F  File data  H  Hidden    O  Orphaned  *  Other data");
    sb.AppendLine(" ?  Unknown");

    return sb.ToString();
}
```

---

## 5. Entropy Map

### 5.1 Purpose

A companion to the sector map that shows *how interesting* each sector is, independent of filesystem structure. Low entropy (all zeroes, repeated patterns) = uninteresting. High entropy (code, compressed data) = worth investigating.

### 5.2 Entropy Calculation

Per-sector Shannon entropy on byte values:

```
H(sector) = -Σ p(b) × log₂(p(b))
```

Where `p(b)` is the frequency of byte value `b` in the sector. Normalized to 0.0–1.0 (divide by log₂(256) = 8).

### 5.3 Display Characters

| Range | Char | Meaning |
|-------|------|---------|
| 0.00 — 0.12 | ` ` (space) | All zeroes or near-zero |
| 0.12 — 0.25 | `░` | Very low entropy (fill patterns, repeated text) |
| 0.25 — 0.50 | `▒` | Low entropy (structured data, mostly-ASCII text) |
| 0.50 — 0.75 | `▓` | Medium entropy (tables, mixed data/code) |
| 0.75 — 1.00 | `█` | High entropy (compressed data, machine code, encrypted) |

Alternative ASCII-safe fallback: digits `0`–`9` (0 = lowest entropy, 9 = highest).

### 5.4 Example Output

```
Entropy Map (720 sectors, 9 lines × 80):
                                                 ▓▓▓█▓▓▓█▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓
█▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓████
███▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓████
██....................................................................................
................................................................................
................................................................................
................................................................................
................................................................................
.............................................................█████████████░░░░░░░░
```

This immediately shows:
- Very low entropy first half of line 1 (boot sector header = structured data)
- High entropy end of lines 1-3 (boot loader code)
- High entropy sectors scattered through the disk (file data)
- Medium entropy patch at end (last file — mixture of data/code)
- Large regions of zero entropy (free space)

### 5.5 Implementation

```csharp
// In a new helper or AtrParser.cs:
public static double SectorEntropy(byte[] sectorData)
{
    if (sectorData.Length == 0) return 0.0;

    var counts = new int[256];
    foreach (var b in sectorData) counts[b]++;

    double entropy = 0.0;
    var n = (double)sectorData.Length;
    for (int i = 0; i < 256; i++)
    {
        if (counts[i] == 0) continue;
        var p = counts[i] / n;
        entropy -= p * Math.Log2(p);
    }

    return entropy / 8.0; // normalize to 0.0–1.0
}

public static char EntropyChar(double entropy) => entropy switch
{
    < 0.12 => ' ',  // empty
    < 0.25 => '░',
    < 0.50 => '▒',
    < 0.75 => '▓',
    _      => '█'
};
```

---

## 6. Sector-Level Anomaly Detection

### 6.1 Categories

| Anomaly | Detection Method | Significance |
|---------|-----------------|--------------|
| **Non-standard sector size** | Check if first 3 sectors are 128 bytes on a 256-byte DD image | Normal on DD — the Atari OS convention, but worth noting |
| **File larger than declared sectors** | Computed size = sectors × sectorSize vs ATR file length | Indicates extra data appended (common with some imaging tools) |
| **Phantom sectors** | Sectors whose chain references point to non-existent sectors | Corrupt filesystem or protection |
| **Sectors in VTOC free map WITH non-zero data** | VTOC bit = 1 and sector not all zeroes | Deleted file remnants — recoverable data |
| **Sectors NOT in any file chain and NOT in VTOC free map** | Neither reachable nor marked free | Hidden data — possibly protection, possibly corruption |
| **Sector chain loops** | Following a file chain visits the same sector twice | Corrupt file or deliberate protection trick |
| **Directory entries with invalid chains** | Start sector = 0 or sector count = 0 but flags non-zero | Corrupt entry |
| **Overlapping file chains** | Two different directory entries reference the same data sector | Could be intentional (shared data) or corruption |
| **Boot sector on non-bootable disk has high entropy** | Boot flag = $00 but boot sectors have high-entropy code | Suspicious — possibly a protection scheme or copy of boot code to data area |
| **Sector count mismatch** | VTOC total sectors ≠ geometry sector count | Non-standard formatting |

### 6.2 Anomaly Report Implementation

Rather than adding each anomaly to individual tools, create a unified [`analyze_anomalies`](Tools/AtrTools.cs) tool that runs all checks and produces a categorized report:

```csharp
[McpServerTool, Description("Analyze an ATR disk image for sector-level anomalies.")]
public static string AnalyzeAnomalies(
    [Description("Path to the ATR file.")] string filePath)
```

**Output format:**

```
ATR Anomaly Analysis: /roms/mystery.atr

Boot Sectors:
  [✓] Boot flag $00 — will attempt boot (sectors 1-3)
  [✓] 3 sectors to load — normal
  [!] High entropy in sector 1 (0.83) — bootable code despite non-DOS layout

Container Integrity:
  [✓] Paragraph count matches sector geometry
  [✓] No extra data beyond last sector
  [✓] Sector size consistent

Filesystem Integrity:
  [✓] All directory chains terminate properly
  [!] Sector 427 is marked free but contains non-zero data (431 bytes)
  [!] Sector 512 contains data but is not in any file chain
  [✓] No overlapping chains detected

Summary:
  2 anomalies found (0 critical, 2 informational)
```

---

## 7. File-Level Detail for DOS Disks

### 7.1 Current State (v2)

[`ReadDirectory()`](Atari/AtrParser.cs:95) returns [`AtrDirectoryEntry`](Atari/AtrParser.cs:8) with basic fields: index, filename, extension, start sector, sector count, deleted/locked/binary flags. [`ListAtrDirectory()`](Tools/AtrTools.cs:197) displays these in a table.

### 7.2 v3 Enhancements

**New fields on `AtrDirectoryEntry`:**

```csharp
public sealed record AtrDirectoryEntry(
    int Index,
    string FileName,
    string Extension,
    int StartSector,
    int SectorCount,
    bool IsDeleted,
    bool IsLocked,
    bool IsBinary,

    // v3 additions
    int? ActualByteCount,         // From last sector's byte-count field
    int? ChainLength,             // Number of sectors in chain (hops)
    int? FragmentationScore,      // Chain hops vs. contiguous allocation
    bool ChainValid,              // Does chain terminate properly?
    string? ChainIssue,           // Description of chain problem if any
    bool IsOpenForWrite,          // Dirty flag (DOS 2.x: flag bit 1)
    int RawFlags                  // The raw flags byte for expert inspection
);
```

**Fragmentation Score:**
A file that occupies N contiguous sectors has score 0 (perfect). Each non-contiguous jump in the chain increments the score. A file with N sectors spread across N-1 jumps has score N-1 (fully fragmented).

### 7.3 Enhanced Directory Listing

```
ATR Directory: /roms/fragmented.atr
  #  Filename     Ext  Sectors  Start   Bytes  Chain  Flags
  0  DOS          SYS      39    0004    4977     15  [system, chain OK]
  1  DUP          SYS      42    0043    5327     12  [system, chain OK]
  2  GAME         EXE     118    0085   15047    118  [binary, FRAGMENTED (87 jumps)]
  3  SAVE         DAT       1    0320      84      1  [open for write!]
  4  GHOST        ---       3    0203     368      3  [deleted]

Flag legend: binary = DOS 2 binary (load segments), locked = write-protected,
             open for write = improper dismount, deleted = recoverable

6 entries (1 deleted), 1 file with chain issues
```

### 7.4 Deleted File Recovery

When a file is deleted in DOS 2.x, its directory entry's flag byte is OR'd with `0x80`. The sector chain and sector count remain intact (unless overwritten). This means deleted files are often recoverable:

```csharp
public static byte[]? RecoverDeletedFile(byte[] data, AtrDirectoryEntry deletedEntry)
{
    // Same as ExtractFile, but operates on deleted entries
    // Warn if sectors appear to have been overwritten
}
```

The directory listing tool should flag deleted entries and optionally attempt recovery:

```
  4  GHOST        ---       3    0203      368      3  [deleted, RECOVERABLE]
```

### 7.5 Byte Count Validation

Each sector chain's last sector has a byte-count field at offset `-1` (last byte of the sector) indicating how many bytes of that sector are file data (vs. unused). For a file with N sectors:

```
Expected bytes = (N-1) × (sectorSize - 3) + lastSectorByteCount
```

The [`ExtractFile()`](Atari/AtrParser.cs:142) method already uses this field but doesn't validate it. The v3 directory listing should compute and display the actual byte count, flagging mismatches:

```
  5  WEIRD        DAT      5    0400      ???      5  [byte count mismatch — 512 expected, 480 in last sector]
```

---

## 8. ATR Container Metadata

### 8.1 Current State (v2)

[`AtrHeader()`](Tools/AtrTools.cs:164) displays the 16-byte ATR header: magic, image size, sector size, sector count, density, write-protect flag.

### 8.2 v3 Additions

| Field | Offset | Description |
|-------|--------|-------------|
| Paragraph count vs computed | — | Do the header paragraph fields agree with sector geometry? |
| Write-protect flag | 8 | Already shown |
| High-capacity flag | — | Some ATR variants use bit 7 of byte 8 or byte 9 for >720 sector images |
| CRC/checksum presence | — | Some ATR variants include a CRC; flag if present |
| ATR version hint | — | Based on byte usage, infer if this is a standard ATR or a variant |

### 8.3 Enhanced `AtrHeader` Output

```
ATR Header: /roms/game.atr
  Magic:           $0296 (standard ATR)
  Image size:      92160 bytes (5760 paragraphs)
  Sector geometry: 720 × 128 bytes = 92160 raw bytes + 16 header = 92176 total
  Sector size:     128 bytes
  Sector count:    720
  Density:         Single (SD)
  Write protect:   No
  ---
  Header integrity:
  [✓] Paragraph count matches computed size
  [✓] No extra bytes beyond last sector
  [✓] Standard ATR header (no CRC, no extended fields)
  ---
  File size:       92176 bytes (matches expected)
```

---

## 9. Disk Fingerprinting

### 9.1 Purpose

Enables matching against preservation databases (AtariAge, TOSEC, AtariMania) and detecting duplicates across collections.

### 9.2 Hashes to Compute

| Hash | Scope | Purpose |
|------|-------|---------|
| SHA-256 of full file | Entire ATR file (header + all sectors) | Exact duplicate detection |
| SHA-256 of boot sectors only | Sectors 1-3 (384 bytes) | Boot-loader fingerprinting — different disks with same loader |
| SHA-256 of data only | All sectors excluding boot, VTOC, directory | Content fingerprint — same game with different DOS formatting |
| MD5 (for TOSEC compat) | Full file | TOSEC/AtariAge databases use MD5 |

### 9.3 Implementation

```csharp
public sealed record DiskHashes(
    string Sha256Full,
    string Sha256BootSectors,
    string Sha256DataOnly,
    string Md5Full       // for TOSEC compatibility
);
```

### 9.4 Integration into `atr_info`

The main [`atr_info`](Tools/AtrTools.cs:17) tool should include hashes at the bottom:

```
---
Disk Fingerprints:
  SHA-256 (full):       e3b0c44298fc1c149afbf4c8996fb924...
  SHA-256 (boot):       a7ffc6f8bf1ed76651c14756a061d662...
  SHA-256 (data only):  6e340b9cffb37a989ca544e6bb780a2c...
  MD5 (full):           d41d8cd98f00b204e9800998ecf8427e
```

---

## 10. New Tool: `analyze_atr` (Unified Deep Analysis)

### 10.1 Rationale

Rather than requiring 5+ separate tool calls to get a complete picture of a disk, provide a single comprehensive analysis tool. It replaces/absorbs `atr_info` (v1/v2) and adds all v3 features.

### 10.2 Specification

```
Tool: analyze_atr
Category: Disk Image / Deep Analysis
Description: Perform comprehensive deep analysis of an ATR disk image.
             Returns DOS flavor identification, boot sector analysis,
             sector usage map, entropy map, directory listing with
             file-level detail, anomaly report, and disk fingerprints.

Parameters:
  filePath     string   yes   Path to the .atr file
  includeMaps  bool     no    Include sector map and entropy map. Default: true
  maxMapLines  int      no    Maximum lines for sector/entropy maps. Default: 40
```

### 10.3 Output Structure

```
═══════════════════════════════════════════════════════════════
ATR DEEP ANALYSIS: /roms/mystery.atr
═══════════════════════════════════════════════════════════════

─── Header ───────────────────────────────────────────────────
  Magic: $0296 | Size: 92176 | Sectors: 720 × 128 bytes
  Density: Single (SD) | Write protect: No

─── DOS Identification ───────────────────────────────────────
  DOS flavor:    Atari DOS 2.0s
  VTOC sector:   360
  VTOC format:   $08 (8 directory sectors)
  Total sectors: 707 (from VTOC)
  Free sectors:  402 (from VTOC) / 402 (counted from bitmap)
  Dir entries:   16 bytes × 64 max (8 sectors × 8)
  Confidence:    High

─── Boot Sector ──────────────────────────────────────────────
  Boot flag:       $00 (continue loading)
  Sectors to load: 3
  Load address:    $0700
  Init address:    $0700
  Entry point:     $0706
  DOS boot:        Yes (init at $0700-$07FF)
  Fingerprint:     Atari DOS 2.0s Boot (matched at $0706)
  Code density:    0.72 (valid 6502 opcodes)
  First instructions at $0706:
    0706  78           SEI
    0707  D8           CLD
    0708  A2 00        LDX #$00
    070A  9A           TXS
    ...

─── Sector Map ───────────────────────────────────────────────
  720 sectors, 9 lines × 80:
  BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB...
  ... (full map) ...

─── Entropy Map ──────────────────────────────────────────────
  720 sectors, 9 lines × 80:
  ░░░▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓█...
  ... (full map) ...

─── Directory ────────────────────────────────────────────────
  #  Filename     Ext  Sectors  Start   Bytes  Chain  Flags
  0  DOS          SYS      39    0004    4977     15  [system, OK]
  1  DUP          SYS      42    0043    5327     12  [system, OK]
  2  GAME         EXE     118    0085   15047     43  [binary, FRAGMENTED]
  3  DATA         DAT      80    0203   10003     80  [OK]
  4  SAVE         DAT       1    0320      84      1  [open for write!]

  5 active entries, 0 deleted
  279 sectors used, 402 free (match VTOC)

─── Anomalies ────────────────────────────────────────────────
  [!] GAME.EXE: fragmented — 43 non-contiguous chain segments
  [!] SAVE.DAT: open for write (flag bit 1 set) — improper dismount?
  [✓] No sector overlaps detected
  [✓] No hidden sectors
  [✓] No orphaned sectors
  [✓] All chains terminate correctly

─── Statistics ───────────────────────────────────────────────
  Empty sectors:        402 (55.8%)
  Data sectors:         279 (38.8%)
  Structural sectors:    12 (1.7%)  [3 boot + 1 VTOC + 8 dir]
  Average entropy:       0.31
  Highest entropy:       0.98 (sector 35 — compressed data?)

─── Fingerprints ─────────────────────────────────────────────
  SHA-256 (full):       e3b0c44298fc1c149afbf4c8996fb924...
  SHA-256 (boot):       a7ffc6f8bf1ed76651c14756a061d662...
  MD5 (full):           d41d8cd98f00b204e9800998ecf8427e
═══════════════════════════════════════════════════════════════
```

---

## 11. Architecture Impact

### 11.1 New Files

| File | Purpose |
|------|---------|
| `Atari/DosDetection.cs` | `DosFlavor` enum, `DosInfo` record, `IdentifyDos()` method, VTOC parsing for each flavor |
| `Atari/BootFingerprints.cs` | `BootSignature` record, fingerprint database, matching logic |
| `Atari/DiskAnalysis.cs` | `GenerateSectorMap()`, `GenerateEntropyMap()`, `SectorEntropy()`, anomaly detection, statistics computation |
| `docs/version3-design.md` | This document |

### 11.2 Modified Files

| File | Changes |
|------|---------|
| [`Atari/AtrParser.cs`](Atari/AtrParser.cs) | Expand `AtrDirectoryEntry` record with v3 fields; add MyDOS directory parsing path; add `IdentifyDos()`; add sector chain validation methods; add deleted file recovery |
| [`Tools/AtrTools.cs`](Tools/AtrTools.cs) | Replace `AtrInfo()` with calls to new unified analysis; add `AnalyzeAnomalies()` tool; enhance `AnalyzeBootSector()` with fingerprint matching; enhance `ListAtrDirectory()` with v3 entry details; enhance `AtrHeader()` with integrity checks |
| [`docs/specification.md`](docs/specification.md) | Update tool catalog and data model documentation |
| [`State/RomSession.cs`](State/RomSession.cs) | No changes needed — `BootHeader` already exists from v2 |

### 11.3 Backward Compatibility

- `HasDosFilesystem()` retains its signature as a convenience wrapper
- Existing tool signatures unchanged; they gain additional output fields
- `AtrInfo()` output is expanded (additive, not breaking)
- New `AtrDirectoryEntry` fields are additive; all existing callers continue to work
- `ReadDirectory()` internally uses new parsing logic but returns the same record type

### 11.4 Non-DOS Disk Handling

The v3 analysis is especially valuable for non-DOS disks, where structure-based analysis fails. The sector map and entropy map become the primary analysis tools:

```
ATR DEEP ANALYSIS: /roms/homebrew.atr
────────────────────────────────────────────────────────────
DOS Identification:
  DOS flavor: No DOS filesystem detected
  This disk uses a custom/non-DOS layout.

Boot Sector:
  Boot flag: $00 | 70 sectors to load
  Load: $3C07 | Init: $4C1A
  Fingerprint: Custom game loader (non-DOS init address)
  Code density: 0.89 — executable code detected

Sector Map:
  ... (usage map is still informative — shows data distribution) ...

Entropy Map:
  ... (entropy map shows exactly where code/data lives) ...

Statistics:
  Empty sectors: 12 (1.7%)  ← almost full disk!
  Average entropy: 0.78  ← very high — packed with code/data
  Boot sector entropy: 0.91  ← complex loader

Fingerprints:
  SHA-256 (full): ...
  (Data-only hash not available — no DOS filesystem to identify data region)
```

---

## 12. Updated Tool Catalog (v3)

Tools marked **V3** are new; **ENHANCED** are significantly upgraded from v2.

| # | Tool | Category | Status | Change Summary |
|---|------|----------|--------|----------------|
| 1 | `load_rom` | File | — | No change |
| 2 | `rom_info` | File | — | No change |
| 3 | `atr_info` | Disk Image | **ENHANCED** | Absorbed into `analyze_atr`; now shows DOS flavor, boot fingerprint, sector/entropy maps, anomalies, fingerprints |
| 4 | `load_atr_file` | Disk Image | — | No change |
| 5 | `load_atr_boot` | Disk Image | — | No change |
| 6 | `atr_header` | Disk Image | **ENHANCED** | Adds paragraph-vs-computed integrity check, format variant hints |
| 7 | `list_atr_directory` | Disk Image | **ENHANCED** | Shows byte counts, chain length, fragmentation, dirty/deleted flags with recovery hints |
| 8 | `sector_dump` | Disk Image | — | No change |
| 9 | `analyze_boot_sector` | Disk Image | **ENHANCED** | Adds fingerprint matching, code density, boot code preview |
| 10 | `search_boot_sector` | Disk Image | — | No change |
| 11 | `analyze_atr` | Disk Image | **V3** | Unified deep analysis replacing standalone `atr_info` |
| 12 | `analyze_anomalies` | Disk Image | **V3** | Sector-level anomaly detection |
| 13 | `hex_dump` | Inspection | — | No change |
| 14 | `disassemble` | Disassembly | — | No change |
| 15 | `calculate` | Utility | — | No change |
| 16 | `hex_to_decimal` | Utility | — | No change |
| 17 | `decimal_to_hex` | Utility | — | No change |
| 18 | `define_symbol` | Symbol Table | — | No change |
| 19 | `remove_symbol` | Symbol Table | — | No change |
| 20 | `lookup_symbol` | Symbol Table | — | No change |
| 21 | `list_symbols` | Symbol Table | — | No change |
| 22 | `annotate_zero_page` | Zero Page | — | No change |
| 23 | `show_zero_page_map` | Zero Page | — | No change |
| 24 | `x_ref` | Analysis | — | No change |
| 25 | `find_pattern` | Analysis | — | No change |
| 26 | `find_strings` | Analysis | — | No change |
| 27 | `trace_control_flow` | Analysis | — | No change |

---

## 13. Implementation Phases

| Phase | Items | Rationale |
|-------|-------|-----------|
| **Phase 1: Data model** | `DosFlavor`, `DosInfo`, expanded `AtrDirectoryEntry`, `BootAnalysis`, `DiskHashes`, `BootSignature` | Foundation records/enums needed by all other work |
| **Phase 2: Core engines** | `IdentifyDos()`, MyDOS directory parsing, sector chain validation, `SectorEntropy()`, `GenerateSectorMap()`, `GenerateEntropyMap()` | Pure computation, no tool surface yet |
| **Phase 3: Enhanced tools** | Enhance `analyze_boot_sector` with fingerprints, enhance `list_atr_directory` with v3 fields, enhance `atr_header` with integrity checks | Upgrades to existing tools with backward-compatible output |
| **Phase 4: New tools** | `analyze_atr` (unified), `analyze_anomalies` | New tool surface |
| **Phase 5: Polish** | Fingerprint database expansion, edge case hardening, documentation updates | Final quality pass |

---

## 14. Architecture Diagram (v3)

```
┌──────────────────────────────────────────────────────────────────┐
│  LLM Client                                                       │
│  stdio JSON-RPC (MCP protocol)                                    │
└───────────────────────────┬──────────────────────────────────────┘
                            │
┌───────────────────────────▼──────────────────────────────────────┐
│  AtariHackerMCP Process (v3)                                      │
│                                                                    │
│  ┌────────────────────────────────────────────────────────────┐  │
│  │  Program.cs — DI container / stdio transport                │  │
│  └──────────────┬─────────────────────────────────────────────┘  │
│                 │                                                  │
│  ┌──────────────▼─────────────────────────────────────────────┐  │
│  │  Tool Classes                                               │  │
│  │                                                              │  │
│  │  AtrTools (v3 expanded):                                    │  │
│  │    LoadAtrFile  LoadAtrBoot                                 │  │
│  │    AtrHeader [ENHANCED]  ListAtrDirectory [ENHANCED]        │  │
│  │    SectorDump  AnalyzeBootSector [ENHANCED]                 │  │
│  │    SearchBootSector                                         │  │
│  │    AnalyzeAtr [NEW]  AnalyzeAnomalies [NEW]                 │  │
│  │                                                              │  │
│  │  (all other tools unchanged)                                 │  │
│  └──────────────┬─────────────────────────────────────────────┘  │
│                 │                                                  │
│  ┌──────────────▼─────────────────────────────────────────────┐  │
│  │  Session State (unchanged from v2)                           │  │
│  │  RomSession  ·  SymbolTable  ·  ZeroPageMap  ·  Persistence │  │
│  └──────────────┬─────────────────────────────────────────────┘  │
│                 │                                                  │
│  ┌──────────────▼─────────────────────────────────────────────┐  │
│  │  Atari Domain Layer (v3 expanded)                            │  │
│  │                                                              │  │
│  │  AtrParser.cs [ENHANCED]:                                    │  │
│  │    + IdentifyDos() → DosFlavor + DosInfo                     │  │
│  │    + Expanded AtrDirectoryEntry (v3 fields)                  │  │
│  │    + MyDOS 32-byte entry parsing                             │  │
│  │    + Sector chain validation                                 │  │
│  │    + Deleted file recovery                                   │  │
│  │                                                              │  │
│  │  DosDetection.cs [NEW]:                                      │  │
│  │    DosFlavor enum, DosInfo record                            │  │
│  │    Per-DOS-type VTOC parsing                                 │  │
│  │                                                              │  │
│  │  BootFingerprints.cs [NEW]:                                  │  │
│  │    BootSignature record, fingerprint database                │  │
│  │    Fingerprint matching engine                               │  │
│  │                                                              │  │
│  │  DiskAnalysis.cs [NEW]:                                      │  │
│  │    GenerateSectorMap()  GenerateEntropyMap()                 │  │
│  │    SectorEntropy()  Anomaly detection                        │  │
│  │    Hash computation (SHA-256, MD5)                           │  │
│  │    Statistics aggregation                                    │  │
│  │                                                              │  │
│  │  (unchanged: Opcodes6502, XexParser, AtariHardwareMap,       │  │
│  │   AtasciiDecoder)                                            │  │
│  └─────────────────────────────────────────────────────────────┘  │
└──────────────────────────────────────────────────────────────────┘
```

---

## 15. Summary of Changes from v2

| Area | v2 | v3 |
|------|----|----|
| DOS detection | Binary yes/no (`HasDosFilesystem`) | Multi-flavor (`IdentifyDos` → `DosFlavor` + `DosInfo`) |
| Directory parsing | Atari DOS 2.x only (16-byte entries) | Atari DOS 2.x + MyDOS (32-byte entries) + SpartaDOS detection path |
| Boot analysis | Header decode only | Header + fingerprint matching + code density + instruction preview |
| Directory listing | Filename, extension, sectors, start, flags | + byte counts, chain length, fragmentation, dirty/deleted flags, recovery hints |
| Sector visualization | None | Sector map (80-char lines) + entropy map (░▒▓█) |
| Anomaly detection | None | Comprehensive sector-level anomaly checks |
| Container metadata | Basic header fields | + integrity checks, paragraph-vs-computed, format variant hints |
| Disk fingerprinting | None | SHA-256 (full, boot-only, data-only) + MD5 for TOSEC compatibility |
| Tool count | 10 ATR tools | 12 ATR tools (+`analyze_atr`, +`analyze_anomalies`) |
| Non-DOS disk utility | Minimal (error messages) | Full: sector/entropy maps, boot analysis, statistics — useful even without filesystem |
| Code files | 2 domain files (AtrParser, AtrTools) | 5 domain files (+DosDetection, +BootFingerprints, +DiskAnalysis) |
