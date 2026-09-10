using System;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;

namespace ReportDesk.Core;

public sealed class CatalogStore
{
    public string FilePath { get; }
    public CatalogStore(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReportDesk");
        FilePath = Path.Combine(directory, "catalog.json");
    }
    public Catalog Load()
    {
        if (!File.Exists(FilePath)) return new Catalog();
        using var stream = File.OpenRead(FilePath);
        return (Catalog)(new DataContractJsonSerializer(typeof(Catalog)).ReadObject(stream) ?? throw new InvalidDataException("报表库为空。"));
    }
    public void Save(Catalog catalog)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { new DataContractJsonSerializer(typeof(Catalog)).WriteObject(stream, catalog); stream.Flush(true); }
            if (File.Exists(FilePath)) File.Replace(temporary, FilePath, FilePath + ".bak");
            else File.Move(temporary, FilePath);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static string Protect(string password) => Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(password), null, DataProtectionScope.CurrentUser));
    public static string Unprotect(string encrypted) => string.IsNullOrEmpty(encrypted) ? "" : Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(encrypted), null, DataProtectionScope.CurrentUser));
}
