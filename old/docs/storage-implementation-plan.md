# Razor Storage Implementation Plan - ZoneTree

## Executive Summary

This document outlines the plan to transform Razor from a simple Cardano node client into a **full-featured Cardano node** with:
- Local blockchain storage using ZoneTree (high-performance LSM tree)
- **Ouroboros protocol server** (serve data to other nodes/clients via Unix sockets and TCP)
- **UTxORPC server** (gRPC API implementing the UTxO RPC specification)

**Goals:** 
1. Store, index, and query blockchain data locally
2. **Serve as an Ouroboros node** for other clients (node-to-client protocols over Unix socket/TCP)
3. **Provide UTxORPC gRPC endpoints** for modern applications (sync, query, submit, watch)

---

## 1. Technology Choice: ZoneTree

### Why ZoneTree?

**ZoneTree** was selected over Microsoft FASTER after comprehensive analysis:

| Feature | FASTER | ZoneTree | Winner |
|---------|--------|----------|--------|
| Data Structure | Hash Table | LSM Tree | ZoneTree |
| Ordered Keys | ❌ No | ✅ Yes | **ZoneTree** |
| Range Queries | ❌ Not supported | ✅ Native | **ZoneTree** |
| Sequential Access | ❌ Random only | ✅ Optimized | **ZoneTree** |
| Transactions | ⚠️ Checkpoints | ✅ ACID | **ZoneTree** |
| Write Performance | Very fast | **500% faster** than RocksDB | **ZoneTree** |
| Pure .NET | ❌ Has C++ | ✅ Pure C# | **ZoneTree** |

### Key Benefits for Blockchain Storage:

1. **LSM Tree Architecture** - Perfect for append-only blockchain data
2. **Ordered Keys** - Essential for slot-based queries and iteration
3. **Range Queries** - Query blocks by slot range, epochs, time periods
4. **ACID Transactions** - Handle chain reorganizations cleanly
5. **Pure C#** - Simpler deployment, no native dependencies
6. **Extreme Performance** - 100M key-value pairs inserted in 20 seconds

---

## 2. Storage Architecture

### 2.1 Multiple ZoneTree Instances

We'll maintain **4 separate ZoneTree databases** for different data types:

```
📁 ~/Projects/Razor/data/
├── 📦 blocks/          → ZoneTree #1: Block storage (hash → block CBOR)
├── 📦 slots/           → ZoneTree #2: Slot index (slot → block hash)
├── 📦 utxos/           → ZoneTree #3: UTxO set (txhash+index → output)
└── 📦 addresses/       → ZoneTree #4: Address index (address+txhash → marker)
```

### 2.2 Storage Schema

#### Database #1: Block Storage
```
Key:   byte[32] BlockHash
Value: byte[] Block CBOR

Purpose: Store complete blocks by their hash
Queries: GetBlock(hash), HasBlock(hash), DeleteBlock(hash)
```

#### Database #2: Slot Index
```
Key:   ulong Slot (8 bytes)
Value: byte[32] BlockHash

Purpose: Map slots to block hashes for ordered access
Queries: 
  - GetBlockAtSlot(slot)
  - GetBlockRange(fromSlot, toSlot)
  - GetLatestBlocks(count)
  - GetCurrentTip()
```

#### Database #3: UTxO Set
```
Key:   byte[32] TxHash + uint OutputIndex (40 bytes total)
Value: byte[] TransactionOutput CBOR

Purpose: Maintain the current unspent transaction output set
Queries:
  - GetUtxo(txHash, index)
  - IsUnspent(txHash, index)
  - AddUtxo(txHash, index, output)
  - SpendUtxo(txHash, index)
```

#### Database #4: Address Index
```
Key:   byte[57] Address + byte[32] TxHash + uint OutputIndex (97 bytes)
Value: byte[1] Marker (0x01 - just presence indicator)

Purpose: Index UTxOs by address for quick address queries
Queries:
  - GetAddressUtxos(address)
  - GetAddressUtxoCount(address)
```

---

## 3. Server Architecture

### 3.1 Dual Server Model

Razor will operate as both:
1. **Ouroboros Protocol Server** - Native Cardano node-to-client protocols
2. **UTxORPC Server** - Modern gRPC API for applications

```
┌─────────────────────────────────────────────────────────────────┐
│                        Razor Node                                │
│                                                                   │
│  ┌────────────────┐           ┌──────────────────────────────┐  │
│  │  Ouroboros     │           │      UTxORPC                 │  │
│  │  Server        │           │      Server                  │  │
│  │                │           │                              │  │
│  │  Unix Socket   │           │  gRPC                        │  │
│  │  TCP Socket    │           │  HTTP/2                      │  │
│  └────────┬───────┘           └───────┬──────────────────────┘  │
│           │                           │                          │
│           └───────────┬───────────────┘                          │
│                       │                                          │
│                       ▼                                          │
│           ┌────────────────────────┐                             │
│           │   Storage Layer        │                             │
│           │   (ZoneTree)           │                             │
│           └────────────────────────┘                             │
└─────────────────────────────────────────────────────────────────┘
```

