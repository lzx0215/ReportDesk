using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Web.Script.Serialization;
using System.Xml.Linq;

// Run against the actual payload DLL and exact server Core, without project references or Oracle.
internal static class Program
{
    private static readonly JavaScriptSerializer Json = new() { MaxJsonLength = int.MaxValue };
    private static Assembly web = null!;
    private static int checks;
    private static void Check(bool value, string name)
    { if (!value) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
    private static Dictionary<string, object> D(object value) => Json.Deserialize<Dictionary<string, object>>(Json.Serialize(value));
    private static Dictionary<string, object>[] Items(object value) => ((IEnumerable)value).Cast<object>().Select(D).ToArray();
    private static string Hash(string path)
    { using var sha = SHA256.Create(); return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", ""); }
    private static object Invoke(object target, string method, params object[] args)
    {
        try { return target.GetType().GetMethod(method)!.Invoke(target, args)!; }
        catch (TargetInvocationException ex) when (ex.InnerException != null) { throw ex.InnerException; }
    }
    private static object Call(object backend, IDisposable context, string method, string id = "")
        => Invoke(backend, "Call", context, method, new Dictionary<string, object> { ["reportId"] = id }, false,
            CancellationToken.None, (Action<string>)(_ => { }));
    private static int Main(string[] args)
    {
        try
        {
            string bin = Path.GetFullPath(args[0]), source = Path.GetFullPath(args[1]), evidence = Path.GetFullPath(args[2]);
            if (Directory.Exists(evidence)) throw new Exception("Evidence directory must be new.");
            AppDomain.CurrentDomain.AssemblyResolve += (_, request) => {
                var path = Path.Combine(bin, new AssemblyName(request.Name).Name + ".dll");
                return File.Exists(path) ? Assembly.LoadFrom(path) : null;
            };
            var corePath = Path.Combine(bin, "ReportDesk.Core.dll");
            Check(Hash(corePath) == "6249D398ECC6B39F258AA2BF439BF4ED249576C43170ECE15015953234C7BDA7", "exact server legacy Core");
            var core = Assembly.LoadFrom(corePath);
            Check(core.GetType("ReportDesk.Core.ParameterOptions")!.GetMethod("IsCurrentDepartment") == null, "no new Core API available");
            web = Assembly.LoadFrom(Path.Combine(bin, "ReportDesk.Web.dll"));
            string reports = Path.Combine(evidence, "reports"), data = Path.Combine(evidence, "data"), site = Path.Combine(evidence, "site");
            Directory.CreateDirectory(reports); Directory.CreateDirectory(site);
            var names = new[] { "住院人次查询", "门诊胸痛患者查询", "住院胸痛患者查询" };
            var originals = new Dictionary<string, string>();
            foreach (var name in names)
                foreach (var suffix in new[] { "查询设置.xml", "报表设置.xml" })
                {
                    var input = Directory.GetFiles(source, name + suffix, SearchOption.AllDirectories).Single();
                    originals.Add(input, Hash(input)); File.Copy(input, Path.Combine(reports, name + suffix));
                }
            var options = Activator.CreateInstance(web.GetType("ReportDesk.Web.WebOptions")!)!;
            foreach (var pair in new Dictionary<string, object> { ["DataDirectory"] = data, ["ReportsDirectory"] = reports, ["SiteDirectory"] = site, ["Offline"] = true })
                options.GetType().GetProperty(pair.Key)!.SetValue(options, pair.Value);
            Invoke(options, "Validate");
            core.GetType("ReportDesk.Core.ErrorLog")!.GetMethod("Initialize")!.Invoke(null, new object[] { Path.Combine(data, "Logs") });
            using var backend = (IDisposable)Activator.CreateInstance(web.GetType("ReportDesk.Web.BackendAdapter")!, options)!;
            using var context = (IDisposable)Invoke(backend, "CreateContext");
            var list = Items(D(Call(backend, context, "bootstrap"))["reports"]);
            Check(list.Length == 3, "three real XML definitions loaded offline");
            var details = new List<Dictionary<string, object>>();
            foreach (var name in names)
            {
                var report = list.Single(r => (string)r["name"] == name);
                var selected = D(Call(backend, context, "select", (string)report["id"]));
                var preview = D(selected["headerPreview"]); var columns = Items(preview["columns"]);
                Check((string)preview["title"] == name && columns.Length == (name == names[0] ? 8 : name == names[1] ? 8 : 11), name + " title and visible headers before query");
                Check((int)columns[0]["ordinal"] == 0 && (int)columns[0]["width"] > 0, name + " template order/width retained");
                Check(((string)preview["message"]).Contains("候选"), name + " filename candidate disclosed");
                Check(!Items(selected["parameters"]).Any(p => p.ContainsKey("contextDepartment")), name + " legacy parameter contract unchanged");
                var text = Json.Serialize(preview);
                Check(!text.Contains("dtMain") && !text.Contains(reports.Replace("\\", "\\\\")) && !text.Contains("SELECT "), name + " preview exposes no paths, SQL or data tags");
                details.Add(selected);
            }
            var inpatient = D(details[0]["headerPreview"]);
            Check(Items(inpatient["columns"]).Select(c => (string)c["label"]).SequenceEqual(new[] { "科室", "入院人数", "转入人数", "转出人数", "出院人数", "在院人数", "待入院人数", "当前病区空床数" }), "screenshot's exact eight headers; hidden ninth column omitted");
            File.WriteAllText(Path.Combine(evidence, "details.json"), Json.Serialize(details));
            var selectedId = (string)details[1]["id"];
            string layout = Path.Combine(reports, names[1] + "报表设置.xml");
            var original = File.ReadAllBytes(layout);
            void Unavailable(string name)
            {
                var dto = D(Call(backend, context, "select", selectedId));
                Check(Items(D(dto["headerPreview"])["columns"]).Length == 0 && Items(dto["sources"]).Length > 0, name + " degrades without breaking selection");
            }
            var document = XDocument.Load(layout);
            document.Descendants("Tag").Single().Value = "different-source"; document.Save(layout);
            Unavailable("wrong data-source binding"); File.WriteAllBytes(layout, original);
            document = XDocument.Load(layout);
            document.Descendants("SpanModels").Descendants("CellRange").First().SetAttributeValue("RowCount", 2); document.Save(layout);
            Unavailable("merged multi-row header"); File.WriteAllBytes(layout, original);
            document = XDocument.Load(layout);
            document.Descendants("Cell").First().Add(new XElement("Formula", "external()")); document.Save(layout);
            Unavailable("formula header"); File.WriteAllBytes(layout, original);
            document = XDocument.Load(layout);
            document.Descendants("ColumnCount").First().Value = "invalid"; document.Save(layout);
            Unavailable("invalid numeric layout metadata"); File.WriteAllBytes(layout, original);
            File.WriteAllText(layout, "<!DOCTYPE Spread [<!ENTITY x SYSTEM 'file:///not-read'>]><Spread>&x;</Spread>");
            Unavailable("DTD/external entity rejected"); File.WriteAllBytes(layout, original);
            File.WriteAllText(layout, "<Spread>"); Unavailable("malformed template"); File.WriteAllBytes(layout, original);
            File.Move(layout, layout + ".held");
            try { Unavailable("missing template"); } finally { File.Move(layout + ".held", layout); }
            var attributes = File.GetAttributes(layout); File.SetAttributes(layout, attributes | FileAttributes.ReadOnly);
            try { Check(Items(D(D(Call(backend, context, "select", selectedId))["headerPreview"])["columns"]).Length == 8, "read-only template preview supported"); }
            finally { File.SetAttributes(layout, attributes); }
            Check(originals.All(p => Hash(p.Key) == p.Value), "all real source XML bytes unchanged");
            Check(originals.All(p => Hash(Path.Combine(reports, Path.GetFileName(p.Key))) == p.Value), "selection leaves copied XML unchanged");
            Check(!Directory.GetFiles(data, "pending.xml", SearchOption.AllDirectories).Any(), "no XML transaction created by selection");
            Console.WriteLine("PASS " + checks + " header preview checks; no SQL executed; real Oracle/IIS NOT RUN.");
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("FAIL " + ex); return 1; }
    }
}
