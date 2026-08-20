# Host-Authoritative Multiplayer Replica Client Plan

**Status:** Proposed implementation plan
**Research snapshot:** August 2026
**Audience:** Implementation subagents and maintainers
**Prerequisite:** Complete the local co-op M5/M6 gates before claiming network support.

## 1. Decision

Build a host-authoritative multiplayer system with a dedicated, asset-driven
replica client. The host runs the authoritative SymphonyRecomp process; the
joiner renders locally from a lawful, compatible local game installation and
receives compact state and event messages. No video, audio, textures, disc
sectors, raw RAM, VRAM, GPU packets, or save files cross the network.

Do not make two normal SOTN simulations cooperate through lockstep, rollback,
memory copying, or state injection. The native engine is single-player,
non-deterministic for this purpose, and has no complete save-state or portable
entity contract. A second modified engine may be used only as a development
visual oracle, never as the primary network-client architecture.

## 2. Product Contract

### 2.1 Authority

- The host is authoritative for P1, movement validation, collision, camera,
  room transitions, RNG, enemy AI, target HP, combat outcomes, drops, pickups,
  rewards, progression, saves, menus, cutscenes, and disconnect policy.
- The remote client supplies bounded P2 intent only. It never supplies health,
  position, hit, pickup, target, damage, progression, or collision truth.
- P2 remains mod-managed. The host alone may publish a transient native attack
  through the existing exact-owned lease machinery.
- The remote client predicts only reversible P2 presentation. It does not
  predict damage, drops, inventory, enemy behavior, or progression.
- P1 remains the sole camera/menu/progression owner in the first release.

### 2.2 First Supported Experience

The first networked release supports one joiner and one host in the same
supported room/route matrix. It presents host-selected rooms, camera, P1/P2,
the common supported entity set, P2 health/revive, and host-authoritative
combat events.

It explicitly excludes, until separately accepted:

- Split-screen, independent rooms, client-owned camera, host migration, or
  peer-to-peer authority.
- Deterministic lockstep, rollback of native simulation, and arbitrary
  savestate replication.
- Generic boss, familiar, cinematic, map/menu, video, or special-effect
  parity.
- Internet player-hosting before authenticated transport plus ICE/TURN support
  has passed impairment and security gates.

## 3. Architecture

```text
Remote input device
        |
        v
Replica client input sender -- encrypted/authenticated transport --> Host transport
        ^                                                        |
        |                                                        v
Replica renderer <-- snapshots, keyframes, events -- Host co-op authority adapter
                                                                  |
                                                                  v
                                                       SymphonyRecomp native game
```

### 3.1 Process Boundaries

| Component | Responsibility | Must not do |
| --- | --- | --- |
| Host runtime transport | Socket I/O, authentication, bounded queues, connection lifecycle | Read/write emulated memory on I/O threads |
| Host co-op authority adapter | Apply validated P2 input on the game thread; emit snapshots/events | Trust client state or mutate P1/world outside current contracts |
| Replica client transport | Connect, sequence/ack, clock samples, keyframe repair | Decide gameplay truth |
| Replica presentation model | Interpolation, local P2 visual prediction, event buffering | Use native slots/pointers as wire identity |
| Replica renderer | Decode client-local compatible assets and draw host-selected state | Download or receive copyrighted assets |

### 3.2 Proposed Repository Layout

Create these only when the corresponding work package begins:

```text
RecompOne/RecompOne.Runtime/Multiplayer/
  MultiplayerTransportService.cs
  MultiplayerConnection.cs
  MultiplayerMainThreadBridge.cs
  MultiplayerCompatibility.cs

mods/coop/source/
  NetworkProtocol.cs
  NetworkInputAuthority.cs
  NetworkHostSnapshot.cs
  NetworkEventLedger.cs
  NetworkReplicaPublication.cs

clients/SymphonyRecomp.ReplicaClient/
  SymphonyRecomp.ReplicaClient.csproj
  Protocol/
  Assets/
  World/
  Presentation/
  Rendering/
  Audio/
  Tests/
```

Keep socket dependencies out of the dynamically compiled mod source. The mod
compiler compiles source files at runtime and is not a safe package-management
or background-thread ownership boundary. The runtime owns transport and feeds
immutable messages to the game thread through a bounded bridge.

