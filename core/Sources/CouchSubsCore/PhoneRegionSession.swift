// SPDX-License-Identifier: AGPL-3.0-only

import CoreGraphics
import Foundation

/// Tek telefon açılışının son karesi, okunmuş balonları ve birebir çevirileri. Ana altyazı ve
/// elle seçilen bölgeler kendi saatlerinde kalır. Telefon kapanınca bu oturum tekrar kullanılmaz.
@MainActor
final class PhoneRegionSession {
    private struct Capture: Sendable {
        let frame: CGImage
        let pose: GamePhoneGeometry.Pose
        var region: CropRegion { pose.bounds }

        func isPresent() -> Bool {
            guard let found = GamePhoneGeometry.locate(frame) else { return false }
            return region.isClose(to: found.bounds, tolerance: 0.035)
        }
    }

    private struct Bubble: Sendable {
        var group: ScreenTranslation.Group
        let style: PhoneTextStyle
    }

    private struct Page: Sendable {
        let region: CropRegion
        let signature: [UInt8]
        let pixels: GamePhonePage
        let bubbles: [Bubble]
    }

    private let reader: TextReader
    private let translator: TextTranslator
    private let server: RegionSending
    private var latest: Capture?
    private var lastPass: Int?
    private var task: Task<Void, Never>?
    private var page: Page?
    private var rendered: [ScreenTranslation.Item] = []
    private var translations: [String: String] = [:]
    private var keys: [String] = []
    private var nextBubble = 0
    private var anchors: [(prefix: String, x: Double, y: Double)] = []
    private var needsWork = false
    private var emptyOnce = false

    init(reader: TextReader, translator: TextTranslator, server: RegionSending) {
        self.reader = reader
        self.translator = translator
        self.server = server
    }

    func observe(_ frame: CGImage, pose: GamePhoneGeometry.Pose) {
        latest = Capture(frame: frame, pose: pose)
    }

    func beginIfDue(_ time: Int) -> Bool {
        guard latest != nil, lastPass.map({ time - $0 >= 450 }) ?? true else { return false }
        lastPass = time
        return true
    }

    func close() {
        latest = nil
        task?.cancel()
        publish([])
    }

    func visit(packLookup: ((String) -> PackMatch?)?, skipsTurkish: Bool, english: Bool) async {
        guard latest != nil else { return }
        let work = Task {
            await update(packLookup: packLookup, skipsTurkish: skipsTurkish, english: english)
        }
        task = work
        await work.value
        task = nil
    }

    private func read(_ capture: Capture, signature: [UInt8]) async throws -> Page {
        let reader = self.reader
        return try await ReadQueue.shared.run {
            let region = capture.region
            let reading = GamePhoneText.forTranslation(try GamePhoneReader.read(capture.frame, region: region,
                                                                                pose: capture.pose, reader: reader))
            let crop = RegionCropper.crop(capture.frame, to: region)
            let place = TextBox(left: region.left, top: region.top, right: region.right, bottom: region.bottom)
            let bubbles = ScreenTranslation.withImages(GamePhoneText.groups(reading), from: crop).map { group in
                let style = GamePhonePalette.sample(crop, box: group.box)
                var placed = group
                placed.box = group.box.placed(in: place)
                placed.lineHeight *= region.bottom - region.top
                return Bubble(group: placed, style: style)
            }
            return Page(region: region, signature: signature,
                        pixels: GamePhonePage(frame: capture.frame, region: region, reading: reading), bubbles: bubbles)
        }
    }

    private func update(packLookup: ((String) -> PackMatch?)?, skipsTurkish: Bool, english: Bool) async {
        guard let start = latest, start.isPresent() else { clearMissing(); return }
        let signature = RegionCropper.signature(of: RegionCropper.crop(start.frame, to: start.region))
        let changed = page.map {
            !$0.region.isClose(to: start.region, tolerance: 0.01)
                || ReadScheduler.changed($0.signature, signature, threshold: ReadScheduler.defaultChangeThreshold)
                || $0.pixels.changed(start.frame, region: start.region)
        } ?? true
        if changed || emptyOnce {
            do { page = try await read(start, signature: signature) }
            catch { page = nil; return }
        } else if !needsWork && !emptyOnce { return }
        guard !Task.isCancelled, var current = page, var verified = latest else { return }
        // Okuma sürerken farklı bir sayfa gelmişse eski sayfadan yeni çeviri isteği çıkarma.
        if start.frame !== verified.frame, !matches(current, verified) {
            guard let fresh = await reread(verified) else { clearMissing(); return }
            current = fresh
        }
        guard latest != nil, !Task.isCancelled else { return }
        if current.bubbles.isEmpty {
            if emptyOnce || rendered.isEmpty {
                publish([])
                emptyOnce = false
            } else { emptyOnce = true }
            needsWork = false
            return
        }
        emptyOnce = false
        let groups = current.bubbles.map(\.group)
        let paragraphs = groups.flatMap(\.paragraphs)
        var available = availableTranslations(paragraphs, packLookup: packLookup, skipsTurkish: skipsTurkish)
        // Önce yalnız güncel sayfayla eşleşen balonlar kalır. Örneğin yeni WhatUp yanıtı ağdayken
        // önceki yanıtın üstü kendi özgün yazısıyla görünür, bitmiş komşu balonlar okunabilir.
        publish(items(current, translations: available))
        var missing = paragraphs.filter { available[$0] == nil }
        guard !missing.isEmpty else { needsWork = false; return }
        if !translator.translatesTogether {
            followPage(current)
            for offset in groups.indices {
                let index = (nextBubble + offset) % groups.count
                let lead = groups[index].paragraphs.filter { available[$0] == nil }
                if lead.contains(where: GamePhoneText.isMessagePreview) {
                    missing = lead
                    nextBubble = (index + 1) % groups.count
                    break
                }
            }
        }
        var unique: Set<String> = []
        missing = missing.filter { unique.insert($0).inserted }
        let inputs = missing.map {
            let text = IconText.protect($0).text
            return english ? GamePhoneText.englishModelInput(text) : text
        }
        do {
            let result = try await translator.translateAll(inputs)
            guard !Task.isCancelled, latest != nil else { return }
            guard result.count == inputs.count else { needsWork = true; return }
            for (source, translated) in zip(missing, result) where !translated.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                remember(source, translated)
                available[source] = translated
            }
        } catch { needsWork = true; return }

