using System.Text.Json;
using AtariHackerMCP.Atari;
using AtariHackerMCP.Helpers;

namespace AtariHackerMCP.State;

public sealed class SessionPersistence(RomSession session, SymbolTable symbols, ZeroPageMap zeroPageMap)
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public void ResetToDefaults()
    {
        AtariHardwareMap.Populate(symbols);
        AtariHardwareMap.PopulateZeroPage(zeroPageMap);
    }

    public void Save()
    {
        if (string.IsNullOrWhiteSpace(session.FilePath))
        {
            return;
        }

        var sidecarPath = GetSidecarPath(session.FilePath);
        var parent = Path.GetDirectoryName(sidecarPath);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }

        var payload = new SessionSidecar(
            session.FilePath,
            symbols.OrderBy(pair => pair.Key).ToDictionary(
                pair => $"0x{pair.Key:X4}",
                pair => new PersistedSymbol(pair.Value.Label, pair.Value.Comment, pair.Value.IsHardware, pair.Value.IsUserDefined)),
            zeroPageMap.OrderBy(pair => pair.Key).ToDictionary(
                pair => $"0x{pair.Key:X2}",
                pair => new PersistedSymbol(pair.Value.Label, pair.Value.Comment, pair.Value.IsHardware, pair.Value.IsUserDefined)));

        File.WriteAllText(sidecarPath, JsonSerializer.Serialize(payload, SerializerOptions));
    }

    public bool TryLoad(string romPath)
    {
        ResetToDefaults();

        var sidecarPath = GetSidecarPath(romPath);
        if (!File.Exists(sidecarPath))
        {
            return false;
        }

        var sidecar = JsonSerializer.Deserialize<SessionSidecar>(File.ReadAllText(sidecarPath), SerializerOptions);
        if (sidecar is null)
        {
            return false;
        }

        foreach (var pair in sidecar.Symbols)
        {
            var address = AddressParser.ParseAddress(pair.Key);
            symbols[address] = new SymbolEntry(pair.Value.Label, pair.Value.Comment, pair.Value.IsHardware, pair.Value.IsUserDefined);
        }

        foreach (var pair in sidecar.ZeroPage)
        {
            var address = AddressParser.ParseZeroPageAddress(pair.Key);
            zeroPageMap[address] = new SymbolEntry(pair.Value.Label, pair.Value.Comment, pair.Value.IsHardware, pair.Value.IsUserDefined);
        }

        foreach (var pair in AtariHardwareMap.HardwareSymbols)
        {
            symbols.TryAdd(pair.Key, pair.Value);
        }

        foreach (var pair in AtariHardwareMap.ZeroPageSymbols)
        {
            zeroPageMap.TryAdd(pair.Key, pair.Value);
        }

        return true;
    }

    public static string GetSidecarPath(string romPath)
    {
        var candidateDirectory = Path.GetDirectoryName(romPath);
        if (!string.IsNullOrWhiteSpace(candidateDirectory) && Directory.Exists(candidateDirectory))
        {
            return romPath + ".atarihacker.json";
        }

        var fullPath = Path.GetFullPath(romPath);
        var current = fullPath;
        while (!string.IsNullOrWhiteSpace(current) && !Directory.Exists(current))
        {
            current = Path.GetDirectoryName(current);
        }

        if (string.IsNullOrWhiteSpace(current))
        {
            return fullPath.Replace(Path.DirectorySeparatorChar, '_').Replace(Path.AltDirectorySeparatorChar, '_') + ".atarihacker.json";
        }

        var relative = Path.GetRelativePath(current, fullPath)
            .Replace(Path.DirectorySeparatorChar, '_')
            .Replace(Path.AltDirectorySeparatorChar, '_');
        return Path.Combine(current, relative + ".atarihacker.json");
    }

    private sealed record SessionSidecar(
        string? RomPath,
        Dictionary<string, PersistedSymbol> Symbols,
        Dictionary<string, PersistedSymbol> ZeroPage);

    private sealed record PersistedSymbol(
        string Label,
        string? Comment,
        bool IsHardware,
        bool IsUserDefined);
}
