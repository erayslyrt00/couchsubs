// SPDX-License-Identifier: AGPL-3.0-only

using CouchSubs.Core;

namespace CouchSubs.Core.Tests;

/// <summary>Ek alanlar: ana altyazıdan ayrı, seyrek okunur; yazı kararlıysa ve değiştiyse çevrilir (Mac: RegionWatcherTests.swift).</summary>
public class RegionWatcherTests
{
    private sealed class Reader : ITextReader
    {
        public List<TextLine> Lines { get; set; } = [];
        public int Count;
        public int FailuresRemaining;
        public Action? OnRead;
        public string Read(Frame image) => string.Join('\n', Lines.Select(line => line.Text));
        public Reading ReadLines(Frame image, bool deep)
        {
            Interlocked.Increment(ref Count);
            OnRead?.Invoke();
            if (FailuresRemaining > 0)
            {
                FailuresRemaining--;
                throw new InvalidOperationException("Expected OCR failure");
            }
            return new Reading(Lines);
        }
    }

    private sealed class Translator : ITextTranslator
    {
        public List<string> Calls { get; } = [];
        public int FailuresRemaining;
        public Task<string> TranslateAsync(string text, CancellationToken cancellationToken = default)
        {
            lock (Calls)
            {
                Calls.Add(text);
                if (FailuresRemaining > 0)
                {
                    FailuresRemaining--;
                    return Task.FromException<string>(new InvalidOperationException("Expected test failure"));
                }
            }
            return Respond?.Invoke(text) ?? Task.FromResult(Map(text));
        }
        public Func<string, Task<string>>? Respond { get; set; }
        public Func<string, string> Map { get; set; } = text => "TR: " + text;
    }

    private sealed class Server : IRegionSender
    {
        public List<(string Id, string Text, TextBox? Box, double? LineHeight)> Sent { get; } = [];
        /// <summary>Her gönderimin blokları (<see cref="Sent"/> ile aynı sırada; bloksuz gönderimde null).</summary>
        public List<IReadOnlyList<ScreenTranslation.Item>?> Blocks { get; } = [];
        public int Clears;
        public void SendRegion(string id, string text, TextBox? box, double? lineHeight = null) =>
            SendRegion(id, text, box, lineHeight, null, null);
        public void SendRegion(string id, string text, TextBox? box, double? lineHeight, IReadOnlyList<IconPlacement>? icons,
                               IReadOnlyList<ScreenTranslation.Item>? blocks)
        {
            lock (Sent)
            {
                Sent.Add((id, text, box, lineHeight));
                Blocks.Add(blocks);
            }
        }
        public void ClearRegions() => Interlocked.Increment(ref Clears);
    }

    /// <summary>Düz gri kare; <paramref name="shade"/> değişince bölge izi değişir.</summary>
    private static Frame Shade(byte shade)
    {
        var frame = Frame.Blank(320, 180);
        Array.Fill(frame.Pixels, shade);
        return frame;
    }

    private static readonly WatchedArea Quest = new("q", "Quest", ExtraAreaPreset.Quest.Region());
    private static readonly WatchedArea Phone = new("p", "Phone", ExtraAreaPreset.Phone.Region());

    private static TextLine Line(string text, double top = 0.2, double height = 0.1) => new(text, new TextBox(0.1, top, 0.9, top + height));

    private static readonly WatchedArea Hud = new("hud", "HUD", new CropRegion(0, 1, 0, 1));

    // Beş piksel boyundaki etiket, tüm ekranı örnekleyen iki satırın arasına düşer.
    private static Frame HudFrame(int letters)
    {
        var frame = Frame.Blank(320, 180);
        for (var y = 38; y < 43; y++)
            for (var x = 33; x < 33 + 5 * letters; x++)
                if (x % 5 < 3)
                    Array.Fill(frame.Pixels, (byte)245, y * frame.Stride + x * 4, 3);
        return frame;
    }

