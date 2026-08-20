# SymphonyRecomp MCP Automation

This fork provides a local development control plane for agent-assisted SymphonyRecomp testing. It targets the MCP `2026-07-28` specification and uses the official Tier 1 C# SDK `2.2.0`.

The automation feature is opt-in, contains no game or disc data, and requires a legally owned North American PlayStation copy of Castlevania: Symphony of the Night.

## Architecture

```text
OpenCode or another MCP host
        |
        | MCP over stdio
        v
SymphonyRecomp.Mcp companion process
        |
        | authenticated, length-prefixed JSON
        | current-user-only named pipe
        v
SymphonyRecomp automation bridge
        |
        | bounded queue drained on VSync
        v
RecompOne runtime and typed SOTN wrappers
```

MCP protocol parsing, process management, PNG validation, and response serialization stay outside the game process. Runtime, memory, mod, input, and GPU operations run on the game thread at a VSync boundary. PNG encoding happens after RGB pixels are copied away from emulated and GPU state.

Automation protocol `1.2` also discovers optional mod diagnostics through the exact public convention methods `CaptureAutomationDiagnostics(long)` and `TryResetAutomationDiagnostics(string, int)`. RecompOne owns the resulting delegates, publishes them only after successful mod load, and removes them before unload so collectible mod assemblies are not retained. The bridge invokes them only through the serialized runtime main-thread queue. Protocol `1.2` adds an internal atomic input-batch request: one or two unique, fully validated port timelines are prepared and installed by one serialized game-thread command at one VSync frame. Invalid batches install nothing. The public single-port tool remains unchanged.

A normal dynamically compiled mod is not used as the control plane because mods load after disc validation, cannot launch the game, cannot safely disable or reload themselves, cannot discover newly installed mods, and do not expose a supported final-frame capture or main-thread RPC API.

## Capabilities

| Tool | Effect |
| --- | --- |
| `sotn_launch_game` | Launches the configured executable and legally owned disc, then waits for bridge readiness. |
| `sotn_stop_game` | Stops only the process launched by this MCP server. Requires confirmation. |
| `sotn_process_status` | Reports sanitized process, bridge, and configuration status. |
| `sotn_get_state` | Returns frame-stamped CPU, GPU, CD, overlay, game, player, input, and randomizer telemetry. |
| `sotn_wait_for_state` | Waits for bounded game-state, stage, in-game, `g_GameStep`, or full-width engine-step predicates. |
| `sotn_capture_screenshot` | Returns a validated PNG as MCP image content plus frame metadata. |
| `sotn_run_input` | Queues a bounded controller timeline on port 0 or 1. |
| `sotn_clear_input` | Immediately returns both automation ports to neutral. |
| `sotn_run_scenario` | Runs one embedded bounded scenario by catalog ID under its declared diagnostics-reset policy and returns its result and artifact ID. Requires confirmation. |
| `sotn_start_campaign` | Starts one embedded background observer against an already-running, manually prepared Play session. Requires confirmation. |
| `sotn_get_campaign` | Returns bounded current/last campaign status, identity, progress, cleanup, and private artifact ID. |
| `sotn_cancel_campaign` | Explicitly cancels the running observer and finalizes neutral-input cleanup. Requires confirmation. |
| `sotn_list_entities` | Returns bounded structured data for active native entities. |
| `sotn_list_mods` | Lists discovered mods and load state. |
| `sotn_set_mod_enabled` | Enables or disables an existing discovered mod. Requires confirmation. |
| `sotn_reload_mod` | Reloads an existing enabled mod. Requires confirmation. |
| `sotn_get_mod_diagnostics` | Captures a loaded participating mod's bounded JSON object with reset session and generation identity. |
| `sotn_reset_mod_diagnostics` | Resets diagnostics only when the exact latest session and generation still match. Requires confirmation. |
| `sotn_get_logs` | Returns bounded game/bridge logs and managed-process stdout/stderr. |
| `sotn_read_memory` | Reads up to 4096 bytes from emulated RAM. No memory-write tool exists. |
| `sotn_hard_reset` | Clears automation input and requests a hard reset. Requires confirmation. |

