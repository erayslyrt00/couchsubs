// SPDX-License-Identifier: AGPL-3.0-only

import CoreGraphics
import Foundation

/// Ek alanların çevirisini TV'ye ileten taraf (gerçekte `SubtitleServer`).
public protocol RegionSending: AnyObject {
    /// `text` boşsa alanın kutusu kalkar.
    func sendRegion(id: String, text: String, box: TextBox?, lineHeight: Double?)
    /// Çevirinin içinde kumanda simgeleri varsa (`ButtonIcons`); simgeyi bilmeyen gönderici düz metni yollar.
    func sendRegion(id: String, text: String, box: TextBox?, lineHeight: Double?, icons: [IconPlacement]?)
    /// Alanda birbirinden ayrı duran yazı grupları varsa (görev başlığı ve ayrı sütundaki hedef) `blocks` her grubun
    /// çevirisini kendi yeriyle taşır; TV her birini kendi İngilizce yazısının üstüne koyar. Blokları bilmeyen gönderici
    /// birleşik metni yollar.
    func sendRegion(id: String, text: String, box: TextBox?, lineHeight: Double?, icons: [IconPlacement]?,
                    blocks: [ScreenTranslation.Item]?)
    func clearRegions()
}

extension RegionSending {
    public func sendRegion(id: String, text: String, box: TextBox?, lineHeight: Double?, icons: [IconPlacement]?) {
        sendRegion(id: id, text: text, box: box, lineHeight: lineHeight)
    }

    public func sendRegion(id: String, text: String, box: TextBox?, lineHeight: Double?, icons: [IconPlacement]?,
                           blocks: [ScreenTranslation.Item]?) {
        sendRegion(id: id, text: text, box: box, lineHeight: lineHeight, icons: icons)
    }
}

extension SubtitleServer: RegionSending {}

/// Ek alanları (görev yazısı, telefon mesajı, bildirim) ana altyazıdan ayrı ve seyrek okur, çevirip TV'ye kendi
/// yerleriyle gönderir (Windows: CouchSubs.Core/RegionWatcher.cs ile aynı kurallar).
///
/// - Ana okuma hattını beklemez, onu da bekletmez: `offer` hemen döner; tur ayrı bir görevde ve düşük öncelikle sürer.
/// - En çok `intervalMs`'te bir tur atılır; önceki tur (okuma ya da çeviri) bitmediyse kare atlanır.
/// - Bölge izi (`RegionCropper.signature`) değişmeyen alan yeniden okunmaz.
/// - Okunan yazı ancak kararlıysa çevrilir: iz bir tur değişmeden kaldıysa ya da iki okuma aynı yazıyı verdiyse (arkası
///   kıpırdayan köşe yazıları). Böylece belirirken yarım okunan yazı çevrilmez.
/// - Yazı değişmedikçe yeniden çevrilmez; boşalan alanın kutusu kalkar.
/// - Paket eşleşmesi ve çeviri motoru ana altyazıyla aynıdır; ama ek alan satırları ortak havuza önerilmez (arayüz
///   yazıları havuzu kirletmesin).
@MainActor
public final class RegionWatcher {
    /// İki tur arasındaki en kısa süre.
    public static let defaultIntervalMs = 1000

    private struct State {
        var signature: [UInt8]?
        /// Son okumanın sadeleştirilmiş yazısı; bir sonraki okuma aynıysa yazı kararlıdır.
        var lastRead: String?
        /// Okunmuş ama henüz kararlı sayılmamış yazı.
        var candidate: Candidate?
        /// TV'de şu an çevirisi duran yazı (sadeleştirilmiş); boşsa kutu yok.
        var shown = ""
        /// Son gönderilen çeviri, aynı yazı yeni bir konuma taşındığında yeniden kullanılır.
        var renderedKey: String?
        var renderedText: String?
        var renderedIcons: [IconPlacement]?
        var renderedBox: TextBox?
        var renderedLineHeight: Double?
        var renderedLayout: [Block]?
        /// Gönderilen bloklar ve her birinin `Candidate.blocks` içindeki sırası.
        var renderedBlocks: [(group: Int, item: ScreenTranslation.Item)]?
    }

