using System;
using System.IO;
using System.Linq;
using System.Text;
using ReportDesk.Web.Publish;
using ReportDesk.Web.Sync;

internal static class Program
{
    private const string Query = "<ReportQueryInfo><QueryDataSource><QueryDataSource><SqlType>MainReportUsing</SqlType><Sql>select 1</Sql></QueryDataSource></QueryDataSource><ReportInfo><IsDetail>false</IsDetail></ReportInfo></ReportQueryInfo>";
    private const string Layout = "<Spread class=\"FarPoint.Win.Spread.FpSpread\" />";
    private static int checks;

    private static void Check(bool condition, string label)
    { if (!condition) throw new Exception(label); checks++; }
    private static void Reject(string code, Action action)
    {
        try { action(); throw new Exception("Expected " + code); }
        catch (SyncSafetyException ex) { Check(ex.Code == code, "Expected " + code + ", got " + ex.Code); }
    }
    private static void Write(string path, string content)
    { Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, content, new UTF8Encoding(false)); }
    private static void Main()
    {
        string temp = Path.Combine(Path.GetTempPath(), "ReportDesk-publish-checks-" + Guid.NewGuid().ToString("N"));
        string root = Path.Combine(temp, "work"); string state = Path.Combine(temp, "state");
        Directory.CreateDirectory(root);
        try
        {
            var work = new ReportWorkDirectory(root, state);
            var query = Path.Combine(root, "示例查询设置.xml");
            var main = Path.Combine(root, "示例报表设置.xml");
            Write(query, Query); Write(main, Layout);
            var preview = ReportPublishPreparation.Preview(work, "示例查询设置.xml");
            Check(preview.Files.Count == 2 && preview.RequiresConfirmation, "Candidate main preview");
            Check(preview.Files[0].Role == "Query" && preview.Files[1].Role == "Main" && preview.Files[1].Status == "Candidate", "Roles and evidence");
            Check(preview.Files.All(f => f.Sha256.Length == 64), "Hashes");
            Reject("CandidateConfirmationRequired", () => ReportPublishPreparation.VerifyCurrent(work, preview, false));
            ReportPublishPreparation.VerifyCurrent(work, preview, true); checks++;
            Write(main, Layout.Replace(" />", "><changed /></Spread>"));
            Reject("PreviewChanged", () => ReportPublishPreparation.VerifyCurrent(work, preview, true));
            File.Delete(main);
            Reject("CompanionUnresolved", () => ReportPublishPreparation.Preview(work, "示例查询设置.xml"));
            Write(main, Layout);
            var unrelated = Path.Combine(root, "无约定.xml"); Write(unrelated, Query);
            Reject("MainCompanionUnresolved", () => ReportPublishPreparation.Preview(work, "无约定.xml"));
            Reject("UnsafePath", () => ReportPublishPreparation.Preview(work, "../outside.xml"));
            File.Delete(unrelated);
            var detailQuery = Query.Replace("<IsDetail>false</IsDetail>", "<IsDetail>true</IsDetail><DetailDirectory>明细.xml</DetailDirectory>");
            Write(query, detailQuery); Write(Path.Combine(root, "明细.xml"), Layout);
            preview = ReportPublishPreparation.Preview(work, "示例查询设置.xml");
            Check(preview.Files.Count == 3 && preview.Files[2].Role == "Detail" && preview.Files[2].Status == "Matched", "Explicit detail reference");
            File.Delete(Path.Combine(root, "明细.xml"));
            Write(Path.Combine(root, "a", "明细.xml"), Layout); Write(Path.Combine(root, "b", "明细.xml"), Layout);
            Reject("CompanionUnresolved", () => ReportPublishPreparation.Preview(work, "示例查询设置.xml"));
            Write(query, "<!DOCTYPE ReportQueryInfo [<!ENTITY x 'x'>]><ReportQueryInfo>&x;</ReportQueryInfo>");
            Reject("ReportXmlInvalid", () => ReportPublishPreparation.Preview(work, "示例查询设置.xml"));
            Console.WriteLine("PASS: " + checks + " publish preparation checks");
        }
        finally { Directory.Delete(temp, true); }
    }
}
