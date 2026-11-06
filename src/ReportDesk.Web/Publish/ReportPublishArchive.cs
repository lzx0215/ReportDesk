#nullable disable
using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace ReportDesk.Web.Publish
{
    // Offline preparation only. The historic HIS code uses one ZIP entry with an empty name.
    // The resulting ZIP has not been validated against the HIS client's decompressor.
    public static class ReportPublishArchive
    {
        public const int MaximumContentBytes = 32 * 1024 * 1024;
        public const int MaximumArchiveBytes = 32 * 1024 * 1024;
        private static readonly uint[] CrcTable = CreateCrcTable();

        public static byte[] Create(byte[] content)
        {
            if (content == null) throw new ArgumentNullException(nameof(content));
            if (content.Length == 0 || content.Length > MaximumContentBytes)
                throw new ArgumentOutOfRangeException(nameof(content));
            // .NET Framework ZipArchive.CreateEntry does not allow an empty entry name.
            // Emit the standard single-entry ZIP envelope around a raw DeflateStream instead.
            using (var compressed = new MemoryStream())
            {
                using (var deflate = new DeflateStream(compressed, CompressionLevel.Optimal, true))
                    deflate.Write(content, 0, content.Length);
                if (compressed.Length + 98 > MaximumArchiveBytes)
                    throw new InvalidOperationException("压缩后文件超过允许大小。");
                uint crc = Crc(content);
                using (var output = new MemoryStream())
                using (var w = new BinaryWriter(output, Encoding.UTF8, true))
                {
                    var size = checked((uint)compressed.Length);
                    w.Write(0x04034b50u); w.Write((ushort)20); w.Write((ushort)0); w.Write((ushort)8);
                    w.Write(0x00210000u); w.Write(crc); w.Write(size); w.Write((uint)content.Length); w.Write((ushort)0); w.Write((ushort)0);
                    compressed.Position = 0; compressed.CopyTo(output);
                    uint central = checked((uint)output.Position);
                    w.Write(0x02014b50u); w.Write((ushort)20); w.Write((ushort)20); w.Write((ushort)0); w.Write((ushort)8);
                    w.Write(0x00210000u); w.Write(crc); w.Write(size); w.Write((uint)content.Length);
                    w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)0); w.Write(0u); w.Write(0u);
                    uint centralSize = checked((uint)output.Position - central);
                    w.Write(0x06054b50u); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)1);
                    w.Write(centralSize); w.Write(central); w.Write((ushort)0); w.Flush();
                    return output.ToArray();
                }
            }
        }

        private static uint Crc(byte[] content)
        {
            uint crc = 0xffffffff;
            foreach (byte value in content) crc = CrcTable[(crc ^ value) & 0xff] ^ (crc >> 8);
            return ~crc;
        }

        private static uint[] CreateCrcTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < table.Length; i++)
            {
                uint crc = i;
                for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0u);
                table[i] = crc;
            }
            return table;
        }
    }
}
