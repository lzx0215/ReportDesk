using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using ReportDesk.Web.Publish;
using ReportDesk.Web.Sync;

internal static class Program
{
    private static int checks;
    private static void Check(bool condition, string label)
    { if (!condition) throw new Exception(label); checks++; }
    private static void Reject<T>(Action action) where T : Exception
    { try { action(); } catch (T) { checks++; return; } throw new Exception("Expected " + typeof(T).Name); }
    private static void Roundtrip(byte[] content)
    {
        var zip = ReportPublishArchive.Create(content);
        using (var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read))
        {
            Check(archive.Entries.Count == 1 && archive.Entries[0].FullName == "", "single empty-name entry");
            Check(archive.Entries[0].Length == content.Length && BitConverter.ToUInt16(zip, 8) == 8, "length and deflate method");
            using (var extracted = new MemoryStream())
            { archive.Entries[0].Open().CopyTo(extracted); Check(extracted.ToArray().SequenceEqual(content), "ZipArchive roundtrip"); }
        }
        Check(SafeReportArchive.Extract(zip, ReportPublishArchive.MaximumArchiveBytes, ReportPublishArchive.MaximumContentBytes).SequenceEqual(content), "safe extractor roundtrip and CRC");
    }
    private static void Main()
    {
        Roundtrip(Encoding.UTF8.GetBytes("<ReportQueryInfo>中文报表</ReportQueryInfo>"));
        var random = new byte[65536]; new Random(1234).NextBytes(random); Roundtrip(random);
        Roundtrip(Enumerable.Repeat((byte)'x', 65536).ToArray());
        var known = ReportPublishArchive.Create(Encoding.ASCII.GetBytes("123456789"));
        Check(BitConverter.ToUInt32(known, 14) == 0xcbf43926u, "known CRC-32 vector");
        Reject<ArgumentNullException>(() => ReportPublishArchive.Create(null));
        Reject<ArgumentOutOfRangeException>(() => ReportPublishArchive.Create(Array.Empty<byte>()));
        Reject<ArgumentOutOfRangeException>(() => ReportPublishArchive.Create(new byte[ReportPublishArchive.MaximumContentBytes + 1]));
        var damaged = (byte[])known.Clone();
        var central = (int)BitConverter.ToUInt32(damaged, damaged.Length - 6);
        damaged[central + 16] ^= 1;
        Reject<SyncSafetyException>(() => SafeReportArchive.Extract(damaged, ReportPublishArchive.MaximumArchiveBytes, ReportPublishArchive.MaximumContentBytes));
        Console.WriteLine("PASS: " + checks + " archive preparation checks; HIS client NOT RUN.");
    }
}
