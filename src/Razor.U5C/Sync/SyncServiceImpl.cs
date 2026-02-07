using System.Text;
using Google.Protobuf;
using Grpc.Core;
using Utxorpc.V1alpha.Sync;

namespace Razor.U5C.Sync;

public sealed class SyncServiceImpl : SyncService.SyncServiceBase
{
    private static readonly byte[] DummyHash =
    [
        0x10, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF,
        0x01, 0x12, 0x23, 0x34, 0x45, 0x56, 0x67, 0x78,
        0x89, 0x9A, 0xAB, 0xBC, 0xCD, 0xDE, 0xEF, 0xF0,
        0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88
    ];

    private static readonly ulong DummyTimestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static readonly BlockRef DummyTip = new()
    {
        Hash = ByteString.CopyFrom(DummyHash),
        Height = 123_456,
        Slot = 3_456_789,
        Timestamp = DummyTimestamp
    };

    private static readonly AnyChainBlock DummyBlock = new()
    {
        NativeBytes = ByteString.CopyFrom(Encoding.UTF8.GetBytes("razor:u5c:dummy:block"))
    };

    public override Task<FetchBlockResponse> FetchBlock(FetchBlockRequest request, ServerCallContext context)
    {
        var response = new FetchBlockResponse();
        if (request.Ref.Count == 0)
        {
            response.Block.Add(DummyBlock);
            return Task.FromResult(response);
        }

        foreach (var _ in request.Ref)
        {
            response.Block.Add(DummyBlock);
        }

        return Task.FromResult(response);
    }

    public override Task<DumpHistoryResponse> DumpHistory(DumpHistoryRequest request, ServerCallContext context)
    {
        var response = new DumpHistoryResponse();
        var maxItems = request.MaxItems == 0 ? 5 : request.MaxItems;
        var count = (int)Math.Min(maxItems, 5);

        for (var i = 0; i < count; i++)
        {
            response.Block.Add(DummyBlock);
        }

        var nextSlot = DummyTip.Slot + (ulong)count;
        response.NextToken = new BlockRef
        {
            Hash = ByteString.CopyFrom(DummyHash),
            Height = DummyTip.Height + (ulong)count,
            Slot = nextSlot,
            Timestamp = DummyTimestamp + (ulong)(count * 1000)
        };

        return Task.FromResult(response);
    }

    public override async Task FollowTip(FollowTipRequest request, IServerStreamWriter<FollowTipResponse> responseStream, ServerCallContext context)
    {
        var response = new FollowTipResponse
        {
            Apply = DummyBlock,
            Tip = DummyTip
        };

        await responseStream.WriteAsync(response, context.CancellationToken);
    }

    public override Task<ReadTipResponse> ReadTip(ReadTipRequest request, ServerCallContext context)
    {
        return Task.FromResult(new ReadTipResponse { Tip = DummyTip });
    }
}