### 3.2 UTxORPC Specification

UTxORPC is a **gRPC-based interface** for UTxO blockchains with 4 main modules:

#### Module 1: Sync
```protobuf
service SyncService {
  // Follow the chain from a specific point
  rpc FollowTip(FollowTipRequest) returns (stream FollowTipResponse);
  
  // Fetch block by reference
  rpc FetchBlock(FetchBlockRequest) returns (stream FetchBlockResponse);
  
  // Dump history from point
  rpc DumpHistory(DumpHistoryRequest) returns (stream DumpHistoryResponse);
}
```

**Purpose:** Chain synchronization - stream blocks, follow tip, dump history

#### Module 2: Query
```protobuf
service QueryService {
  // Read parameters of the ledger
  rpc ReadParams(ReadParamsRequest) returns (ReadParamsResponse);
  
  // Read UTxOs by reference
  rpc ReadUtxos(ReadUtxosRequest) returns (ReadUtxosResponse);
  
  // Search UTxOs by pattern
  rpc SearchUtxos(SearchUtxosRequest) returns (stream SearchUtxosResponse);
}
```

**Purpose:** Query blockchain state - parameters, UTxOs, addresses

#### Module 3: Submit
```protobuf
service SubmitService {
  // Submit a transaction
  rpc SubmitTx(SubmitTxRequest) returns (SubmitTxResponse);
  
  // Wait for transaction confirmation
  rpc WaitForTx(WaitForTxRequest) returns (WaitForTxResponse);
  
  // Read mempool
  rpc ReadMempool(ReadMempoolRequest) returns (stream ReadMempoolResponse);
}
```

**Purpose:** Transaction submission and mempool monitoring

#### Module 4: Watch
```protobuf
service WatchService {
  // Watch for transactions matching a pattern
  rpc WatchTx(WatchTxRequest) returns (stream WatchTxResponse);
  
  // Watch mempool  
  rpc WatchMempool(WatchMempoolRequest) returns (stream WatchMempoolResponse);
}
```

**Purpose:** Real-time notifications for transactions and events

### 3.3 UTxORPC Benefits

| Feature | Traditional REST | UTxORPC |
|---------|------------------|---------|
| **Protocol** | HTTP/1.1 JSON | HTTP/2 Protobuf |
| **Message Size** | Large (JSON) | **Small (binary)** |
| **Streaming** | ❌ No | ✅ **Bi-directional** |
| **Type Safety** | ⚠️ Runtime | ✅ **Compile-time** |
| **Code Gen** | Manual | ✅ **Auto-generated** |
| **Multi-lang** | Manual clients | ✅ **SDKs for all langs** |
| **Spec** | OpenAPI (optional) | ✅ **Proto files** |

**Key Advantages:**
- ✅ **50-80% smaller messages** than JSON
- ✅ **Streaming support** for real-time sync
- ✅ **Auto-generated clients** (Go, Rust, JS, Python, .NET, etc.)
- ✅ **Standardized** across all UTxO blockchains
- ✅ **Type-safe** with protobuf schema validation

### 3.4 Ouroboros Protocol Server

Razor will also serve the native Ouroboros **node-to-client protocols** for compatibility with existing Cardano tools:

**Supported Protocols:**
- ✅ **Handshake** - Version negotiation
- ✅ **ChainSync** - Block streaming
- ✅ **LocalStateQuery** - Ledger queries
- ✅ **LocalTxSubmit** - Transaction submission
- ✅ **LocalTxMonitor** - Mempool monitoring

**Transports:**
- ✅ **Unix Domain Socket** - `/tmp/razor-node.socket`
- ✅ **TCP Socket** - `localhost:3001` (configurable)

This allows existing tools like `cardano-cli`, `cardano-wallet`, and other Cardano clients to connect to Razor directly!

---

## 4. Project Structure