    private static async Task WaitForPass(RegionWatcher watcher)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (watcher.IsBusy) await Task.Delay(5, timeout.Token);
    }

    [Fact]
    public async Task SmallChangingMenuLabelIsReadEvenWhenTheAreaSignatureMatches()
    {
        var greet = HudFrame(5);
        var antagonize = HudFrame(10);
        Assert.Equal(RegionCropper.Signature(greet), RegionCropper.Signature(antagonize));
        var reader = new Reader { Lines = [Line("Greet", 0.2, 0.05)] };
        var server = new Server();
        long clock = 0;
        var watcher = new RegionWatcher(reader, new Translator(), server, clock: () => clock);
        await watcher.PassAsync(greet, [Hud]);
        await watcher.PassAsync(greet, [Hud]);
        reader.Lines = [Line("Antagonize", 0.2, 0.05)];
        await watcher.PassAsync(antagonize, [Hud]);
        await watcher.PassAsync(antagonize, [Hud]);
        clock += 6000;
        await watcher.PassAsync(antagonize, [Hud]);
        Assert.Equal(2, reader.Count);
        Assert.Equal(["TR: Greet", "TR: Antagonize"], server.Sent.Select(message => message.Text));
    }

    [Fact]
    public async Task NewTextOutsideEarlierBoxesIsEventuallyReadOnASparseSignatureCollision()
    {
        long clock = 0;
        var reader = new Reader();
        var server = new Server();
        var watcher = new RegionWatcher(reader, new Translator(), server, clock: () => clock);
        await watcher.PassAsync(HudFrame(0), [Hud]);
        await watcher.PassAsync(HudFrame(0), [Hud]);
        clock += 6000;
        reader.Lines = [Line("Antagonize", 0.2, 0.05)];
        await watcher.PassAsync(HudFrame(10), [Hud]);
        await watcher.PassAsync(HudFrame(10), [Hud]);
        Assert.Equal(2, reader.Count);
        Assert.Equal("TR: Antagonize", Assert.Single(server.Sent).Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InFlightTranslationOnlyPublishesIfTheSourceStillMatches(bool newCommand)
    {
        var greet = HudFrame(5);
        var reader = new Reader { Lines = [Line("Greet", 0.2, 0.05)] };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var translator = new Translator { Respond = text =>
        {
            if (text != "Greet") return Task.FromResult("Kışkırt");
            entered.TrySetResult();
            return release.Task;
        } };
        var server = new Server();
        var watcher = new RegionWatcher(reader, translator, server, intervalMs: 0);
        watcher.Offer(greet, [Hud]);
        await WaitForPass(watcher);
        watcher.Offer(greet, [Hud]);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var next = HudFrame(newCommand ? 10 : 5);
        // Başka bölgede de hareket var. O tek başına değişmeyen komutun çevirisini düşürmemeli.
        Array.Fill(next.Pixels, (byte)200, 0, next.Stride);
        if (newCommand) reader.Lines = [Line("Antagonize", 0.2, 0.05)];
        watcher.Offer(next, [Hud]);
        release.SetResult("Selam ver");
        await WaitForPass(watcher);
        watcher.Offer(next, [Hud]);
        await WaitForPass(watcher);
        Assert.Equal(newCommand ? "Kışkırt" : "Selam ver", Assert.Single(server.Sent).Text);
    }

    [Fact]
    public async Task AConfirmedNewCommandClearsOnlyItsOldTranslationWhileWaiting()
    {
        var reader = new Reader { Lines = [Line("Greet", 0.2, 0.05)] };
        var translator = new Translator();
        var server = new Server();
        var watcher = new RegionWatcher(reader, translator, server);
        await watcher.PassAsync(HudFrame(5), [Hud]);
        await watcher.PassAsync(HudFrame(5), [Hud]);
        reader.Lines = [Line("Antagonize", 0.2, 0.05)];
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        translator.Respond = _ => release.Task;
        await watcher.PassAsync(HudFrame(10), [Hud]);
        var pending = watcher.PassAsync(HudFrame(10), [Hud]);
        Assert.Equal(["TR: Greet", ""], server.Sent.Select(message => message.Text));
        Assert.All(server.Sent, message => Assert.Equal(Hud.Id, message.Id));
        release.SetResult("Kışkırt");
        await pending;
        Assert.Equal("Kışkırt", server.Sent.Last().Text);
    }

    [Fact]
    public async Task CommandChangedDuringTheFinalReadDoesNotPublishTheOldResponse()
    {
        var greet = HudFrame(5);
        var next = HudFrame(10);
        var reader = new Reader { Lines = [Line("Greet", 0.2, 0.05)] };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var translator = new Translator { Respond = text =>
        {
            if (text != "Greet") return Task.FromResult("Kışkırt");
            entered.TrySetResult();
            return release.Task;
        } };
        var server = new Server();
        var watcher = new RegionWatcher(reader, translator, server, intervalMs: 0);
        watcher.Offer(greet, [Hud]);
        await WaitForPass(watcher);
        watcher.Offer(greet, [Hud]);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var sameWithMotion = HudFrame(5);
        Array.Fill(sameWithMotion.Pixels, (byte)200, 0, sameWithMotion.Stride);
        watcher.Offer(sameWithMotion, [Hud]);
        reader.OnRead = () => watcher.Offer(next, [Hud]);
        release.SetResult("Selam ver");
        await WaitForPass(watcher);
        Assert.Empty(server.Sent);
        reader.OnRead = null;
        reader.Lines = [Line("Antagonize", 0.2, 0.05)];
        watcher.Offer(next, [Hud]);
        await WaitForPass(watcher);
        watcher.Offer(next, [Hud]);
        await WaitForPass(watcher);
        Assert.Equal("Kışkırt", Assert.Single(server.Sent).Text);
    }

    [Fact]
    public async Task FailedReadRetriesOnAnUnchangedFrameThenStopsReading()
    {
        var reader = new Reader { Lines = [Line("Find the blacksmith")], FailuresRemaining = 1 };
        var translator = new Translator();
        var server = new Server();
        var watcher = new RegionWatcher(reader, translator, server);
        await watcher.PassAsync(Shade(10), [Quest]);
        await watcher.PassAsync(Shade(10), [Quest]);
        Assert.Empty(server.Sent); // Başarılı yeni okuma yine kararlılık bekler.
        for (var i = 0; i < 4; i++) await watcher.PassAsync(Shade(10), [Quest]);
        Assert.Equal(2, reader.Count);
        Assert.Equal(["Find the blacksmith"], translator.Calls);
        Assert.Equal(["TR: Find the blacksmith"], server.Sent.Select(s => s.Text));
    }

    [Fact]
    public async Task FailedReadKeepsShownTextAndDiscardsUnconfirmedReading()
    {
        var reader = new Reader { Lines = [Line("Talk to Arthur")] };
        var translator = new Translator();
        var server = new Server();
        var watcher = new RegionWatcher(reader, translator, server);
        await watcher.PassAsync(Shade(10), [Quest]);
        await watcher.PassAsync(Shade(10), [Quest]);
        reader.Lines = [Line("Meet Arthur at the barn")];
        await watcher.PassAsync(Shade(60), [Quest]);
        reader.FailuresRemaining = 1;
        await watcher.PassAsync(Shade(100), [Quest]);
        reader.Lines = [Line("Find the blacksmith")];
        await watcher.PassAsync(Shade(100), [Quest]);
        Assert.Equal(["TR: Talk to Arthur"], server.Sent.Select(s => s.Text));
        await watcher.PassAsync(Shade(100), [Quest]);
        Assert.Equal(["Talk to Arthur", "Find the blacksmith"], translator.Calls);
        Assert.Equal(["TR: Talk to Arthur", "TR: Find the blacksmith"], server.Sent.Select(s => s.Text));
    }

    [Fact]
    public async Task FailedReadDoesNotBlockAnotherArea()
    {
        var reader = new Reader { Lines = [Line("Find the blacksmith")], FailuresRemaining = 1 };
        var server = new Server();
        var watcher = new RegionWatcher(reader, new Translator(), server);
        await watcher.PassAsync(Shade(10), [Quest, Phone]);
        await watcher.PassAsync(Shade(10), [Quest, Phone]);
        Assert.Equal(["p"], server.Sent.Select(s => s.Id));
        await watcher.PassAsync(Shade(10), [Quest, Phone]);
        Assert.Equal(["p", "q"], server.Sent.Select(s => s.Id));
        Assert.Equal(3, reader.Count);
    }

    [Fact]
    public async Task RepeatedReadFailuresStillRespectThePassInterval()
    {
        long clock = 0;
        var reader = new Reader { FailuresRemaining = 10 };
        var server = new Server();
        var watcher = new RegionWatcher(reader, new Translator(), server, clock: () => clock);
        for (var step = 0; step <= 20; step++)
        {
            clock = step * RegionWatcher.DefaultIntervalMs / 10;
            watcher.Offer(Shade(10), [Quest]);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (watcher.IsBusy) await Task.Delay(5, timeout.Token);
        }
        Assert.Equal(3, watcher.Passes);
        Assert.Equal(3, reader.Count);
        Assert.Empty(server.Sent);
    }

    [Fact]
    public async Task StableTextIsTranslatedOnceAndPlacedInItsArea()
    {
        var (reader, translator, server) = (new Reader(), new Translator(), new Server());
        var watcher = new RegionWatcher(reader, translator, server);
        reader.Lines = [Line("Find the blacksmith")];
        await watcher.PassAsync(Shade(10), [Quest]);
        Assert.Empty(server.Sent);
        await watcher.PassAsync(Shade(10), [Quest]);
        var sent = Assert.Single(server.Sent);
        Assert.Equal("TR: Find the blacksmith", sent.Text);
        Assert.True(sent.Box!.Value.Left > Quest.Region.Left && sent.Box.Value.Right < Quest.Region.Right);
        await watcher.PassAsync(Shade(10), [Quest]);
        await watcher.PassAsync(Shade(10), [Quest]);
        Assert.Equal(1, reader.Count);
        Assert.Single(translator.Calls);
        Assert.Single(server.Sent);
    }

    /// <summary>Ekranda zaten Türkçe olan yazı (bozuk okunmuş olsa da) çevrilmez, kutu gösterilmez (#289).</summary>
    [Fact]
    public async Task TurkishTextIsNotTranslated()
    {
        var reader = new Reader { Lines = [Line("Sistem Yazillml Güncellem€")] };
        var translator = new Translator();
        var server = new Server();
        var watcher = new RegionWatcher(reader, translator, server);
        for (var i = 0; i < 3; i++) await watcher.PassAsync(Shade(10), [Quest]);
        Assert.Empty(translator.Calls);
        Assert.All(server.Sent, sent => Assert.Equal("", sent.Text));
    }

    [Fact]
    public async Task EnglishTextWithTurkishNameIsTranslated()
    {
        var reader = new Reader { Lines = [Line("Atatürk said no.")] };
        var translator = new Translator();
        var server = new Server();
        var watcher = new RegionWatcher(reader, translator, server);
        for (var i = 0; i < 3; i++) await watcher.PassAsync(Shade(10), [Quest]);
        Assert.Equal(["Atatürk said no."], translator.Calls);
        Assert.Equal(["TR: Atatürk said no."], server.Sent.Select(s => s.Text));
    }

    [Fact]
    public async Task SameTextAtNewGeometryUpdatesRegionWithoutRetranslating()
    {
        var reader = new Reader { Lines = [Line("Find the blacksmith", 0.2, 0.1)] };
        var translator = new Translator();
        var server = new Server();
        var watcher = new RegionWatcher(reader, translator, server);
        await watcher.PassAsync(Shade(10), [Quest]);
        await watcher.PassAsync(Shade(10), [Quest]);
        var first = server.Sent[0];

        reader.Lines = [Line("Find the blacksmith", 0.45, 0.2)];
        await watcher.PassAsync(Shade(60), [Quest]);

        Assert.Equal(["TR: Find the blacksmith", "TR: Find the blacksmith"], server.Sent.Select(s => s.Text));
        Assert.NotEqual(first.Box?.Top, server.Sent[1].Box?.Top);
        Assert.NotEqual(first.LineHeight, server.Sent[1].LineHeight);
        Assert.Equal(["Find the blacksmith"], translator.Calls);
    }

    [Fact]
    public async Task FailedTranslationRetriesWithoutLosingPreviouslyShownText()
    {
        var (reader, translator, server) = (new Reader(), new Translator(), new Server());
        var watcher = new RegionWatcher(reader, translator, server);
        reader.Lines = [Line("Talk to Arthur")];
        await watcher.PassAsync(Shade(10), [Quest]);
        await watcher.PassAsync(Shade(10), [Quest]);
        Assert.Equal(["TR: Talk to Arthur"], server.Sent.Select(s => s.Text));

        reader.Lines = [Line("Meet Arthur at the barn")];
        translator.FailuresRemaining = 1;
        await watcher.PassAsync(Shade(90), [Quest]);
        await watcher.PassAsync(Shade(90), [Quest]);
        Assert.Equal(["TR: Talk to Arthur"], server.Sent.Select(s => s.Text));

        await watcher.PassAsync(Shade(90), [Quest]);
        Assert.Equal(["Talk to Arthur", "Meet Arthur at the barn", "Meet Arthur at the barn"], translator.Calls);
        Assert.Equal(["TR: Talk to Arthur", "TR: Meet Arthur at the barn"], server.Sent.Select(s => s.Text));
    }

    [Fact]
    public async Task MovingBackgroundWithTheSameTextIsNotTranslatedAgain()
    {
        var (reader, translator, server) = (new Reader(), new Translator(), new Server());
        var watcher = new RegionWatcher(reader, translator, server);
        reader.Lines = [Line("Talk to Arthur")];
        foreach (var shade in new byte[] { 10, 60, 110, 160, 210 }) await watcher.PassAsync(Shade(shade), [Quest]);
        Assert.Equal(5, reader.Count);
        Assert.Equal(["Talk to Arthur"], translator.Calls);
        Assert.Equal(["TR: Talk to Arthur"], server.Sent.Select(s => s.Text));
    }

    [Fact]
    public async Task ChangedTextIsTranslatedAndEmptyAreaRemovesTheBox()
    {
        var (reader, server) = (new Reader(), new Server());
        var watcher = new RegionWatcher(reader, new Translator(), server);
        reader.Lines = [Line("Talk to Arthur")];
        await watcher.PassAsync(Shade(10), [Quest]);
        await watcher.PassAsync(Shade(10), [Quest]);
        reader.Lines = [Line("Meet Arthur at the barn")];
        await watcher.PassAsync(Shade(90), [Quest]);
        await watcher.PassAsync(Shade(90), [Quest]);
        reader.Lines = [];
        await watcher.PassAsync(Shade(200), [Quest]);
        await watcher.PassAsync(Shade(200), [Quest]);
        Assert.Equal(["TR: Talk to Arthur", "TR: Meet Arthur at the barn", ""], server.Sent.Select(s => s.Text));
    }

    [Fact]
    public async Task PromptsAndNumbersShowNothing()
    {
        var (reader, server) = (new Reader(), new Server());
        var watcher = new RegionWatcher(reader, new Translator(), server);
        reader.Lines = [Line("R2 Swing"), Line("23m", 0.5)];
        await watcher.PassAsync(Shade(10), [Quest]);
        await watcher.PassAsync(Shade(10), [Quest]);
        Assert.Empty(server.Sent);
    }

    [Fact]
    public async Task PackTranslationIsUsedBeforeTheEngine()
    {
        var (reader, translator, server) = (new Reader(), new Translator(), new Server());
        var watcher = new RegionWatcher(reader, translator, server)
        {
            PackLookup = text => text == "New message" ? new PackMatch("Yeni mesaj", []) : null,
        };
        reader.Lines = [Line("New message", 0.1), Line("Call me when you get this.", 0.25)];
        await watcher.PassAsync(Shade(10), [Quest]);
        await watcher.PassAsync(Shade(10), [Quest]);
        Assert.Equal(["Yeni mesaj\nTR: Call me when you get this."], server.Sent.Select(s => s.Text));
        Assert.Equal(["Call me when you get this."], translator.Calls);
    }

    [Fact]
    public async Task RemovedAreaLosesItsBoxAndResetClearsAll()
    {
        var (reader, server) = (new Reader(), new Server());
        var watcher = new RegionWatcher(reader, new Translator(), server);
        reader.Lines = [Line("Where are you?")];
        await watcher.PassAsync(Shade(10), [Quest, Phone]);
        await watcher.PassAsync(Shade(10), [Quest, Phone]);
        Assert.Equal(["p", "q"], server.Sent.Select(s => s.Id).Order());
        await watcher.PassAsync(Shade(10), [Quest]);
        Assert.Equal(("p", ""), (server.Sent[^1].Id, server.Sent[^1].Text));
        watcher.Reset();
        Assert.Equal(1, server.Clears);
        watcher.Reset();
        Assert.Equal(1, server.Clears);
    }

    [Fact]
    public async Task OfferRunsAtMostOncePerInterval()
    {
        long clock = 0;
        var (reader, server) = (new Reader(), new Server());
        var watcher = new RegionWatcher(reader, new Translator(), server, clock: () => clock);
        reader.Lines = [Line("Find the blacksmith")];
        var frame = Shade(10);
        async Task OfferAndWait()
        {
            watcher.Offer(frame, [Quest]);
            while (watcher.IsBusy) await Task.Delay(5);
        }
        await OfferAndWait();
        const int interval = RegionWatcher.DefaultIntervalMs;
        for (var step = 1; step <= 9; step++)
        {
            clock = step * interval / 10;
            await OfferAndWait();
        }
        Assert.Equal(1, watcher.Passes);
        clock = interval;
        await OfferAndWait();
        Assert.Equal(2, watcher.Passes);
        Assert.Equal(["TR: Find the blacksmith"], server.Sent.Select(s => s.Text));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EarlyConfirmationOnlyPublishesAnUnchangedCandidateWithoutOcr(bool changed)
    {
        long clock = 0;
        var reader = new Reader { Lines = [Line("Find the blacksmith")] };
        var server = new Server();
        var watcher = new RegionWatcher(reader, new Translator(), server, clock: () => clock);
        async Task Offer(Frame frame)
        {
            watcher.Offer(frame, [Quest]);
            while (watcher.IsBusy) await Task.Delay(1);
        }
        await Offer(Shade(10));
        clock = 249;
        await Offer(Shade(10));
        Assert.Empty(server.Sent);
        var next = Shade(changed ? (byte)100 : (byte)10);
        clock = 250;
        await Offer(next);
        Assert.Equal(1, reader.Count);
        Assert.Equal(changed ? 0 : 1, server.Sent.Count);

        reader.Lines = [Line("Talk to Arthur")];
        clock = 750;
        await Offer(Shade(100));
        Assert.Equal(1, reader.Count);
        clock = 1000;
        await Offer(Shade(100));
        Assert.Equal(2, reader.Count);
        clock = 1250;
        await Offer(Shade(100));
        Assert.Equal("TR: Talk to Arthur", server.Sent.Last().Text);
    }

    private static TextLine At(string text, double left, double top, double right, double bottom) =>
        new(text, new TextBox(left, top, right, bottom));

    private static TextBox InQuest(TextBox box)
    {
        var region = Quest.Region;
        return box.PlacedIn(new TextBox(region.Left, region.Top, region.Right, region.Bottom));
    }

    /// <summary>
    /// Alanda birbirinden ayrı duran yazılar (görev başlığı ve sola kaymış ayrı hedef) alt alta tek kutuda değil, her biri
    /// kendi yeriyle gider; eski TV'ler için birleşik metin de kalır.
    /// </summary>
    [Fact]
    public async Task SeparateGroupsInAnAreaAreSentAsBlocksAtTheirOwnPlaces()
    {
        var reader = new Reader { Lines = [At("Find the blacksmith", 0.5, 0.1, 0.95, 0.2), At("Go to the barn", 0.1, 0.6, 0.4, 0.7)] };
        var translator = new Translator();
        var server = new Server();
        var watcher = new RegionWatcher(reader, translator, server);
        await watcher.PassAsync(Shade(10), [Quest]);
        await watcher.PassAsync(Shade(10), [Quest]);
        Assert.Equal(["TR: Find the blacksmith\nTR: Go to the barn"], server.Sent.Select(s => s.Text));
        var blocks = Assert.Single(server.Blocks)!;
        Assert.Equal(["TR: Find the blacksmith", "TR: Go to the barn"], blocks.Select(b => b.Translation));
        Assert.Equal(["Find the blacksmith", "Go to the barn"], blocks.Select(b => b.Source));
        Assert.Equal(InQuest(new TextBox(0.5, 0.1, 0.95, 0.2)), blocks[0].Box);
        Assert.Equal(InQuest(new TextBox(0.1, 0.6, 0.4, 0.7)), blocks[1].Box);
        var height = Quest.Region.Bottom - Quest.Region.Top;
        Assert.Equal(0.1 * height, blocks[0].LineHeight, 9);
        // Birleşik kutu iki yazıyı da kapsar (bloksuz eski TV uygulamaları için).
        Assert.Equal(InQuest(new TextBox(0.1, 0.1, 0.95, 0.7)), server.Sent[0].Box);
    }

    /// <summary>Tek grup (alt alta sarılmış tek yazı) eskisi gibi tek kutudur, blok gönderilmez.</summary>
    [Fact]
    public async Task OneGroupIsSentWithoutBlocks()
    {
        var reader = new Reader { Lines = [Line("Find the blacksmith and ask him", 0.2), Line("to repair your sword.", 0.3)] };
        var translator = new Translator { Map = _ => "Demirciyi bul ve kılıcını onarmasını iste." };
        var server = new Server();
        var watcher = new RegionWatcher(reader, translator, server);
        await watcher.PassAsync(Shade(10), [Quest]);
        await watcher.PassAsync(Shade(10), [Quest]);
        Assert.Equal(["Find the blacksmith and ask him to repair your sword."], translator.Calls);
        Assert.Equal(["Demirciyi bul ve kılıcını onarmasını iste."], server.Sent.Select(s => s.Text));
        Assert.Equal([null], server.Blocks);
    }

    /// <summary>Çevirisi aslıyla aynı grup (ad) blok olarak gönderilmez; İngilizcesi görünür kalır.</summary>
    [Fact]
    public async Task UntranslatedGroupIsLeftOutOfTheBlocks()
    {
        var reader = new Reader { Lines = [At("Arthur", 0.1, 0.1, 0.3, 0.2), At("Meet me at the barn", 0.1, 0.6, 0.6, 0.7)] };
        var translator = new Translator { Map = text => text == "Arthur" ? "Arthur" : "TR: " + text };
        var server = new Server();
        var watcher = new RegionWatcher(reader, translator, server);
        await watcher.PassAsync(Shade(10), [Quest]);
        await watcher.PassAsync(Shade(10), [Quest]);
        var blocks = Assert.Single(server.Blocks)!;
        var block = Assert.Single(blocks);
        Assert.Equal("TR: Meet me at the barn", block.Translation);
        Assert.Equal(InQuest(new TextBox(0.1, 0.6, 0.6, 0.7)), block.Box);
    }

    /// <summary>Aynı yazı kayınca yeniden çevrilmez; bloklar aynı çeviriyle yeni yerlerine gönderilir.</summary>
    [Fact]
    public async Task MovedBlocksKeepTheirTranslations()
    {
        var reader = new Reader { Lines = [At("Find the blacksmith", 0.5, 0.1, 0.95, 0.2), At("Go to the barn", 0.1, 0.6, 0.4, 0.7)] };
        var translator = new Translator();
        var server = new Server();
        var watcher = new RegionWatcher(reader, translator, server);
        await watcher.PassAsync(Shade(10), [Quest]);
        await watcher.PassAsync(Shade(10), [Quest]);
        reader.Lines = [At("Find the blacksmith", 0.5, 0.1, 0.95, 0.2), At("Go to the barn", 0.1, 0.7, 0.4, 0.8)];
        await watcher.PassAsync(Shade(60), [Quest]);
        Assert.Equal(["Find the blacksmith", "Go to the barn"], translator.Calls);
        Assert.Equal(2, server.Sent.Count);
        var moved = server.Blocks[1]!;
        Assert.Equal(["TR: Find the blacksmith", "TR: Go to the barn"], moved.Select(b => b.Translation));
        Assert.Equal(InQuest(new TextBox(0.1, 0.7, 0.4, 0.8)), moved[1].Box);
        // Yer değişmeden yeniden okunan aynı yazı bir şey göndermez.
        await watcher.PassAsync(Shade(100), [Quest]);
        Assert.Equal(2, server.Sent.Count);
    }

    /// <summary>GTA 6'nın etkileşim menüsü: sağa yaslı, sıkışık satırlar; her birinin sağında kendi tuş simgesi var.</summary>
    private static List<TextLine> Gta6Menu() =>
        [At("HAIR", 0.80, 0.50, 0.92, 0.60), At("GREET", 0.76, 0.65, 0.92, 0.75), At("STYLIST", 0.70, 0.80, 0.92, 0.90)];

    /// <summary>
    /// GTA 6'da menünün her satırı kendi yerine çevrilir: tek kutuda satır aralığı tutmuyor, "SAÇ" HAIR ile GREET arasına
    /// düşüyor, HAIR açıkta kalıyordu. Her blok kendi satırının üstünde, sağdaki simgesinin yanında durur.
    /// </summary>
    [Fact]
    public async Task Gta6MenuRowsAreSentAsOneBlockPerRow()
    {
        var reader = new Reader { Lines = Gta6Menu() };
        var translator = new Translator { Map = text => text switch { "HAIR" => "SAÇ", "GREET" => "SELAM", _ => text } };
        var server = new Server();
        var watcher = new RegionWatcher(reader, translator, server) { Game = () => "Grand Theft Auto VI" };
        await watcher.PassAsync(Shade(10), [Quest]);
        await watcher.PassAsync(Shade(10), [Quest]);
        Assert.Equal(["HAIR", "GREET", "STYLIST"], translator.Calls);
        var blocks = Assert.Single(server.Blocks)!;
        // STYLIST çevrilmedi (aynı kaldı): bloğu gönderilmez, İngilizcesi görünür kalır.
        Assert.Equal(["SAÇ", "SELAM"], blocks.Select(b => b.Translation));
        Assert.Equal(InQuest(new TextBox(0.80, 0.50, 0.92, 0.60)), blocks[0].Box);
        Assert.Equal(InQuest(new TextBox(0.76, 0.65, 0.92, 0.75)), blocks[1].Box);
    }

    /// <summary>Menü satırlarını bölme yalnız GTA 6'da: başka oyunda (ya da oyun bilinmiyorsa) yerleşim değişmez.</summary>
    [Theory]
    [InlineData("Red Dead Redemption 2")]
    [InlineData(null)]
    public async Task MenuRowsStayInOneBoxOutsideGta6(string? game)
    {
        var reader = new Reader { Lines = Gta6Menu() };
        var server = new Server();
        var watcher = new RegionWatcher(reader, new Translator(), server) { Game = () => game };
        await watcher.PassAsync(Shade(10), [Quest]);
        await watcher.PassAsync(Shade(10), [Quest]);
        Assert.Equal([null], server.Blocks);
    }

    /// <summary>GTA 6'da da büyük-küçük harfli bilgi kutusu bölünmez: tek cümle, tek kutu.</summary>
    [Fact]
    public async Task Gta6InfoTextIsNotSplitIntoRows()
    {
        var reader = new Reader { Lines = [Line("Your actions will affect your criminal", 0.2), Line("profile and may have a lasting impact.", 0.3)] };
        var server = new Server();
        var watcher = new RegionWatcher(reader, new Translator(), server) { Game = () => "GTA 6" };
        await watcher.PassAsync(Shade(10), [Quest]);
        await watcher.PassAsync(Shade(10), [Quest]);
        Assert.Equal([null], server.Blocks);
    }

    /// <summary>Kararlı gösterilip shown edilmiş yazı.</summary>
    private static async Task Show(RegionWatcher watcher, Reader reader, string text, byte shade)
    {
        reader.Lines = [Line(text)];
        await watcher.PassAsync(Shade(shade), [Quest]);
        await watcher.PassAsync(Shade(shade), [Quest]);
    }

    /// <summary>Görev yazısı arada kaybolup yeniden çıkınca çevirmene yeniden gidilmez.</summary>
    [Fact]
    public async Task ReappearingTextUsesTheEarlierTranslation()
    {
        var (reader, translator, server) = (new Reader(), new Translator(), new Server());
        var watcher = new RegionWatcher(reader, translator, server);
        await Show(watcher, reader, "Find the blacksmith", 10);
        await Show(watcher, reader, "", 90);
        await Show(watcher, reader, "Find the blacksmith", 170);
        Assert.Equal(["Find the blacksmith"], translator.Calls);
        Assert.Equal(["TR: Find the blacksmith", "", "TR: Find the blacksmith"], server.Sent.Select(s => s.Text));
    }

    /// <summary>
    /// Yazı belirirken renkli sözcük geç geliyor: az önce gösterilen yazının başı okunursa tamamı beklenir; görev iki kez
    /// çevrilip değişmez.
    /// </summary>
    [Fact]
    public async Task PartialReadingOfTheLastTextWaitsForTheWholeText()
    {
        var (reader, translator, server) = (new Reader(), new Translator(), new Server());
        var watcher = new RegionWatcher(reader, translator, server);
        await Show(watcher, reader, "Fly to a drop point", 10);
        await Show(watcher, reader, "", 90);
        await Show(watcher, reader, "Fly to a", 170);
        reader.Lines = [Line("Fly to a drop point")];
        await watcher.PassAsync(Shade(250), [Quest]);
        await watcher.PassAsync(Shade(250), [Quest]);
        Assert.Equal(["Fly to a drop point"], translator.Calls);
        Assert.Equal(["TR: Fly to a drop point", "", "TR: Fly to a drop point"], server.Sent.Select(s => s.Text));
    }

    /// <summary>Arka plan oynarken ekrandakinin harfi bozuk okuması kutuyu değiştirmez.</summary>
    [Fact]
    public async Task NearlySameReadingKeepsTheShownText()
    {
        var (reader, translator, server) = (new Reader(), new Translator(), new Server());
        var watcher = new RegionWatcher(reader, translator, server);
        await Show(watcher, reader, "SET DESTINATION EXIT", 10);
        await Show(watcher, reader, "SET DESTINATIPN EXIT", 90);
        await Show(watcher, reader, "SET DESTINATION EXIT", 170);
        Assert.Equal(["SET DESTINATION EXIT"], translator.Calls);
        Assert.Single(server.Sent);
    }

    /// <summary>Yalnız bir sözcüğü değişen görev, o hâlde kalırsa birkaç tur sonra gösterilir.</summary>
    [Fact]
    public async Task NearlySameTextThatStaysIsShownAfterTheHoldLimit()
    {
        var (reader, translator, server) = (new Reader(), new Translator(), new Server());
        var watcher = new RegionWatcher(reader, translator, server);
        await Show(watcher, reader, "Go to the barn", 10);
        await Show(watcher, reader, "Go to the bank", 90);
        Assert.Single(server.Sent);
        for (var i = 0; i < RegionWatcher.HoldLimit; i++) await watcher.PassAsync(Shade(90), [Quest]);
        Assert.Equal(["TR: Go to the barn", "TR: Go to the bank"], server.Sent.Select(s => s.Text));
    }


    [Theory]
    [InlineData("Find the blacksmith", "Escape")]
    [InlineData("Meet me at gate 5", "Meet me at gate 6")]
    [InlineData("You can open it", "You can't open it")]
    public async Task ConfirmedNewPromptDoesNotWaitForTheHoldLimit(string first, string next)
    {
        var reader = new Reader();
        var server = new Server();
        var watcher = new RegionWatcher(reader, new Translator(), server);
        await Show(watcher, reader, first, 10);
        await Show(watcher, reader, next, 90);
        Assert.Equal(["TR: " + first, "TR: " + next], server.Sent.Select(s => s.Text));
    }

    [Fact]
    public async Task IntermittentTypoDoesNotAccumulateHolds()
    {
        var reader = new Reader();
        var server = new Server();
        var watcher = new RegionWatcher(reader, new Translator(), server);
        await Show(watcher, reader, "Go to the barn", 10);
        for (var i = 0; i <= RegionWatcher.HoldLimit; i++)
        {
            await Show(watcher, reader, "Go to the bank", 90);
            await Show(watcher, reader, "Go to the barn", 10);
        }
        Assert.Equal(["TR: Go to the barn"], server.Sent.Select(s => s.Text));
    }

    [Fact]
    public async Task CancelledConfirmedTranslationCanBeRetriedOnTheSameFrame()
    {
        var reader = new Reader { Lines = [Line("Find the blacksmith")] };
        var translator = new Translator { Respond = _ => Task.FromCanceled<string>(new CancellationToken(true)) };
        var server = new Server();
        var watcher = new RegionWatcher(reader, translator, server);
        await watcher.PassAsync(Shade(10), [Quest]);
        Assert.Empty(translator.Calls);
        await watcher.PassAsync(Shade(10), [Quest]);
        translator.Respond = null;
        await watcher.PassAsync(Shade(10), [Quest]);
        Assert.Equal(["Find the blacksmith", "Find the blacksmith"], translator.Calls);
        Assert.Equal(["TR: Find the blacksmith"], server.Sent.Select(s => s.Text));
    }

    [Fact]
    public async Task FailedTranslationDoesNotHoldTheNextConfirmedPrompt()
    {
        var reader = new Reader();
        var translator = new Translator { FailuresRemaining = 1 };
        var server = new Server();
        var watcher = new RegionWatcher(reader, translator, server);
        await Show(watcher, reader, "Find the blacksmith", 10);
        await Show(watcher, reader, "Find", 90);
        Assert.Equal(["TR: Find"], server.Sent.Select(s => s.Text));
    }

    [Fact]
    public void PartialAndWordLosingReadingsAreRecognised()
    {
        Assert.True(RegionWatcher.IsPartOf("add marker set destination", "add marker set destination exit"));
        Assert.False(RegionWatcher.IsPartOf("talk to arthur", "meet arthur at the barn"));
        Assert.True(RegionWatcher.LosesWords("find ￼", "find donnie"));
        Assert.False(RegionWatcher.LosesWords("hold ￼ to climb", "hold to climb"));
    }

    [Fact]
    public void TextKeyKeepsButtonIcons()
    {
        Assert.Equal("set destination ￼ exit", RegionWatcher.TextKey(["SET DESTINATION ￼ EXIT"]));
        Assert.NotEqual(RegionWatcher.TextKey(["SET DESTINATION"]), RegionWatcher.TextKey(["SET DESTINATION ￼"]));
    }

    [Fact]
    public void NoAreasMeansNoWork()
    {
        var reader = new Reader();
        var watcher = new RegionWatcher(reader, new Translator(), new Server());
        watcher.Offer(Shade(10), []);
        Assert.False(watcher.IsBusy);
        Assert.Equal(0, reader.Count);
    }
}
