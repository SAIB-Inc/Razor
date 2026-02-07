# Razor Rebuild Plan (U5C v1alpha, Native Bytes MVP)

## Goals
- Rebuild Razor as a real project (not a PoC).
- Use Chrysalis to sync a Cardano node and persist blocks.
- Serve U5C **v1alpha** SyncService (`FetchBlock`, `DumpHistory`, `FollowTip`, `ReadTip`).
- Store data in **ZoneTree** with strong performance and rollback support.

## Non-Goals (MVP)
- U5C Query/Watch/Submit services.
- Ouroboros TCP/Unix socket service (beyond gRPC U5C).
- Full parsed Cardano data; **native bytes only** for now.

## High-Level Architecture
- **Sync**: Chrysalis ChainSync → ingestion pipeline.
- **Storage**: ZoneTree-based block store + indexes + rollback log.
- **API**: gRPC server implementing U5C v1alpha SyncService.

## Repository Restructure (Planned)
- Move existing PoC code into `old/` or `poc/` for reference (later delete).
- New clean layout under `src/` for the rebuild.
- Use a small set of projects:
  - `Razor.Core` (domain interfaces + storage abstractions)
  - `Razor.Storage` (ZoneTree storage implementation)
  - `Razor.Sync` (Chrysalis ChainSync ingestion and chain event stream)
  - `Razor.U5C` (U5C v1alpha gRPC service implementations and mappings)
  - `Razor.App` (CLI/host for services and configuration wiring)

## Scaffold Status (Done)
- `old/` now contains the previous PoC.
- New solution `Razor.slnx` created.
- New projects created under `src/`:
  - `src/Razor.Core`
  - `src/Razor.Storage`
  - `src/Razor.Sync`
  - `src/Razor.U5C`
  - `src/Razor.App`
- Project references wired:
  - `Razor.Storage` → `Razor.Core`
  - `Razor.Sync` → `Razor.Core`
  - `Razor.U5C` → `Razor.Core`
  - `Razor.App` → `Razor.Core`, `Razor.Storage`, `Razor.Sync`, `Razor.U5C`

## Storage Design (ZoneTree)
**Primary data**
- `blocks_by_hash`: hash -> raw block bytes

**Indexes**
- `hash_by_slot`: slot -> hash
- `hash_by_height`: height -> hash (if/when available)

**Chain log (rollback support)**
- append-only log of applied blocks:
  - `(slot, hash, prev_hash, timestamp, height?)`
- rollback uses log to revert indexes and tip.

**Tip**
- `tip` pointer: current `BlockRef`.

## Sync Pipeline
- Connect to node via Chrysalis ChainSync.
- On roll-forward:
  - persist raw block
  - update indexes
  - update tip
  - append to chain log
- On roll-backward:
  - revert indexes using chain log
  - update tip

## U5C v1alpha SyncService
- `FetchBlock`: lookup by hash/slot/height, return `AnyChainBlock.native_bytes`.
- `DumpHistory`: range scan via slot index + pagination token.
- `ReadTip`: return current tip.
- `FollowTip`: stream apply/undo/reset events.

## Configuration
- `Storage:Path` in config file.
- `RAZOR_` env var prefix for all settings.
- CLI override: `--data-path`.

## Milestones
1. **Repo reset + skeleton**: new `src/`, move PoC to `old/`.
2. **Storage layer**: ZoneTree schema + CRUD + rollback log.
3. **Sync ingestion**: ChainSync → storage, basic health logging.
4. **U5C SyncService**: gRPC server + reflection + native bytes.
5. **Validation**: integration tests with preview node + grpcurl.

## Open Questions
- Default storage path (if config/CLI not set): `./data` vs `~/.razor/data`.
- Do we want gRPC reflection enabled by default?
- How do we derive `height` and `timestamp` for `BlockRef` (needs genesis/era data)?