### 3.3 Existing Integration Seams

| Need | Existing seam | Use |
| --- | --- | --- |
| Host P2 update | `mods/coop/source/CoopFeasibilityMod.cs`, `UpdateProxy` / `AfterPlayerEntities` | Replace direct local Pad 2 selection with an authority-selected input frame |
| Lifecycle/epochs | `ManagedMovementSessionState.cs` | Include room epoch and suspend/neutralize on invalid lifecycle state |
| P2 state | `ManagedReplayState.cs`, `ManagedProxySnapshot` | Reuse selected fields, but create a new wire schema; do not send its 116-byte replay encoding directly |
| P2 health/revive | `ManagedHealthState.cs` | Host owns it; replicate projections/events |
| P2 visual pose/HUD | `CoopFeasibilityMod.cs` rendering hooks and GP0 helpers | Decouple presentation from mutable host simulation fields |
| Native target/entity reads | `wrapers/Game.cs`, `wrapers/Entity.cs` | Host creates bounded semantic entity views only |
| Room and local disc lookup | `wrapers/Stage.cs`, `events/LevelEvents.cs`, `events/RenderEvents.cs` | Host supplies identity/camera; client loads its own matching local assets |
| Safe game-thread handoff | `Runtime.TryEnqueueMainThreadAction` | Queue immutable validated inbound messages only; respect its bounded capacity |
| Automation evidence | `tools/SymphonyRecomp.Automation.*` and `P2D4Report.cs` | Test and diagnose; never reuse diagnostic JSON as gameplay transport |

## 4. Compatibility and Asset Policy

### 4.1 Connection Preconditions

Reject before allocating gameplay state unless all required values match:

- Multiplayer protocol major version and negotiated minor/features.
- Supported SymphonyRecomp build/runtime compatibility manifest.
- Co-op mod version and protocol manifest revision.
- US game region and local content/asset-set hash.
- Supported route/room matrix revision.
- Required renderer asset decoder revision.

The initial client verifies its own local lawful game files using the same US
baseline discipline as `misc/DiscCheck.cs`. It never uploads files or hashes
of individual extracted art/audio assets to a public service.

### 4.2 Asset Boundary

Ship only code, parsers, manifests, and hashes. Each participant supplies their
own compatible game installation. Host messages contain symbolic references,
for example `assetSetId`, `bank`, `frame`, `palette`, `clut`, draw flags, and
animation timing, never sprite pixels or GPU/VRAM commands.

This reduces distribution and version-coupling risk but is not legal advice.

## 5. Wire Protocol

Use an explicitly serialized, bounded binary protocol. Do not serialize C#
object layouts, native `Entity`, `Entity.ext`, pointers, raw RAM, or replay
codec bytes.

### 5.1 Message Plan

| Message | Delivery | Sender | Essential fields |
| --- | --- | --- | --- |
| `Hello` / `Reject` / `Accept` | Reliable control | Both | Protocol, build/content revisions, role, feature bits, reason code |
| `InputBundle` | Unreliable sequenced | Client | Match epoch, input sequence, intended host tick, current plus two prior commands |
| `Snapshot` | Unreliable sequenced | Host | Snapshot sequence, host tick, acknowledged input sequence, baseline ID, room/camera revision, interest delta |
| `Keyframe` | Reliable or repeated until app-acknowledged | Host | Full current interest set, room/layer/camera state, entity generations, P2 state |
| `WorldEvent` | Reliable ordered/sequenced by event kind | Host | Event ID, room epoch, host tick, semantic payload, idempotency rule |
| `ApplicationAck` | Unreliable sequenced | Both | Last accepted snapshot/keyframe/event/input sequence ranges |
| `ClockSample` | Low-rate reliable/control | Both | Four monotonic timestamps and host tick |
| `ResyncRequest` / `Disconnect` | Reliable control | Both | Reason, connection epoch, requested room/keyframe revision |

All state-bearing messages include `protocolMajor`, `matchEpoch`, and a bounded
payload length. Entity messages include a `(networkId, incarnation)` pair.
Native entity slot IDs are never sufficient identities because slots are reused.

### 5.2 Reliability Rules

- Inputs and snapshots are stale quickly: send them unreliable and sequenced.
  Drop old sequences; never wait for retransmission before accepting newer data.