Tool schemas explicitly reject additional unsafe behavior: there is no shell execution, arbitrary filesystem path argument, arbitrary game-function call, mod source upload, mod installation, memory write, instruction stepping, or purported full-machine savestate.

## Embedded Scenarios

`sotn_run_scenario` accepts only an ID from the MCP assembly's bounded descriptor/resource catalog plus `confirm=true`. Duplicate, missing, oversized, invalid, or identity-mismatched resources reject catalog construction. It accepts no filesystem path or inline source and never implicitly launches or stops the game, reloads a mod, or hard-resets the console. It is annotated destructive because scenarios can reset participating-mod diagnostics and drive input. Scenario entry fails immediately while another scenario or any direct launch, stop, input, input-clear, hard-reset, diagnostic-reset, or mod mutation is active; those direct mutations likewise fail immediately while a scenario runs. Direct mutation leases cover their complete awaited operations. The runner itself uses its private automation adapter, so its input, optional diagnostic reset, and final neutral-input cleanup are not blocked by that gate.

Both exact `sotn-scenario/1` and `sotn-scenario/2` are supported. V1 retains reset-before, ordered single-port submission, and legacy diagnostic-field semantics. V2 declares `diagnosticsReset` as `before` or `none`, adds area/room/roomX/roomY predicates, and supports only closed `p2d4/2` scalar metric names. Existing `eq`, `ne`, `gte`, and `lte` semantics are unchanged. Integer-only `deltaEq`, `deltaGte`, and `deltaLte` compare against one immutable baseline captured immediately after an accepted reset or, for reset-none, at the accepted start checkpoint. Missing/type-mismatched baselines, subtraction overflow, or session/generation changes fail closed. V2 multi-port steps use the atomic protocol batch.

The closed metrics include reconstruction retry cooldown, retry count, suppressed-attempt count, and stable suspension reason. Nonfatal unsupported-terrain/no-safe-candidate failures suppress probes for 29 safe updates and permit one probe on the 30th; repeated failure rearms that interval. Visible suspended status remains eligible while ordinary Player 2 mutation is stopped. Collision-disabled and fatal states remain terminal.

Catalog entry `coop-locomotion-jump`, version `2` under schema `sotn-scenario/2`, is the canonical co-op locomotion/normal-jump check for mod ID `coop-feasibility`. It requires Play as Alucard, no loading/menu/map, active co-op hooks (`H=P`), exact diagnostic schema `p2d4/2`, scalar `errorCode="0"`, `fatal=false`, and `K=-`. The `K=-` predicate deliberately fails closed unless Pad 2/native-controller mode is active; it does not silently drive the mod's virtual-key mode. The scenario does not pin a stage or room and does not require coyote or buffered-jump completion.

Its paired 72-frame timelines begin with an eight-frame neutral recovery lead. Port 0 stays neutral while port 1 then sends Right 12, neutral 4, Left 12, neutral 4, Cross 2, and neutral 30. Protocol `1.2` installs both at the exact shared `StartsAfterFrame`. Checkpoints capture telemetry and then diagnostics as separate serialized observations, not an atomic state/diagnostic snapshot; frame deadlines use the later observation. The post-input checkpoint requires `M=P`, configured processed Pad 2 availability, no fatal/error state, and no orphan attack marker; input neutrality is independently enforced by the runner.