```
Razor/
├── Storage/
│   ├── Interfaces/
│   │   ├── IBlockStorage.cs           # Block storage operations
│   │   ├── ISlotIndex.cs              # Slot indexing operations
│   │   ├── IUtxoStorage.cs            # UTxO set management
│   │   └── IAddressIndex.cs           # Address indexing
│   │
│   ├── ZoneTree/
│   │   ├── ZoneTreeBlockStorage.cs    # Implements IBlockStorage
│   │   ├── ZoneTreeSlotIndex.cs       # Implements ISlotIndex
│   │   ├── ZoneTreeUtxoStorage.cs     # Implements IUtxoStorage
│   │   ├── ZoneTreeAddressIndex.cs    # Implements IAddressIndex
│   │   └── ZoneTreeManager.cs         # Lifecycle management
│   │
│   ├── Models/
│   │   ├── StoredBlock.cs             # Block metadata wrapper
│   │   ├── ChainTip.cs                # Current chain tip info
│   │   ├── UtxoKey.cs                 # Composite key for UTxOs
│   │   ├── AddressIndexKey.cs         # Composite key for address index
│   │   └── StorageStats.cs            # Storage metrics
│   │
│   └── Serializers/
│       ├── BlockHashSerializer.cs     # ZoneTree serializer
│       ├── SlotSerializer.cs          # ZoneTree serializer
│       ├── UtxoKeySerializer.cs       # ZoneTree serializer
│       └── AddressKeySerializer.cs    # ZoneTree serializer
│
├── Indexer/
│   ├── BlockIndexer.cs                # Main block processing logic
│   ├── UtxoIndexer.cs                 # UTxO set maintenance
│   └── AddressIndexer.cs              # Address index maintenance
│
├── Services/
│   ├── NodeService.cs                 # UPDATED - simplified
│   ├── StorageService.cs              # NEW - central storage manager
│   └── ChainSyncService.cs            # NEW - dedicated chain sync
│
├── Commands/
│   ├── RunCommand.cs                  # UPDATED - uses new services
│   ├── QueryCommand.cs                # UPDATED - queries storage
│   ├── TransactionCommand.cs          # Existing
│   └── StorageCommand.cs              # NEW - storage management
│
└── Configuration/
    ├── NodeConfiguration.cs           # Existing
    └── StorageConfiguration.cs        # NEW - storage settings
```

---

## 4. Data Flow Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                     Cardano Node                             │
│                  (via Unix Socket)                           │
└────────────────────┬────────────────────────────────────────┘
                     │ ChainSync Protocol
                     ▼
┌─────────────────────────────────────────────────────────────┐
│                  ChainSyncService                            │
│  • Connects to node via Chrysalis.Network                   │
│  • FindIntersection at configured slot                       │
│  • Continuous NextRequest loop                               │
│  • Handles MessageRollForward / MessageRollBackward          │
└────────────────────┬────────────────────────────────────────┘
                     │ Block Messages
                     ▼
┌─────────────────────────────────────────────────────────────┐
│                   BlockIndexer                               │
│  • Deserialize block CBOR (handles all eras)                │
│  • Extract metadata: slot, hash, transactions                │
│  • Process transactions: inputs (spends) + outputs (creates) │
│  • Coordinate storage updates                                │
└─────┬──────────────┬──────────────┬────────────────────────┘
      │              │              │
      ▼              ▼              ▼
┌──────────┐  ┌──────────┐  ┌──────────────┐
│  Block   │  │  Slot    │  │    UTxO      │
│ Storage  │  │  Index   │  │   Indexer    │
│          │  │          │  │              │
│ ZoneTree │  │ ZoneTree │  │ • Add UTxOs  │
│    #1    │  │    #2    │  │ • Spend UTxOs│
└──────────┘  └──────────┘  └──────┬───────┘
                                    │
                                    ▼
                            ┌──────────────┐
                            │   Address    │
                            │   Indexer    │
                            │              │
                            │ • Index by   │
                            │   address    │
                            │              │
                            │  ZoneTree #4 │
                            └──────────────┘
                                    │
                                    ▼
                            ┌──────────────┐
                            │    UTxO      │
                            │   Storage    │
                            │              │
                            │  ZoneTree #3 │
                            └──────────────┘
```

---

## 5. Core Interfaces

### 5.1 IBlockStorage

```csharp
public interface IBlockStorage : IDisposable
{
    // Store a block
    Task StoreBlockAsync(byte[] blockHash, byte[] blockCbor, CancellationToken ct);
    
    // Retrieve a block
    Task<byte[]?> GetBlockAsync(byte[] blockHash, CancellationToken ct);
    
    // Check if block exists
    Task<bool> HasBlockAsync(byte[] blockHash, CancellationToken ct);
    
    // Delete block (for rollback)
    Task DeleteBlockAsync(byte[] blockHash, CancellationToken ct);
    
    // Batch operations for performance
    Task StoreBatchAsync(IEnumerable<(byte[] hash, byte[] cbor)> blocks, CancellationToken ct);
    
    // Get storage statistics
    StorageStats GetStats();
}
```

### 5.2 ISlotIndex

```csharp
public interface ISlotIndex : IDisposable
{
    // Map slot to block hash
    Task SetSlotAsync(ulong slot, byte[] blockHash, CancellationToken ct);
    
