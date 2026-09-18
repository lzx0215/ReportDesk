#nullable disable
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Threading;
using Oracle.ManagedDataAccess.Client;

namespace ReportDesk.Web.Sync
{
    public sealed class OracleReleaseReader : IReleaseReader
    {
        private readonly IOracleSyncConnectionProvider connections;
        private readonly SyncOptions limits;
        public bool IsValidated { get; private set; }

        // Set true ONLY after approved read-only metadata/BLOB/hash comparison against a known published sample.
        // There is deliberately no auto-probe or connection in this constructor.
        public OracleReleaseReader(IOracleSyncConnectionProvider provider, SyncOptions options, bool validated = false)
        { connections = provider; limits = options; IsValidated = validated; }

        public IEnumerable<PublishedRelease> ReadAll(CancellationToken cancellation)
        {
            if (!IsValidated) throw new SyncSafetyException("OracleNotValidated");
            using (var connection = new OracleConnection(connections.GetConnectionString()))
            {
                cancellation.ThrowIfCancellationRequested(); connection.Open();
                using (var readOnly = Command(connection, "SET TRANSACTION READ ONLY")) readOnly.ExecuteNonQuery();
                var ids = new List<int>();
                using (var command = Command(connection,
                    "SELECT RELEASE_ID FROM HIS.SYS_UPDATE_RELEASE WHERE DESCRIPTION LIKE :prefix ORDER BY RELEASE_ID"))
                {
                    command.Parameters.Add("prefix", OracleDbType.Varchar2).Value = "一键发布配置信息%";
                    using (cancellation.Register(() => Cancel(command)))
                    using (var reader = command.ExecuteReader())
                        while (reader.Read()) { cancellation.ThrowIfCancellationRequested(); ids.Add(Convert.ToInt32(reader.GetValue(0))); }
                }
                foreach (int id in ids)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var release = new PublishedRelease { ReleaseId = id };
                    using (var command = Command(connection,
                        "SELECT FILE_ID, FILE_NAME, DIRECTORY, DIRECTORY_FLAG, CONTENT FROM HIS.SYS_UPDATE_RELEASE_FILE WHERE RELEASE_ID = :releaseId ORDER BY FILE_ID"))
                    {
                        command.Parameters.Add("releaseId", OracleDbType.Int32).Value = id;
                        using (cancellation.Register(() => Cancel(command)))
                        using (var reader = command.ExecuteReader(CommandBehavior.SequentialAccess))
                        {
                            long batchSize = 0;
                            while (reader.Read())
                            {
                                cancellation.ThrowIfCancellationRequested();
                                if (release.Files.Count >= limits.MaximumFilesPerRelease) throw new SyncSafetyException("ReleaseSizeRejected");
                                string fileId = reader.IsDBNull(0) ? "" : reader.GetString(0);
                                string fileName = reader.IsDBNull(1) ? "" : reader.GetString(1);
                                string directory = reader.IsDBNull(2) ? "" : reader.GetString(2);
                                string flag = reader.IsDBNull(3) ? "" : Convert.ToString(reader.GetValue(3));
                                // DIRECTORY_FLAG representation is unverified; fail closed unless an explicit false value.
                                bool isDirectory = flag != "0" && !string.Equals(flag, "false", StringComparison.OrdinalIgnoreCase);
                                var file = new PublishedFile
                                {
                                    FileId = fileId,
                                    FileName = fileName,
                                    Directory = directory,
                                    IsDirectory = isDirectory
                                };
                                if (reader.IsDBNull(4)) throw new SyncSafetyException("PublicationContentMissing");
                                using (var blob = reader.GetOracleBlob(4))
                                using (var output = new MemoryStream())
                                {
                                    if (blob.Length > limits.MaximumCompressedBytes) throw new SyncSafetyException("ArchiveSizeRejected");
                                    byte[] buffer = new byte[8192]; int count;
                                    while ((count = blob.Read(buffer, 0, buffer.Length)) > 0)
                                    {
                                        cancellation.ThrowIfCancellationRequested();
                                        batchSize += count;
                                        if (output.Length + count > limits.MaximumCompressedBytes || batchSize > limits.MaximumBatchBytes)
                                            throw new SyncSafetyException("ReleaseSizeRejected");
                                        output.Write(buffer, 0, count);
                                    }
                                    file.Content = output.ToArray();
                                }
                                release.Files.Add(file);
                            }
                        }
                    }
                    yield return release;
                }
                // Disposing the read-only connection ends the transaction; no COMMIT, DML, DDL or release writes.
            }
        }

        private static OracleCommand Command(OracleConnection connection, string sql)
        { return new OracleCommand(sql, connection) { BindByName = true, CommandTimeout = 0 }; }
        private static void Cancel(OracleCommand command) { try { command.Cancel(); } catch { } }
    }
}