- Control, keyframes, join, room-transition commitments, and irreversible
  progression events need reliable delivery, isolated from state traffic.
- Transport-level acknowledgement is not an accepted replication baseline.
  Delta snapshots only against the last application-acknowledged snapshot.
- If the acknowledged baseline is unavailable, send a keyframe. Never delta
  against merely the last sent packet.
- Bound all arrays, string lengths, entity counts, reliable queues, packet
  sizes, decompression output, and event retention windows.

### 5.3 Initial Rates and Budgets

These are starting values, not compatibility guarantees:

- Host simulation: 60 Hz logical ticks.
- Client input bundles: 30 Hz, current input plus two prior inputs.
- Nearby snapshots: 20 Hz; distant presentation-only entities: 5-10 Hz.
- Snapshot datagram budget: target 1000-1200 bytes before transport overhead.
- Interpolation delay: adaptive 60-200 ms, initially 100 ms.
- Extrapolation cap: 100 ms, then hold/fade rather than simulate indefinitely.
- Local input/prediction history: 2 seconds.
- Clock sample cadence: 1 Hz while connected.

Under congestion, drop distant/cosmetic precision and frequency first. Preserve
local P2 corrections, transition state, nearby threats, and irreversible events.

## 6. Transport and Security Decision

### 6.1 Prototype LAN Transport

Use a maintained UDP game transport such as LiteNetLib 2 from the runtime and
replica-client projects, with separate unreliable-sequenced and reliable
channels. This is suitable for a controlled LAN prototype after bounded parser,
rate-limit, and explicit local-trust warnings are implemented.

Do not expose this prototype directly to the Internet without reviewed
authentication and encryption. Do not hand-roll congestion control, crypto,
or NAT traversal on raw UDP.

### 6.2 Internet Transport Gate

Before public/player-hosted Internet support, select one reviewed solution:

1. QUIC with DATAGRAM support plus reliable streams, TLS 1.3, address
   validation, and a maintained .NET/native binding.
2. A reviewed UDP transport integrated with a reviewed authenticated-encryption
   design, token validation, replay protection, and relay infrastructure.

At the August 2026 research snapshot, QUIC DATAGRAM is standardized but
`System.Net.Quic` does not provide a turnkey public DATAGRAM surface. Treat
the concrete transport binding as a design gate, not an implementation detail.

For player-hosted Internet sessions, add ICE/STUN and TURN fallback with
short-lived authenticated relay credentials. For dedicated hosts, clients make
outbound connections and normally need no client-side NAT traversal.

### 6.3 Authentication Rules

- Obtain an HTTPS/platform-authenticated, short-lived, audience-bound join
  token before transport connect.
- Bind token claims to player, match, role, protocol range, expiry, and nonce.
- Use a connection epoch; invalidate all old baselines and resume tokens after
  reconnect.
- Rate-limit handshake, input, resync, and reliable-event queues.
- Validate every host command against identity, epoch, rate, allowed state,
  room epoch, cooldown, and payload bounds.
- Never log bearer tokens, session keys, raw packets, or unredacted player data.
- Do not use replayable 0-RTT data for join or gameplay mutation.

## 7. Host Simulation and Publication

### 7.1 Input Authority Adapter

Create `NetworkInputAuthority` with one host-visible result per simulation tick:

```text
AcceptedInputFrame = connectionEpoch, roomEpoch, inputSequence,
hostTick, pressed, tapped, source, stale
```

Rules:

- Validate sequence windows with unsigned wraparound-safe comparison.
- Apply only current/allowed room-epoch input; neutralize stale, missing,
  malformed, disconnected, loading, menu, transition, or unsafe input.
- Remote input must pass through the same `ManagedInputFrame`/movement and
  attack safety contracts as local Pad 2 input.
- Socket threads enqueue immutable input bundles. Only the game thread changes
  co-op reducers, memory, GPU, or native entity state.
- Disconnect and timeout cancel/neutralize P2, release attack ownership, and
  publish a reliable status event.

### 7.2 Snapshot Publication

Publish only at a safe host boundary, initially when
`ManagedMovementSessionReducer.SnapshotEligible` is true. Publish a keyframe
on join, reconnect, baseline loss, room/layer generation change, and bounded
periodic interval.

Initial `Snapshot` sections:

