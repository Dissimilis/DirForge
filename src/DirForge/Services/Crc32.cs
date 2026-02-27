namespace DirForge.Services;

internal static class Crc32
{
    private static readonly uint[] Table = GenerateTable();
    private const uint InitialValue = 0xFFFFFFFF;

    private static uint[] GenerateTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var crc = i;
            for (int j = 0; j < 8; j++)
                crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
            table[i] = crc;
        }
        return table;
    }

    public static uint Init() => InitialValue;

    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
            crc = Table[(byte)(crc ^ b)] ^ (crc >> 8);
        return crc;
    }

    public static string Finalize(uint crc) => (crc ^ InitialValue).ToString("x8");
}
