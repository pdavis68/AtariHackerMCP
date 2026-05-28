using System.ComponentModel;
using AtariHackerMCP.Atari;
using AtariHackerMCP.Helpers;
using AtariHackerMCP.State;
using ModelContextProtocol.Server;

namespace AtariHackerMCP.Tools;

[McpServerToolType]
public static class AtrTools
{
    [McpServerTool, Description("Display structural information about an ATR disk image.")]
    public static string AtrInfo([Description("Path to the ATR file.")] string filePath)
    {
        try
        {
            var resolvedPath = Path.GetFullPath(filePath);
            var bytes = File.ReadAllBytes(resolvedPath);
            if (!AtrParser.IsAtr(bytes))
            {
                return "ERROR: Not a valid ATR image.";
            }

            var geometry = AtrParser.ParseGeometry(bytes);
            var directory = AtrParser.ReadDirectory(bytes).Where(entry => !entry.IsDeleted).ToList();
            var lines = new List<string>
            {
                $"ATR Disk Image: {resolvedPath}",
                $"Density  : {DescribeDensity(geometry)}",
                $"Sectors  : {geometry.SectorCount} x {geometry.SectorSize} bytes = {geometry.SectorCount * geometry.SectorSize:N0} bytes",
                $"Free     : {AtrParser.FreeSegmentCount(bytes, geometry)} sectors",
                string.Empty,
                "Directory:",
                "  #  Filename     Ext  Sectors  Bytes   Start  Flags"
            };

            foreach (var entry in directory)
            {
                var extracted = AtrParser.ExtractFile(bytes, geometry, entry);
                var flags = new List<string>();
                if (entry.IsBinary) flags.Add("binary");
                if (entry.IsLocked) flags.Add("locked");
                var displayFlags = flags.Count == 0 ? "[]" : $"[{string.Join(',', flags)}]";
                lines.Add($"  {entry.Index,2}  {entry.FileName,-12} {entry.Extension,-3} {entry.SectorCount,7} {extracted.Length,6} {entry.StartSector,6}  {displayFlags}");
            }

            return string.Join('\n', lines);
        }
        catch (Exception ex)
        {
            return $"ERROR: {ex.Message}";
        }
    }

    [McpServerTool, Description("Extract a file from an ATR image and load it into the active session.")]
    public static string LoadAtrFile(
        RomSession session,
        SymbolTable symbols,
        ZeroPageMap zeroPageMap,
        SessionPersistence persistence,
        [Description("Path to the ATR file.")] string filePath,
        [Description("Atari DOS filename to extract.")] string fileName)
    {
        try
        {
            var resolvedPath = Path.GetFullPath(filePath);
            var bytes = File.ReadAllBytes(resolvedPath);
            if (!AtrParser.IsAtr(bytes))
            {
                return "ERROR: Not a valid ATR image.";
            }

            var geometry = AtrParser.ParseGeometry(bytes);
            var directory = AtrParser.ReadDirectory(bytes);
            var match = MatchEntry(directory, fileName);
            if (match is null)
            {
                return $"ERROR: File \"{fileName}\" not found in ATR directory.";
            }

            if (match.IsDeleted)
            {
                return $"ERROR: File \"{fileName}\" exists but is deleted.";
            }

            var extracted = AtrParser.ExtractFile(bytes, geometry, match);
            var syntheticPath = BuildSyntheticPath(resolvedPath, match);
            session.Load(syntheticPath, extracted);
            FileTools.PopulateMetadata(session, extracted);
            var sidecarLoaded = persistence.TryLoad(syntheticPath);
            return $"Extracted {match.FileName}{(string.IsNullOrWhiteSpace(match.Extension) ? string.Empty : "." + match.Extension)} from ATR.\n" + FileTools.BuildRomInfo(session, symbols, zeroPageMap, sidecarLoaded);
        }
        catch (Exception ex)
        {
            return $"ERROR: {ex.Message}";
        }
    }

    [McpServerTool, Description("Extract the boot sectors from an ATR image and load them at base address $0700.")]
    public static string LoadAtrBoot(
        RomSession session,
        SessionPersistence persistence,
        [Description("Path to the ATR file.")] string filePath)
    {
        try
        {
            var resolvedPath = Path.GetFullPath(filePath);
            var bytes = File.ReadAllBytes(resolvedPath);
            if (!AtrParser.IsAtr(bytes))
            {
                return "ERROR: Not a valid ATR image.";
            }

            var boot = AtrParser.ExtractBootSectors(bytes);
            var syntheticPath = resolvedPath + "/BOOT";
            session.Load(syntheticPath, boot);
            session.BaseAddress = 0x0700;
            persistence.TryLoad(syntheticPath);
            return $"Loaded ATR boot sectors: {boot.Length} bytes at $0700\n" + HexDumpTool.GenerateHexDump(session, 0, boot.Length, 0x0700);
        }
        catch (Exception ex)
        {
            return $"ERROR: {ex.Message}";
        }
    }

    private static AtrDirectoryEntry? MatchEntry(IEnumerable<AtrDirectoryEntry> entries, string requestedName)
    {
        var normalized = requestedName.Trim().ToUpperInvariant();
        return entries.FirstOrDefault(entry =>
        {
            var fullName = string.IsNullOrWhiteSpace(entry.Extension)
                ? entry.FileName
                : $"{entry.FileName}.{entry.Extension}";
            return string.Equals(entry.FileName, normalized, StringComparison.OrdinalIgnoreCase)
                || string.Equals(fullName, normalized, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static string BuildSyntheticPath(string atrPath, AtrDirectoryEntry entry)
    {
        var fileName = string.IsNullOrWhiteSpace(entry.Extension) ? entry.FileName : $"{entry.FileName}.{entry.Extension}";
        return atrPath + "/" + fileName;
    }

    private static string DescribeDensity(AtrGeometry geometry) => geometry.Density switch
    {
        "SD" => "Single (SD)",
        "ED" => "Enhanced (ED)",
        "DD" => "Double (DD)",
        "Extended" => "Extended",
        _ => geometry.Density
    };
}
