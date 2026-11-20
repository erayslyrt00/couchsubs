// SPDX-License-Identifier: AGPL-3.0-only

namespace CouchSubs.Core;

public sealed partial class RegionWatcher
{
    private (Frame Frame, WatchedArea Area)? LatestPhone(WatchedArea area)
    {
        var update = Latest();
        if (update is null) return null;
        var phone = update.Value.Areas.FirstOrDefault(candidate => candidate.PanelLifetime == area.PanelLifetime);
        return phone is null ? null : (update.Value.Frame, phone);
    }

    // Yakalama, telefonun konumunu OCR turundan bağımsız aralıklarla günceller. Son konum
    // geçerli görünse de bu karede kasa kaybolmuş olabilir. Denetim yalnız telefon işinde çalışır.
    private static bool PhoneMissing(Frame frame, WatchedArea area) => area.PhonePose.HasValue
        && (!GamePhoneGeometry.TryLocate(frame, out var pose) || !pose.Bounds.IsClose(area.Region, 0.035));

    private async Task VisitPhoneAsync(WatchedArea area, Frame frame, Dictionary<string, State> map, int started)
    {
        if (area.PanelLifetime is { IsCancellationRequested: true } || PhoneMissing(frame, area)) return;
        var crop = RegionCropper.Crop(frame, area.Region);
        var signature = RegionCropper.Signature(crop);
        State state;
        lock (map)
        {
            if (area.PanelLifetime is { IsCancellationRequested: true }) return;
            if (!map.TryGetValue(area.Id, out state!)) map[area.Id] = state = new State();
        }
        var moved = state.ReadRegion is { } region && !area.Region.IsClose(region, 0.01);
        var changed = moved || state.Signature is not { } previous || ReadScheduler.Changed(previous, signature, ReadScheduler.ChangeThreshold)
            || state.ReadPhonePage?.Changed(frame, area.Region) == true;
        state.Signature = signature;
        if (!changed)
        {
            // İz bir tur değişmedi: okunan yazı kararlıdır.
            if (state.Candidate is { } stable)
            {
                state.Candidate = null;
                await CommitPhoneAsync(stable, area, state, started).ConfigureAwait(false);
            }
            return;
        }
        Candidate candidate;
        try { candidate = ReadPhoneCandidate(frame, area, crop); }
        catch
        {
            // OCR hatası boş alan değildir. Eski kutuyu koru; aynı kareyi sonraki normal turda yeniden oku.
            // Başarısız kare, önceki adayın kararlılığını da doğrulayamaz.
            state.Signature = null;
            state.LastRead = null;
            state.Candidate = null;
            return;
        }
        if (Volatile.Read(ref session) != started || area.PanelLifetime is { IsCancellationRequested: true }) return;
        state.ReadRegion = area.Region;
        state.ReadPhonePage = candidate.PhonePage;
        // Telefonun açılışı iki geometri örneğiyle doğrulandı. Okunan sayfayı çevirmeye hemen başla;
        // yavaş motorun yanıtı gösterilmeden önce beklerken gelen son kare ayrıca doğrulanır.
        if (state.LastRead == candidate.Key || candidate.Key.Length > 0)
        {
            state.LastRead = candidate.Key;
            state.Candidate = null;
            await CommitPhoneAsync(candidate, area, state, started).ConfigureAwait(false);
        }
        else
        {
            state.LastRead = candidate.Key;
            state.Candidate = candidate;
        }
    }

    private Candidate ReadPhoneCandidate(Frame frame, WatchedArea area, Frame crop)
    {
        var clock = now();
        var reading = area.PhonePose is { } pose ? GamePhoneReader.Read(frame, area.Region, pose, reader)
            : reader.ReadScreen(crop);
        area.PanelLifetime?.ThrowIfCancellationRequested();
        Interlocked.Increment(ref reads);
        ReadMeasured?.Invoke(now() - clock);
        reading = GamePhoneText.ForTranslation(reading);
        var candidate = MakeCandidate(reading, area.Region, crop, phone: true);
        return candidate.Key.Length > 0
            ? candidate with { PhonePage = new GamePhonePage(frame, area.Region, reading) }
            : candidate;
    }