        guard let latest else { return }
        if latest.frame !== verified.frame, !matches(current, latest) {
            verified = latest
            guard let fresh = await reread(verified) else { clearMissing(); return }
            current = fresh
            available = availableTranslations(current.bubbles.flatMap { $0.group.paragraphs },
                                                packLookup: packLookup, skipsTurkish: skipsTurkish)
        }
        guard !Task.isCancelled, self.latest != nil else { return }
        publish(items(current, translations: available))
        needsWork = current.bubbles.contains { $0.group.paragraphs.contains { available[$0] == nil } }
    }

    private func matches(_ page: Page, _ capture: Capture) -> Bool {
        capture.isPresent() && page.region.isClose(to: capture.region, tolerance: 0.01)
            && !page.pixels.changed(capture.frame, region: capture.region)
    }

    private func reread(_ capture: Capture) async -> Page? {
        guard capture.isPresent(), !Task.isCancelled else { return nil }
        let signature = RegionCropper.signature(of: RegionCropper.crop(capture.frame, to: capture.region))
        guard let fresh = try? await read(capture, signature: signature), let last = latest, !Task.isCancelled,
              last.frame === capture.frame || matches(fresh, last) else { return nil }
        page = fresh
        return fresh
    }

    private func clearMissing() {
        page = nil
        emptyOnce = false
        needsWork = false
        publish([])
    }

    private func remember(_ source: String, _ translation: String) {
        if translations[source] == nil {
            if keys.count >= 2 * ScreenTranslation.maxGroups { translations[keys.removeFirst()] = nil }
            keys.append(source)
        }
        translations[source] = translation
    }

    private func availableTranslations(_ paragraphs: [String], packLookup: ((String) -> PackMatch?)?,
                                       skipsTurkish: Bool) -> [String: String] {
        var result: [String: String] = [:]
        for text in paragraphs {
            let known = packLookup?(IconText.plain(text))?.text ?? translations[text]
            result[text] = known ?? ((skipsTurkish && TurkishText.isSure(text)) ? text : nil)
        }
        return result
    }

    private func items(_ page: Page, translations: [String: String]) -> [ScreenTranslation.Item] {
        page.bubbles.compactMap { bubble in
            let group = bubble.group
            let result = group.paragraphs.compactMap { GamePhoneText.keepIdentifiers($0, translation: translations[$0]) }
            guard result.count == group.paragraphs.count,
                  var item = ScreenTranslation.item(group, group.paragraphs.map(IconText.protect), result) else { return nil }
            item.style = bubble.style
            return item
        }
    }

    private func publish(_ items: [ScreenTranslation.Item]) {
        guard rendered != items else { return }
        rendered = items
        let boxes = items.map(\.box)
        let box = boxes.dropFirst().reduce(boxes.first) { $0?.union($1) }
        let heights = items.map(\.lineHeight).sorted()
        server.sendRegion(id: "game-phone", text: items.map(\.translation).joined(separator: "\n"), box: box,
                          lineHeight: heights.isEmpty ? nil : heights[heights.count / 2], icons: nil,
                          blocks: items.isEmpty ? nil : items)
    }

    private func followPage(_ page: Page) {
        let w = page.region.right - page.region.left, h = page.region.bottom - page.region.top
        let current: [(prefix: String, x: Double, y: Double)] = page.bubbles.compactMap { bubble in
            guard let first = bubble.group.paragraphs.first, GamePhoneText.isMessagePreview(first) else { return nil }
            return (TextMatch.normalize(first).split(separator: " ").prefix(3).joined(separator: " "),
                    (bubble.group.box.left - page.region.left) / w, (bubble.group.box.top - page.region.top) / h)
        }
        if !current.isEmpty {
            if !current.contains(where: { b in anchors.contains { a in
                a.prefix == b.prefix && abs(a.x - b.x) <= 0.10 && abs(a.y - b.y) <= 0.08
            } }) { nextBubble = 0 }
            anchors = current
        }
    }
}