The stable M5 probe inventory also includes `coop-transition-west`, `coop-contact-hit`, `coop-projectile-hit`, `coop-damage-revive`, and `coop-drop-observe`. They are setup-gated evidence probes, not general gameplay claims. Telemetry stage predicates use exact enum string `MarbleGallery`; `NO0` is only the overlay code in descriptions. Transition version 6 requires area 40 telemetry room 140/map cell `32,27` (the lower clock-room junction, NO0 room table index 21) and a manual near-west-exit setup; its west crossing first walks P1 into the live-observed threshold step to guarantee contact, then hops it and enters the plain-door save room (telemetry room 220, index 31). Both ports have equal 252-frame atomic duration: P1 Left 60, Left+Cross 12, then Left 55, a conservative reconstruction delay, then P2 Left/inward 8. Its per-run transition/reconstruction evidence uses deltas. Contact/projectile version 2 starts only with `EN=P`, uses an eight-frame neutral lead and two-frame attack, and requires exact per-run allocation/window/cleanup with zero failure, quarantine, restoration, marker, fatal, and error evidence. Damage/revive remains reset-none and uses revive deltas instead of unavailable live `PlayerHasControl`. `coop-drop-observe` requires an already observed unique association and preserves it during a neutral reset-none step; repeated curated attacks may be needed, and a causal no-drop attempt is valid but incomplete evidence. Every scenario clears both ports in `finally`.

The parent also owns a game-free `no0-marble-gallery-candidate-25` aggregate definition mirroring the route manifest. Its bounded executor consumes at most 25 ordered child transition observations, stops at the first failed/mismatched edge, and emits only a bounded passed/failed/incomplete artifact with the next expected edge. It accepts no filesystem path and is consumed by the embedded observer campaigns below.

## Embedded Observer Campaigns

The exact embedded-only `sotn-campaign/1` inventory is `coop-route-25` version 1 and `coop-soak-60m` version 1. Start accepts only a catalog ID and `confirm`; there are no inline definitions, paths, scripts, loops, memory operations, save operations, or mod installation/reload controls. A campaign observes an already-running session and never launches, stops, resets, reloads, changes mods, selects a save, or sends gameplay input. File-select and save actions remain visible human-approval steps, and physical controllers remain usable.

Preflight requires a ready exact protocol 1.2 bridge, Play/Alucard with loading/menu/map closed, loaded `coop-feasibility`, exact `p2d4/2` identity, configured Pad 2 mode (`K=-`), and clean fatal/error, ownership, cleanup, quarantine, restoration, protection, health, and drop-tracker metrics. Before taking the lifetime gate and again after taking it, bridge `InputActive` must be false and both telemetry automation masks and remaining-frame counts must be zero. This closes the direct-mutation/timeline race and rejects without creating a run or background state. One campaign holds the scenario execution gate for its lifetime, excluding scenarios and direct mutations. Read-only tools and campaign status remain available. Caller cancellation after a successful start response does not cancel the background run; only confirmed campaign cancellation or server shutdown does.

`coop-route-25` starts in Marble Gallery area 40 telemetry room 140 and observes the exact alternating `140,220` west/east sequence across the clock junction/save-room doorway (route manifest v2, live-observed room bytes). Humans drive gameplay. Each of 25 changed-room observations must advance frames and add exactly one completed and passed transition, successful reconstruction, at least eight accepted post-transition commanded pixels, no pending/abandonment/failure, and unchanged identity/safety. `RouteAggregateExecutor` consumes every accepted transition. The first wrong edge or metric fails the run; the deadline is 30 minutes.

`coop-soak-60m` uses monotonic time and takes exactly 13 samples at minute 0 and every five minutes through minute 60. Crossing 3600 seconds triggers an explicit fresh minute-60 state and diagnostic capture unless that sample is already complete; it must validate before final persistence and `Passed`. Bounded loading/transition states are allowed between samples, but samples must be safe Play/Alucard. One exact-owned marker and transient cleanup are valid during ordinary combat. The mod counts every exact-owned native attack window and retains the cumulative maximum until diagnostic reset. The observer rejects a maximum above the exact 48-window bound derived from the 40-window supported projectile lifecycle plus eight cleanup-grace windows, so attacks completed entirely between polls cannot escape enforcement. Sampled stuck-marker timing remains defense in depth. Multiple/orphan markers, counter or identity regression, quarantine, fatal/error, restoration/protection failure, or a stuck transient fails immediately. Route transition acceptance remains quiescent. Prolonged game-frame stagnation also fails. An idle hour establishes observer stability only and does **not** satisfy the playable soak gate unless humans play the declared route and ordinary combat for the full hour.

