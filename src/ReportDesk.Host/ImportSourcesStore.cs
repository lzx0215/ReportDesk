using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace ReportDesk.Host;

// Persist source locations only; definitions and query data stay in memory.
[DataContract]
internal sealed class ImportSource
{
    [DataMember(IsRequired = true)] public string Path { get; set; } = "";
    [DataMember(IsRequired = true)] public bool Folder { get; set; }
}

internal sealed class ImportSourcesStore
{
    private readonly string file;
    public ImportSourcesStore(string? directory) => file = System.IO.Path.Combine(directory ??
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReportDesk"), "import-sources.json");

    public List<ImportSource> Load()
    {
        if (!File.Exists(file)) return new();
        using var stream = File.OpenRead(file);
        var sources = (List<ImportSource>?)new DataContractJsonSerializer(typeof(List<ImportSource>)).ReadObject(stream);
        if (sources == null || sources.Any(s => s == null || string.IsNullOrWhiteSpace(s.Path) || !System.IO.Path.IsPathRooted(s.Path)))
            throw new InvalidDataException("已保存的导入来源无效。");
        return sources;
    }

    public void Remember(string path, bool folder)
    {
        path = System.IO.Path.GetFullPath(path);
        var sources = Load();
        if (sources.Any(s => s.Folder == folder && string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase))) return;
        sources.Add(new ImportSource { Path = path, Folder = folder });
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { new DataContractJsonSerializer(typeof(List<ImportSource>)).WriteObject(stream, sources); stream.Flush(true); }
            if (File.Exists(file)) File.Replace(temporary, file, null);
            else File.Move(temporary, file);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
