using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Oracle.ManagedDataAccess.Client;

namespace ReportDesk.Core;

public static class OracleQueryService
{
    // Explicit user action only. No report SQL, no persistence, no pooled session.
    public static string TestConnection(ConnectionSettings settings, string password)
    {
        var connectionString = ConnectionString(settings, password);
        try
        {
            using var connection = new OracleConnection(connectionString);
            connection.Open();
            return connection.ServerVersion;
        }
        catch (OracleException ex)
        {
            // Do not display raw driver text that could include connect descriptor contents.
            var notice = ErrorLog.Write("TestConnection", ex, ErrorLog.ConnectionValues(settings, password));
            throw new InvalidOperationException("连接失败（Oracle 错误码 " + ex.Number + "）。请核对网络服务名、账号密码和网络配置。\n" + notice);
        }
        catch (Exception ex)
        { var notice = ErrorLog.Write("TestConnection", ex, ErrorLog.ConnectionValues(settings, password)); throw new InvalidOperationException("连接失败。请核对连接配置、网络和运行依赖。\n" + notice); }
    }

    public static string ConnectionString(ConnectionSettings settings, string password)
    {
        if (string.IsNullOrWhiteSpace(settings.Username)) throw new InvalidOperationException("请填写只读用户名。");
        string dataSource;
        if (settings.Mode == ConnectionMode.Tns)
            dataSource = TnsNames.Resolve(settings.TnsFile, settings.TnsAlias);
        else if (settings.Mode == ConnectionMode.Direct)
        {
            if (!Regex.IsMatch(settings.Host ?? "", @"^[A-Za-z0-9_.-]+$") || !Regex.IsMatch(settings.Service ?? "", @"^[A-Za-z0-9_.-]+$") || settings.Port < 1 || settings.Port > 65535)
                throw new InvalidOperationException("请填写有效的服务器、端口和 Service Name；SID 请使用 TNS 文件模式。");
            dataSource = "(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=" + settings.Host + ")(PORT=" + settings.Port + "))(CONNECT_DATA=(SERVICE_NAME=" + settings.Service + ")))";
        }
        else throw new InvalidOperationException("未知的连接方式，请重新选择连接设置。");
        var builder = new OracleConnectionStringBuilder
        {
            DataSource = dataSource,
            UserID = settings.Username, Password = password, Pooling = false, Enlist = "false",
            ConnectionTimeout = 0, PersistSecurityInfo = false
        };
        return builder.ConnectionString;
    }

    // Legacy managed driver uses synchronous I/O; caller runs on a worker thread.
    // Open uses descriptor/driver timeouts; after Open, cancellation calls OracleCommand.Cancel.
    public static QueryResult Execute(ConnectionSettings settings, string password, string template,
        IReadOnlyDictionary<string, string> values, CancellationToken cancellation, IReadOnlyDictionary<string,string[]>? multiple = null)
    {
        try { return ExecuteCore(settings, password, template, values, cancellation, multiple); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            ErrorLog.Write("Query", ex, ErrorLog.ConnectionValues(settings, password).Concat(values.Values).Concat(multiple?.Values.SelectMany(x=>x)??Enumerable.Empty<string>()).Concat(new[] { template }));
            throw;
        }
    }

    private static QueryResult ExecuteCore(ConnectionSettings settings, string password, string template,
        IReadOnlyDictionary<string, string> values, CancellationToken cancellation, IReadOnlyDictionary<string,string[]>? multiple)
    {
        var query = SqlTemplate.Compile(template, values, multiple);
        var watch = Stopwatch.StartNew();
        using var connection = new OracleConnection(ConnectionString(settings, password));
        cancellation.ThrowIfCancellationRequested(); connection.Open(); cancellation.ThrowIfCancellationRequested();
        using var command = connection.CreateCommand();
        command.CommandTimeout = 0;
        using var registration = cancellation.Register(() => { try { command.Cancel(); } catch (Exception ex) when (ex is OracleException || ex is InvalidOperationException || ex is ObjectDisposedException) { ErrorLog.Write("CancelQuery", ex, includeMessage: false); } });
        try
        {
            cancellation.ThrowIfCancellationRequested();
            command.CommandText = "SET TRANSACTION READ ONLY"; command.ExecuteNonQuery();
            cancellation.ThrowIfCancellationRequested();
            command.BindByName = true; command.CommandText = query.Sql;
            foreach (var entry in query.Values)
                command.Parameters.Add(entry.Key, OracleDbType.Varchar2, entry.Value.Length == 0 ? (object)DBNull.Value : entry.Value, ParameterDirection.Input);
            using var reader = command.ExecuteReader(CommandBehavior.SingleResult);
            var result = ReadResult(reader, cancellation);
            result.Milliseconds = watch.ElapsedMilliseconds;
            return result;
        }
        catch (Exception) when (cancellation.IsCancellationRequested)
        { throw new OperationCanceledException(cancellation); }
        // Pooling is disabled. Closing this connection rolls back its read-only transaction.
    }
    public static QueryResult ReadResult(IDataReader reader, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
            var result = new QueryResult();
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var name = reader.GetName(i); if (string.IsNullOrWhiteSpace(name)) name = "列" + (i + 1);
                var unique = name; var suffix = 2;
                while (result.Table.Columns.Contains(unique)) unique = name + "_" + suffix++;
                var type = reader.GetFieldType(i);
                if (type != typeof(string) && type != typeof(decimal) && type != typeof(int) && type != typeof(long) && type != typeof(double) && type != typeof(DateTime) && type != typeof(float) && type != typeof(short))
                    throw new InvalidOperationException("该结果包含尚未适配的数据类型：" + name + " / " + type.Name);
                result.Table.Columns.Add(unique, type);
            }
            while (reader.Read())
            {
                cancellation.ThrowIfCancellationRequested();
                var cells = new object[reader.FieldCount];
                for (var i = 0; i < cells.Length; i++)
                {
                    cells[i] = reader.IsDBNull(i) ? DBNull.Value : reader.GetValue(i);
                }
                result.Table.Rows.Add(cells);
            }
            cancellation.ThrowIfCancellationRequested();

        return result;
    }

}
