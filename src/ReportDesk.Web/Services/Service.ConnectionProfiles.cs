using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using ReportDesk.Core;

namespace ReportDesk.Host;

// Web-only extension of connection.json. The root remains a legacy
// ConnectionSettings object; the active projection and all profiles commit in
// one atomic replacement. Neither passwords nor ciphertext enter HTTP DTOs.
internal sealed partial class Service
{
    private sealed class WebConnectionProfile
    {
        internal string Id = "", Password = "";
        internal ConnectionSettings Settings = new();
    }

    private List<WebConnectionProfile>? webConnectionProfiles;
    private string webActiveConnectionId = "";
    private string webConnectionFingerprint = "";
    private bool webConnectionInjectedBeforeProfiles;
    private sealed class WebProfilesChangedException : InvalidOperationException
    {
        internal WebProfilesChangedException() : base("连接配置已由其他进程修改，请重新打开连接设置后重试；本次修改未保存。") { }
    }
    private string WebConnectionFile => Path.Combine(configDirectory, "connection.json");
    private static JavaScriptSerializer WebConnectionJson() => new() { MaxJsonLength = int.MaxValue };
    private static ConnectionSettings WebCopyConnection(ConnectionSettings settings)
        => WebConnectionJson().Deserialize<ConnectionSettings>(WebConnectionJson().Serialize(settings));

    private static string WebConnectionHash(byte[]? bytes)
    {
        if (bytes == null) return "missing";
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
    }

