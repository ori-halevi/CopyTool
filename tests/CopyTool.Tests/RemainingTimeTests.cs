using CopyTool.Host.Ui;
using Xunit;

namespace CopyTool.Tests;

/// <summary>
/// The "time left" wording.
///
/// It used to read <c>left.Hours</c>, which is 0-23, so a copy with thirty hours
/// to go announced six — the one number a person walking away from a transfer
/// actually acts on. And it composed every quantity the same way, which in Hebrew
/// produces "נותרו 1 שעות": the verb has to agree with the noun, and the dual
/// form exists.
/// </summary>
public class RemainingTimeTests
{
    [Theory]
    // seconds
    [InlineData(0, "נותרה שנייה")]              // still running, so never "0 שניות"
    [InlineData(1, "נותרה שנייה")]
    [InlineData(2, "נותרו שתי שניות")]
    [InlineData(45, "נותרו 45 שניות")]
    // minutes
    [InlineData(60, "נותרה דקה")]
    [InlineData(120, "נותרו שתי דקות")]
    [InlineData(12 * 60, "נותרו 12 דקות")]
    // hours, with the minutes joined on
    [InlineData(3600, "נותרה שעה")]
    [InlineData(3600 + 12 * 60, "נותרה שעה ו-12 דקות")]
    [InlineData(2 * 3600, "נותרו שעתיים")]
    [InlineData(5 * 3600 + 30 * 60, "נותרו 5 שעות ו-30 דקות")]
    // days — the case the old code could not express at all
    [InlineData(24 * 3600, "נותר יום")]
    [InlineData(2 * 24 * 3600, "נותרו יומיים")]
    [InlineData(30 * 3600, "נותר יום ו-6 שעות")]
    [InlineData(3 * 24 * 3600 + 4 * 3600, "נותרו 3 ימים ו-4 שעות")]
    [InlineData(2 * 24 * 3600 + 2 * 3600, "נותרו יומיים ושעתיים")]
    public void Durations_read_as_Hebrew(int seconds, string expected) =>
        Assert.Equal(expected, Text.Remaining(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void A_span_longer_than_a_day_still_names_the_day()
    {
        // The regression, stated on its own so it cannot be lost in a table.
        // TimeSpan.Hours wraps at 24, so thirty hours used to render as
        // "נותרו 6 שעות ו-0 דקות" — the six is real, the missing day is the bug.
        Assert.Equal("נותר יום ו-6 שעות", Text.Remaining(TimeSpan.FromHours(30)));
    }

    [Fact]
    public void A_negative_span_does_not_produce_nonsense()
    {
        // Progress can overshoot for a moment when a retry gives bytes back.
        Assert.Equal("נותרה שנייה", Text.Remaining(TimeSpan.FromSeconds(-5)));
    }

    [Theory]
    [InlineData(0, "0 פריטים")]
    [InlineData(1, "פריט אחד")]
    [InlineData(2, "שני פריטים")]
    [InlineData(3, "3 פריטים")]
    [InlineData(60000, "60,000 פריטים")]
    public void Item_counts_agree_too(int n, string expected) =>
        Assert.Equal(expected, Text.Items(n));

    [Theory]
    [InlineData(2 * 24 * 3600 + 2 * 3600)]   // יומיים ושעתיים
    [InlineData(3 * 24 * 3600 + 4 * 3600)]   // 3 ימים ו-4 שעות
    public void The_conjunction_takes_a_hyphen_only_before_a_numeral(int seconds)
    {
        // "3 ימים ו-4 שעות" but "יומיים ושעתיים" — ו attaches straight to a word.
        string text = Text.Remaining(TimeSpan.FromSeconds(seconds));
        int hyphen = text.IndexOf("ו-", StringComparison.Ordinal);

        if (hyphen >= 0) Assert.True(char.IsDigit(text[hyphen + 2]), text);
    }
}