1. Header: protocol, match epoch, sequence, host tick, baseline, last accepted
   P2 input, content/room/camera revisions.
2. Display/room: mode, stage/area/room, room bounds, layer generation, loading
   and host-menu/cutscene state.
3. Camera: fixed-point host scroll and display adjustment.
4. P1/P2: transforms, velocity, facing, selected visual frame/pose, health,
   down/revive/tether/transition projections.
5. Interest set: bounded semantic `EntityView` records.
6. Diagnostics: optional low-rate counters, never P2D4 text or raw memory.

Initial `WorldEvent` kinds:

- Room/layer transition begin/commit.
- P2 attack begin/end/cancel, hit, projectile, and visual effect.
- P2 health damage/down/revive/recovery.
- Native enemy defeat, drop spawn/collection/expiry, pickup/reward.
- Host display mode/BGM transition.
- Disconnect, suspend, resume, and compatibility failure.

The M7/v0.8 event ledger and serializable keyframe contract are mandatory
before network combat/progression claims. Event IDs must never contain native
attack slots as their durable identity.

## 8. Replica Client

### 8.1 Presentation Model

The client receives authoritative state into immutable buffers. It renders at
`estimatedHostNow - interpolationDelay`:

- Interpolate remote P1, P2, camera, and safely interpolable entity transforms
  between snapshots.
- Extrapolate from host velocity for at most 100 ms; then hold or fade.
- Predict only local P2 presentation. Store input/predicted state by input
  sequence; on a snapshot, restore authoritative P2, discard acknowledged
  inputs, replay remaining input, and visually smooth the correction.
- Snap room/layer generation changes and clear stale entity views atomically.
- Do not let client prediction decide collisions, damage, drops, or rewards.

### 8.2 Dedicated Asset-Driven Renderer

The client opens verified local stage/room data and uses host-selected symbolic
references. Build fidelity in this order:

1. Static supported room layers at 256x240 logical resolution.
2. Host camera scroll and normal foreground/background ordering.
3. P1/P2 using host-resolved, locally validated frame references.
4. Common enemies, projectiles, drops, and hit effects via `EntityView`.
5. Layer parallax/wrap, common SFX event IDs, BGM track state.
6. Coverage-driven adapters for multipart enemies, bosses, special entities,
   transformations, water, and custom primitives.

The host must never transmit frame pixels. The client may use a bounded generic
marker for unknown presentation-only entity kinds, but it must not invent
gameplay behavior.

### 8.3 Camera, UI, and Audio

- Camera comes from the host every snapshot. Do not locally follow P1/P2 during
  locks, doors, cutscenes, or scripted rooms.
- First UI is a network/session HUD, host P1/P2 health, room/loading state,
  and P2 suspension/revive state.
- During host menu/map/cutscene/loading, show an explicit host-controlled
  presentation state rather than pretend local gameplay is active.
- Phase 1 audio is silent or client-local BGM selected by host track ID. Add
  SFX only as host-timestamped IDs referencing locally verified assets.
- Menus, inventory, map, dialogue, cinematics, and video need separate
  product/renderer plans and are not launch requirements.

## 9. Work Packages

Each package is independently reviewable. A subagent must not start a package
until all listed predecessor gates are green.

### MP-00: Freeze the Network Contract

**Depends on:** Local co-op M5/M6 completion.
**Files:** `docs/multiplayer-replica-client-plan.md`, `mods/coop/ROADMAP.md`.
**Deliverables:** Supported route matrix, explicit non-goals, host authority
table, compatibility manifest fields, packet budgets, privacy policy.

**Acceptance:** Maintainers approve the boundaries; no runtime behavior changes.

### MP-01: Pure Protocol Library

**Depends on:** MP-00.
**Files:** `mods/coop/source/NetworkProtocol.cs`, new pure validation project
under `mods/coop/.validation/CoopNetworkProtocol/`.

**Deliverables:** Fixed binary serializer/parser, message type/length framing,
sequence arithmetic, entity incarnation IDs, bounded payloads, compatibility
hello/reject records, event IDs, error codes.

**Acceptance tests:** Golden bytes, round trips, malformed/truncated/oversized
packets, unknown optional fields, duplicate/reordered/wrapped sequences,
major/minor compatibility, no allocations in warmed encode/decode loops.

