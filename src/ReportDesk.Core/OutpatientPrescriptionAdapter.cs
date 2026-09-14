using System;
using System.IO;

namespace ReportDesk.Core;

// A reviewed, bounded table adapter, not a general implementation of the report map engine.
// It requires exact content hashes and never identifies a report by its filename alone.
public static class OutpatientPrescriptionAdapter
{
    public const string QueryHash = "7aac5c3c2adb9e63714309f0f81ab28f510c59e81ae1e25595bedd7ab111f35c";
    public const string MainLayoutName = "门诊处方患者明细报表设置.xml";
    public const string DetailLayoutName = "门诊处方患者明细的明细报表设置.xml";
    private const string MainLayoutHash = "d29dc84d85488f48b5a80f2b56b703eb8bef748128d27231c3a9317199daecda";
    private const string DetailLayoutHash = "67ba5534d94024e7fd391126fd07423250ba21c4741710724bd66a0aeac600d5";
    public const string Notice = "已适配此版本的表格查询：dtMain 显示处方列表及原 SQL 合计行；dtALL 查询药品明细，需填写主表的处方号和唯一号。主明细分别执行，未复刻 HIS 右键联动及打印版式；业务结果仍需在内网核对。";

    public static bool IsReviewedQuery(string sourceHash) => QueryHash.Equals(sourceHash, StringComparison.OrdinalIgnoreCase);

    public static bool Matches(string sourceHash, string sourcePath)
    {
        if (!IsReviewedQuery(sourceHash)) return false;
        var directory = Path.GetDirectoryName(Path.GetFullPath(sourcePath))!;
        return LayoutMatches(directory, MainLayoutName, MainLayoutHash) && LayoutMatches(directory, DetailLayoutName, DetailLayoutHash);
    }

    public static string NoticeFor(ReportDefinition report) => report.Issues.Count == 0 && IsReviewedQuery(report.SourceHash) ? Notice : "";

    private static bool LayoutMatches(string directory, string name, string hash)
    {
        var file = Path.Combine(directory, name);
        try
        {
            // Known small local templates only; reject changed, missing, linked or oversized files.
            var info = new FileInfo(file);
            if (!info.Exists || info.Length > 1024 * 1024 || (info.Attributes & FileAttributes.ReparsePoint) != 0) return false;
            return ReportImporter.Hash(File.ReadAllBytes(file)) == hash;
        }
        catch (Exception ex) when (ReportFileDiscovery.IsFileError(ex))
        { ErrorLog.Write("CheckPrescriptionLayout", ex, includeMessage: false); return false; }
    }
}