    // Yeni OCR'nin kutularına yalnız tam eşleşen parçalar konur. Bir balonun herhangi bir parçası
    // değiştiyse balonun tamamı özgün dilinde kalır; başka bir balonun çevirisini engellemez.
    private static string?[]? MatchingPhoneBlocks(Candidate fresh, Dictionary<string, string> translations)
    {
        var matched = fresh.Paragraphs.Select(translations.GetValueOrDefault).ToArray();
        if (matched.Length == 0) return null;
        if (Array.IndexOf(matched, null) < 0) return matched;
        if (fresh.Blocks is null) return null;
        foreach (var block in fresh.Blocks)
            if (Array.IndexOf(matched, null, block.Start, block.Count) >= 0)
                Array.Clear(matched, block.Start, block.Count);
        return matched.Any(value => value is not null) ? matched : null;
    }

    private static void FollowPhonePage(State state, Candidate candidate, CropRegion region)
    {
        if (candidate.Blocks is not { Count: > 0 } blocks) return;
        var anchors = new List<(string Prefix, double X, double Y)>();
        foreach (var block in blocks)
        {
            var message = candidate.Paragraphs[block.Start];
            if (!GamePhoneText.IsMessagePreview(message)) continue;
            // Kesilen son sözcük aynı önizlemede her karede değişebilir. Önek yalnız iş sırasını belirler;
            // çevirinin yeniden kullanılması için hâlâ metnin tamamı birebir aynı olmalıdır.
            var words = TextMatch.Normalize(message).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var prefix = string.Join(' ', words.Take(3));
            anchors.Add((prefix, (block.Box.Left - region.Left) / (region.Right - region.Left),
                                 (block.Box.Top - region.Top) / (region.Bottom - region.Top)));
        }
        if (anchors.Count == 0) return;
        if (!anchors.Any(current => state.PhoneOrderAnchors.Any(old => old.Prefix == current.Prefix
            && Math.Abs(old.X - current.X) <= .10 && Math.Abs(old.Y - current.Y) <= .08)))
            state.NextPhoneBlock = 0;
        state.PhoneOrderAnchors = [.. anchors];
    }

