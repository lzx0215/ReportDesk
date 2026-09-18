using System;
using System.Collections.Generic;
using System.Threading;

namespace ReportDesk.Web;

// The HTTP boundary owns authentication, task ownership and per-tab serialization.
// Business implementations never receive arbitrary HTTP paths or file destinations.
public interface IWebBackend : IDisposable
{
    IDisposable CreateContext();
    object Call(IDisposable context, string method, Dictionary<string, object> args, bool maintenance, CancellationToken cancellation, Action<string> progress);
    byte[] Export(IDisposable context, Dictionary<string, object> args, CancellationToken cancellation);
    object SyncStatus();
    object Synchronize(CancellationToken cancellation, Action<string> progress);
    object BrowseReports(string relative);
    object UploadImport(Dictionary<string, object> args);
}
