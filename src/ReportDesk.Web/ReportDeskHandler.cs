using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Web.Hosting;

namespace ReportDesk.Web;

public class ReportDeskApplication : HttpApplication
{
    protected void Application_Start(object sender, EventArgs e) => WebHost.Start();
    protected void Application_End(object sender, EventArgs e) => WebHost.Stop();
}

internal static class WebHost
{
    private static readonly object Gate = new();
    private static WebRuntime? runtime;
    internal static WebRuntime Start()
    {
        lock (Gate)
        {
            if (runtime == null)
            {
                var site = HostingEnvironment.ApplicationPhysicalPath;
                runtime = new WebRuntime(WebOptions.Load(site), action => HostingEnvironment.QueueBackgroundWorkItem(action));
                runtime.StartBackground();
            }
            return runtime;
        }
    }
    internal static void Stop() { lock (Gate) { runtime?.Dispose(); runtime = null; } }
}

public sealed class ReportDeskHandler : HttpTaskAsyncHandler
{
    public override async Task ProcessRequestAsync(HttpContext context)
    {
        WebResponseData response;
        try
        {
            var request = context.Request;
            if (request.ContentLength > WebOptions.MaxRequestBytes) response = WebResponseData.Error(413, "请求超过允许大小。");
            else
            {
                // Read a bounded body even when Content-Length is absent/chunked.
                using var buffer = new MemoryStream();
                var chunk = new byte[16384];
                int read;
                while ((read = await request.InputStream.ReadAsync(chunk, 0, chunk.Length).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + read > WebOptions.MaxRequestBytes) throw new RequestTooLargeException();
                    buffer.Write(chunk, 0, read);
                }
                var app = request.ApplicationPath == "/" ? "/" : request.ApplicationPath.TrimEnd('/') + "/";
                var path = request.Url.AbsolutePath;
                if (app != "/" && path.StartsWith(app, StringComparison.OrdinalIgnoreCase)) path = "/" + path.Substring(app.Length);
                response = await WebHost.Start().HandleAsync(new WebRequestData {
                    Method = request.HttpMethod, Path = path, BasePath = app,
                    Authority = request.Url.GetLeftPart(UriPartial.Authority), Origin = request.Headers["Origin"] ?? "",
                    // TCP peer only. Do not read X-Forwarded-For, Forwarded, or X-Real-IP.
                    RemoteAddress = request.ServerVariables["REMOTE_ADDR"] ?? request.UserHostAddress ?? "", Secure = request.IsSecureConnection,
                    Cookie = request.Cookies[WebRuntime.CookieName]?.Value ?? "",
                    Csrf = request.Headers["X-ReportDesk-CSRF"] ?? "", ContentType = request.ContentType ?? "",
                    Body = Encoding.UTF8.GetString(buffer.ToArray())
                }).ConfigureAwait(false);
            }
        }
        catch (RequestTooLargeException) { response = WebResponseData.Error(413, "请求超过允许大小。"); }
        catch (Exception) { response = WebResponseData.Error(503, "网站启动失败，请由维护人员核对数据目录、配置和访问权限。"); }
        context.Response.StatusCode = response.Status;
        context.Response.ContentType = response.ContentType;
        context.Response.TrySkipIisCustomErrors = true;
        foreach (var header in response.Headers) context.Response.Headers[header.Key] = header.Value;
        context.Response.BufferOutput = false;
        if (response.Body.Length > 0) await context.Response.OutputStream.WriteAsync(response.Body, 0, response.Body.Length).ConfigureAwait(false);
    }
    private sealed class RequestTooLargeException : Exception { }
}
