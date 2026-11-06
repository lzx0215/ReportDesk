using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using ReportDesk.Core;
using ReportDesk.Web;

internal static class Program
{
    private static readonly JavaScriptSerializer Json = new() { MaxJsonLength = int.MaxValue };
    private static int count;
    private static void Assert(bool condition, string name)
    { if (!condition) throw new Exception("FAIL " + name); count++; Console.WriteLine("PASS " + name); }
    private static Dictionary<string, object> Parse(WebResponseData response) => Json.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(response.Body));
    private static WebRequestData Request(string method, string path, string ip = "127.0.0.1") => new() { Method = method, Path = path,
        Secure = true, RemoteAddress = ip, Authority = "https://reportdesk.test", Origin = "https://reportdesk.test", ContentType = "application/json" };
    private static string Hash(string path) { using var sha = SHA256.Create(); return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", ""); }
    private static async Task<int> Main()
    {
        var source = Path.GetFullPath("artifacts/verification/sql-export-20261030/final-binary-check/reports/Config/Xml");
        string qName = "抗菌药物消耗情况查询设置.xml", lName = "抗菌药物消耗情况报表设置.xml";
        if (!File.Exists(Path.Combine(source, qName)) || !File.Exists(Path.Combine(source, lName)))
        { Console.Error.WriteLine("Required retained XML fixtures are unavailable."); return 1; }
        string sourceQueryHash = Hash(Path.Combine(source, qName)), sourceLayoutHash = Hash(Path.Combine(source, lName));
        string detailSource = Path.GetFullPath("artifacts/verification/header-preview-20261019-101843/full-lib-regression/reports/Config/Xml");
        string detailQueryName = "住院业务查询查询设置.xml", detailName = "住院业务查询报表明细设置.xml";
        if (!File.Exists(Path.Combine(detailSource, detailQueryName)) || !File.Exists(Path.Combine(detailSource, detailName)))
        { Console.Error.WriteLine("Required retained detail fixtures are unavailable."); return 1; }
        string sourceDetailQueryHash = Hash(Path.Combine(detailSource, detailQueryName)), sourceDetailHash = Hash(Path.Combine(detailSource, detailName));
        string root = Path.Combine(Path.GetTempPath(), "reportdesk-web-xml-check-" + Guid.NewGuid().ToString("N"));
        string reports = Path.Combine(root, "reports"), site = Path.Combine(root, "site"), data = Path.Combine(root, "data");
        Directory.CreateDirectory(reports); Directory.CreateDirectory(site); Directory.CreateDirectory(data);
        string query = Path.Combine(reports, qName), layout = Path.Combine(reports, lName);
        File.Copy(Path.Combine(source, qName), query); File.Copy(Path.Combine(source, lName), layout);
        string detailDir = Path.Combine(reports, "Config", "Xml"); Directory.CreateDirectory(detailDir);
        string detailQuery = Path.Combine(detailDir, detailQueryName), detail = Path.Combine(detailDir, detailName);
        File.Copy(Path.Combine(detailSource, detailQueryName), detailQuery); File.Copy(Path.Combine(detailSource, detailName), detail);
        try
        {
            var report = ReportImporter.ImportFile(query) ?? throw new Exception("Fixture query could not be imported.");
            var options = new WebOptions { DataDirectory = data, SiteDirectory = site, ReportsDirectory = reports,
                Offline = true, MaintenanceAddresses = new[] { "127.0.0.1" }, AllowLoopbackHttp = true };
            using var runtime = new WebRuntime(options, action => { _ = Task.Run(() => action(CancellationToken.None)); });
            var session = await runtime.HandleAsync(Request("GET", "/api/session"));
            Assert(session.Status == 200, "open maintenance session");
            string cookie = session.Headers["Set-Cookie"].Split(';')[0].Split('=')[1], csrf = (string)Parse(session)["csrf"];
            async Task<WebResponseData> Send(string path, object args, string ip = "127.0.0.1", string? token = null)
            {
                var request = Request("POST", path, ip); request.Cookie = cookie; request.Csrf = token ?? csrf;
                request.Body = Json.Serialize(new { tabId = "xml-integration-check", args });
                return await runtime.HandleAsync(request);
            }
            async Task<Dictionary<string, object>> Call(string path, object args)
            {
                var submitted = await Send(path, args);
                if (submitted.Status != 202) throw new Exception("HTTP status " + submitted.Status + " for " + path);
                string id = (string)Parse(submitted)["jobId"];
                for (int i = 0; i < 600; i++)
                {
                    var request = Request("GET", "/api/tasks/" + id); request.Cookie = cookie;
                    var response = Parse(await runtime.HandleAsync(request));
                    if ((string)response["state"] != "running") return response;
                    await Task.Delay(10);
                }
                throw new Exception("Timed out: " + path);
            }
            Dictionary<string, object> Data(Dictionary<string, object> result, string name)
            { if ((string)result["state"] != "succeeded") throw new Exception(name + " state=" + result["state"] + " error=" + (result.TryGetValue("error", out var error) ? error : "")); return (Dictionary<string, object>)result["data"]; }
            Assert((await Send("/api/admin/xmlEditorOpen", new { reportId = report.Id, kind = "query" }, "192.0.2.55")).Status == 403, "non-maintenance IP denied");
            Assert((await Send("/api/xmlEditorOpen", new { reportId = report.Id, kind = "query" })).Status == 404, "public API cannot call editor");
            Assert((await Send("/api/admin/xmlEditorOpen", new { reportId = report.Id, kind = "query" }, token: "invalid")).Status == 403, "CSRF required");
            var layoutOpen = Data(await Call("/api/admin/xmlEditorOpen", new { reportId = report.Id, kind = "layout" }), "open layout");
            Assert((string)layoutOpen["path"] == layout && (string)layoutOpen["kind"] == "layout", "open server-selected retained template");
            string layoutText = (string)layoutOpen["text"], layoutToken = (string)layoutOpen["token"];
            Assert(layoutText.Contains("药物名称") && ((string)layoutOpen["matchNotice"]).Contains("候选"), "candidate template is clearly labelled");
            string updatedLayout = layoutText.Replace("药物名称", "药品名称（测试）");
            var missingDetail = await Call("/api/admin/xmlEditorOpen", new { reportId = report.Id, kind = "detail" });
            Assert((string)missingDetail["state"] == "failed", "missing detail template refuses switch");
            var originalSession = Data(await Call("/api/admin/xmlEditorCheck", new { reportId = report.Id, token = layoutToken, text = updatedLayout }), "check original session after failed switch");
            Assert((bool)originalSession["passed"], "failed detail switch preserves original session token");
            var invalid = await Call("/api/admin/xmlEditorCheck", new { reportId = report.Id, token = layoutToken, text = "<Spread>" });
            Assert((string)invalid["state"] == "failed" && Hash(layout) == sourceLayoutHash, "invalid layout rejected without write");
            var wrongToken = await Call("/api/admin/xmlEditorSave", new { reportId = report.Id, token = "wrong", text = updatedLayout });
            Assert((string)wrongToken["state"] == "failed" && Hash(layout) == sourceLayoutHash, "wrong editor token rejected");
            var checkedLayout = Data(await Call("/api/admin/xmlEditorCheck", new { reportId = report.Id, token = layoutToken, text = updatedLayout }), "check layout");
            Assert((bool)checkedLayout["passed"] && Hash(layout) == sourceLayoutHash, "valid layout check does not write");
            var savedLayout = Data(await Call("/api/admin/xmlEditorSave", new { reportId = report.Id, token = layoutToken, text = updatedLayout }), "save layout");
            Assert((bool)savedLayout["saved"] && (bool)savedLayout["changed"] && FullXmlEditor.Read(layout, "Spread").Text == updatedLayout, "template save and disk readback");
            var selected = Data(await Call("/api/select", new { reportId = report.Id }), "select after layout");
            var header = (Dictionary<string, object>)selected["headerPreview"];
            var columns = ((System.Collections.IEnumerable)header["columns"]).Cast<Dictionary<string, object>>().ToArray();
            Assert(columns.Any(c => (string)c["label"] == "药品名称（测试）"), "template save reloads real header preview");
            var queryOpen = Data(await Call("/api/admin/xmlEditorOpen", new { reportId = report.Id, kind = "query" }), "open query");
            Assert((string)queryOpen["path"] == query, "open server-selected query");
            string queryText = (string)queryOpen["text"], queryToken = (string)queryOpen["token"];
            string changedQuery = queryText.Replace("</ReportQueryInfo>", "<!-- integration-test -->\r\n</ReportQueryInfo>");
            Assert(changedQuery != queryText, "query sample has expected root closing element");
            Data(await Call("/api/admin/xmlEditorCheck", new { reportId = report.Id, token = queryToken, text = changedQuery }), "check query");
            var savedQuery = Data(await Call("/api/admin/xmlEditorSave", new { reportId = report.Id, token = queryToken, text = changedQuery }), "save query");
            Assert((bool)savedQuery["saved"] && FullXmlEditor.Read(query, "ReportQueryInfo").Text.Contains("integration-test"), "query save and disk readback");
            var selectedAgain = Data(await Call("/api/select", new { reportId = report.Id }), "select after query");
            Assert((string)selectedAgain["definitionHash"] == FullXmlEditor.Read(query, "ReportQueryInfo").Hash, "query definition reload matches disk");
            var brokenOpen = Data(await Call("/api/admin/xmlEditorOpen", new { reportId = report.Id, kind = "query" }), "open query rollback fixture");
            string goodQueryHash = Hash(query);
            var badDefinition = await Call("/api/admin/xmlEditorSave", new { reportId = report.Id, token = (string)brokenOpen["token"], text = "<ReportQueryInfo/>" });
            Assert((string)badDefinition["state"] == "failed" && Hash(query) == goodQueryHash, "invalid report definition triggers rollback without loss");
            var afterRollback = Data(await Call("/api/select", new { reportId = report.Id }), "select after query rollback");
            Assert(string.Equals((string)afterRollback["definitionHash"], goodQueryHash, StringComparison.OrdinalIgnoreCase), "original query reloaded after rollback");
            var conflictOpen = Data(await Call("/api/admin/xmlEditorOpen", new { reportId = report.Id, kind = "layout" }), "open conflict fixture");
            string staleToken = (string)conflictOpen["token"], staleText = (string)conflictOpen["text"];
            File.AppendAllText(layout, " "); string externallyChanged = Hash(layout);
            var conflict = await Call("/api/admin/xmlEditorSave", new { reportId = report.Id, token = staleToken, text = staleText.Replace("药品名称（测试）", "冲突") });
            Assert((string)conflict["state"] == "failed" && Hash(layout) == externallyChanged, "external file change blocks save without overwrite");
            var detailReport = ReportImporter.ImportFile(detailQuery) ?? throw new Exception("Detail fixture query cannot be imported.");
            var detailOpened = Data(await Call("/api/admin/xmlEditorOpen", new { reportId = detailReport.Id, kind = "detail" }), "open explicitly referenced detail");
            Assert((string)detailOpened["path"] == detail && (string)detailOpened["kind"] == "detail", "explicitly referenced detail selects its exact server path");
            Assert(!((string)detailOpened["matchNotice"]).Contains("候选"), "exact explicit detail is not mislabeled as candidate");
            string candidateRoot = Path.Combine(root, "candidate-reports"); Directory.CreateDirectory(candidateRoot);
            string candidateQuery = Path.Combine(candidateRoot, detailQueryName), candidateDetail = Path.Combine(candidateRoot, detailName);
            File.Copy(Path.Combine(detailSource, detailQueryName), candidateQuery); File.Copy(Path.Combine(detailSource, detailName), candidateDetail);
            var candidateOptions = new WebOptions { DataDirectory = Path.Combine(root, "candidate-data"), SiteDirectory = site,
                ReportsDirectory = candidateRoot, Offline = true, MaintenanceAddresses = new[] { "127.0.0.1" }, AllowLoopbackHttp = true };
            using (var candidateRuntime = new WebRuntime(candidateOptions, action => { _ = Task.Run(() => action(CancellationToken.None)); }))
            {
                var candidateSession = await candidateRuntime.HandleAsync(Request("GET", "/api/session"));
                string candidateCookie = candidateSession.Headers["Set-Cookie"].Split(';')[0].Split('=')[1];
                var candidateRequest = Request("POST", "/api/admin/xmlEditorOpen"); candidateRequest.Cookie = candidateCookie;
                candidateRequest.Csrf = (string)Parse(candidateSession)["csrf"];
                candidateRequest.Body = Json.Serialize(new { tabId = "candidate-test", args = new { reportId = ReportImporter.ImportFile(candidateQuery)!.Id, kind = "detail" } });
                var submission = await candidateRuntime.HandleAsync(candidateRequest);
                Assert(submission.Status == 202, "detail candidate request accepted");
                string candidateJob = (string)Parse(submission)["jobId"];
                Dictionary<string, object>? candidateResult = null;
                for (int i = 0; i < 600; i++)
                {
                    var poll = Request("GET", "/api/tasks/" + candidateJob); poll.Cookie = candidateCookie;
                    candidateResult = Parse(await candidateRuntime.HandleAsync(poll));
                    if ((string)candidateResult["state"] != "running") break;
                    await Task.Delay(10);
                }
                var candidateOpen = Data(candidateResult ?? throw new Exception("Detail candidate timed out."), "open detail candidate");
                Assert((string)candidateOpen["path"] == candidateDetail && ((string)candidateOpen["matchNotice"]).Contains("候选"), "relocated detail remains identified as candidate");
            }
            Assert(Hash(Path.Combine(source, qName)) == sourceQueryHash && Hash(Path.Combine(source, lName)) == sourceLayoutHash, "retained original files unchanged");
            Assert(Hash(Path.Combine(detailSource, detailQueryName)) == sourceDetailQueryHash && Hash(Path.Combine(detailSource, detailName)) == sourceDetailHash, "retained detail source files unchanged");
            Console.WriteLine("WebRuntime + BackendAdapter offline checks: " + count + "; Oracle/IIS/HIS NOT RUN.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { Directory.Delete(root, true); }
    }
}
