using System;
using System.IO;
using System.Linq;
using System.Text;
using ReportDesk.Core;

internal static class ReportVisibilityChecks
{
    internal static void Run(string folder, Action<string, Action> check)
    {
        var directory = Path.Combine(folder, "visibility-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, ReportVisibility.FileName);
        var doctor = new ReportDefinition { Id = "doctor-report", Name = "同名报表", Category = "工作量" };
        var nurse = new ReportDefinition { Id = "nurse-report", Name = doctor.Name, Category = doctor.Category };
        check("visibility missing config preserves existing catalog display", () =>
        { Assert(ReportVisibility.Load(path).Includes(doctor) && !File.Exists(path)); });
        check("visibility allow-list matches ID, not name/category, and does not alter files", () =>
        {
            File.WriteAllText(path, "<ReportVisibility mode=\"selected\"><Report id=\"DOCTOR-REPORT\" name=\"仅备注\" /></ReportVisibility>", Encoding.UTF8);
            var before = File.ReadAllBytes(path); var policy = ReportVisibility.Load(path);
            Assert(policy.Includes(doctor) && !policy.Includes(nurse) && !policy.Includes(DemoData.Report()));
            doctor.Name = "改名"; doctor.Category = "改分类"; doctor.Favorite = true;
            Assert(policy.Includes(doctor) && before.SequenceEqual(File.ReadAllBytes(path)));
        });
        check("visibility empty list and unmatched IDs never fall back to all", () =>
        {
            foreach (var entries in new[] { "", "<Report id=\"not-in-catalog\" />" })
            {
                File.WriteAllText(path, "<ReportVisibility mode=\"selected\">" + entries + "</ReportVisibility>");
                var policy = ReportVisibility.Load(path); Assert(!policy.Includes(doctor) && !policy.Includes(nurse));
            }
        });
        check("visibility reload changes display and explicit all restores existing behavior", () =>
        {
            File.WriteAllText(path, "<ReportVisibility mode=\"selected\"><Report id=\"doctor-report\" /></ReportVisibility>");
            var old = ReportVisibility.Load(path);
            File.WriteAllText(path, "<ReportVisibility mode=\"all\" />"); var current = ReportVisibility.Load(path);
            Assert(!old.Includes(nurse) && current.Includes(nurse) && current.Includes(DemoData.Report()));
        });
        check("visibility invalid config fails with fixed diagnostic and preserves original", () =>
        {
            foreach (var bad in new[] { "", "broken", "<ReportVisibility />", "<ReportVisibility mode=\"seleted\" />",
                "<ReportVisibility mode=\"all\"><Report id=\"doctor-report\" /></ReportVisibility>",
                "<ReportVisibility mode=\"all\" extra=\"x\" />", "<ReportVisibility xmlns=\"unexpected\" mode=\"all\" />",
                "<ReportVisibility mode=\"selected\"><Reports id=\"doctor-report\" /></ReportVisibility>",
                "<ReportVisibility mode=\"selected\"><Report name=\"missing-id\" /></ReportVisibility>",
                "<ReportVisibility mode=\"selected\"><Report id=\" \" /></ReportVisibility>",
                "<ReportVisibility mode=\"selected\"><Report id=\"doctor-report\" role=\"x\" /></ReportVisibility>",
                "<ReportVisibility mode=\"selected\">UNTRUSTED_CONFIGURATION_CONTENT</ReportVisibility>",
                "<ReportVisibility mode=\"selected\"><Report id=\"doctor-report\"><Report id=\"nurse-report\" /></Report></ReportVisibility>" })
            {
                File.WriteAllText(path, bad); Reject(() => ReportVisibility.Load(path)); Assert(File.ReadAllText(path) == bad);
            }
        });
        check("visibility external entities and oversized config rejected", () =>
        {
            File.WriteAllText(path, "<!DOCTYPE r [<!ENTITY x SYSTEM 'file:///never-read'>]><ReportVisibility mode=\"selected\">&x;</ReportVisibility>");
            Reject(() => ReportVisibility.Load(path));
            File.WriteAllText(path, "<ReportVisibility mode=\"selected\"><!--" + new string('x', 1024 * 1024) + "--></ReportVisibility>");
            Reject(() => ReportVisibility.Load(path));
        });
        check("visibility unreadable config is not treated as absent", () =>
        {
            File.WriteAllText(path, "<ReportVisibility mode=\"all\" />");
            using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Reject(() => ReportVisibility.Load(path));
        });
        check("visibility shared report may be listed in multiple workstation profiles", () =>
        {
            var shared = new ReportDefinition { Id = "shared" };
            foreach (var id in new[] { doctor.Id, nurse.Id })
            {
                File.WriteAllText(path, "<ReportVisibility mode=\"selected\"><Report id=\"" + id + "\" /><Report id=\"shared\" /></ReportVisibility>");
                var policy = ReportVisibility.Load(path);
                Assert(policy.Includes(shared) && policy.Includes(id == doctor.Id ? doctor : nurse) && !policy.Includes(id == doctor.Id ? nurse : doctor));
            }
        });
    }

    private static void Assert(bool condition) { if (!condition) throw new Exception("Visibility assertion failed"); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidOperationException ex)
        { Assert(ex.Message.Contains(ReportVisibility.FileName) && !ex.Message.Contains("UNTRUSTED_CONFIGURATION_CONTENT")); return; }
        throw new Exception("Expected visibility configuration rejection");
    }
}