Artifacts are private children under the scenario artifact root. On Unix campaign-owned directories are forced to `0700` and files to `0600`; existing ancestor permissions are not changed. Tools return only a random safe artifact ID. A bounded atomically replaced manifest contains sanitized identity, progress, sample hashes/frames/metric summaries, first failure, cleanup, and artifact errors. A run cannot publish `Passed` until its requested success screenshots and final manifest are durable; a final write failure changes the result to `Failed` and attempts a failure manifest. Route evidence is aggregate rather than 25 scenario bundles. Screenshots are limited to start/midpoint/finish/failure for route and minutes 0/30/60/failure for soak. Bounded sanitized logs/entities are failure-only. A fresh cleanup budget clears and verifies both automation ports even though observers never inject input. Host shutdown cancels and awaits observer completion and gate release; an uncooperative client causes an explicit shutdown/disposal failure rather than false completion.

### Exact post-restart live sequence

1. Call `sotn_process_status`, then `sotn_launch_game` only when the process is genuinely absent; reload/reset is not a cold launch.
2. Use the visible native title/file-select workflow below. A human inspects screenshots, chooses the existing Alucard save, and approves final confirmation.
3. Call `sotn_wait_for_state` for Play, then `sotn_get_state`, `sotn_list_mods`, and `sotn_get_mod_diagnostics` for `coop-feasibility`.
4. Explicitly uncheck **Use virtual Player 2 keyboard** for physical Pad 2 campaign play and manually prepare Marble Gallery area 40 telemetry room 140.
5. Call `sotn_start_campaign {"id":"coop-route-25","confirm":true}`. Humans play the exact route while polling `sotn_get_campaign`; cancel only with `sotn_cancel_campaign {"confirm":true}`.
6. After it ends, perform curated contact/projectile/drop/revive work manually or with separately approved bounded scenarios. A no-drop RNG attempt remains incomplete drop evidence.
7. For the hour gate, manually prepare ordinary Play, call `sotn_start_campaign {"id":"coop-soak-60m","confirm":true}`, actively play route and combat, and poll status without automation input.
8. Save only through the native menu with the mod enabled. Fully exit, disable the mod before a genuine restart, then have a human approve loading that visible save and verify ordinary Play/progression. No MCP operation reads, hashes, writes, or directly loads save data.

Live evidence on 2026-08-20 consists of three distinct pre-liveness-fix cold boots with canonical v2 passes plus one post-liveness canonical pass; `K=-` persisted, but the visibly empty card means none counts as cold-save route acceptance. Castle Entrance telemetry populated control/HP/level/EXP. Natural Player 2 damage recorded six events, one down at 0 HP, and zero invariant/fatal/guard/restoration/orphan/quarantine failures. On safe flat terrain, manual overlapping P1 Down/P2 Circle recorded one revive start, one revive, one recovery, and HP 50. A soak observer reached sample 1/13 before explicit cancellation finalized private evidence with cleanup succeeded and verified, so it is not an hour pass. Wrong-stage route preflight rejected and remained Idle. Unsupported stairs had previously churned between Suspended/Reconstructing (206 suspension entries, maximum consecutive 1, 219 attempts); the bounded retry fix has no post-fix live verification yet. The managed process later exited without a logged bridge/runtime error, which remains a soak/cold-run risk rather than attributed crash proof.

Three played route runs, contact/projectile/drop objectives, mod-enabled native save, mod-disabled restart/load, post-retry terrain observation, and the playable hour remain live blockers.

Example call:

```json
{
  "id": "coop-locomotion-jump",
  "confirm": true
}
```

