# GSCLSP: GSC Language Server Protocol

![Visual Studio Marketplace Version](https://img.shields.io/visual-studio-marketplace/v/bbe-tools.gsclsp?style=flat-square&logo=visual-studio-code)
![Visual Studio Marketplace Installs](https://img.shields.io/visual-studio-marketplace/i/bbe-tools.gsclsp?style=flat-square)
![Visual Studio Marketplace Rating](https://img.shields.io/visual-studio-marketplace/r/bbe-tools.gsclsp?style=flat-square)

The core engine behind the GSC Language Support extension. This repository contains both the .NET 10 Language Server and the TypeScript VS Code client.

## Project Structure

- `GSCLSP.Core/`: The C# Library that handles all parsing & indexing logic
- `GSCLSP.Server/`: The C# Language Server using OmniSharp.
- `GSCLSP.Extension/`: The VS Code extension client (TypeScript).
- `GSCLSP.Extension/out/`: Final binaries and data files for distribution.

## Tech Stack

- **Server**: .NET 10 (Self-contained, Single-file, Compressed)
- **Client**: TypeScript
- **Package Manager**: Bun

## Development

### Prerequisites

- .NET 10 SDK
- Bun (https://bun.sh)

### Building the Server

```bash
dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:EnableCompressionInSingleFile=true
```

### Testing the Server and Extension

1. Open `GSCLSP.slnx` in Visual Studio or IDE of choice
2. Setup your NuGet to build the C# server
> You can do so by going to **Tools** -> **NuGet Package Manager** -> **Package Manager Settings**, and then going to **Sources** and adding `nuget.org` for source `https://api.nuget.org/v3/index.json`.
3. Set the target to `Release`, and then right click on the solution on the right and click `Build Solution`
4. Open a command prompt, and run `bun install` inside of the `GSCLSP.Extension` folder.

Now you can open VSCode in the extension source code and click `F5` to test the extension. Any updates done to the server can easily be reloaded from VSCode with the extension tester.

