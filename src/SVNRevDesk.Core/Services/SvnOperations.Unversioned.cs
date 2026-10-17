using System.Xml.Linq;
using SVNRevDesk.Core.Models;
using SVNRevDesk.Core.Svn;

namespace SVNRevDesk.Core.Services;

public sealed partial class SvnOperations
{
    private async Task ExpandUnversionedDirectoriesAsync(Dictionary<string, WorkingStatus> statuses,
        IReadOnlyList<string> scopes, string localRoot, CancellationToken token)
    {
        // SVN status reports an unversioned directory once; it does not list its descendants.
        var directories = statuses.Values.Where(status => status.Item == "unversioned" && Directory.Exists(status.Path))
            .Select(status => status.Path).ToList();
        var roots = directories.Where(path => !directories.Any(parent => parent != path
            && RepositoryService.IsDescendant(path, parent))).ToList();
        var configuration = runner.ConfigurationDirectory
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Subversion");
        var configuredIgnores = SvnIgnoreMatcher.ReadConfiguration(Path.Combine(configuration, "config"));
        var ignoreCache = new Dictionary<string, SvnIgnoreMatcher>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            token.ThrowIfCancellationRequested();
            if (localIgnores?.Matches(root) == true) continue;
            if (AdditionUnavailableReason(root, localRoot).Length > 0) continue;
            var parent = Path.GetDirectoryName(root)!;
            if (!ignoreCache.TryGetValue(parent, out var ignores))
            {
                var properties = XDocument.Parse(await runner.RunAsync(["proplist", "--xml", "--verbose",
                    "--show-inherited-props", "--depth", "empty", SvnRunner.Target(parent)], token));
                var inherited = properties.Descendants().Where(element => element.Name.LocalName is "property" or "inherited_property"
                    && (string?)element.Attribute("name") == "svn:global-ignores")
                    .SelectMany(element => SvnXml.ReadTextProperty(element).Split('\n', StringSplitOptions.RemoveEmptyEntries));
                ignores = new SvnIgnoreMatcher(configuredIgnores.Concat(inherited));
                ignoreCache[parent] = ignores;
            }
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.TryPop(out var directory))
            {
                token.ThrowIfCancellationRequested();
                foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                {
                    token.ThrowIfCancellationRequested();
                    if (Path.GetFileName(path).Equals(".svn", StringComparison.OrdinalIgnoreCase)
                        || localIgnores?.Matches(path) == true
                        || ignores.Matches(Path.GetFileName(path))
                        || !scopes.Any(scope => Inside(path, scope) || Inside(scope, path))) continue;
                    statuses[path] = new(path, "unversioned", "none", "", "", null, null, false, false, null, null);
                    if (Directory.Exists(path) && AdditionUnavailableReason(path, localRoot).Length == 0) pending.Push(path);
                }
            }
        }
    }

    private static string AdditionUnavailableReason(string path, string localRoot)
    {
        for (string? current = path; current != null && Inside(current, localRoot); current = Path.GetDirectoryName(current))
        {
            if (Path.GetFileName(current).Equals(".svn", StringComparison.OrdinalIgnoreCase)) return "不能提交 SVN 元数据。";
            if (!File.Exists(current) && !Directory.Exists(current)) return "本地项目已不存在，请重新检查。";
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return "链接项目请在实际工作副本内单独处理。";
            if (current.Equals(localRoot, StringComparison.OrdinalIgnoreCase)) break;
            if (Directory.Exists(Path.Combine(current, ".svn"))) return "嵌套工作副本请单独打开后提交。";
        }
        return "";
    }
}
