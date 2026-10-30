using System.Text.RegularExpressions;

namespace Compressarr.Web.Tests;

/// <summary>The pages build rows and options as HTML strings, and some put a lane or profile name inside an attribute (<c>title="..."</c>, <c>value="..."</c>). A name can come from a
/// shared config export or an imported profile, so the helper that escapes it must also escape quotes, or a name such as <c>" onmouseover="...</c> becomes live script on the page.
/// The old copies (<c>div.textContent = x; return div.innerHTML</c>) escaped only &amp; &lt; &gt;.</summary>
public class PageEscapingTests
{
    private static readonly Regex Definition = new(@"function\s+(escape\w*Html\w*|escapeAttr)\s*\([^)]*\)\s*\{(?<body>[^}]*(\{[^}]*\}[^}]*)*)\}", RegexOptions.Compiled);

    private static IEnumerable<(string File, string Name, string Body)> Helpers()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        foreach (var path in Directory.GetFiles(folder, "*.js"))
        {
            foreach (Match m in Definition.Matches(File.ReadAllText(path)))
                yield return (Path.GetFileName(path), m.Groups[1].Value, m.Groups["body"].Value);
        }
    }

    [Fact]
    public void EveryEscapeHelperInThePages_EscapesQuotesToo()
    {
        var helpers = Helpers().ToList();
        Assert.True(helpers.Count >= 8, "the pages' escape helpers were not found - has the pattern changed?");

        foreach (var (file, name, body) in helpers.Where(h => h.Name != "escapeAttr"))
        {
            Assert.True(body.Contains("&quot;") && body.Contains("&#39;"), $"{file}: {name} does not escape quotes, so it is unsafe inside an attribute");
            Assert.DoesNotContain("innerHTML", body);   // the textContent / innerHTML trick leaves quotes alone
        }
    }
}