    /// Alanda ayrı duran bir yazı grubu: `Candidate.paragraphs` içinde `start`'tan başlayan `count` parça, karedeki yeri
    /// ve satır yüksekliği (kareye oranla).
    struct Block: Equatable {
        var start: Int
        var count: Int
        var box: TextBox
        var lineHeight: Double
    }

    struct Candidate {
        var paragraphs: [String]
        var box: TextBox?
        var lineHeight: Double?
        var key: String
        /// Her parçadaki kumanda simgeleri (kareden kesilmiş), parçadaki sırasıyla; simge yoksa nil.
        var icons: [[IconSlot]]? = nil
        /// Alanda birden çok ayrı yazı grubu varsa grupları; tek grupta nil (tek kutu).
        var blocks: [Block]? = nil
    }

    private let reader: TextReader
    private let translator: TextTranslator
    private let server: RegionSending
    private let intervalMs: Int
    private let now: () -> Int
    private var states: [String: State] = [:]
    private var lastPass: Int?
    private var running: Task<Void, Never>?
    private var session = 0

    /// Oyun paketi: satırın hazır Türkçesi (ana altyazıyla aynı arama).
    public var packLookup: ((String) -> PackMatch?)?
    /// Hedef dil Türkçe: yüksek güvenle Türkçe olan parça çevrilmez, aslı sayılır (#289).
    public var skipsTurkish = true
    public var onLog: ((String) -> Void)?
    /// Her okumanın süresi (ms; ölçüm için).
    public var onReadMs: ((Int) -> Void)?
    /// Tamamlanan tur sayısı ve okuma sayısı (ölçüm ve testler için).
    public private(set) var passes = 0
    public private(set) var reads = 0

    public init(reader: TextReader, translator: TextTranslator, server: RegionSending,
                intervalMs: Int = RegionWatcher.defaultIntervalMs, clock: @escaping () -> Int = SubtitleStabilizer.uptimeMs) {
        self.reader = reader
        self.translator = translator
        self.server = server
        self.intervalMs = intervalMs
        self.now = clock
    }

    /// Bir tur sürüyor mu.
    public var isBusy: Bool { running != nil }

    /// Her karede çağrılır; tur zamanı geldiyse ve önceki tur bittiyse turu arka planda başlatır, hemen döner.
    public func offer(_ frame: CGImage, areas: [ExtraArea]) {
        guard running == nil else { return }
        guard !areas.isEmpty || states.values.contains(where: { !$0.shown.isEmpty }) else { return }
        let time = now()
        if let lastPass, time - lastPass < intervalMs { return }
        lastPass = time
        let session = self.session
        running = Task { [weak self] in
            await self?.pass(frame, areas: areas)
            // Bu arada sıfırlandıysa yeni oturumun turuna dokunulmaz.
            if self?.session == session { self?.running = nil }
        }
    }

    /// Bir tur: kaldırılan alanların kutusu kalkar, her açık alan gerekirse okunur ve kararlı yazısı çevrilir.
    /// Testler doğrudan çağırır.
    public func pass(_ frame: CGImage, areas: [ExtraArea]) async {
        let started = session
        passes += 1
        let ids = Set(areas.map(\.id))
        for (id, state) in states where !ids.contains(id) {
            if !state.shown.isEmpty { server.sendRegion(id: id, text: "", box: nil, lineHeight: nil) }
            states[id] = nil
        }
        for area in areas {
            await visit(area, frame: frame)
            guard session == started else { return }
        }
    }

