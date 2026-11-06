using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using ReportDesk.Core;
using ReportDesk.Web;

internal static class Program
{
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    private static int checks;
    private static void Check(bool ok, string label) { if (!ok) throw new Exception("FAIL: " + label); checks++; Console.WriteLine("PASS: " + label); }
    private static Dictionary<string, object> Read(WebResponseData response) => Json.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(response.Body));
    private static WebRequestData Request(string method, string path, string address = "127.0.0.1") => new WebRequestData
    { Method = method, Path = path, Secure = true, RemoteAddress = address, Authority = "https://reportdesk.test", Origin = "https://reportdesk.test", ContentType = "application/json" };
    private static async Task Main()
    {
        var temp = Path.Combine(Path.GetTempPath(), "ReportDesk-publish-http-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(temp, "reports"); var site = Path.Combine(temp, "site"); var data = Path.Combine(temp, "data");
        Directory.CreateDirectory(root); Directory.CreateDirectory(site);
        var query = Path.Combine(root, "示例查询设置.xml"); var layout = Path.Combine(root, "示例报表设置.xml");
        File.WriteAllText(query, "<ReportQueryInfo><QueryDataSource><QueryDataSource><SqlType>MainReportUsing</SqlType><Sql>select 1</Sql></QueryDataSource></QueryDataSource><ReportInfo><IsDetail>false</IsDetail></ReportInfo></ReportQueryInfo>", new UTF8Encoding(false));
        File.WriteAllText(layout, "<Spread class=\"FarPoint.Win.Spread.FpSpread\"/>", new UTF8Encoding(false));
        try
        {
            string reportId = ReportImporter.ImportFile(query).Id;
            var options = new WebOptions { DataDirectory = data, SiteDirectory = site, ReportsDirectory = root, Offline = true, SyncEnabled = false, MaintenanceAddresses = new[] { "127.0.0.1" } };
            using (var runtime = new WebRuntime(options, action => { _ = Task.Run(() => action(CancellationToken.None)); }))
            {
                var session = await runtime.HandleAsync(Request("GET", "/api/session"));
                var cookie = session.Headers["Set-Cookie"].Split(';')[0].Split('=')[1]; var csrf = (string)Read(session)["csrf"];
                async Task<WebResponseData> Send(string route, string id, string address = "127.0.0.1")
                {
                    var request = Request("POST", route, address); request.Cookie = cookie; request.Csrf = csrf;
                    request.Body = Json.Serialize(new { tabId = "publish-test-tab", args = new { reportId = id } });
                    return await runtime.HandleAsync(request);
                }
                async Task<Dictionary<string, object>> Call(string id)
                {
                    var submitted = await Send("/api/admin/publishPreview", id);
                    Check(submitted.Status == 202, "preview accepted as job");
                    var jobId = (string)Read(submitted)["jobId"];
                    for (var n = 0; n < 500; n++)
                    {
                        var poll = Request("GET", "/api/tasks/" + jobId); poll.Cookie = cookie;
                        var result = Read(await runtime.HandleAsync(poll));
                        if ((string)result["state"] != "running") return result;
                        await Task.Delay(10);
                    }
                    throw new Exception("Preview job timed out");
                }
                Check((await Send("/api/publishPreview", reportId)).Status == 404, "public route rejected");
                Check((await Send("/api/admin/publishPreview", reportId, "192.0.2.50")).Status == 403, "non-maintenance address rejected");
                var result = await Call(reportId);
                Check((string)result["state"] == "succeeded", "valid server-selected report preview");
                var preview = (Dictionary<string, object>)result["data"];
                var files = ((IEnumerable)preview["files"]).Cast<Dictionary<string, object>>().ToArray();
                Check(files.Length == 2 && (string)files[0]["role"] == "Query" && (string)files[1]["role"] == "Main", "query and candidate main returned");
                Check((string)files[1]["status"] == "Candidate" && ((IEnumerable)preview["warnings"]).Cast<object>().Any(), "candidate has warning");
                result = await Call("not-a-real-report");
                Check((string)result["state"] == "failed", "invalid reportId rejected");
                File.Delete(layout);
                result = await Call(reportId);
                Check((string)result["state"] == "failed" && ((string)result["error"]).Contains("缺失"), "missing association reported");
            }
            Console.WriteLine("PASS: " + checks + " real WebRuntime / BackendAdapter checks; offline, no Oracle connection");
        }
        finally { Directory.Delete(temp, true); }
    }
}
