using System.Globalization;
using SVNRevDesk.Core.Models;
using SVNRevDesk.Core.Svn;

namespace SVNRevDesk.Core.Services;

public sealed partial class SvnOperations
{
    public Task<HistoryPage> LogPageAsync(string url, long? startRevision = null, long? pegRevision = null,
        int pageSize = 100, CancellationToken token = default) =>
        LogPageAsync([url], startRevision, pegRevision, pageSize, token);

    public async Task<HistoryPage> LogPageAsync(IReadOnlyList<string> urls, long? startRevision = null, long? pegRevision = null,
        int pageSize = 100, CancellationToken token = default)
    {
        var targets = urls.Distinct(StringComparer.Ordinal).ToArray();
        if (targets.Length == 0 || targets.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("请选择历史查询目标。", nameof(urls));
        if (pageSize is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(pageSize));
        if (startRevision is < 0) throw new ArgumentOutOfRangeException(nameof(startRevision));
        if (pegRevision is < 0) throw new ArgumentOutOfRangeException(nameof(pegRevision));
        if (pegRevision == null)
        {
            var info = SvnXml.ReadInfo(await runner.RunAsync(
                ["info", "--xml", "-r", "HEAD", SvnRunner.AtRevision(targets[0], "HEAD")], token)).Single();
            pegRevision = info.Revision;
        }
        var start = startRevision ?? pegRevision.Value;
        if (start > pegRevision.Value) throw new ArgumentOutOfRangeException(nameof(startRevision));
        // Freeze the peg and upper bound across pages, including when a path was renamed or
        // another commit arrives. One extra row tells us whether an earlier page really exists.
        // Read each selected path, rather than a common parent that would include siblings.
        // An extra row per path is sufficient to find the next page of the merged history.
        var combined = new List<HistoryEntry>();
        foreach (var url in targets)
        {
            try
            {
                combined.AddRange(SvnXml.ReadLog(await runner.RunAsync(["log", "--xml", "--limit",
                    (pageSize + 1).ToString(CultureInfo.InvariantCulture), "-r",
                    start.ToString(CultureInfo.InvariantCulture) + ":0",
                    SvnRunner.AtRevision(url, pegRevision.Value.ToString(CultureInfo.InvariantCulture))], token)));
            }
            catch (SvnException ex) when (start < pegRevision.Value && !ex.IsAuthenticationError
                && ex.ErrorOutput.Contains("E195012", StringComparison.Ordinal))
            {
                // This selected path did not exist yet at the shared page boundary. Other
                // selected paths may still have older commits; continue merging their history.
            }
        }
        var entries = combined.DistinctBy(entry => entry.Revision).OrderByDescending(entry => entry.Revision).ToArray();
        var page = entries.Take(pageSize).ToArray();
        long? next = entries.Length > pageSize && page[^1].Revision > 0 ? page[^1].Revision - 1 : null;
        return new HistoryPage(page, pegRevision.Value, next);
    }
}