    private void WebLoadConnectionProfiles(bool refresh = false)
    {
        if (webConnectionProfiles != null && !refresh) return;
        var previous = webConnectionProfiles;
        byte[]? bytes = File.Exists(WebConnectionFile) ? File.ReadAllBytes(WebConnectionFile) : null;
        string fingerprint = WebConnectionHash(bytes);
        if (previous != null && fingerprint == webConnectionFingerprint) return;
        if (previous != null && bytes == null)
            throw new InvalidOperationException("连接配置文件已丢失，请先恢复配置文件；已加载的连接未改动。");
        var profiles = new List<WebConnectionProfile>();
        string activeId = "legacy";
        var json = WebConnectionJson();
        Dictionary<string, object>? root = null;
        if (bytes != null)
        {
            using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, true);
            root = json.Deserialize<Dictionary<string, object>>(reader.ReadToEnd());
            if (root == null) throw new InvalidOperationException("连接配置内容为空，请保留原文件并检查备份。");
        }
        if (root != null && root.ContainsKey("WebConnectionProfilesVersion"))
        {
            if (Number(root, "WebConnectionProfilesVersion") != 1 ||
                !root.TryGetValue("WebConnectionProfiles", out var rawProfiles) || !(rawProfiles is IEnumerable entries))
                throw new InvalidOperationException("连接配置列表格式不受支持，请保留原配置并检查备份。");
            activeId = Text(root, "WebActiveConnectionId");
            foreach (var raw in entries)
            {
                if (!(raw is Dictionary<string, object> entry) || !entry.TryGetValue("Settings", out var rawSettings))
                    throw new InvalidOperationException("连接配置列表不完整，请保留原配置并检查备份。");
                string id = Text(entry, "Id");
                if (id.Length == 0 || profiles.Any(p => p.Id == id))
                    throw new InvalidOperationException("连接配置标识无效，请保留原配置并检查备份。");
                var settings = json.Deserialize<ConnectionSettings>(json.Serialize(rawSettings));
                if (settings == null) throw new InvalidOperationException("连接配置内容为空。");
                string secret = "";
                var unchanged = previous?.FirstOrDefault(p => p.Id == id && json.Serialize(p.Settings) == json.Serialize(settings));
                try { secret = unchanged != null ? unchanged.Password : CatalogStore.Unprotect(settings.ProtectedPassword); }
                catch (Exception ex)
                {
                    ErrorLog.Write("WebDecryptConnectionProfile", ex, includeMessage: false);
                    startupWarnings.Add("部分连接的已保存密码无法解密，请重新输入密码并保存该连接。");
                }
                profiles.Add(new WebConnectionProfile { Id = id, Settings = settings, Password = secret });
            }
            if (!profiles.Any(p => p.Id == activeId))
                throw new InvalidOperationException("当前连接不在已保存列表中，请保留原配置并检查备份。");
        }
        else
        {
            var settings = root == null ? WebCopyConnection(catalog.Connection) : json.Deserialize<ConnectionSettings>(json.Serialize(root));
            string secret = root == null ? password : "";
            if (root != null)
            {
                try { secret = CatalogStore.Unprotect(settings.ProtectedPassword); }
                catch (Exception ex) { ErrorLog.Write("WebDecryptLegacyConnection", ex, includeMessage: false); }
            }
            profiles.Add(new WebConnectionProfile { Id = activeId, Settings = settings, Password = secret });
        }
        webConnectionProfiles = profiles;
        webActiveConnectionId = activeId;
        webConnectionFingerprint = fingerprint;
        var active = profiles.Single(p => p.Id == activeId);
        if (previous == null && webConnectionInjectedBeforeProfiles)
        {
            active.Settings = WebCopyConnection(catalog.Connection); active.Password = password;
        }
        catalog.Connection = WebCopyConnection(active.Settings); password = active.Password;
        webConnectionInjectedBeforeProfiles = false;
    }

    private WebConnectionProfile WebProfile(string id)
    {
        WebLoadConnectionProfiles();
        var profile = webConnectionProfiles!.FirstOrDefault(p => p.Id == id);
        return profile ?? throw new InvalidOperationException("此连接已不存在，请重新打开连接设置。");
    }

    private void WebUpdateInjectedProfile()
    {
        if (webConnectionProfiles == null) { webConnectionInjectedBeforeProfiles = true; return; }
        var active = WebProfile(webActiveConnectionId);
        active.Settings = WebCopyConnection(catalog.Connection); active.Password = password;
    }

    private object WebProfileSettings(WebConnectionProfile profile)
    {
        var s = profile.Settings;
        return new { name = s.Name, mode = (int)s.Mode, host = s.Host, port = s.Port, service = s.Service,
            username = s.Username, tnsFile = s.TnsFile, tnsAlias = s.TnsAlias,
            remember = s.RememberPassword ?? true, hasPassword = profile.Password.Length > 0,
            profileId = profile.Id, activeProfileId = webActiveConnectionId,
            profiles = webConnectionProfiles!.Select(p => new { id = p.Id,
                name = string.IsNullOrWhiteSpace(p.Settings.Name) ? "默认连接" : p.Settings.Name,
                isActive = p.Id == webActiveConnectionId, hasPassword = p.Password.Length > 0 }).ToArray() };
    }

    // The backend calls this inside FileGate before activation, including the
    // current server TNS allowlist; a saved path is not a permanent permission.
    internal ConnectionSettings WebProfileConnection(string id) => WebCopyConnection(WebProfile(id).Settings);

    private void WebCommitProfiles(List<WebConnectionProfile> profiles, string activeId, bool updateConnection, CancellationToken token)
    {
        var active = profiles.Single(p => p.Id == activeId);
        var json = WebConnectionJson();
        var root = json.Deserialize<Dictionary<string, object>>(json.Serialize(active.Settings));
        root["WebConnectionProfilesVersion"] = 1;
        root["WebActiveConnectionId"] = activeId;
        root["WebConnectionProfiles"] = profiles.Select(p => new { p.Id, p.Settings }).ToArray();
        Directory.CreateDirectory(configDirectory);
        string temporary = WebConnectionFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        // A path-specific OS mutex also serializes overlapping IIS workers. A
        // stale process must reload explicitly instead of overwriting new profiles.
        string mutexName = @"Local\ReportDesk.ConnectionProfiles." + WebConnectionHash(Encoding.UTF8.GetBytes(Path.GetFullPath(WebConnectionFile).ToUpperInvariant()));
        using var mutex = new Mutex(false, mutexName);
        bool acquired = false;
        try
        {
            try
            {
                while (!(acquired = mutex.WaitOne(100))) token.ThrowIfCancellationRequested();
            }
            catch (AbandonedMutexException) { acquired = true; }
            token.ThrowIfCancellationRequested();
            var current = File.Exists(WebConnectionFile) ? File.ReadAllBytes(WebConnectionFile) : null;
            if (WebConnectionHash(current) != webConnectionFingerprint) throw new WebProfilesChangedException();
            byte[] bytes = new UTF8Encoding(false).GetBytes(json.Serialize(root));
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { output.Write(bytes, 0, bytes.Length); output.Flush(true); }
            if (File.Exists(WebConnectionFile)) File.Replace(temporary, WebConnectionFile, null);
            else File.Move(temporary, WebConnectionFile);
            webConnectionFingerprint = WebConnectionHash(bytes);
        }
        finally
        {
            if (acquired) mutex.ReleaseMutex();
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        webConnectionProfiles = profiles; webActiveConnectionId = activeId;
        if (updateConnection)
        {
            catalog.Connection = WebCopyConnection(active.Settings); password = active.Password;
            Clear();
        }
    }

    internal object WebHandleConnectionProfiles(string method, Dictionary<string, object> args, CancellationToken token, Action<string> progress)
    {
        WebLoadConnectionProfiles(method == "settings");
        string requestedId = Text(args, "profileId");
        if (method == "settings") return WebProfileSettings(WebProfile(requestedId.Length > 0 ? requestedId : webActiveConnectionId));
        if (method == "activateConnection")
        {
            if (requestedId.Length == 0) throw new InvalidOperationException("请选择要使用的连接。");
            var selected = WebProfile(requestedId);
            OracleQueryService.ConnectionString(selected.Settings, selected.Password);
            token.ThrowIfCancellationRequested();
            WebCommitProfiles(new List<WebConnectionProfile>(webConnectionProfiles!), selected.Id, true, token);
            return WebProfileSettings(selected);
        }
        bool create = Flag(args, "newProfile");
        if (create && requestedId.Length > 0) throw new InvalidOperationException("新增连接不能同时指定已有连接标识。");
        var previous = create ? null : WebProfile(requestedId.Length > 0 ? requestedId : webActiveConnectionId);
        var settings = ReadSettings(args);
        if ((create || requestedId.Length > 0) && string.IsNullOrWhiteSpace(settings.Name)) throw new InvalidOperationException("请填写连接名。");
        settings.Name = settings.Name.Trim();
        string nextPassword = Flag(args, "keepPassword") ? previous?.Password ?? "" : Text(args, "password");
        if (create && Flag(args, "keepPassword")) throw new InvalidOperationException("新增连接请单独输入密码。");
        OracleQueryService.ConnectionString(settings, nextPassword);
        string version = "";
        if (method == "testConnection")
        {
            if (offline) throw new InvalidOperationException("离线验证模式禁止真实数据库连接。");
            progress("正在测试连接；握手等待由网络与 TNS 决定…");
            try { version = testConnection(settings, nextPassword); }
            catch (InvalidOperationException ex)
            { throw new InvalidOperationException(ErrorLog.Sanitize(ex.Message, ErrorLog.ConnectionValues(settings, nextPassword))); }
        }
        token.ThrowIfCancellationRequested();
        settings.RememberPassword = method == "testConnection" ? !args.ContainsKey("remember") || Flag(args, "remember") : Flag(args, "remember");
        settings.ProtectedPassword = settings.RememberPassword == true && nextPassword.Length > 0 ? CatalogStore.Protect(nextPassword) : "";
        var next = new WebConnectionProfile { Id = previous?.Id ?? Guid.NewGuid().ToString("N"), Settings = settings, Password = nextPassword };
        var staged = webConnectionProfiles!.Select(p => p.Id == next.Id ? next : p).ToList();
        if (create) staged.Add(next);
        try { WebCommitProfiles(staged, webActiveConnectionId, next.Id == webActiveConnectionId, token); }
        catch (Exception ex) when (method == "testConnection" && !(ex is WebProfilesChangedException) && !(ex is OperationCanceledException))
        {
            ErrorLog.Write("WebSaveConnectedProfile", ex, includeMessage: false);
            throw new InvalidOperationException("连接成功，但配置保存失败；原配置已保留，请检查服务器配置目录的写入权限后重试。");
        }
        var dto = WebProfileSettings(next);
        return method == "testConnection" ? new { version, settings = dto } : dto;
    }
}