The structured response is the bounded `ScenarioExecutionResult`: `runId`, `artifactId`, runner outcome/first failed checkpoint/cleanup evidence, and the sanitized artifact manifest. After runner cleanup, all requested evidence captures and writes share one five-second budget; mandatory source and manifest writes each have a separate narrow bound. Bundles are written below `SYMPHONYRECOMP_SCENARIO_ARTIFACTS`, or `artifacts/scenarios` under the companion working directory when unset. Manifests expose hashes, file names, bounded diagnostics, and sanitized process/build identity rather than configured full paths or credentials. Failure bundles can contain gameplay state, mod diagnostics, logs, entity data, and a game-display screenshot; treat the artifact directory as private test evidence and review it before sharing. The catalog and bundles contain no disc or save data themselves, but captured runtime evidence may reveal gameplay or mod state. The runner attempts to clear both ports in `finally`; after interruption, call `sotn_clear_input` and verify that both masks and remaining-frame counts are zero.

The private 2026-08-19 live smoke predates protocol `1.2` and scenario v2. It launched the managed game and loaded real mod `coop-feasibility` `v0.4.0`. From Play/Alucard with loading/menu/map false and `K=-`, `H=P`, `E=0`, an exact diagnostic reset advanced generation `0` to `1`. The canonical Port 2 sequence produced `M=P:18/18/1`, `H=P`, `E=0`, and normal-jump-only `J=W:N1/C0/B0/R0,0` at automation frame 1912. That historical run did not establish same-frame starts; the new atomic behavior is contract-tested but still awaits a live rerun. State, diagnostics, entities, logs, and PNG evidence remained private, and explicit clear left both ports neutral.

## Build

Build SymphonyRecomp normally after placing legally owned disc files under `disc/`:

```powershell
windows_initial_build.bat
```

Build and test the MCP components independently:

```bash
dotnet test tools/SymphonyRecomp.Automation.Tests/SymphonyRecomp.Automation.Tests.csproj
dotnet publish tools/SymphonyRecomp.Mcp/SymphonyRecomp.Mcp.csproj \
  -c Release -o artifacts/mcp
```

The game build references `SymphonyRecomp.Automation.Contracts.dll`. Release packages place the MCP companion under `mcp/`.

## OpenCode Setup

### Linux OpenCode To Windows Over Tailscale

This is the recommended setup when OpenCode runs on Linux and the visible game runs on a Windows host. The companion runs in the logged-in Windows desktop session, launches `sotn.exe` there, and exposes MCP through Tailscale Serve.

On Windows, open PowerShell in the extracted release directory and set the local paths plus the exact Tailscale HTTPS name:

```powershell
$env:SYMPHONYRECOMP_EXECUTABLE = "C:\SymphonyRecomp\sotn.exe"
$env:SYMPHONYRECOMP_DISC = "C:\Games\SOTN\Castlevania - Symphony of the Night (USA).cue"
$env:SYMPHONYRECOMP_WORKDIR = "C:\SymphonyRecomp"
$env:SYMPHONYRECOMP_MCP_HTTP_HOST = "sotn-windows.example-tailnet.ts.net"
$env:SYMPHONYRECOMP_MCP_HTTP_PORT = "8765"
& ".\mcp\SymphonyRecomp.Mcp.exe" --http
```

The companion binds only `127.0.0.1:8765`. In a separate Administrator PowerShell, expose that loopback service to the tailnet using HTTPS:

```powershell
tailscale serve --bg --https=443 http://127.0.0.1:8765
tailscale serve status --json
```

Do not use `tailscale funnel`; Funnel would expose the game-control API to the public internet.

On Linux, configure OpenCode with the Tailscale HTTPS URL:

```json
{
  "$schema": "https://opencode.ai/config.json",
  "mcp": {
    "symphonyrecomp": {
      "type": "remote",
      "url": "https://sotn-windows.example-tailnet.ts.net/mcp",
      "oauth": false,
      "enabled": true,
      "timeout": 30000
    }
  }
}
```

No application token or OAuth setup is required. Tailscale authenticates devices and encrypts the connection. Anyone permitted by the tailnet policy to reach the Windows host on TCP 443 can invoke these tools, including destructive tools, so restrict that grant to the Linux development machine if the tailnet has other users or devices.

The Windows companion must remain running in the interactive desktop session. `sotn_launch_game` then starts a visible Windows game process, while screenshots, telemetry, logs, input, resets, and mod reloads are controlled from Linux.

