namespace Razor.Sync;

public sealed class ChainSyncOptions
{
    public string? SocketPath { get; set; }
    public string TcpHost { get; set; } = "127.0.0.1";
    public int TcpPort { get; set; } = 3001;
    public ulong NetworkMagic { get; set; } = 2;
    public int KeepAliveSeconds { get; set; } = 20;
    public ulong StartSlot { get; set; }
    public string? StartHash { get; set; }
}