    private async Task CommitPhoneAsync(Candidate candidate, WatchedArea area, State state, int started)
    {
        if (area.PanelLifetime is { IsCancellationRequested: true }) return;
        if (!translator.TranslatesTogether) FollowPhonePage(state, candidate, area.Region);
        if (state.Shown == candidate.Key)
        {
            if (state.RenderedKey != candidate.Key || string.IsNullOrEmpty(state.RenderedText)) return;
            if (state.RenderedBox == candidate.Box && state.RenderedLineHeight == candidate.LineHeight
                && SameLayout(state.RenderedLayout, candidate.Blocks)) return;
            // Aynı yazı kaydı: çeviriler korunur, bloklar yeni yerlerine taşınır (anahtar aynıysa grup sayısı da aynıdır).
            var moved = state.RenderedBlocks is { } rendered && candidate.Blocks is { } current
                ? rendered.Select(block => (Group: block.Group, Item: block.Item with
                    { Box = current[block.Group].Box, LineHeight = current[block.Group].LineHeight, Style = current[block.Group].Style })).ToList()
                : null;
            SendCurrent(area, state, started, state.RenderedText, candidate.Box, candidate.LineHeight, state.RenderedIcons,
                              moved?.Select(block => block.Item).ToList());
            state.RenderedBox = candidate.Box;
            state.RenderedLineHeight = candidate.LineHeight;
            state.RenderedLayout = candidate.Blocks;
            state.RenderedBlocks = moved;
            return;
        }
        var previousShown = state.Shown;
        state.Shown = candidate.Key;
        if (candidate.Key.Length == 0)
        {
            SendCurrent(area, state, started, "", null);
            state.RenderedKey = candidate.Key;
            state.RenderedText = "";
            state.RenderedBox = null;
            state.RenderedLineHeight = null;
            Log?.Invoke($"ek alan {area.Name}: boşaldı");
            return;
        }
        // Simgeler paket aramasında yok sayılır, çeviride uydurma adlarla korunur (IconText).
        var prepared = candidate.Paragraphs.Select(IconText.Protect).ToList();
        string?[] result = [.. candidate.Paragraphs.Select(paragraph => PackLookup?.Invoke(IconText.Plain(paragraph))?.Text)];
        var fromPack = result.Count(text => text is not null);
        if (state.PhoneTranslations is { } known)
            for (var i = 0; i < result.Length; i++)
                if (result[i] is null && known.TryGetValue(candidate.Paragraphs[i], out var cached)) result[i] = cached;
        // Zaten Türkçe olan parça çevrilmez: aslı kalır, tümü öyleyse kutu gösterilmez.
        for (var i = 0; i < result.Length; i++)
            if (result[i] is null && SkipsTurkish && TurkishText.IsSure(candidate.Paragraphs[i])) result[i] = candidate.Paragraphs[i];
        var partialPhone = false;
        try
        {
            var missing = Enumerable.Range(0, result.Length).Where(i => result[i] is null).ToList();
            if (missing.Count > 0)
            {
                if (!string.IsNullOrEmpty(state.RenderedText))
                {
                    // Yeni sayfanın henüz çevrilmeyen bir balonuna eski Türkçeyi basma.
                    var retained = new List<(int Group, ScreenTranslation.Item Item)>();
                    if (state.RenderedBlocks is { } old && candidate.Blocks is { } current)
                        for (int i = 0; i < current.Count; i++)
                        {
                            var block = current[i];
                            if (Array.IndexOf(result, null, block.Start, block.Count) >= 0) continue;
                            var source = string.Join('\n', candidate.Paragraphs.Skip(block.Start).Take(block.Count).Select(IconText.Plain));
                            var prior = old.FirstOrDefault(entry => entry.Item.Source == source).Item;
                            if (prior is not null) retained.Add((i, prior with { Box = block.Box, LineHeight = block.LineHeight, Style = block.Style }));
                        }
                    if (state.RenderedBlocks is null || !state.RenderedBlocks.SequenceEqual(retained))
                    {
                        var visible = retained.Select(entry => entry.Item).ToList();
                        state.RenderedText = string.Join('\n', visible.Select(item => item.Translation));
                        state.RenderedBox = visible.Count == 0 ? null : visible.Select(item => item.Box).Aggregate((a, b) => a.Union(b));
                        state.RenderedKey = null;
                        state.RenderedIcons = null;
                        state.RenderedBlocks = retained;
                        previousShown = "";
                        SendCurrent(area, state, started, state.RenderedText, state.RenderedBox, candidate.LineHeight, blocks: visible.Count == 0 ? null : visible);
                    }
                }
                bool showFirstBubble = false;
                if (!translator.TranslatesTogether && state.RenderedKey != candidate.Key
                    && missing.Count > 1 && candidate.Blocks is { } phoneBlocks)
                {
                    // Yerel motor satırları sırayla üretir. Yeni sohbet kısa sürede kapanabilir:
                    // önce ilk mesaj balonunu hazırla, menü başlıklarını ve kalan balonları sonraki tur tamamlar.
                    // Listenin ilk önizlemesinin son harfi titrerse her turu ona ayırma.
                    for (var offset = 0; offset < phoneBlocks.Count; offset++)
                    {
                        int index = (state.NextPhoneBlock + offset) % phoneBlocks.Count;
                        var block = phoneBlocks[index];
                        var lead = missing.Where(i => i >= block.Start && i < block.Start + block.Count).ToList();
                        if (!lead.Any(i => GamePhoneText.IsMessagePreview(candidate.Paragraphs[i]))) continue;
                        state.NextPhoneBlock = (index + 1) % phoneBlocks.Count;
                        showFirstBubble = lead.Count < missing.Count;
                        missing = lead;
                        break;
                    }
                }
                var texts = missing.Select(i => EnglishPhoneMessages
                    ? GamePhoneText.EnglishModelInput(prepared[i].Text) : prepared[i].Text).ToList();
                var translations = await translator.TranslateAllAsync(texts, area.PanelLifetime ?? default).ConfigureAwait(false);
                if (translations.Count == missing.Count)
                    for (var i = 0; i < missing.Count && i < translations.Count; i++) result[missing[i]] = translations[i];
                if (area.PanelLifetime is { IsCancellationRequested: true }) return;
                for (var i = 0; i < result.Length; i++)
                    if (!string.IsNullOrWhiteSpace(result[i])) state.RememberPhoneTranslation(candidate.Paragraphs[i], result[i]!);
                var latestFrame = LatestPhone(area);
                if (latestFrame is { } observed && PhoneMissing(observed.Frame, observed.Area))
                {
                    if (!string.IsNullOrEmpty(state.RenderedText)) SendCurrent(area, state, started, "", null);
                    state.RenderedText = null;
                    state.Shown = "";
                    state.Candidate = null;
                    state.Signature = null;
                    return;
                }
                if (latestFrame is { } latest && candidate.PhonePage!.Changed(latest.Frame, latest.Area.Region))
                {
                    // Telefon açıkken sayfa kaydırılmış olabilir. Yalnız yazı pikselleri değiştiyse bir kez daha oku.
                    var crop = RegionCropper.Crop(latest.Frame, latest.Area.Region);
                    Candidate? fresh = null;
                    try { fresh = ReadPhoneCandidate(latest.Frame, latest.Area, crop); }
                    catch { /* Okunamayan yeni kare eski çeviriyi doğrulayamaz. */ }
                    if (Volatile.Read(ref session) != started || area.PanelLifetime is { IsCancellationRequested: true }) return;
                    if (LatestPhone(area) is { } newer && !ReferenceEquals(newer.Frame, latest.Frame)
                        && (PhoneMissing(newer.Frame, newer.Area) || fresh?.PhonePage?.Changed(newer.Frame, newer.Area.Region) == true)) fresh = null;
                    state.ReadRegion = latest.Area.Region;
                    state.ReadPhonePage = fresh?.PhonePage;
                    state.Signature = fresh is null ? null : RegionCropper.Signature(crop);
                    if (fresh?.Key != candidate.Key)
                    {
                        state.LastRead = fresh?.Key;
                        state.Candidate = fresh;
                        if (fresh is null || state.PhoneTranslations is not { } confirmed || MatchingPhoneBlocks(fresh, confirmed) is not { } matched)
                        {
                            if (!string.IsNullOrEmpty(state.RenderedText)) SendCurrent(area, state, started, "", null);
                            state.Shown = "";
                            state.RenderedText = null;
                            return;
                        }
                        result = matched;
                        prepared = fresh.Paragraphs.Select(IconText.Protect).ToList();
                        state.Shown = fresh.Key;
                        partialPhone = Array.IndexOf(matched, null) >= 0;
                        if (!partialPhone) state.Candidate = null;
                    }
                    candidate = fresh!;
                }
                if (showFirstBubble && Array.IndexOf(result, null) >= 0)
                {
                    partialPhone = true;
                    state.LastRead = candidate.Key;
                    state.Candidate = candidate;
                }
            }
        }
        catch (OperationCanceledException) when (area.PanelLifetime is { IsCancellationRequested: true }) { return; }
        catch (Exception error)
        {
            if (Volatile.Read(ref session) == started)
            {
                if (state.Shown == candidate.Key)
                {
                    state.Shown = previousShown;
                    state.Candidate = candidate;
                }
                Log?.Invoke($"ek alan {area.Name}: {SubtitleLog.Excerpt(IconText.Plain(string.Join(' ', candidate.Paragraphs)))} çevrilemedi ({error.Message})");
            }
            return;
        }
        if (Volatile.Read(ref session) != started || state.Shown != candidate.Key) return;
        if (partialPhone) state.Shown = ""; // Sonraki tur, yeni okumanın yalnız eksik kalan balonlarını tamamlar.
        Publish(area, state, started, candidate, result, fromPack, prepared, partialPhone: partialPhone);
    }

    /// <summary>Ömrü iptal edilen panelin eski okumasını ve yalnız o panelin kutusunu kaldırır.</summary>
    public void RemovePanel(string id)
    {
        lock (captureGate) lastPhonePass = null;
        var map = states;
        lock (map)
        {
            if (map.Remove(id)) server.SendRegion(id, "", null);
        }
    }

}