    private func visit(_ area: ExtraArea, frame: CGImage) async {
        let region = area.region
        let (crop, signature) = await Task.detached(priority: .utility) {
            let crop = RegionCropper.crop(frame, to: region)
            return (crop, RegionCropper.signature(of: crop))
        }.value
        var state = states[area.id] ?? State()
        let changed = state.signature.map {
            ReadScheduler.changed($0, signature, threshold: ReadScheduler.defaultChangeThreshold)
        } ?? true
        state.signature = signature
        guard changed else {
            // İz bir tur değişmedi: okunan yazı kararlıdır.
            if let candidate = state.candidate {
                state.candidate = nil
                states[area.id] = state
                await commit(candidate, area: area)
            } else {
                states[area.id] = state
            }
            return
        }
        let started = now()
        let session = self.session
        let reader = self.reader
        let reading = await Task.detached(priority: .utility) { try? reader.readLines(crop, deep: false) }.value
        guard session == self.session else { return }
        guard let reading else {
            // OCR hatası boş alan değildir. Eski kutuyu koru; aynı kareyi sonraki normal turda yeniden oku.
            // Başarısız kare, önceki adayın kararlılığını da doğrulayamaz.
            state.signature = nil
            state.lastRead = nil
            state.candidate = nil
            states[area.id] = state
            return
        }
        reads += 1
        onReadMs?(now() - started)
        let candidate = Self.candidate(reading, in: region, frame: crop)
        if state.lastRead == candidate.key {
            state.candidate = nil
            states[area.id] = state
            await commit(candidate, area: area)
        } else {
            state.lastRead = candidate.key
            state.candidate = candidate
            states[area.id] = state
        }
    }

    /// Okumadan çevrilecek parçalar: kumanda ipuçları, yazı tanıma çöpleri ve harfsiz göstergeler ("23m", "9") atılır;
    /// satırlar yukarıdan aşağı parçalara toplanır (başlık, cümle, madde). Yazının içindeki kumanda simgeleri `frame`'den
    /// (okunan alan) kesilir; çeviride yerleri korunur.
    static func candidate(_ reading: Reading, in region: CropRegion, frame: CGImage? = nil) -> Candidate {
        let lines = reading.withButtonIcons().withoutButtonPrompts().withoutOcrNoise().lines
            .filter { ScreenTranslation.isWords($0.text) }
            .sorted { ($0.box?.top ?? 0) < ($1.box?.top ?? 0) }
        var paragraphs = ScreenTranslation.paragraphs(lines)
        let place = TextBox(left: region.left, top: region.top, right: region.right, bottom: region.bottom)
        let boxes = lines.compactMap(\.box)
        let box = boxes.dropFirst().reduce(boxes.first) { $0?.union($1) }?.placed(in: place)
        let height = region.bottom - region.top
        let heights = boxes.map { ($0.bottom - $0.top) * height }.sorted()
        let lineHeight = heights.isEmpty ? nil : heights[heights.count / 2]
        var key = TextMatch.normalize(paragraphs.joined(separator: " "))
        var icons = ScreenTranslation.paragraphIcons(lines)
        // Alanda birbirinden ayrı duran yazılar (başlık ile ayrı sütundaki hedef, alt alta iki bildirim) "Ekranı çevir"deki
        // gibi gruplanır; her grubun çevirisi TV'de kendi yerine konur. Tek grupta (ya da yeri bilinmeyen satır varsa)
        // eskisi gibi tek kutu. Anahtar grup yapısını da içerir: aynı yazı farklı gruplanırsa bloklar yeniden kurulur.
        var blocks: [Block]?
        let groups = lines.count > 1 && boxes.count == lines.count ? ScreenTranslation.groups(reading) : []
        if groups.count > 1, groups.count < ScreenTranslation.maxGroups {
            paragraphs = groups.flatMap(\.paragraphs)
            icons = groups.flatMap { $0.icons ?? $0.paragraphs.map { _ in [IconSlot]() } }
            var list: [Block] = []
            var start = 0
            for group in groups {
                list.append(Block(start: start, count: group.paragraphs.count, box: group.box.placed(in: place),
                                  lineHeight: group.lineHeight * height))
                start += group.paragraphs.count
            }
            blocks = list
            key = TextMatch.normalize(paragraphs.joined(separator: " ")) + "|" + list.map { String($0.count) }.joined(separator: ",")
        }
        var cut: [[IconSlot]]?
        if let frame, icons.contains(where: { !$0.isEmpty }) {
            cut = icons.map { ButtonIcons.withImages($0, frame: frame) }
        }
        return Candidate(paragraphs: paragraphs, box: box, lineHeight: lineHeight, key: key, icons: cut, blocks: blocks)
    }

