#nullable disable
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace ReportDesk.Web.Sync
{
    public sealed class ReportWorkDirectory
    {
        public string Root { get; private set; }
        public string StateRoot { get; private set; }
        public ReportWorkDirectory(string root, string stateRoot)
        {
            Root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            StateRoot = Path.GetFullPath(stateRoot).TrimEnd(Path.DirectorySeparatorChar);
            if (!Directory.Exists(Root) || Root.Length <= 3 || StateRoot.Length <= 3 ||
                Within(Root, StateRoot) || Within(StateRoot, Root) || Root.Equals(StateRoot, StringComparison.OrdinalIgnoreCase))
                throw new SyncSafetyException("InvalidRoots");
            CheckLinks(Root); CheckLinks(StateRoot);
            Directory.CreateDirectory(StateRoot);
        }

        // Lock file lives in the working root so alternate state directories cannot bypass the lock.
        // Use this same lease around Web edits, import/reload and new-query definition preparation.
        public IDisposable Acquire(CancellationToken cancellation = default(CancellationToken))
        {
            CheckLinks(Root);
            var path = Path.Combine(Root, ".reportdesk-write.lock");
            while (true)
            {
                cancellation.ThrowIfCancellationRequested(); CheckLinks(path);
                try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException ex)
                {
                    int code = ex.HResult & 0xffff;
                    if (code != 32 && code != 33) throw new SyncSafetyException("WorkLockUnavailable");
                    if (cancellation.WaitHandle.WaitOne(50)) cancellation.ThrowIfCancellationRequested();
                }
            }
        }

        public string Resolve(string relativePath)
        {
            var relative = Normalize(relativePath);
            var full = Path.GetFullPath(Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!Within(Root, full)) throw new SyncSafetyException("UnsafePath");
            CheckLinks(full); return full;
        }

        public static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.IndexOf(':') >= 0)
                throw new SyncSafetyException("UnsafePath");
            var parts = path.Replace('\\', '/').Split('/');
            foreach (var part in parts)
            {
                var stem = part.Split('.')[0].ToUpperInvariant();
                if (part.Length == 0 || part == "." || part == ".." || part.EndsWith(".", StringComparison.Ordinal) ||
                    part.EndsWith(" ", StringComparison.Ordinal) || part.Any(c => c < 32 || "<>:\"|?*".IndexOf(c) >= 0) ||
                    new[] { "CON", "PRN", "AUX", "NUL", "CLOCK$" }.Contains(stem) ||
                    (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
                     "123456789¹²³".IndexOf(stem[3]) >= 0)) throw new SyncSafetyException("UnsafePath");
            }
            return string.Join("/", parts);
        }

        internal static bool Within(string root, string path)
        { return path.StartsWith(root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase); }

        internal static void CheckLinks(string path)
        {
            var current = Path.GetFullPath(path);
            while (!string.IsNullOrEmpty(current))
            {
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new SyncSafetyException("ReparsePointRejected");
                current = Path.GetDirectoryName(current);
            }
        }

        internal static string Hash(byte[] bytes)
        { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        internal static string HashFile(string path)
        {
            CheckLinks(path);
            if (!File.Exists(path)) return "";
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
        internal static string Identifier(string value) { return Hash(Encoding.UTF8.GetBytes(value)); }
        internal static void WriteDurable(string path, byte[] bytes)
        {
            CheckLinks(path);
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
        }
        internal static void AtomicWrite(string path, byte[] bytes)
        {
            CheckLinks(path);
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            WriteDurable(temp, bytes);
            try { if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
}