    // Get block hash for slot
    Task<byte[]?> GetBlockHashAsync(ulong slot, CancellationToken ct);
    
    // Range query: get all blocks in slot range
    IAsyncEnumerable<(ulong slot, byte[] blockHash)> GetRangeAsync(
        ulong fromSlot, 
        ulong toSlot, 
        CancellationToken ct);
    
    // Get latest N slots (for recent blocks)
    IAsyncEnumerable<(ulong slot, byte[] blockHash)> GetLatestAsync(
        int count, 
        CancellationToken ct);
    
    // Delete slot (for rollback)
    Task DeleteSlotAsync(ulong slot, CancellationToken ct);
    
    // Get current tip slot
    Task<ulong?> GetLatestSlotAsync(CancellationToken ct);
}
```

### 5.3 IUtxoStorage

```csharp
public interface IUtxoStorage : IDisposable
{
    // Add UTxO to the set
    Task AddUtxoAsync(byte[] txHash, uint outputIndex, byte[] outputCbor, CancellationToken ct);
    
    // Spend UTxO (remove from set)
    Task SpendUtxoAsync(byte[] txHash, uint outputIndex, CancellationToken ct);
    
    // Get UTxO data
    Task<byte[]?> GetUtxoAsync(byte[] txHash, uint outputIndex, CancellationToken ct);
    
    // Check if UTxO is unspent
    Task<bool> IsUnspentAsync(byte[] txHash, uint outputIndex, CancellationToken ct);
    
    // Batch operations for performance
    Task AddBatchAsync(IEnumerable<(byte[] txHash, uint index, byte[] output)> utxos, CancellationToken ct);
    Task SpendBatchAsync(IEnumerable<(byte[] txHash, uint index)> utxos, CancellationToken ct);
    
    // Get statistics
    Task<long> GetUtxoCountAsync(CancellationToken ct);
}
```

### 5.4 IAddressIndex

```csharp
public interface IAddressIndex : IDisposable
{
    // Index UTxO by address
    Task AddAddressUtxoAsync(byte[] address, byte[] txHash, uint outputIndex, CancellationToken ct);
    
    // Remove from index (when spent)
    Task RemoveAddressUtxoAsync(byte[] address, byte[] txHash, uint outputIndex, CancellationToken ct);
    
    // Query all UTxOs for an address
    IAsyncEnumerable<(byte[] txHash, uint outputIndex)> GetAddressUtxosAsync(
        byte[] address, 
        CancellationToken ct);
    
    // Batch operations
    Task AddBatchAsync(
        IEnumerable<(byte[] address, byte[] txHash, uint index)> entries, 
        CancellationToken ct);
    
    // Count UTxOs for address
    Task<long> GetAddressUtxoCountAsync(byte[] address, CancellationToken ct);
}
```

---

## 6. Configuration

### 6.1 appsettings.json (Updated)

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft": "Warning",
      "Razor": "Information"
    }
  },
  "NodeConfiguration": {
    "SocketPath": "/tmp/node.socket",
    "IntersectionPoint": {
      "Slot": 93121991,
      "Hash": "400b1bd59f83be8f911b101178b05de48b1e26301359c19641a7774e9b656444"
    }
  },
  "StorageConfiguration": {
    "DataDirectory": "./data",
    "EnableCompression": true,
    "EnableWAL": true,
    "MemoryLimit": 1073741824,
    "DiskSegmentSize": 16777216,
    "EnableMetrics": true,
    "Databases": {
      "Blocks": {
        "Path": "blocks",
        "MemorySize": 536870912
      },
      "Slots": {
        "Path": "slots",
        "MemorySize": 134217728
      },
      "Utxos": {
        "Path": "utxos",
        "MemorySize": 268435456
      },
      "Addresses": {
        "Path": "addresses",
        "MemorySize": 134217728
      }
    }
  }
}
```

### 6.2 StorageConfiguration.cs (NEW)

```csharp
namespace Razor.Configuration;

public class StorageConfiguration
{
    public string DataDirectory { get; set; } = "./data";
    public bool EnableCompression { get; set; } = true;
    public bool EnableWAL { get; set; } = true;
    public long MemoryLimit { get; set; } = 1_073_741_824; // 1GB
    public int DiskSegmentSize { get; set; } = 16_777_216; // 16MB
    public bool EnableMetrics { get; set; } = true;
    public Dictionary<string, DatabaseConfig> Databases { get; set; } = new();
}

public class DatabaseConfig
{
    public string Path { get; set; } = string.Empty;
    public long MemorySize { get; set; }
}
```

---

## 7. Service Responsibilities

### 7.1 ChainSyncService (NEW)

**Purpose:** Dedicated service for blockchain synchronization

