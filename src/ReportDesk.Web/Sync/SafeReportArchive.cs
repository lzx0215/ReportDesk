#nullable disable
using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace ReportDesk.Web.Sync
{
    public static class SafeReportArchive
    {
        // The HIS writer uses ONE empty-name entry. ZipArchive permits reading it; never use ExtractToDirectory.
        public static byte[] Extract(byte[] content, long compressedLimit, long expandedLimit)
        {
            try
            {
                if (content == null || content.Length == 0 || content.LongLength > compressedLimit)
                    throw new SyncSafetyException("ArchiveSizeRejected");
                using (var stream = new MemoryStream(content, false))
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Read, false))
                {
                    if (archive.Entries.Count != 1) throw new SyncSafetyException("ArchiveEntryCountRejected");
                    var entry = archive.Entries[0];
                    if (entry.FullName.Length != 0) ReportWorkDirectory.Normalize(entry.FullName);
                    if (entry.Length < 1 || entry.Length > expandedLimit) throw new SyncSafetyException("ArchiveSizeRejected");
                    // Framework DeflateStream does not guarantee CRC validation. Check the ZIP central CRC ourselves.
                    uint expectedCrc = ReadCentralCrc(content);
                    using (var input = entry.Open())
                    using (var output = new MemoryStream())
                    {
                        var buffer = new byte[8192]; int count; uint crc = 0xffffffff;
                        while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            if (output.Length + count > expandedLimit) throw new SyncSafetyException("ArchiveSizeRejected");
                            output.Write(buffer, 0, count);
                            for (int i = 0; i < count; i++)
                            {
                                crc ^= buffer[i];
                                for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 1 ? 0xedb88320u : 0u);
                            }
                        }
                        if (output.Length != entry.Length || ~crc != expectedCrc) throw new SyncSafetyException("ArchiveIntegrityRejected");
                        return output.ToArray();
                    }
                }
            }
            catch (SyncSafetyException) { throw; }
            catch { throw new SyncSafetyException("ArchiveInvalid"); }
        }

        private static uint ReadCentralCrc(byte[] bytes)
        {
            // Reject ZIP64/multidisk/encryption/unsupported codecs, inconsistent headers and trailing payload.
            int end = -1;
            for (int i = bytes.Length - 22; i >= Math.Max(0, bytes.Length - 65557); i--)
                if (U32(bytes, i) == 0x06054b50 && i + 22 + U16(bytes, i + 20) == bytes.Length) { end = i; break; }
            if (end < 0 || U16(bytes, end + 4) != 0 || U16(bytes, end + 6) != 0 || U16(bytes, end + 8) != 1 || U16(bytes, end + 10) != 1)
                throw new SyncSafetyException("ArchiveFormatRejected");
            long central = U32(bytes, end + 16), size = U32(bytes, end + 12);
            if (central < 30 || central + size != end || size < 46 || central > int.MaxValue)
                throw new SyncSafetyException("ArchiveFormatRejected");
            int at = (int)central;
            if (U32(bytes, at) != 0x02014b50 || U32(bytes, at + 42) != 0 || U32(bytes, 0) != 0x04034b50 ||
                U16(bytes, at + 34) != 0 || (U16(bytes, at + 8) & (1 | 64)) != 0 ||
                (U16(bytes, at + 10) != 0 && U16(bytes, at + 10) != 8) ||
                U16(bytes, at + 8) != U16(bytes, 6) || U16(bytes, at + 10) != U16(bytes, 8) ||
                46L + U16(bytes, at + 28) + U16(bytes, at + 30) + U16(bytes, at + 32) != size)
                throw new SyncSafetyException("ArchiveFormatRejected");
            int nameLength = U16(bytes, at + 28);
            if (nameLength != U16(bytes, 26)) throw new SyncSafetyException("ArchiveFormatRejected");
            for (int i = 0; i < nameLength; i++)
                if (bytes[30 + i] != bytes[at + 46 + i]) throw new SyncSafetyException("ArchiveFormatRejected");
            long dataEnd = 30L + nameLength + U16(bytes, 28) + U32(bytes, at + 20);
            if (dataEnd > central || dataEnd < 30 || U32(bytes, at + 24) == uint.MaxValue)
                throw new SyncSafetyException("ArchiveFormatRejected");
            // ZipArchiveEntry.ExternalAttributes is unavailable on net462. Read the same
            // field from the already validated central header, preserving link rejection.
            uint attributes = U32(bytes, at + 38);
            if ((attributes >> 16 & 0xF000) == 0xA000 || (attributes & 0x400) != 0)
                throw new SyncSafetyException("ArchiveLinkRejected");
            return U32(bytes, at + 16);
        }
        private static ushort U16(byte[] b, int at) { return BitConverter.ToUInt16(b, at); }
        private static uint U32(byte[] b, int at) { return BitConverter.ToUInt32(b, at); }
    }
}
