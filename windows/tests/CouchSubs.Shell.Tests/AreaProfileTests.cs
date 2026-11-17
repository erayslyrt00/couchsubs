// SPDX-License-Identifier: AGPL-3.0-only

using CouchSubs.Core;
using CouchSubs.Core.LgTv;
using CouchSubs.RemotePlay;
using CouchSubs.Shell;
using Xunit;

namespace CouchSubs.Shell.Tests;

/// <summary>Oyuna göre alanlar: kaydetme, oyun seçilince uygulama, geri alma ve oturum sonunda genel alanlara dönüş.</summary>
public class AreaProfileTests
{
    private static readonly CropRegion General = new(0.62, 0.97, 0.14, 0.86);
    private static readonly CropRegion FarCryArea = new(0.70, 0.95, 0.20, 0.80);
    private static readonly CropRegion Quest = new(0.03, 0.30, 0.66, 0.98);

    private static (AppController Controller, MemorySettings Settings) Ready(UserSettings? saved = null)
    {
        var store = new MemoryStore();
        store.Save(StoreKeys.AccountId, Enumerable.Repeat((byte)1, 8).ToArray());
        store.Save(StoreKeys.Console, new RegisteredConsole("192.168.1.253", "PS5", Enumerable.Repeat((byte)0x31, 16).ToArray(), Enumerable.Repeat((byte)2, 16).ToArray()));
        store.Save(StoreKeys.Tv, new LgTv("LG TV", "127.0.0.1"));
        store.Save(StoreKeys.TvKey, "tv-key");
        var settings = new MemorySettings();
        if (saved is not null) settings.Save(saved);
        return (AppControllerTests.Make(store: store, settings: settings), settings);
    }

    /// <summary>Far Cry 3 için alanları kaydeder ve oturumu bitirir: genel alan <see cref="General"/>, profil <see cref="FarCryArea"/> + görev alanı.</summary>
    private static async Task<(AppController Controller, MemorySettings Settings)> WithFarCryProfile()
    {
        var (controller, settings) = Ready();
        await controller.StartAsync();
        controller.ChooseGame("Far Cry 3");
        controller.Settings = controller.Settings.WithCustomRegion(FarCryArea).WithExtraRegion(1, Quest).WithExtraLive(1, true);
        controller.SaveAreaProfile();
        controller.Stop();
        return (controller, settings);
    }

    [Fact]
    public async Task AreasCanBeSavedOnlyWhileTranslatingAGameThatIsKnown()
    {
        var (controller, _) = Ready();
        Assert.Null(controller.AreaProfileTarget);
        controller.SaveAreaProfile();
        Assert.Empty(controller.Settings.AreaProfiles);

        await controller.StartAsync();
        Assert.Null(controller.AreaProfileTarget);
        controller.ChooseGame("Far Cry 3");
        Assert.Equal("Far Cry 3", controller.AreaProfileTarget);
        controller.SaveAreaProfile();
        var profile = Assert.Single(controller.Settings.AreaProfiles);
        Assert.Equal("far cry 3|stream", profile.Key);
        Assert.Equal("Far Cry 3", controller.AreaProfileGame);
        Assert.False(controller.AreaProfileUnsaved);
    }

    [Fact]
    public async Task TheSessionEndsWithTheGeneralAreasAndTheGamesAreasComeBackWithTheGame()
    {
        var (controller, settings) = await WithFarCryProfile();
        // Profil yokken yapılan değişiklik genel ayardı; kaydedilince profil o alanlarla oluşur, oturum bitince genel ayar kalır.
        Assert.Null(controller.AreaProfileGame);
        Assert.Null(controller.Settings.GeneralAreas);
        Assert.Equal(FarCryArea, controller.Settings.Region);

        controller.Settings = controller.Settings.WithCustomRegion(General).WithExtraRegion(1, null);
        // Elle seçilen oyun uygulama açık kaldıkça hatırlanır: yeni oturum başlar başlamaz o oyunun alanları gelir.
        await controller.StartAsync();
        Assert.Equal("Far Cry 3", controller.AreaProfileGame);
        controller.ChooseGame("Far Cry III");
        Assert.Equal("Far Cry 3", controller.AreaProfileGame);
        Assert.Equal(FarCryArea, controller.Settings.Region);
        Assert.Equal(Quest, controller.Settings.ExtraRegion1);
        Assert.True(controller.Settings.ExtraLive1);
        Assert.Single(controller.Settings.WatchedAreas());
        Assert.Equal(General, settings.Saved?.GeneralAreas?.Region);

        controller.Stop();
        Assert.Null(controller.AreaProfileGame);
        Assert.Equal(General, controller.Settings.Region);
        Assert.Null(controller.Settings.ExtraRegion1);
        Assert.Null(settings.Saved?.ActiveAreaProfile);
    }