**Responsibilities:**
- Connect to Cardano node via Chrysalis ChainSync protocol
- Find intersection point at configured slot/hash
- Continuous NextRequest loop to receive blocks
- Handle MessageRollForward (new blocks)
- Handle MessageRollBackward (chain reorganizations)
- Pass blocks to BlockIndexer for processing
- Maintain sync state (current tip, sync progress %)
- Emit sync progress events

**Key Methods:**
```csharp
Task StartSyncAsync(CancellationToken ct);
Task StopSyncAsync();
Task<ChainTip> GetCurrentTipAsync();
Task<SyncProgress> GetSyncProgressAsync();
event EventHandler<BlockReceivedEventArgs> BlockReceived;
event EventHandler<RollbackEventArgs> RollbackDetected;
```

### 7.2 StorageService (NEW)

**Purpose:** Central manager for all storage operations

**Responsibilities:**
- Initialize all 4 ZoneTree instances on startup
- Provide access to all storage interfaces (IBlockStorage, etc.)
- Handle lifecycle (startup, shutdown, flush, dispose)
- Coordinate transactions across multiple stores
- Collect and expose storage metrics
- Health checks and diagnostics
- Periodic maintenance (compaction, WAL rotation)

**Key Methods:**
```csharp
Task InitializeAsync();
IBlockStorage GetBlockStorage();
ISlotIndex GetSlotIndex();
IUtxoStorage GetUtxoStorage();
IAddressIndex GetAddressIndex();
Task<StorageHealth> GetHealthAsync();
Task FlushAsync();
```

### 7.3 BlockIndexer (NEW)

**Purpose:** Process blocks and extract indexable data

**Responsibilities:**
- Deserialize block CBOR (handle all Cardano eras: Alonzo, Babbage, Conway)
- Extract block metadata: slot, hash, previous hash, era
- Process all transactions in the block
- Identify new UTxOs (transaction outputs)
- Identify spent UTxOs (transaction inputs)
- Extract addresses from transaction outputs
- Coordinate updates to all storage layers:
  - Store block → BlockStorage
  - Index slot → SlotIndex
  - Add/spend UTxOs → UtxoStorage
  - Index addresses → AddressIndex
- Handle errors gracefully

**Key Methods:**
```csharp
Task ProcessBlockAsync(byte[] blockCbor, CancellationToken ct);
Task ProcessRollbackAsync(ulong toSlot, CancellationToken ct);
BlockMetadata ExtractMetadata(Block block);
IEnumerable<UtxoChange> ExtractUtxoChanges(Block block);
```

### 7.4 NodeService (UPDATED)

**Purpose:** High-level node operations (simplified)

**Changes:**
- Remove direct ChainSync logic → delegate to ChainSyncService
- Add StorageService dependency
- `GetCurrentTipAsync()` → query from local storage first, fallback to node
- `GetUtxosByAddress()` → query from local AddressIndex
- `SubmitTransaction()` → still connects to node (submission requires node)
- New method: `QueryLocalBlock(hash)` → query from storage

---

## 8. Implementation Sequence

### Phase 1: Foundation (Week 1)

**Tasks:**
1. Add ZoneTree NuGet package to Razor.csproj
2. Create all interface definitions (IBlockStorage, ISlotIndex, IUtxoStorage, IAddressIndex)
3. Create configuration classes (StorageConfiguration, DatabaseConfig)
4. Create storage models (UtxoKey, AddressIndexKey, StorageStats, ChainTip)
5. Create key serializers for ZoneTree (IRefSerializer<T> implementations)

**Deliverables:**
- [ ] ZoneTree package added
- [ ] All interfaces defined in `/Storage/Interfaces/`
- [ ] Configuration classes in `/Configuration/`
- [ ] Models in `/Storage/Models/`
- [ ] Serializers in `/Storage/Serializers/`

### Phase 2: Core Storage (Week 1-2)

**Tasks:**
1. Implement ZoneTreeBlockStorage (IBlockStorage)
2. Implement ZoneTreeSlotIndex (ISlotIndex with range queries)
3. Implement ZoneTreeUtxoStorage (IUtxoStorage)
4. Implement ZoneTreeAddressIndex (IAddressIndex)
5. Implement ZoneTreeManager (lifecycle, initialization, disposal)
6. Add comprehensive unit tests for each storage layer

**Deliverables:**
- [ ] ZoneTreeBlockStorage fully implemented and tested
- [ ] ZoneTreeSlotIndex with range query support
- [ ] ZoneTreeUtxoStorage with batch operations
- [ ] ZoneTreeAddressIndex with efficient lookups
- [ ] ZoneTreeManager managing all instances
- [ ] Unit test coverage > 80%

### Phase 3: Services (Week 2)

