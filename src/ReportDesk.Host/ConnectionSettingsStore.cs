using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using ReportDesk.Core;

namespace ReportDesk.Host;

// Desktop reports live only in the Service session. This file holds connection
// preferences alone, and never SQL, report definitions, inputs, or results.
internal sealed class ConnectionSettingsStore
{
    private readonly string file;
    public ConnectionSettingsStore(string? directory) => file = Path.Combine(directory ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReportDesk"), "connection.json");

    [DataContract]
    private sealed class LegacyConnection
    {
        [DataMember] public ConnectionSettings Connection { get; set; } = new();
    }

    public ConnectionSettings Load()
    {
        if (File.Exists(file))
        {
            using var stream = File.OpenRead(file);
            return (ConnectionSettings)(new DataContractJsonSerializer(typeof(ConnectionSettings)).ReadObject(stream)
                ?? throw new InvalidDataException("连接设置为空。"));
        }
        // Read only the old connection member for upgrade compatibility. Do not
        // restore reports, rewrite, migrate, or delete the user's old catalog.
        var legacy = Path.Combine(Path.GetDirectoryName(file)!, "catalog.json");
        if (!File.Exists(legacy)) return new ConnectionSettings();
        try
        {
            using var stream = File.OpenRead(legacy);
            return ((LegacyConnection?)new DataContractJsonSerializer(typeof(LegacyConnection)).ReadObject(stream))?.Connection ?? new();
        }
        catch (Exception ex) when (ex is SerializationException || ex is System.Xml.XmlException || ex is IOException)
        {
            ErrorLog.Write("ReadLegacyConnection", ex, includeMessage: false);
            return new ConnectionSettings();
        }
    }

    public void Save(ConnectionSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { new DataContractJsonSerializer(typeof(ConnectionSettings)).WriteObject(stream, settings); stream.Flush(true); }
            if (File.Exists(file)) File.Replace(temporary, file, null);
            else File.Move(temporary, file);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