### MP-02: Runtime Transport and Main-Thread Bridge

**Depends on:** MP-01.
**Files:** `RecompOne.Runtime/Multiplayer/`, runtime tests.

**Deliverables:** Host/listen and client/connect lifecycles, bounded queues,
socket-thread isolation, explicit shutdown, connection metrics, LAN transport
adapter behind an interface.

**Acceptance tests:** No emulated-memory/GPU access from I/O threads, queue
overflow neutralizes/disconnects safely, shutdown closes all queues, packet
loss/reorder/duplicate injector tests, no leaked background threads.

### MP-03: Host P2 Remote Input Authority

**Depends on:** MP-02 and local P2 safety gates.
**Files:** `mods/coop/source/NetworkInputAuthority.cs`,
`CoopFeasibilityMod.cs`, pure reducer tests.

**Deliverables:** Sequence/epoch-validated remote intent path, timeout-neutral
fallback, source selection UI/configuration, lifecycle neutralization.

**Acceptance tests:** Local and remote input produce the same managed P2 state
under an identical recorded input sequence; stale/wrong-room/replayed input is
neutralized; disconnect while attacking/downed/transitioning restores safety.

### MP-04: Host Snapshot and Keyframe Publication

**Depends on:** MP-03.
**Files:** `NetworkHostSnapshot.cs`, `NetworkReplicaPublication.cs`, tests.

**Deliverables:** Snapshot eligibility gate, application-acknowledged delta
baselines, bounded keyframes, room/layer/camera revisions, semantic P1/P2 and
common `EntityView` records.

**Acceptance tests:** Baseline loss produces a keyframe; stale snapshot/event
does not resurrect an entity; room change atomically invalidates old interest;
all records respect maximum packet/batch budgets.

### MP-05: Offline Replica Renderer Testbed

**Depends on:** MP-01 and verified local asset policy.
**Files:** `clients/SymphonyRecomp.ReplicaClient/`.

**Deliverables:** Local content verifier, supported-room asset loader, 256x240
renderer, camera, snapshot recording/replay harness, P1/P2 placeholders then
validated frame rendering.

**Acceptance tests:** Deterministic recorded-snapshot playback; reference
captures for each supported room/camera; unsupported content fails closed with
a clear compatibility status; no game files leave the local machine.

### MP-06: Live Replica Presentation

**Depends on:** MP-04 and MP-05.
**Deliverables:** Snapshot interpolation buffer, P2 prediction/reconciliation,
entity view registry, room transition/cache invalidation, connection HUD.

**Acceptance tests:** 50/100/200 ms RTT, jitter, reordering, 1/5/10% loss,
baseline loss, reconnect keyframe, host transition, P2 down/revive, and clean
disconnect all maintain bounded presentation behavior without client authority.

### MP-07: Shared-World Event Ledger and Reconnect

**Depends on:** v0.8/M7 shared-world contracts and MP-06.
**Deliverables:** Idempotent semantic combat/drop/progression events, world
keyframe schema without native transient identities, resume tokens, state hash
diagnostics, resync protocol.

**Acceptance tests:** Duplicate/reordered events, entity slot reuse, reconnect
mid-attack/mid-transition/mid-revive, keyframe repair, and deliberate hash
mismatch all converge to authoritative host state.

### MP-08: LAN Supported-Route Release Gate

**Depends on:** MP-07.
**Deliverables:** Automation host/client pair harness, impairment profiles,
published supported-route matrix, failure taxonomy, operator documentation.

**Acceptance:** Three independent cold-launch host/client runs of the supported
route under defined LAN and impairment profiles; clean disconnect/reconnect;
no authority, native ownership, save, or protected-state violations.

### MP-09: Internet/Player-Hosted Gate

**Depends on:** MP-08 and selected reviewed encrypted transport.
**Deliverables:** Token authentication, encryption, replay protection,
ICE/STUN/TURN or dedicated-host policy, abuse limits, privacy review.

**Acceptance:** External security review or documented threat-model signoff,
NAT/relay test matrix, expiry/replay tests, and no secrets in logs/artifacts.

## 10. Validation Matrix

Every package must include pure tests before live tests.

