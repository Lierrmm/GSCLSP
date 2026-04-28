# GSCLSP (GSC Language Server Protocol)

![Visual Studio Marketplace Version](https://img.shields.io/badge/VS-Install_Extension-blue?link=https://marketplace.visualstudio.com%2Fitems%3FitemName%3Dbbe-tools.gsclsp)

The core engine behind the GSC Language Support extension. This repository contains both the .NET 10 Language Server and the TypeScript VS Code client.

## Structure

- `GSCLSP.Core/`: The C# Library that handles all parsing & indexing logic
- `GSCLSP.Server/`: The C# Language Server using OmniSharp. (.NET 10 (Self-contained, Single-file, Compressed))
- `GSCLSP.Extension/`: The VS Code extension client. (TypeScript)
- `GSCLSP.Extension/out/`: Final binaries and data files for distribution.

## Development

### Prerequisites

- .NET 10 SDK
- Bun (https://bun.sh)

### Building the Server

1. Download the [.NET 10.0 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0), and make sure it is the latest version. 
> You want **Build apps - SDK** -> **SDK 10.X.XXX** on the left side.
2. Download [Bun](https://bun.sh/) by going to their website and selecting your OS, and running the command.
3. Run the following command to build the server:

```bash
dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:EnableCompressionInSingleFile=true
```

### Testing the Server and VSCode Extension

1. Open `GSCLSP.slnx` in Visual Studio or IDE of choice
2. Setup your NuGet to build the C# server
> You can do so by going to **Tools** -> **NuGet Package Manager** -> **Package Manager Settings**, and then going to **Sources** and adding `nuget.org` for source `https://api.nuget.org/v3/index.json`.
3. Set the target to `Release`, and then right click on the solution on the right and click `Build Solution`
4. Open a command prompt, and run `bun install` inside of the `GSCLSP.Extension` folder.

Now you can open VSCode in the `GSCLSP.Extension` source code, and click `F5` in a extension's file to test. Using the VSCode Extension Tester's reload function will work for server changes you build on the `.slnx` easily.
