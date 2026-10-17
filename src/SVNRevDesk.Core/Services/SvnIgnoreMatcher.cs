using System.Text;
using System.Text.RegularExpressions;

namespace SVNRevDesk.Core.Services;

// For files below an unversioned directory, SVN cannot supply status/ignore information.
// Use the same client-wide and inherited global ignore patterns when discovering those files.
internal sealed class SvnIgnoreMatcher
{
    private const string DefaultPatterns = "*.o *.lo *.la *.al .libs *.so *.so.[0-9]* *.a *.pyc *.pyo __pycache__ *.rej *~ #*# .#* .*.swp .DS_Store [Tt]humbs.db";
    private readonly IReadOnlyList<Regex> patterns;

    public SvnIgnoreMatcher(IEnumerable<string> globs) => patterns = globs.Where(glob => glob.Length > 0)
        .Select(glob => new Regex("\\A" + ToRegex(glob) + "\\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))).ToList();

    public bool Matches(string name) => patterns.Any(pattern => pattern.IsMatch(name));

    public static IReadOnlyList<string> ReadConfiguration(string path)
    {
        string? configured = null;
        var inMiscellany = false;
        var continuing = false;
        if (File.Exists(path))
            foreach (var line in File.ReadLines(path))
            {
                var text = line.Trim();
                if (text.Length == 0 || text.StartsWith('#') || text.StartsWith(';')) continue;
                if (text.StartsWith('['))
                {
                    inMiscellany = text.Equals("[miscellany]", StringComparison.OrdinalIgnoreCase);
                    continuing = false;
                    continue;
                }
                if (!inMiscellany) continue;
                var separator = text.IndexOf('=');
                if (separator < 0) separator = text.IndexOf(':');
                if (separator >= 0)
                {
                    continuing = text[..separator].Trim().Equals("global-ignores", StringComparison.OrdinalIgnoreCase);
                    if (continuing) configured = text[(separator + 1)..].Trim();
                }
                else if (continuing && char.IsWhiteSpace(line[0])) configured += " " + text;
            }
        return (configured ?? DefaultPatterns).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    }

    private static string ToRegex(string glob)
    {
        var result = new StringBuilder();
        for (var index = 0; index < glob.Length; index++)
        {
            var character = glob[index];
            if (character == '*') result.Append(".*");
            else if (character == '?') result.Append('.');
            else if (character == '[' && glob.IndexOf(']', index + 1) is var end && end > index + 1)
            {
                var content = glob[(index + 1)..end];
                result.Append('[');
                if (content[0] == '!') { result.Append('^'); content = content[1..]; }
                else if (content[0] == '^') result.Append('\\');
                result.Append(content.Replace("\\", "\\\\")).Append(']');
                index = end;
            }
            else result.Append(Regex.Escape(character.ToString()));
        }
        return result.ToString();
    }
}
