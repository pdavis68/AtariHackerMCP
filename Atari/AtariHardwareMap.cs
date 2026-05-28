using AtariHackerMCP.State;

namespace AtariHackerMCP.Atari;

public static class AtariHardwareMap
{
    public static IReadOnlyDictionary<ushort, SymbolEntry> HardwareSymbols { get; } =
        new Dictionary<ushort, SymbolEntry>
        {
            [0xC000] = Hardware("SYSVBL"),
            [0xC002] = Hardware("SYSVBV"),
            [0xC00C] = Hardware("SETVBV"),
            [0xC012] = Hardware("XITVBV"),
            [0xC300] = Hardware("CIOV"),
            [0xC400] = Hardware("SIOV"),
            [0xD000] = Hardware("HPOSP0"),
            [0xD001] = Hardware("HPOSP1"),
            [0xD002] = Hardware("HPOSP2"),
            [0xD003] = Hardware("HPOSP3"),
            [0xD004] = Hardware("HPOSM0"),
            [0xD005] = Hardware("HPOSM1"),
            [0xD006] = Hardware("HPOSM2"),
            [0xD007] = Hardware("HPOSM3"),
            [0xD008] = Hardware("SIZEP0"),
            [0xD009] = Hardware("SIZEP1"),
            [0xD00A] = Hardware("SIZEP2"),
            [0xD00B] = Hardware("SIZEP3"),
            [0xD00C] = Hardware("SIZEM"),
            [0xD00D] = Hardware("GRAFP0"),
            [0xD00E] = Hardware("GRAFP1"),
            [0xD00F] = Hardware("GRAFP2"),
            [0xD010] = Hardware("GRAFP3"),
            [0xD011] = Hardware("GRAFM"),
            [0xD012] = Hardware("COLPM0"),
            [0xD013] = Hardware("COLPM1"),
            [0xD014] = Hardware("COLPM2"),
            [0xD015] = Hardware("COLPM3"),
            [0xD016] = Hardware("COLPF0"),
            [0xD017] = Hardware("COLPF1"),
            [0xD018] = Hardware("COLPF2"),
            [0xD019] = Hardware("COLPF3"),
            [0xD01A] = Hardware("COLBK"),
            [0xD01B] = Hardware("PRIOR"),
            [0xD01D] = Hardware("GRACTL"),
            [0xD01F] = Hardware("CONSOL"),
            [0xD200] = Hardware("AUDF1"),
            [0xD201] = Hardware("AUDC1"),
            [0xD202] = Hardware("AUDF2"),
            [0xD203] = Hardware("AUDC2"),
            [0xD204] = Hardware("AUDF3"),
            [0xD205] = Hardware("AUDC3"),
            [0xD206] = Hardware("AUDF4"),
            [0xD207] = Hardware("AUDC4"),
            [0xD208] = Hardware("AUDCTL"),
            [0xD209] = Hardware("STIMER"),
            [0xD20A] = Hardware("SKREST"),
            [0xD20B] = Hardware("POTGO"),
            [0xD20D] = Hardware("SEROUT"),
            [0xD20E] = Hardware("IRQEN"),
            [0xD20F] = Hardware("SKCTL"),
            [0xD209] = Hardware("KBCODE"),
            [0xD20A] = Hardware("RANDOM"),
            [0xD20E] = Hardware("IRQST"),
            [0xD300] = Hardware("PORTA"),
            [0xD301] = Hardware("PORTB"),
            [0xD302] = Hardware("PACTL"),
            [0xD303] = Hardware("PBCTL"),
            [0xD400] = Hardware("DMACTL"),
            [0xD401] = Hardware("CHACTL"),
            [0xD402] = Hardware("DLISTL"),
            [0xD403] = Hardware("DLISTH"),
            [0xD404] = Hardware("HSCROL"),
            [0xD405] = Hardware("VSCROL"),
            [0xD407] = Hardware("PMBASE"),
            [0xD409] = Hardware("CHBASE"),
            [0xD40A] = Hardware("WSYNC"),
            [0xD40B] = Hardware("VCOUNT"),
            [0xD40E] = Hardware("NMIEN"),
            [0xD40F] = Hardware("NMIST")
        };

    public static IReadOnlyDictionary<byte, SymbolEntry> ZeroPageSymbols { get; } =
        new Dictionary<byte, SymbolEntry>
        {
            [0x02] = Hardware("RTCLOK2"),
            [0x03] = Hardware("RTCLOK1"),
            [0x04] = Hardware("RTCLOK0"),
            [0x0A] = Hardware("DOSVEC"),
            [0x0C] = Hardware("DOSINI"),
            [0x14] = Hardware("POKMSK"),
            [0x18] = Hardware("RTCLOCK"),
            [0x2C] = Hardware("ICAX1Z"),
            [0x42] = Hardware("RUNAD"),
            [0x44] = Hardware("INITAD"),
            [0x4E] = Hardware("COLDST"),
            [0x58] = Hardware("SAVMSC"),
            [0x59] = Hardware("SAVMSCH"),
            [0x5A] = Hardware("OLDROW"),
            [0x5B] = Hardware("OLDCOL"),
            [0x70] = Hardware("SDLSTL"),
            [0x71] = Hardware("SDLSTH"),
            [0x72] = Hardware("SSKCTL"),
            [0x77] = Hardware("ROWCRS"),
            [0x78] = Hardware("COLCRS"),
            [0x79] = Hardware("DINDEX"),
            [0x7C] = Hardware("CH"),
            [0x82] = Hardware("DSTAT"),
            [0x84] = Hardware("ATRACT"),
            [0x85] = Hardware("DRKMSK"),
            [0x86] = Hardware("COLRSH"),
            [0x88] = Hardware("LMARGN"),
            [0x89] = Hardware("RMARGN"),
            [0x8A] = Hardware("ROWINC"),
            [0x8B] = Hardware("COLINC"),
            [0xD0] = Hardware("COLOR0"),
            [0xD1] = Hardware("COLOR1"),
            [0xD2] = Hardware("COLOR2"),
            [0xD3] = Hardware("COLOR3"),
            [0xD4] = Hardware("COLOR4"),
            [0xD8] = Hardware("STICK0"),
            [0xD9] = Hardware("STICK1"),
            [0xDA] = Hardware("STICK2"),
            [0xDB] = Hardware("STICK3"),
            [0xDC] = Hardware("STRIG0"),
            [0xDD] = Hardware("STRIG1"),
            [0xDE] = Hardware("STRIG2"),
            [0xDF] = Hardware("STRIG3")
        };

    public static void Populate(SymbolTable table)
    {
        table.Clear();
        foreach (var pair in HardwareSymbols)
        {
            table[pair.Key] = pair.Value;
        }
    }

    public static void PopulateZeroPage(ZeroPageMap map)
    {
        map.Clear();
        foreach (var pair in ZeroPageSymbols)
        {
            map[pair.Key] = pair.Value;
        }
    }

    public static bool TryGetHardwareSymbol(ushort address, out SymbolEntry entry) =>
        HardwareSymbols.TryGetValue(address, out entry!);

    public static bool TryGetZeroPageHardwareSymbol(byte address, out SymbolEntry entry) =>
        ZeroPageSymbols.TryGetValue(address, out entry!);

    private static SymbolEntry Hardware(string label) => new(label, null, true, false);
}
