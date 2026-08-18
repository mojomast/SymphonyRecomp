# Castlevania: Symphony of the Night PSX Recomp

> **Automation fork:** The `mcp-automation` branch adds an opt-in, local-only development bridge and a standalone MCP server for automated testing. It is maintained in the `mojomast` fork and is not an upstream BlackLabelHQ feature. See [MCP Automation](docs/mcp-automation.md).

The Castlevania: Symphony of the Night PlayStation Recomp, called SymphonyRecomp, is proudly brought to you by the BlackLabelHQ team! 

# Please Read This
Before we get started on the README  - This project is a "RE"comp. It is NOT a "DE"comp. Please do NOT go to the SOTN Decomp Discord server to talk about SymphonyRecomp. They are two separate concepts! We, however, encourage you to help out with the SOTN Decomp project if you're interested in helping us fully DECOMPILE the game!

Please note this is an Open BETA and this is NOT the final version! This Recomp was made by human hands, no AI is involved in writing this code!

We value human work, PRs made with AI will be closed.

# Do You Just Want To Play?
If you just want to play [download the latest release here](https://github.com/BlackLabelHQ/SymphonyRecomp/releases)!

# Do You Need Help?
You can join our Discord or open an issue on this GitHub! Again, you'll join the BlackLabelHQ Discord Server for help... NOT the SOTN Decomp server.

[![Discord](https://discord.com/api/guilds/1525942688728481983/widget.png?style=banner2)](https://discord.gg/65g8ZEPnbR)

# Special Notes Section

This version is currently in BETA stages. You may experience disastrous game breaking bugs! Every effort has been done so that this will not happen but you should be warned regardless. Stable version 1.0 has YET to be released!

The goal of this project is to help bring the game to modern computers without some of the limitations of older consoles. This was accomplished through both recompilation means and decompilation efforts. Stay tuned for the full SOTN Decomp release by the SOTN Decomp community, which will be the de facto means of the modern "PC port" efforts once it's fully released.

As mentioned above, SymphonyRecomp is NOT the same as the SOTN Decomp project, although several members of Black Label HQ are contributing to that project, as well. They are separate. Please treat them as such.

# Instructions To Build From Source

Clone repo. Add legally owned game files to disc. Run windows_run.bat or windows_initial_build.bat or manually run RecompOne against sotn.json, this will produce the game code, you can then compile it yourself, dev builds do not auto-update

## Prerequisites
- An GPU that supports at least OpenGL 3.3
- [.NET 10](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)
- [OpenAL](https://www.openal.org/documentation/) 
- [Git](https://git-scm.com/install/)
- A legally owned copy of the North American PSX (PlayStation) version of Castlevania: Symphony of the Night to rip your game from, bin/cue format. The files should be hard named the following and placed inside the `disc` directory in the main directory of `SymphonyRecomp`.
    - Castlevania - Symphony of the Night (Track 1).bin
    - Castlevania - Symphony of the Night (Track 2).bin
    - Castlevania - Symphony of the Night (USA).cue

## Nice To Haves (If Wish To Contribute)

- [Visual Studio 2026](https://visualstudio.microsoft.com/downloads/) - More Ideal way to work with the project, you can also use VSCode.
- [VSCode](https://code.visualstudio.com/)

## MCP Development Automation

This fork can launch a configured SymphonyRecomp build, drive controller input, inspect structured game/runtime state, capture screenshots, read bounded RAM, collect logs, inspect entities, and enable or reload existing mods through MCP. The bridge is disabled during ordinary play and listens only on a current-user named pipe when explicitly started with `--automation`.

Build and test the standalone MCP companion:

```bash
dotnet test tools/SymphonyRecomp.Automation.Tests/SymphonyRecomp.Automation.Tests.csproj
dotnet publish tools/SymphonyRecomp.Mcp/SymphonyRecomp.Mcp.csproj -c Release -o artifacts/mcp
```

Configuration, security boundaries, tool documentation, save-loading workflow, and OpenCode examples are in [`docs/mcp-automation.md`](docs/mcp-automation.md).

## How Was This Made?
This project was made using RecompOne to statically recompile the game, it also used some references from the decomp to help name functions and make patches, please show some love for the Decomp team, they deserve it!

## Warning To AI Bros
Again, BlackLabelHQ and by extension SymphonyRecomp is proudly made entirely with human hands. We want to keep it that way, so please respect that.

- AI written issues will be automatically closed without us reading them, even if they are legitimate issues, so make sure you write them to the best of your ability in English. If you do not speak English, please use a basic translator and do not ask AI to translate it for you even if it reads really weird.

- ALL AI based PRs will be rejected innately, however this is your warning that you will be permanently banned from BlackLabelHQ's repos if you do it anyway.

# Todo:

- The rest of the README.MD ... eventually.