**Tasks:**
1. Create StorageService (central manager)
2. Create ChainSyncService (extract logic from NodeService)
3. Create BlockIndexer with era support
4. Update NodeService to use new services
5. Wire up dependency injection in Program.cs
6. Add logging throughout

**Deliverables:**
- [ ] StorageService managing all storage
- [ ] ChainSyncService handling sync
- [ ] BlockIndexer processing blocks
- [ ] Updated NodeService
- [ ] DI configuration complete
- [ ] Comprehensive logging

### Phase 4: Indexing Logic (Week 2-3)

**Tasks:**
1. Implement UtxoIndexer (add/spend logic)
2. Implement AddressIndexer (address extraction and indexing)
3. Handle block deserialization for all eras (Alonzo, Babbage, Conway)
4. Implement proper rollback handling
5. Add transaction batching for performance
6. Integration tests with real blocks

**Deliverables:**
- [ ] UtxoIndexer maintaining UTXO set correctly
- [ ] AddressIndexer tracking all addresses
- [ ] Support for Alonzo/Babbage/Conway blocks
- [ ] Rollback logic tested
- [ ] Batch operations optimized
- [ ] Integration tests passing

### Phase 5: Query Updates (Week 3)

**Tasks:**
1. Update QueryCommand to query local storage
2. Add new query commands (range queries, block by hash)
3. Update RunCommand with sync progress display
4. Create StorageCommand (stats, compact, verify)
5. Add storage metrics and monitoring

**Deliverables:**
- [ ] `query tip` uses local storage
- [ ] `query utxos` uses AddressIndex
- [ ] New commands: `query blocks --from-slot X --to-slot Y`
- [ ] New commands: `storage stats`, `storage compact`
- [ ] Sync progress shown during `run`
- [ ] Metrics dashboard ready

### Phase 6: Testing & Optimization (Week 3-4)

**Tasks:**
1. Integration tests with live Preview network
2. Performance benchmarks (blocks/sec, storage size)
3. Memory usage profiling and optimization
4. Add metrics collection (Prometheus, StatsD)
5. Documentation (usage guide, architecture docs)
6. Code review and cleanup

**Deliverables:**
- [ ] Full sync test on Preview network
- [ ] Performance report (target: 1000 blocks/sec)
- [ ] Memory optimization (target: < 2GB for full sync)
- [ ] Metrics exporter
- [ ] Complete documentation
- [ ] Production-ready code

---

## 9. Transaction Boundaries

### Per-Block Processing Transaction

```
1. BEGIN TRANSACTION
2. Store block → BlockStorage
3. Update slot index → SlotIndex
4. FOR EACH transaction in block:
   a. FOR EACH input (spend):
      - Remove from UtxoStorage
      - Remove from AddressIndex
   b. FOR EACH output (create):
      - Add to UtxoStorage
      - Extract address and add to AddressIndex
5. COMMIT TRANSACTION
```

### Rollback Transaction

```
When MessageRollBackward received to slot X:

1. Get current tip slot Y
2. BEGIN TRANSACTION
3. FOR slot in REVERSE [X+1 .. Y]:
   a. Get block at slot from SlotIndex
   b. Load block from BlockStorage
   c. FOR EACH transaction in block (REVERSE order):
      - Reverse UTxO operations (add back spends, remove creates)
      - Reverse address index operations
   d. Delete block from BlockStorage
   e. Delete slot from SlotIndex
4. Update tip to slot X
5. COMMIT TRANSACTION
```

---

## 10. Performance Optimizations

### Batch Operations

**Strategy:** Process multiple blocks in a single transaction

```
During sync from genesis:
- Accumulate 100 blocks
- Process all in single transaction
- Batch UTxO updates
- Batch address index updates
- Commit once

Benefits:
- Reduces transaction overhead
- Improves throughput (10x faster)
- Better disk I/O patterns
```

### Parallel Processing

**Strategy:** Use available CPU cores

```
1. Deserialize blocks in parallel (CPU-bound)
2. Extract UTxO changes in parallel
3. Sequential storage writes (I/O-bound)

Benefits:
- Better CPU utilization
- Faster processing during sync
```

### Memory Management

**Configuration:**
```
ZoneTree settings:
- MemoryLimit: 1GB total
- BlockStorage: 512MB (largest)
- UtxoStorage: 256MB (frequent access)
- SlotIndex: 128MB
- AddressIndex: 128MB

WAL settings:
- MaxWALSize: 100MB
- AutoRotation: true
- FlushInterval: 5 seconds
```

### Caching

**Hot Data Cache:**
```
In-memory LRU caches:
- Recent 1000 blocks
- Hot UTxOs (frequently queried)
- Current tip (always cached)

Benefits:
- Faster queries
- Reduced disk I/O
```