    [Fact]
    public async Task ChangesInAProfileAreKeptOnlyWhenSaved()
    {
        var (controller, _) = await WithFarCryProfile();
        controller.Settings = controller.Settings.WithCustomRegion(General);
        await controller.StartAsync();
        controller.ChooseGame("Far Cry 3");
        var moved = new CropRegion(0.50, 0.80, 0.20, 0.80);
        controller.Settings = controller.Settings.WithCustomRegion(moved);
        Assert.True(controller.AreaProfileUnsaved);
        controller.Stop();
        Assert.Equal(General, controller.Settings.Region);

        await controller.StartAsync();
        controller.ChooseGame("Far Cry 3");
        Assert.Equal(FarCryArea, controller.Settings.Region);
        controller.Settings = controller.Settings.WithCustomRegion(moved);
        controller.SaveAreaProfile();
        Assert.False(controller.AreaProfileUnsaved);
        controller.Stop();
        Assert.Equal(General, controller.Settings.Region);
        Assert.Equal(moved, Assert.Single(controller.Settings.AreaProfiles).Areas.Region);
    }

    [Fact]
    public async Task UndoKeepsTheGeneralAreasForTheRestOfTheSession()
    {
        var (controller, _) = await WithFarCryProfile();
        controller.Settings = controller.Settings.WithCustomRegion(General);
        await controller.StartAsync();
        controller.ChooseGame("Far Cry 3");
        controller.UndoAreaProfile();
        Assert.Null(controller.AreaProfileGame);
        Assert.Equal(General, controller.Settings.Region);
        controller.ChooseGame("Far Cry 3");
        Assert.Equal(General, controller.Settings.Region);

        // Yeni oturumda yeniden uygulanır.
        controller.Stop();
        await controller.StartAsync();
        controller.ChooseGame("Far Cry 3");
        Assert.Equal(FarCryArea, controller.Settings.Region);
    }

    [Fact]
    public async Task AnotherGameWithoutAProfileGoesBackToTheGeneralAreas()
    {
        var (controller, _) = await WithFarCryProfile();
        controller.Settings = controller.Settings.WithCustomRegion(General);
        await controller.StartAsync();
        controller.ChooseGame("Far Cry 3");
        controller.ChooseGame("Hades");
        Assert.Null(controller.AreaProfileGame);
        Assert.Equal(General, controller.Settings.Region);
        Assert.Equal("Hades", controller.AreaProfileTarget);
    }

    [Fact]
    public async Task DeletingTheProfileGoesBackToTheGeneralAreas()
    {
        var (controller, _) = await WithFarCryProfile();
        controller.Settings = controller.Settings.WithCustomRegion(General);
        await controller.StartAsync();
        controller.ChooseGame("Far Cry 3");
        controller.DeleteAreaProfile();
        Assert.Empty(controller.Settings.AreaProfiles);
        Assert.Null(controller.AreaProfileGame);
        Assert.Equal(General, controller.Settings.Region);
    }

    [Fact]
    public void AProfileLeftOpenByACrashIsClosedAtStartup()
    {
        var general = new UserSettings().WithCustomRegion(General).Areas;
        var profile = new AreaProfile("far cry 3|stream", "Far Cry 3", general with { Region = FarCryArea });
        var saved = new UserSettings().WithAreas(profile.Areas) with
        {
            AreaProfiles = [profile], ActiveAreaProfile = profile.Key, GeneralAreas = general,
        };
        var (controller, settings) = Ready(saved);
        Assert.Equal(General, controller.Settings.Region);
        Assert.Null(controller.Settings.ActiveAreaProfile);
        Assert.Null(controller.Settings.GeneralAreas);
        Assert.Single(controller.Settings.AreaProfiles);
        Assert.Null(settings.Saved?.GeneralAreas);
    }

    [Fact]
    public void OldSettingsFilesHaveNoProfiles()
    {
        var old = System.Text.Json.JsonSerializer.Deserialize<UserSettings>("""{"Region":{"Top":0.5,"Bottom":0.9,"Left":0.1,"Right":0.9}}""")!;
        Assert.Empty(old.AreaProfiles);
        Assert.Null(old.ActiveAreaProfile);
        var copy = System.Text.Json.JsonSerializer.Deserialize<UserSettings>(System.Text.Json.JsonSerializer.Serialize(
            old.WithSavedAreaProfile("Hades")))!;
        Assert.Equal("Hades", Assert.Single(copy.AreaProfiles).Game);
        Assert.Equal(old.Region, copy.AreaProfiles[0].Areas.Region);
    }
}
