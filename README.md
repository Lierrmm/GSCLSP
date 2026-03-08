# GSCLSP: GSC Language Server Protocol

![Visual Studio Marketplace Version](https://img.shields.io/visual-studio-marketplace/v/bbe-tools.gsclsp?style=flat-square&logo=visual-studio-code)
![Visual Studio Marketplace Installs](https://img.shields.io/visual-studio-marketplace/i/bbe-tools.gsclsp?style=flat-square)
![Visual Studio Marketplace Rating](https://img.shields.io/visual-studio-marketplace/r/bbe-tools.gsclsp?style=flat-square)

The core engine behind the GSC Language Support extension. This repository contains both the .NET 10 Language Server and the TypeScript VS Code client.

## Project Structure

- `GSCLSP.Server/`: The C# Language Server using OmniSharp.
- `GSCLSP.Extension/`: The VS Code extension client (TypeScript).
- `GSCLSP.Extension/out/`: Final binaries and data files for distribution.

## Tech Stack

- **Server**: .NET 8 (Self-contained, Single-file, Compressed).
- **Client**: TypeScript / Node.js.
- **Package Manager**: Bun.

## Development

### Prerequisites

- .NET 10 SDK
- Bun (https://bun.sh)

### Building the Server

```bash
dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:EnableCompressionInSingleFile=true
```