---

## 11. New CLI Commands

```bash
# Existing commands (unchanged)
dotnet run -- run
dotnet run -- query tip
dotnet run -- query utxos --address addr_test1...
dotnet run -- transaction submit <hex>

# New storage management commands
dotnet run -- storage stats                    # Show database statistics
dotnet run -- storage compact                  # Manual compaction
dotnet run -- storage export --path <dir>      # Export all data
dotnet run -- storage import --path <dir>      # Import data
dotnet run -- storage verify                   # Verify data integrity
dotnet run -- storage rollback --slot <slot>   # Manual rollback to slot

# New query commands
dotnet run -- query block --hash <hex>                      # Get block by hash
dotnet run -- query blocks --from-slot <X> --to-slot <Y>    # Range query
dotnet run -- query tx --hash <hex>                         # Get transaction
dotnet run -- query utxo --txhash <hex> --index <n>         # Get specific UTxO
dotnet run -- query epoch --number <n>                      # Get epoch info

# Sync commands
dotnet run -- sync --from-slot <slot>          # Sync from specific slot
dotnet run -- sync --reset                     # Clear storage and resync
```

---

## 12. Metrics & Monitoring

### Key Metrics

**Sync Metrics:**
```
- blocks_synced_total (counter)
- blocks_per_second (gauge)
- current_slot (gauge)
- tip_age_seconds (gauge)
- sync_progress_percent (gauge)
- rollbacks_total (counter)
```

**Storage Metrics:**
```
- storage_blocks_count (gauge)
- storage_utxos_count (gauge)
- storage_size_bytes{db="blocks|slots|utxos|addresses"} (gauge)
- storage_operations_total{op="read|write|delete"} (counter)
- storage_operation_duration_seconds (histogram)
```

**Performance Metrics:**
```
- block_processing_duration_seconds (histogram)
- utxo_indexing_duration_seconds (histogram)
- memory_usage_bytes (gauge)
- disk_io_bytes_total (counter)
```

### Monitoring Dashboard

```
Grafana panels:
1. Sync Progress (% complete, blocks/sec)
2. Chain Tip (current slot, age)
3. Storage Size (per database)
4. UTxO Set Growth
5. Performance (latency p50, p95, p99)
6. System Resources (CPU, Memory, Disk I/O)
```

---

## 13. Rollback Handling Strategy

### Detection

```
MessageRollBackward received with:
- Target slot: X
- Target hash: <hash>

Current state:
- Tip slot: Y (where Y > X)
```

### Execution Steps

```csharp
async Task HandleRollbackAsync(ulong targetSlot, byte[] targetHash)
{
    ulong currentSlot = await _slotIndex.GetLatestSlotAsync();
    
    _logger.LogWarning("Rollback detected: {CurrentSlot} → {TargetSlot}", 
        currentSlot, targetSlot);
    
    // Begin transaction
    using var transaction = _storageService.BeginTransaction();
    
    try
    {
        // Roll back each slot in reverse
        for (ulong slot = currentSlot; slot > targetSlot; slot--)
        {
            byte[]? blockHash = await _slotIndex.GetBlockHashAsync(slot);
            if (blockHash == null) continue;
            
            byte[]? blockCbor = await _blockStorage.GetBlockAsync(blockHash);
            if (blockCbor == null) continue;
            
            // Deserialize and reverse operations
            Block block = DeserializeBlock(blockCbor);
            await ReverseBlockOperations(block);
            
            // Delete from storage
            await _blockStorage.DeleteBlockAsync(blockHash);
            await _slotIndex.DeleteSlotAsync(slot);
        }
        
        // Commit transaction
        await transaction.CommitAsync();
        
        _logger.LogInformation("Rollback completed to slot {TargetSlot}", targetSlot);
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Rollback failed, rolling back transaction");
        await transaction.RollbackAsync();
        throw;
    }
}
```

---

## 14. Error Handling & Recovery

### Corruption Detection

```
On startup:
1. Verify database integrity
2. Check tip consistency (slot index matches block storage)
3. Sample UTxO set integrity
4. Verify address index consistency
```

### Recovery Strategies

**Scenario 1: Corrupted Block**
```
- Mark block as corrupted
- Delete from storage
- Re-sync from node
```

**Scenario 2: Inconsistent UTxO Set**
```
- Rebuild UTxO set from blocks
- Rebuild address index
- Verify consistency
```

**Scenario 3: Database Corruption**
```
- ZoneTree auto-recovery via WAL
- If WAL corrupt: restore from checkpoint
- If checkpoint corrupt: resync from genesis
```

---

## 15. Future Enhancements

### Phase 2 Features (Post-MVP)

1. **Block Validation**
   - Verify block signatures
   - Validate transactions
   - Check Plutus scripts

