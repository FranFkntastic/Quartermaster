using RQ.Automation;

namespace RQ.Tests;

public sealed class RetainerUiAutomationTextTests
{
    [Theory]
    [InlineData("Entrust or withdraw items.", "Entrust or withdraw items", true)]
    [InlineData("Entrust or withdraw items. (22)", "Entrust or withdraw items", true)]
    [InlineData("\uE03CEntrust or withdraw items.", "Entrust or withdraw items", true)]
    [InlineData("Assign venture.", "Entrust or withdraw items", false)]
    public void SelectStringMatch_NormalizesDecoratedLocalizedEntries(string entry, string target, bool expected) =>
        Assert.Equal(expected, RetainerUiAutomationText.IsSelectStringEntryMatch(entry, target));

    [Fact]
    public void RetainerSelection_RequiresActiveMatchingRow()
    {
        var rows = new[]
        {
            new RetainerListEntry("Alpha", true),
            new RetainerListEntry("Beta", false),
            new RetainerListEntry("Gamma", true),
        };

        Assert.Equal(2, RetainerUiAutomationText.FindRetainerListIndex(rows, "gamma"));
        Assert.Null(RetainerUiAutomationText.FindRetainerListIndex(rows, "Beta"));
    }
}
