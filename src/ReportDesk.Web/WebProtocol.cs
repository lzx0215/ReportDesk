using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace ReportDesk.Web;

public sealed class WebRequestData
{
    public string Method { get; set; } = "GET";
    public string Path { get; set; } = "/";
    public string BasePath { get; set; } = "/";
    public string Authority { get; set; } = "";
    public string Origin { get; set; } = "";
    public string RemoteAddress { get; set; } = "";
    public string Cookie { get; set; } = "";
    public string Csrf { get; set; } = "";
    public string ContentType { get; set; } = "";
    public string Body { get; set; } = "";
    public bool Secure { get; set; }
}

public sealed class WebResponseData
{
    public int Status { get; set; } = 200;
    public string ContentType { get; set; } = "application/json; charset=utf-8";
    public byte[] Body { get; set; } = Array.Empty<byte>();
    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase) {
        ["Cache-Control"] = "no-store", ["X-Content-Type-Options"] = "nosniff", ["X-Frame-Options"] = "DENY",
        ["Referrer-Policy"] = "no-referrer",
        ["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self'; connect-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'"
    };
    public static WebResponseData Json(object value, int status = 200) => new() { Status = status, Body = Encoding.UTF8.GetBytes(JsonCodec.Serialize(value)) };
    public static WebResponseData Error(int status, string message) => Json(new { ok = false, message }, status);
}

internal static class JsonCodec
{
    public static JavaScriptSerializer Create() => new() { MaxJsonLength = int.MaxValue, RecursionLimit = 64 };
    public static string Serialize(object value) => Create().Serialize(value);
    public static Dictionary<string, object> Object(string text) => Create().Deserialize<Dictionary<string, object>>(text) ?? throw new InvalidOperationException("请求为空。");
    public static string Token() { var bytes = new byte[32]; using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes); return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant(); }
    public static bool Equal(string left, string right)
    {
        if (left.Length != right.Length) return false;
        var difference = 0;
        for (var i = 0; i < left.Length; i++) difference |= left[i] ^ right[i];
        return difference == 0;
    }
}