2. **P2P Networking**
   - Implement node-to-node protocols
   - Block fetching
   - Transaction relay

3. **Staking Support**
   - Track stake pools
   - Calculate rewards
   - Track delegations

4. **Smart Contract Support**
   - Full Plutus evaluation
   - Script validation
   - Datum/Redeemer storage

5. **API Server**
   - REST API for queries
   - WebSocket for real-time updates
   - GraphQL support

6. **Snapshots**
   - Create blockchain snapshots
   - Fast bootstrap from snapshot
   - Incremental snapshots

---

## 16. Success Criteria

### MVP (Minimum Viable Product)

- ✅ Full blockchain sync from genesis
- ✅ Store all blocks locally
- ✅ Maintain UTxO set
- ✅ Query by address works
- ✅ Handle rollbacks correctly
- ✅ Sync at > 500 blocks/second
- ✅ Memory usage < 2GB during sync
- ✅ Storage size < 50GB for Preview network
- ✅ 100% uptime during 7-day test

### Performance Targets

```
Sync Performance:
- Genesis → Tip: < 4 hours (Preview network)
- Blocks/second: > 500 average
- Peak throughput: > 1000 blocks/sec

Query Performance:
- Block by hash: < 10ms p95
- Block by slot: < 10ms p95
- UTxOs by address: < 100ms p95
- Range query (100 blocks): < 500ms p95

Resource Usage:
- Memory: < 2GB sustained
- Disk space: < 50GB (Preview)
- CPU: < 50% on 4-core system
```

---

## 17. Testing Strategy

### Unit Tests

```
Coverage targets:
- Storage layer: > 90%
- Indexers: > 85%
- Services: > 80%

Test cases:
- All storage operations (CRUD)
- Range queries with edge cases
- Batch operations
- Error handling
- Serialization/deserialization
```

### Integration Tests

```
Scenarios:
1. Sync from genesis on testnet
2. Handle multiple rollbacks
3. Query after rollback
4. Restart during sync
5. Corrupt database recovery
6. Full sync + query stress test
```

### Performance Tests

```
Benchmarks:
1. Storage throughput (ops/sec)
2. Sync speed (blocks/sec)
3. Query latency (p50, p95, p99)
4. Memory usage over time
5. Disk I/O patterns
```

---

## 18. Documentation Deliverables

1. **Architecture Document** (this file)
2. **API Documentation** (XML docs + generated)
3. **User Guide** (how to run, query, troubleshoot)
4. **Developer Guide** (how to extend, contribute)
5. **Performance Tuning Guide**
6. **Deployment Guide** (production setup)

---

## 19. Dependencies

### NuGet Packages

```xml
<PackageReference Include="ZoneTree" Version="latest" />
<PackageReference Include="Chrysalis.Network" Version="latest" />
<PackageReference Include="Chrysalis.Cbor" Version="latest" />
<PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="9.0.0" />
<PackageReference Include="Microsoft.Extensions.Configuration" Version="9.0.0" />
<PackageReference Include="Microsoft.Extensions.Logging" Version="9.0.0" />
```

### External Systems

- **Cardano Node** (via Unix socket)
  - Version: 10.4.1+
  - Network: Preview (testnet)
  - Socket path: /tmp/node.socket

---

## 20. Timeline Summary

| Phase | Duration | Key Deliverables |
|-------|----------|------------------|
| **Phase 1: Foundation** | Week 1 | Interfaces, models, serializers |
| **Phase 2: Storage** | Week 1-2 | ZoneTree implementations |
| **Phase 3: Services** | Week 2 | ChainSyncService, StorageService |
| **Phase 4: Indexing** | Week 2-3 | BlockIndexer, rollback handling |
| **Phase 5: Queries** | Week 3 | Updated commands, new CLI |
| **Phase 6: Testing** | Week 3-4 | Integration tests, optimization |

**Total: ~4 weeks to MVP**

---

## Conclusion

This plan transforms Razor from a simple node client into a full Cardano node with local storage, enabling:

- 🚀 **Fast local queries** without node dependency
- 📊 **Rich indexing** (blocks, UTxOs, addresses)
- 🔍 **Advanced queries** (range queries, epoch queries)
- ⚡ **High performance** (500+ blocks/sec sync)
- 🔄 **Rollback support** (handle chain reorganizations)
- 📈 **Observability** (metrics, monitoring)

The use of ZoneTree as the storage engine provides the perfect foundation for blockchain data with its LSM tree architecture, ordered keys, range query support, and extreme performance.

---

**Next Steps:**
1. Review and approve this plan
2. Begin Phase 1: Foundation
3. Set up project tracking (GitHub issues/projects)
4. Establish CI/CD pipeline
5. Start implementation!
