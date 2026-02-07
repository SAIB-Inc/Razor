using System.Buffers.Binary;

namespace Razor.Storage.Internal;

internal static class StorageKeys
{
    public static byte[] SlotKey(ulong slot)
    {
        byte[] buffer = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(buffer, slot);
        return buffer;
    }

    public static byte[] HeightKey(ulong height)
    {
        byte[] buffer = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(buffer, height);
        return buffer;
    }
}
