namespace Razor.Configuration;

public class NodeConfiguration
{
    public string SocketPath { get; set; } = string.Empty;

    public IntersectionPointConfiguration IntersectionPoint { get; set; } = new();
}

public class IntersectionPointConfiguration
{
    public ulong Slot { get; set; }

    public string Hash { get; set; } = string.Empty;
}