    /// Kararlı yazı: öncekinden farklıysa çevrilip gönderilir, boşsa kutu kalkar.
    private func commit(_ candidate: Candidate, area: ExtraArea) async {
        guard var state = states[area.id] else { return }
        if state.shown == candidate.key {
            guard state.renderedKey == candidate.key,
                  let renderedText = state.renderedText, !renderedText.isEmpty else { return }
            guard state.renderedBox != candidate.box || state.renderedLineHeight != candidate.lineHeight
                    || state.renderedLayout != candidate.blocks else { return }
            // Aynı yazı kaydı: çeviriler korunur, bloklar yeni yerlerine taşınır (anahtar aynıysa grup sayısı da aynıdır).
            var moved: [(group: Int, item: ScreenTranslation.Item)]?
            if let rendered = state.renderedBlocks, let current = candidate.blocks {
                moved = rendered.map { block -> (group: Int, item: ScreenTranslation.Item) in
                    var item = block.item
                    item.box = current[block.group].box
                    item.lineHeight = current[block.group].lineHeight
                    return (block.group, item)
                }
            }
            server.sendRegion(id: area.id, text: renderedText, box: candidate.box, lineHeight: candidate.lineHeight,
                              icons: state.renderedIcons, blocks: moved?.map(\.item))
            state.renderedBox = candidate.box
            state.renderedLineHeight = candidate.lineHeight
            state.renderedLayout = candidate.blocks
            state.renderedBlocks = moved
            states[area.id] = state
            return
        }
        let previousShown = state.shown
        states[area.id]?.shown = candidate.key
        guard !candidate.key.isEmpty else {
            server.sendRegion(id: area.id, text: "", box: nil, lineHeight: nil)
            states[area.id]?.renderedKey = candidate.key
            states[area.id]?.renderedText = ""
            states[area.id]?.renderedIcons = nil
            states[area.id]?.renderedBox = nil
            states[area.id]?.renderedLineHeight = nil
            onLog?("ek alan \(area.name): boşaldı")
            return
        }
        let session = self.session
        // Simgeler paket aramasında yok sayılır, çeviride uydurma adlarla korunur (`IconText`).
        let prepared = candidate.paragraphs.map(IconText.protect)
        let translated: [String?]
        var fromPack = 0
        do {
            translated = try await translate(candidate.paragraphs, prepared: prepared, packHits: &fromPack)
        } catch {
            guard session == self.session else { return }
            if states[area.id]?.shown == candidate.key {
                states[area.id]?.shown = previousShown
                states[area.id]?.candidate = candidate
            }
            onLog?("ek alan \(area.name): \(SubtitleLog.excerpt(IconText.plain(candidate.paragraphs.joined(separator: " ")))) çevrilemedi (\(error))")
            return
        }
        guard session == self.session, states[area.id]?.shown == candidate.key else { return }
        var slots: [IconSlot] = []
        var text: [String] = []
        for (index, source) in candidate.paragraphs.enumerated() {
            let trimmed = (index < translated.count ? translated[index] : nil)?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
            text.append(trimmed.isEmpty ? IconText.indexed(source, offset: slots.count)
                        : IconText.restore(trimmed, names: prepared[index].names, offset: slots.count))
            if let all = candidate.icons, index < all.count { slots += all[index] }
        }
        // Çevirisi aslıyla aynı (ad, sayı, zaten hedef dilde): kutu gösterilmez.
        let similar = zip(candidate.paragraphs, text).map {
            TextMatch.isSimilar(TextMatch.normalize(IconText.plain($0)), TextMatch.normalize(IconText.plain($1)), threshold: 0.90)
        }
        if similar.allSatisfy({ $0 }) {
            server.sendRegion(id: area.id, text: "", box: nil, lineHeight: nil)
            states[area.id]?.renderedKey = candidate.key
            states[area.id]?.renderedText = ""
            states[area.id]?.renderedIcons = nil
            states[area.id]?.renderedBox = nil
            states[area.id]?.renderedLineHeight = nil
            states[area.id]?.renderedLayout = nil
            states[area.id]?.renderedBlocks = nil
            return
        }
        let origin = fromPack == candidate.paragraphs.count ? "paket" : fromPack > 0 ? "paket+motor" : "motor"
        onLog?("ek alan \(area.name): \(SubtitleLog.excerpt(IconText.plain(candidate.paragraphs.joined(separator: " ")))) → \(origin)")
        let rendered = IconText.render(text.joined(separator: "\n"), slots: slots)
        let icons = rendered.icons.isEmpty ? nil : rendered.icons
        let blocks = candidate.blocks.map { Self.renderBlocks(candidate, layout: $0, text: text, similar: similar, slots: slots) }
        server.sendRegion(id: area.id, text: rendered.text, box: candidate.box, lineHeight: candidate.lineHeight, icons: icons,
                          blocks: blocks?.map(\.item))
        states[area.id]?.renderedKey = candidate.key
        states[area.id]?.renderedText = rendered.text
        states[area.id]?.renderedIcons = icons
        states[area.id]?.renderedBox = candidate.box
        states[area.id]?.renderedLineHeight = candidate.lineHeight
        states[area.id]?.renderedLayout = candidate.blocks
        states[area.id]?.renderedBlocks = blocks
    }