Quit and restart OpenCode after changing `opencode.json`; MCP configuration is loaded only at startup.

### Same-Machine Stdio

OpenCode local MCP configuration uses `environment`, not `env`, and `command` must be an array. Add an entry to the appropriate `opencode.json` and use absolute paths for your machine:

```json
{
  "$schema": "https://opencode.ai/config.json",
  "mcp": {
    "symphonyrecomp": {
      "type": "local",
      "command": [
        "dotnet",
        "run",
        "--project",
        "C:\\src\\SymphonyRecomp\\tools\\SymphonyRecomp.Mcp\\SymphonyRecomp.Mcp.csproj",
        "--no-build"
      ],
      "cwd": "C:\\src\\SymphonyRecomp",
      "enabled": true,
      "timeout": 30000,
      "environment": {
        "SYMPHONYRECOMP_EXECUTABLE": "C:\\src\\SymphonyRecomp\\bin\\Debug\\net10.0\\sotn.exe",
        "SYMPHONYRECOMP_DISC": "C:\\Games\\SOTN\\Castlevania - Symphony of the Night (USA).cue",
        "SYMPHONYRECOMP_WORKDIR": "C:\\src\\SymphonyRecomp\\bin\\Debug\\net10.0"
      }
    }
  }
}
```

For a published companion, replace the `command` array with an array containing the absolute path to `SymphonyRecomp.Mcp.exe`.

Do not add an automation token. The MCP companion generates a fresh 256-bit token and random pipe name for every managed launch, passes them to the child through its environment, and never returns or logs them.

Quit and restart OpenCode after changing `opencode.json`; MCP configuration is loaded only at startup.

## First Automated Session

1. Call `sotn_process_status` and verify the configured executable and disc are available.
2. Call `sotn_launch_game`.
3. Call `sotn_get_state` and `sotn_capture_screenshot`.
4. Use `sotn_run_scenario` for the canonical co-op check, or `sotn_run_input` with explicit pressed and neutral segments.
5. Use `sotn_wait_for_state` instead of fixed sleeps whenever possible.
6. Call `sotn_clear_input` after an interrupted workflow.
7. Gather `sotn_get_logs`, `sotn_get_state`, and a screenshot when reporting a failure.

Example bounded input timeline:

```json
{
  "port": 0,
  "steps": [
    { "buttons": ["Right"], "frames": 12 },
    { "buttons": [], "frames": 4 },
    { "buttons": ["Cross"], "frames": 2 },
    { "buttons": [], "frames": 2 }
  ]
}
```

Button names are `L2`, `R2`, `L1`, `R1`, `Triangle`, `Circle`, `Cross`, `Square`, `Select`, `L3`, `R3`, `Start`, `Up`, `Right`, `Down`, and `Left`. Contradictory horizontal or vertical directions are rejected. Timelines contain at most 120 segments and 1800 total frames and always return to neutral.

## Loading An Existing Save

Save loading intentionally uses the game's normal title and file-select UI. Direct calls to `ApplySaveData` or direct writes to game state are not exposed because they can bypass memory-card handling, overlay loading, audio transitions, and mod save hooks.

A state-aware UI workflow is:

1. Wait for `gameState="Title"`, `gameStepRaw=6`, and `engineStepRaw=2`.
2. Send Start for two frames followed by two neutral frames.
3. Wait for `gameState="MainMenu"`, `gameStepRaw=6`, and `engineStepRaw=2`.
4. Send Cross for two frames followed by two neutral frames to enter File Select.
5. Wait for `gameState="MainMenu"` and `engineStepRaw=51` (`0x33`).
6. Capture a screenshot and verify the desired visible save exists.
7. Navigate with discrete two-frame direction pulses followed by neutral pulses.
8. Capture another screenshot before sending the final Cross.
9. Wait for `gameState="Play"`; confirm `loading=false` with `sotn_get_state` before beginning a test.