| Category | Required cases |
| --- | --- |
| Compatibility | Wrong region/build/mod/protocol/content hash; feature downgrade; stale match epoch |
| Transport | Loss, jitter, duplication, reordering, MTU pressure, queue overflow, disconnect, reconnect |
| Input | Missing, stale, replayed, future, wrong-epoch, excessive-rate, malformed, held-button timeout |
| Snapshots | Baseline loss, keyframe repair, entity reuse, spawn/despawn ordering, unknown entity kind |
| Lifecycle | Loading, menu, cutscene, layer load, transition, unsupported terrain, reload, unload |
| Authority | Client attempts to send position/damage/drop/progression; host rejects and records bounded reason |
| Rendering | Camera lock, room transition, normal layers, missing asset, unknown effect fallback, P2 down/revive |
| Privacy/security | Token redaction, payload bounds, protocol fuzzing, rate limits, no asset/raw-memory transmission |

## 11. Operational Diagnostics

Expose bounded, redacted per-connection values:

- Protocol/build/content revisions and connection epoch.
- RTT percentiles, jitter, loss, input gaps, snapshot drops, baseline misses,
  keyframe/resync count, interest cardinality, queue depths.
- Last accepted host tick/input sequence/snapshot sequence/event sequence.
- Compatibility rejection and neutralization reason codes.
- Rate-limited host/client state hashes for critical replicated projections.

Do not expose raw packet payloads, tokens, IP addresses, disc paths, or game
assets in normal diagnostics/artifacts.

## 12. Explicit Stop Conditions

Pause expansion and keep the feature experimental if any occur:

- A client can cause native world mutation without validated host intent.
- Native attack ownership, guest-stack, CPU/GPU, or VRAM restoration contracts
  fail under transport impairment.
- A room/layer change permits stale entities or events to affect the new room.
- Client and host content revisions differ without a fail-closed rejection.
- Required presentation requires transmitting game assets or raw engine state.
- Internet transport lacks a reviewed authentication/encryption/NAT plan.

## 13. Sources

### Repository Evidence

- `mods/coop/ROADMAP.md` v0.8-v1.0 milestones and host-authoritative contract.
- `mods/coop/source/CoopFeasibilityMod.cs` P2 update, lifecycle, rendering,
  native attack, terrain, and sprite-frame validation seams.
- `mods/coop/source/ManagedMovementSessionState.cs`, `ManagedReplayState.cs`,
  `ManagedHealthState.cs`, and `TetherSuspensionPolicy.cs` pure authority state.
- `RecompOne/RecompOne.Runtime/Runtime.cs` main-thread action queue.
- `RecompOne/RecompOne.Runtime/Modding/ModLoader.cs` and `ModCompiler.cs`
  dynamic-mod compilation boundary.
- `wrapers/Game.cs`, `wrapers/Entity.cs`, `wrapers/Stage.cs`,
  `events/LevelEvents.cs`, and `events/RenderEvents.cs` host observation and
  local asset/room seams.
- `patches/widescreen/WidescreenPatch.cs` layer representation reference.
- `tools/SymphonyRecomp.Automation.Contracts/AutomationProtocol.cs` local
  automation protocol boundary; not a multiplayer wire protocol.

### External Engineering References

- Glenn Fiedler, [Snapshot Interpolation](https://gafferongames.com/post/snapshot_interpolation/).
- Glenn Fiedler, [Networked Physics](https://gafferongames.com/post/networked_physics_2004/).
- [RFC 8085: UDP Usage Guidelines](https://www.rfc-editor.org/rfc/rfc8085.html).
- [RFC 9000: QUIC](https://www.rfc-editor.org/rfc/rfc9000.html).
- [RFC 9221: QUIC DATAGRAM](https://www.rfc-editor.org/rfc/rfc9221.html).
- [RFC 8445: ICE](https://www.rfc-editor.org/rfc/rfc8445.html),
  [RFC 8489: STUN](https://www.rfc-editor.org/rfc/rfc8489.html), and
  [RFC 8656: TURN](https://www.rfc-editor.org/rfc/rfc8656.html).
- [.NET System.Net.Quic](https://learn.microsoft.com/en-us/dotnet/api/system.net.quic?view=net-10.0).
- [LiteNetLib](https://github.com/RevenantX/LiteNetLib).
- [SotN Decomp](https://github.com/Xeeynamo/sotn-decomp), for the no-assets,
  user-supplied compatible-content model.