    /// Her grubun çevirisi kendi yeriyle. Çevirisi aslıyla aynı grup (ad, sayı) atlanır; İngilizcesi görünür kalır.
    /// `text` simge imlerini bütün alanın sırasıyla taşır; `slots` o sıradaki simgeler.
    static func renderBlocks(_ candidate: Candidate, layout: [Block], text: [String], similar: [Bool],
                             slots: [IconSlot]) -> [(group: Int, item: ScreenTranslation.Item)] {
        var result: [(group: Int, item: ScreenTranslation.Item)] = []
        for (index, block) in layout.enumerated() {
            let range = block.start..<(block.start + block.count)
            if range.allSatisfy({ similar[$0] }) { continue }
            let rendered = IconText.render(range.map { text[$0] }.joined(separator: "\n"), slots: slots)
            let source = range.map { IconText.plain(candidate.paragraphs[$0]) }.joined(separator: "\n")
            result.append((index, ScreenTranslation.Item(source: source, translation: rendered.text, box: block.box,
                                                          lineHeight: block.lineHeight,
                                                          icons: rendered.icons.isEmpty ? nil : rendered.icons)))
        }
        return result
    }

    /// Parçalar önce pakette aranır; kalanlar çeviri motoruna gider (yapay zekâda tek istekte). Motora simgeleri adlarla
    /// korunmuş metin (`prepared`) gider. Çevrilemeyen parça nil kalır.
    private func translate(_ paragraphs: [String], prepared: [IconText.Protected], packHits: inout Int) async throws -> [String?] {
        var result = paragraphs.map { packLookup?(IconText.plain($0))?.text }
        packHits = result.compactMap { $0 }.count
        // Zaten Türkçe olan parça çevrilmez: aslı kalır, tümü öyleyse kutu gösterilmez.
        for index in result.indices where result[index] == nil && skipsTurkish && TurkishText.isSure(paragraphs[index]) {
            result[index] = paragraphs[index]
        }
        let missing = result.indices.filter { result[$0] == nil }
        if !missing.isEmpty {
            let texts = missing.map { prepared[$0].text }
            let translations = translator.translatesTogether ? try await translator.translateAll(texts)
                : try await sequential(texts)
            for (index, translation) in zip(missing, translations) { result[index] = translation }
        }
        return result
    }

    private func sequential(_ texts: [String]) async throws -> [String] {
        var result: [String] = []
        for text in texts { result.append(try await translator.translate(text)) }
        return result
    }

    /// Çeviri durdu ya da hat yeniden kuruldu: süren tur bırakılır, TV'deki ek alan kutuları kalkar.
    public func reset() {
        session += 1
        running?.cancel()
        running = nil
        lastPass = nil
        let anyShown = states.values.contains { !$0.shown.isEmpty }
        states.removeAll()
        if anyShown { server.clearRegions() }
    }
}
