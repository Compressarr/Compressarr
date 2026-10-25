namespace Compressarr.Web.Tests;

/// <summary>The stat cards at the top of the History page can be rearranged (dragged by the dots on a card's top edge, or moved with the arrow keys), and the order is remembered in the
/// browser. The behaviour itself was checked in the real page; these keep the pieces from being lost: only the top cards get a handle, never the History-at-a-glance group or the tables.</summary>
public class HistoryStatOrderTests
{
    private static string Script(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "wwwroot", name);
        Assert.True(File.Exists(path), $"{name} is not in the test output");
        return File.ReadAllText(path);
    }

    [Fact]
    public void TheTopStatCards_AreShownInTheUsersOrder_AndRememberedSeparatelyFromWhichAreShown()
    {
        var script = Script("history-overview.js");

        Assert.Contains("compressarr.history.cardOrder", script);
        Assert.Contains("compressarr.history.cards", script);                       // which stats are shown keeps its own key, so nobody's choice is lost
        Assert.Contains("ovRenderStrip(ovShown().map(", script);                     // drawn in the arranged order, not the built-in one
        Assert.DoesNotContain("OV_CARDS.filter(x => ovChosen.includes(x.id)).map(x => ovTile(", script);
        Assert.Contains("a stat you add comes last", script);
        Assert.Contains("ovOrder = OV_CARDS.map(c => c.id);", script);               // Reset to defaults puts the order back too
    }

    [Fact]
    public void OnlyTheTopCards_HaveAHandle_WithDragAndKeyboardMoving()
    {
        var script = Script("history-overview.js");

        Assert.Contains("class=\"ov-grip\"", script);
        Assert.Equal(1, CountOf(script, "class=\"ov-grip\""));                    // in ovTile only: not the ring, the weekly bars, the ribbon or the table
        Assert.Contains("getElementById('ovStrip')", script);
        Assert.Contains("'pointerdown'", script);
        Assert.Contains("ArrowLeft", script);
        Assert.Contains("ArrowRight", script);
        Assert.Contains(".ov-grip", Script("styles.css"));
        Assert.Contains("touch-action: none", Script("styles.css"));
    }

    private static int CountOf(string text, string part)
    {
        var count = 0;
        for (var i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + part.Length, StringComparison.Ordinal)) count++;
        return count;
    }
}
