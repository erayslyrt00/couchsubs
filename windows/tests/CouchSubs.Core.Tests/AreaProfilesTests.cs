// SPDX-License-Identifier: AGPL-3.0-only

using CouchSubs.Core;
using Xunit;
using static CouchSubs.Core.AreaProfiles;
using Source = CouchSubs.Core.GameSense.GameSource;

namespace CouchSubs.Core.Tests;

/// <summary>Oyuna göre alan profilinin ne zaman uygulandığı (Mac/iPhone AreaProfilesTests.swift ve Android ile aynı örnekler).</summary>
public class AreaProfilesTests
{
    private const string FarCry = "far cry 3|stream";

    private static Decision Run(string? active, string? game, Source? source, AreaSource area = AreaSource.Stream,
                                string[]? saved = null, string[]? declined = null) =>
        Decide(active, game, source, area, key => (saved ?? [FarCry]).Contains(key), key => (declined ?? []).Contains(key));

    [Fact]
    public void KeyUsesTheServersGameKeyAndTheSource()
    {
        Assert.Equal("gta 6|stream", Key("Grand Theft Auto VI", AreaSource.Stream));
        Assert.Equal("gta 6|window", Key("GTA 6", AreaSource.Window));
        Assert.Null(Key("", AreaSource.Stream));
        Assert.Null(Key(null, AreaSource.Stream));
    }

    [Theory]
    [InlineData(Source.Console)]
    [InlineData(Source.User)]
    public void ACertainGameAppliesItsProfileAtOnce(Source source) =>
        Assert.Equal(new Decision(Step.Apply, FarCry), Run(null, "Far Cry 3", source));

    [Fact]
    public void AGameRecognisedOnlyFromPacksIsOfferedNotApplied() =>
        Assert.Equal(new Decision(Step.Offer, FarCry), Run(null, "Far Cry 3", Source.Pack));

    [Fact]
    public void TheAppliedProfileIsNotAppliedAgain() =>
        Assert.Equal(Step.None, Run(FarCry, "Far Cry III", Source.Console).Step);

    [Fact]
    public void AnUnknownGameKeepsTheLastProfile()
    {
        Assert.Equal(Step.None, Run(FarCry, null, null).Step);
        Assert.Equal(Step.None, Run(null, null, null).Step);
    }

    [Fact]
    public void AnotherGameWithoutAProfileGoesBackToTheGeneralAreas()
    {
        Assert.Equal(Step.UseGeneral, Run(FarCry, "Hades", Source.Console).Step);
        // Genel alanlardayken profilsiz oyunda hiçbir şey değişmez.
        Assert.Equal(Step.None, Run(null, "Hades", Source.Console).Step);
    }

    [Fact]
    public void AnotherGameWithAProfileSwitchesToIt() =>
        Assert.Equal(new Decision(Step.Apply, "hades|stream"),
            Run(FarCry, "Hades", Source.User, saved: [FarCry, "hades|stream"]));

    [Fact]
    public void ADeclinedProfileIsNotAppliedOrOfferedAgain()
    {
        Assert.Equal(Step.None, Run(null, "Far Cry 3", Source.Console, declined: [FarCry]).Step);
        Assert.Equal(Step.None, Run(null, "Far Cry 3", Source.Pack, declined: [FarCry]).Step);
        Assert.Equal(Step.UseGeneral, Run("hades|stream", "Far Cry 3", Source.Console, declined: [FarCry]).Step);
    }

    [Fact]
    public void TheWindowSourceHasItsOwnProfile()
    {
        Assert.Equal(Step.None, Run(null, "Far Cry 3", Source.Console, AreaSource.Window).Step);
        Assert.Equal(new Decision(Step.Apply, "far cry 3|window"),
            Run(null, "Far Cry 3", Source.Console, AreaSource.Window, saved: [FarCry, "far cry 3|window"]));
    }

    [Fact]
    public void SavingMovesTheProfileToTheEndAndKeepsTwenty()
    {
        IReadOnlyList<string> list = ["a", "b", "c"];
        Assert.Equal(["a", "c", "b"], Saved(list, "b", item => item));
        IReadOnlyList<string> full = [.. Enumerable.Range(0, MaxCount).Select(i => "g" + i)];
        var next = Saved(full, "new", item => item);
        Assert.Equal(MaxCount, next.Count);
        Assert.Equal("g1", next[0]);
        Assert.Equal("new", next[^1]);
    }
}
