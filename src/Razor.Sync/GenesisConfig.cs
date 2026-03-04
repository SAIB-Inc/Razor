using System.Globalization;
using System.Text.Json;

namespace Razor.Sync;

public sealed class GenesisConfig
{
    public required long SystemStartUnixSeconds { get; init; }
    public required int SlotLengthSeconds { get; init; }

    public ulong SlotToTimestamp(ulong slot)
    {
        return (ulong)(SystemStartUnixSeconds + ((long)slot * SlotLengthSeconds));
    }

    public static GenesisConfig LoadFromShelleyGenesis(string path)
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(path));
        JsonElement root = doc.RootElement;

        string systemStart = root.GetProperty("systemStart").GetString()
            ?? throw new InvalidOperationException("shelley-genesis.json missing 'systemStart'.");

        long systemStartUnix = DateTimeOffset.Parse(systemStart, CultureInfo.InvariantCulture).ToUnixTimeSeconds();
        int slotLength = root.GetProperty("slotLength").GetInt32();

        return new GenesisConfig
        {
            SystemStartUnixSeconds = systemStartUnix,
            SlotLengthSeconds = slotLength
        };
    }
}