The file selector exposes 15 visible positions per memory card. Do not blindly confirm an empty position: it starts New Game. Card absence, unformatted-card prompts, read errors, or unknown selector state should stop the workflow. Never send Triangle during an unformatted-card prompt because the original game uses Triangle to approve formatting.

This staged workflow is deliberately model-visible so screenshots and telemetry can be checked between actions. A future high-level `load_save` tool should only be added after read-only save-summary and selector telemetry can validate the requested card and visible slot.

## Attaching To An Existing Automation Instance

Managed launch is recommended. Advanced users may start the game and MCP companion from the same trusted user context with matching `SYMPHONYRECOMP_AUTOMATION_PIPE` and `SYMPHONYRECOMP_AUTOMATION_TOKEN` environment variables, then run the game with `--automation --disc <cue>`. The pipe name must be private to that instance. The token must be exactly 64 hexadecimal characters generated from 32 bytes produced by an operating-system CSPRNG; format validation cannot prove how a manually supplied token was generated.

Never place the token in command-line arguments, checked-in configuration, URLs, logs, or issue reports. `sotn_stop_game` never terminates an attached process; it controls only a process launched by that MCP companion.

## Security Model

- The companion uses stdio by default. `--http` binds Streamable HTTP only to Windows loopback; Tailscale Serve is the tailnet-facing HTTPS listener.
- HTTP mode accepts only exact `POST /mcp`, validates the configured Tailscale FQDN and any supplied Origin, applies Kestrel request/header/connection limits, and enables no CORS.
- One companion process is one control authority for one configured game instance. Do not share it between independent hosts or mutually untrusted tasks.
- Game IPC uses `NamedPipeServerStream` and `NamedPipeClientStream` with `CurrentUserOnly`.
- Every internal request authenticates with a constant-time comparison of a per-launch token hash.
- Requests are length-prefixed, size-limited, schema-validated, timeout-bounded, and serialized one at a time.
- The game queue holds at most 32 commands and drains at most eight per VSync.
- Runtime state is accessed only on the game thread.
- Mod diagnostics responses must be JSON objects with a 32-hex session ID and nonnegative generation; the final structured response is bounded to 64 KiB. Co-op `p2d4/2` retains the exact legacy line and `fields` object and adds only a closed flat `metrics` object of JSON integers, booleans, and printable ASCII strings. Current metrics include transition/tether evidence and bounded read-only NO0 drop scans, exact prize/equipment spawns, unique P2-associated/ambient/ambiguous/no-drop classifications, lifecycle outcomes, tracker faults, and observed native EXP deltas when a validated read is available.
- Diagnostic reset is destructive, generation-checked, and returns only whether reset applied. Capture again to obtain the new identity.
- Screenshot GPU readback occurs only on the render/game thread; PNG encoding occurs off-thread.
- Controller state is released on clear, disconnect, reset, timeout-before-execution, and bridge shutdown.
- Logs and tool errors redact configured paths and tokens.
- Managed game processes receive a minimal allowlist of desktop/runtime environment variables instead of inheriting the MCP host's cloud, source-control, proxy, or CI credentials.
- Model-facing process status exposes only file names, not full paths.
- Destructive MCP tools carry explicit annotations and require `confirm=true` as an accidental-call guard. The boolean is model-supplied and is not proof of human consent; the MCP host must still show its normal tool-approval UI with the complete arguments.
- The server only manages preinstalled mods. Mods are full-trust code; enabling one is equivalent to executing it as the current user.

Treat the MCP server as a powerful developer tool. Use Tailscale Serve, never Funnel, and run it only with trusted MCP hosts, tailnet devices, and mods.

## Screenshot Semantics

`sotn_capture_screenshot` returns the raw PSX display region without ImGui or unrelated desktop content. The bridge reads canonical HLE VRAM when the HLE backend is active and software VRAM otherwise. It supports 15-bit and 24-bit display conversion and reports frame, dimensions, source, MIME type, byte length, and SHA-256.

This initial implementation does not capture widescreen extension surfaces or post-processing outside the canonical PSX display. That limitation is explicit so automated visual assertions are reproducible.

