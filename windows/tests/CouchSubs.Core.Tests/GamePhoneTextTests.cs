// SPDX-License-Identifier: AGPL-3.0-only

using CouchSubs.Core;

namespace CouchSubs.Core.Tests;

public class GamePhoneTextTests
{
    private static TextLine Line(string text, double y, double left = 0.20, double right = 0.80) =>
        new(text, new TextBox(left, y, right, y + 0.031));

    [Fact]
    public void SocialCaptionStaysAboveOriginalTags()
    {
        var words = new TextWord[] {
            new("Meet", new(0.12, 0.68, 0.24, 0.71)),
            new("me", new(0.26, 0.68, 0.34, 0.71)),
            new("#NightRide", new(0.38, 0.68, 0.92, 0.71)) };
        var reading = new Reading([Line("@vice.305", 0.63), new("Meet me #NightRide", new(0.12, 0.68, 0.92, 0.71), Words: words),
            Line("#sunset #leonida", 0.73), Line("Tell @vice.305 to wait.", 0.80)]);
        var result = GamePhoneText.ForTranslation(reading);
        var groups = GamePhoneText.Groups(result);
        Assert.Equal(["Meet me #NightRide", "Tell @vice.305 to wait."], groups.SelectMany(g => g.Paragraphs));
        Assert.Equal(words[^1].Box.Right, result.Lines[0].Box!.Value.Right);
    }

    [Fact]
    public void MessageStillRefersToTheSameAccountAfterTranslation()
    {
        const string source = "Send the photo to @KaterinaDiva.";
        Assert.Equal("Fotoğrafı @KaterinaDiva'ya gönder.", GamePhoneText.KeepIdentifiers(source, "Fotoğrafı @katerinadiva'ya gönder."));
        Assert.Null(GamePhoneText.KeepIdentifiers(source, "Fotoğrafı ona gönder."));
        Assert.Null(GamePhoneText.KeepIdentifiers(source, "Fotoğrafı @Joey'e gönder."));
        var candidate = RegionWatcher.MakeCandidate(new Reading([Line(source, 0.55)]), new CropRegion(), phone: true);
        Assert.Equal([source], candidate.Paragraphs);
    }

    [Fact]
    public void TextingShorthandSurvivesOcrCleanupAndReachesTranslation()
    {
        const string source = "We're in this 2gether. I left it 4u.";
        var candidate = RegionWatcher.MakeCandidate(new Reading([Line(source, 0.5)]), new CropRegion(), phone: true);
        Assert.Equal([source], candidate.Paragraphs);
        Assert.Equal("We're in this together. I left it for you.", GamePhoneText.EnglishModelInput(candidate.Paragraphs[0]));
        const string identifiers = "Use B4, @4u, #2gether or gate C4.";
        Assert.Equal(identifiers, GamePhoneText.EnglishModelInput(identifiers));
        Assert.Equal("10 min out.", GamePhoneText.EnglishModelInput("IO min out."));
        Assert.Equal("IO port. I owe him.", GamePhoneText.EnglishModelInput("IO port. I owe him."));
    }

    [Fact]
    public void ContactsStayOnScreenAndIncompletePreviewsAreKeptAsRead()
    {
        var rows = new Reading([Line("Suggestions", 0.04), Line("Chase", 0.12), Line("call me after ...", 0.16),
            Line("May", 0.25), Line("Don't leave yet.", 0.29), Line("Messages", 0.42),
            Line("Rose", 0.54), Line("Meet me at 06:40.", 0.58)]);
        var kept = GamePhoneText.ForTranslation(rows);
        Assert.Equal(["Suggestions", "call me after ...", "Don't leave yet.", "Messages", "Meet me at 06:40."],
            kept.Lines.Select(l => l.Text));
        Assert.DoesNotContain(GamePhoneText.Groups(kept), g => g.Paragraphs.Count > 1);
        var chat = GamePhoneText.ForTranslation(new Reading(rows.Lines.Skip(1)));
        Assert.Contains(chat.Lines, l => l.Text == "May");
    }

    [Fact]
    public void OpposingRepliesStaySeparateAndWrappedTextStaysTogether()
    {
        var reading = new Reading([Line("Find someone who", 0.30, 0.10, 0.69),
            Line("knows this road.", 0.334, 0.10, 0.64), Line("on my way", 0.372, 0.38, 0.93)]);
        var groups = GamePhoneText.Groups(reading);
        Assert.Equal(["Find someone who knows this road.", "on my way"], groups.SelectMany(g => g.Paragraphs));
        Assert.Equal(2, groups.Count);
    }
}
