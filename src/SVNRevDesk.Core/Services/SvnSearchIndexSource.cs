using System.Xml.Linq;
using SVNRevDesk.Core.Models;
using SVNRevDesk.Core.Svn;

namespace SVNRevDesk.Core.Services;

public sealed class SvnSearchIndexSource(SvnRunner runner, bool ownsRunner = false) : ISearchIndexSource
{
    public async Task<long> ReadRevisionAsync(RepositorySession session, CancellationToken token) =>
        SvnXml.ReadInfo(await runner.RunAsync(["info", "--xml", "-r", "HEAD", SvnRunner.Target(session.RepositoryRoot)], token)).Single().Revision;

    public async Task<IReadOnlyList<ServerEntry>> ReadEntriesAsync(string url, long revision, bool recursive, CancellationToken token) =>
        SvnXml.ReadList(await runner.RunAsync(["list", "--xml", "--depth", recursive ? "infinity" : "empty", "-r",
            revision.ToString(System.Globalization.CultureInfo.InvariantCulture), SvnRunner.AtRevision(url, revision.ToString(System.Globalization.CultureInfo.InvariantCulture))], token));

    public async Task<SvnInfo?> ReadNodeAsync(string url, long revision, CancellationToken token)
    {
        try
        {
            return SvnXml.ReadInfo(await runner.RunAsync(["info", "--xml", "-r", revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
                SvnRunner.AtRevision(url, revision.ToString(System.Globalization.CultureInfo.InvariantCulture))], token)).Single();
        }
        catch (SvnException ex) when (IsMissingPath(ex)) { return null; }
    }

    public async Task<IReadOnlyList<SearchPathChange>> ReadChangesAsync(string url, long fromRevision, long toRevision, CancellationToken token)
    {
        var xml = await runner.RunAsync(["diff", "--summarize", "--xml", "--notice-ancestry", "--depth", "infinity", "-r",
            $"{fromRevision}:{toRevision}", SvnRunner.AtRevision(url, toRevision.ToString(System.Globalization.CultureInfo.InvariantCulture))], token);
        var root = new Uri(url.TrimEnd('/') + "/");
        return XDocument.Parse(xml).Descendants("path").Select(element =>
        {
            if (!Uri.TryCreate(element.Value, UriKind.Absolute, out var target)) throw new IOException("SVN 返回了无效的变更路径。");
            var relative = new Uri(url).Equals(target) ? "" : Uri.UnescapeDataString(root.MakeRelativeUri(target).ToString()).TrimEnd('/');
            SearchIndexStore.ValidateRelative(relative, allowEmpty: true);
            return new SearchPathChange(relative, (string?)element.Attribute("kind") == "dir", (string?)element.Attribute("item") ?? "");
        }).ToArray();
    }

    public async Task<RepositorySnapshot> ReadLocalAsync(RepositorySession session, DirectoryRefresh scope, CancellationToken token)
    {
        SearchIndexStore.ValidateRelative(scope.RelativePath, allowEmpty: true);
        var statuses = new Dictionary<string, WorkingStatus>(StringComparer.OrdinalIgnoreCase);
        var infos = new Dictionary<string, SvnInfo>(StringComparer.OrdinalIgnoreCase);
        if (session.LocalRoot == null) return new(0, DateTimeOffset.Now, statuses, infos);
        var path = RepositoryService.GetLocalPath(session, scope.RelativePath)!;
        var depth = scope.IncludeDescendants ? "infinity" : "immediates";
        try
        {
            foreach (var status in SvnXml.ReadStatus(await runner.RunAsync(["status", "--xml", "--verbose", "--no-ignore", "--ignore-externals",
                "--depth", depth, SvnRunner.Target(path)], token))) statuses[status.Path] = status;
            if (statuses.TryGetValue(path, out var own) && own.Item is not ("unversioned" or "ignored" or "external" or "none")
                && (own.BaseRevision != null || own.Item is "added" or "replaced"))
                foreach (var info in SvnXml.ReadInfo(await runner.RunAsync(["info", "--xml", "--depth", depth, SvnRunner.Target(path)], token)))
                {
                    var full = Path.GetFullPath(info.Path);
                    infos[full] = info with { Path = full };
                }
        }
        catch (SvnException ex) when (scope.RelativePath.Length > 0 && !Directory.Exists(path) && !File.Exists(path) && IsMissingPath(ex)) { }
        return new(0, DateTimeOffset.Now, statuses, infos);
    }

    internal static bool IsMissingPath(SvnException ex) => ex.ErrorOutput.Contains("E160013", StringComparison.Ordinal)
        || ex.ErrorOutput.Contains("E195012", StringComparison.Ordinal) || ex.ErrorOutput.Contains("E155010", StringComparison.Ordinal)
        || ex.ErrorOutput.Contains("E200009", StringComparison.Ordinal);

    public void Dispose() { if (ownsRunner) runner.Dispose(); }
}