## Known Limits

- There is no full-machine savestate. RecompOne does not serialize translated execution continuation, GPU/SPU/CD/DMA/BIOS state, or mod-owned state.
- There is no pause, exact frame advance, or instruction stepping yet.
- Save selection uses safe UI automation rather than a direct load call.
- Mod management covers discovered mods. It does not install, remove, update, or rescan packages during the process lifetime.
- The screenshot is the canonical game display, not the final post-processed widescreen output or the complete application window.
- A request interrupted after a game-thread mutation reaches its commit point may have an unknown client-visible outcome. Read telemetry before retrying non-idempotent operations.
- Live integration testing requires the user's legally owned US disc and configured memory cards.

## Verification

Public tests require no game data:

```bash
dotnet test tools/SymphonyRecomp.Automation.Tests/SymphonyRecomp.Automation.Tests.csproj
```

They compile the game-side bridge against RecompOne with test SOTN wrappers and cover framed transport, input bounds, atomic same-frame dual-port install and clear, strict all-or-nothing batch validation, exact protocol negotiation, strict complete `p2d4/2` envelope/metric identity and bounds, both scenario schemas, typed metrics, reset-none identity, descriptor catalogs and route fingerprint, exact Marble Gallery facts, campaign neutral races, transient attacks, route/soak clocks and bounds, durable private artifacts, cancellation/shutdown/gate completion, deferred shutdown, and HTTP guards.

The final 2026-08-20 sequential Release run passed 149/149 parent tests and 244/244 co-op contracts. Current and pinned dynamic mod compiles were both 284672 bytes. MCP Release built with zero warnings; the nonincremental full host build passed with the same five existing warnings.

The fork's automation CI builds the MCP companion and runs these tests without private disc material. A complete private integration run should additionally verify process launch, startup-disc validation, both rendering paths, screenshots, input neutralization, mod reload, hard reset, memory bounds, and save loading through the normal UI.

The official Inspector can validate discovery over stdio after publishing:

```bash
bunx @modelcontextprotocol/inspector@2.2.0 --cli \
  /absolute/path/to/dotnet /absolute/path/to/SymphonyRecomp.Mcp.dll \
  --transport stdio --method tools/list --format json
```

## Private Integration Release Gate

Before tagging an automation build, run this checklist on a machine with the legally owned US disc:

1. Build RecompOne, generate game sources, build SymphonyRecomp, and publish the companion from a clean recursive checkout.
2. Confirm an invalid, missing, JP, or EU cue is rejected in automation mode, including when window initialization is unavailable.
3. Launch through `sotn_launch_game` and verify the reported active disc file matches the configured cue.
4. Reach Title, File Select, and Play through bounded input and state predicates; load an existing save through the normal UI.
5. Capture and decode screenshots with HLE enabled and disabled; verify frame metadata and dimensions.
6. Cancel and disconnect during queued input and screenshot encoding; verify both ports return to neutral.
7. Exercise RAM reads at the first/last valid bytes and every rejected overflow boundary.
8. Enable, disable, and reload a test mod; verify returned state, hooks, logs, and subsequent game behavior.
9. Hard-reset during idle and after input; verify a new game boot and neutral automation state.
10. Stop the managed process and verify no unrelated or manually attached process is terminated.
11. Inspect child process environment and logs to verify MCP-host credentials and automation tokens are absent.
12. Run the official Inspector tool listing and archive the sanitized telemetry, logs, and screenshots as release evidence.

## MCP References

- MCP specification `2026-07-28`: <https://modelcontextprotocol.io/specification/2026-07-28>
- MCP security guidance: <https://modelcontextprotocol.io/docs/2026-07-28/tutorials/security/security_best_practices>
- Official C# SDK: <https://github.com/modelcontextprotocol/csharp-sdk>
- C# SDK `2.2.0`: <https://github.com/modelcontextprotocol/csharp-sdk/releases/tag/v2.2.0>
- OpenCode configuration schema: <https://opencode.ai/config.json>
