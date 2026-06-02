# AtariHackerMCP

A local MCP server for reverse-engineering Atari 8-bit binaries and ATR disk images.

## Requirements

- .NET SDK 10.0+
- Linux, macOS, or Windows

## Build

Restore packages and compile:

```bash
dotnet build
```

Publish a release build if needed:

```bash
dotnet publish -c Release -o publish
```

## Run

This server uses MCP stdio transport, so it is normally launched by an MCP client rather than run interactively.

For local testing:

```bash
dotnet run --no-build
```

Or run the published executable:

```bash
./publish/AtariHackerMCP
```

## MCP Client Configuration

Example stdio server entry:

```json
{
  "mcpServers": {
    "atari-hacker": {
      "command": "dotnet",
      "args": [
        "run",
        "--project",
        "/absolute/path/to/AtariHackerMCP/AtariHackerMCP.csproj",
        "--no-build"
      ]
    }
  }
}
```

If you prefer using a published build:

```json
{
  "mcpServers": {
    "atari-hacker": {
      "command": "/absolute/path/to/AtariHackerMCP/publish/AtariHackerMCP",
      "args": []
    }
  }
}
```

## Available Tools

### File and disk tools

- `load_rom`
- `rom_info`
- `atr_info`
- `atr_header`
- `load_atr_file`
- `load_atr_boot`
- `list_atr_directory`
- `analyze_boot_sector`
- `sector_dump`
- `search_boot_sector`

### Inspection and disassembly

- `hex_dump`
- `disassemble`

### Utility tools

- `calculate`
- `hex_to_decimal`
- `decimal_to_hex`

### Symbol and zero-page tools

- `define_symbol`
- `remove_symbol`
- `lookup_symbol`
- `list_symbols`
- `annotate_zero_page`
- `show_zero_page_map`

### Analysis tools

- `x_ref`
- `find_pattern`
- `find_strings`
- `trace_control_flow`

## Typical Workflow

1. Load a file with `load_rom`, `load_atr_file`, or `load_atr_boot`.
2. Inspect structure with `rom_info` or `atr_info`.
3. For ATR disk images: use `atr_header` for container metadata, `list_atr_directory` for DOS file listings, `analyze_boot_sector` to decode the boot header, `sector_dump` to inspect raw sectors, and `search_boot_sector` to compare boot code across images.
4. Review bytes with `hex_dump`.
5. Decode code with `disassemble`.
6. Add labels with `define_symbol` and `annotate_zero_page`.
7. Search with `x_ref`, `find_pattern`, `find_strings`, and `trace_control_flow`.

## Persistence

User-defined symbols and zero-page annotations are saved automatically to a sidecar JSON file next to the loaded target:

```text
<rom-or-synthetic-path>.atarihacker.json
```

This file is loaded automatically the next time the same target is opened.

## Example MCP Smoke Test

This sends `initialize` and `tools/list` directly over stdio:

```bash
(
  printf '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"test-client","version":"1.0"}}}\n'
  sleep 0.2
  printf '{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}\n'
  sleep 1
) | dotnet run --no-build
```

## Notes

- Logs are written to stderr so stdout remains clean for MCP JSON-RPC traffic.
- The current build may show a NuGet compatibility warning for `NCalc`, but the server builds and runs successfully.

## License

This project is licensed under the MIT License. See the [LICENSE](LICENSE) file for details.
