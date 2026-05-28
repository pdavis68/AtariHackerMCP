using AtariHackerMCP.Atari;

namespace AtariHackerMCP.State;

public sealed class RomSession
{
    public string? FilePath { get; set; }

    public byte[]? Data { get; set; }

    public IReadOnlyList<XexSegment>? Segments { get; set; }

    public ushort? RunAddress { get; set; }

    public ushort? InitAddress { get; set; }

    public ushort? BaseAddress { get; set; }

    public int Length => Data?.Length ?? 0;

    public bool IsLoaded => Data is not null;

    public void Load(string filePath, byte[] data)
    {
        FilePath = filePath;
        Data = data;
        Segments = null;
        RunAddress = null;
        InitAddress = null;
        BaseAddress = null;
    }

    public void ClearMetadata()
    {
        Segments = null;
        RunAddress = null;
        InitAddress = null;
        BaseAddress = null;
    }
}
