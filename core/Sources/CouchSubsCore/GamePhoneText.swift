// SPDX-License-Identifier: AGPL-3.0-only

import Foundation

enum GamePhoneText {
    private static let identifier = try! NSRegularExpression(pattern: #"(?<![\p{L}\p{N}_])[@#][\p{L}\p{N}_]+(?:[.-][\p{L}\p{N}_]+)*"#)
    private static let chatWord = try! NSRegularExpression(pattern: #"(?<![\p{L}\p{N}_@#.-])(?:2gether|4u)(?![\p{L}\p{N}_])"#)
    private static let misreadTen = try! NSRegularExpression(pattern: #"(?<![\p{L}\p{N}_@#])(?:IO|I0|lO|l0)(?=\s+min(?:s|utes?)?\b)"#)

    static func containsIdentifier(_ text: String) -> Bool {
        (text.contains("@") || text.contains("#")) && identifier.firstMatch(in: text, range: NSRange(text.startIndex..., in: text)) != nil
    }

    static func isWrittenChatWord(_ token: String) -> Bool {
        (token.contains("2") || token.contains("4")) && chatWord.firstMatch(in: token, range: NSRange(token.startIndex..., in: token)) != nil
    }

    static func englishModelInput(_ text: String) -> String {
        var value = text
        if text.contains("min") {
            value = misreadTen.stringByReplacingMatches(in: text, range: NSRange(text.startIndex..., in: text), withTemplate: "10")
        }
        guard isWrittenChatWord(value) else { return value }
        let out = NSMutableString(string: value)
        for match in chatWord.matches(in: value, range: NSRange(value.startIndex..., in: value)).reversed() {
            out.replaceCharacters(in: match.range, with: out.substring(with: match.range) == "2gether" ? "together" : "for you")
        }
        return out as String
    }

    static func keepIdentifiers(_ source: String, translation: String?) -> String? {
        guard let translation, !translation.isEmpty, containsIdentifier(source) else { return translation }
        let original = source as NSString
        var names = identifier.matches(in: source, range: NSRange(source.startIndex..., in: source)).map { original.substring(with: $0.range) }
        let result = NSMutableString(string: translation)
        for match in identifier.matches(in: translation, range: NSRange(translation.startIndex..., in: translation)).reversed() {
            let name = result.substring(with: match.range)
            guard let index = names.lastIndex(where: { $0.caseInsensitiveCompare(name) == .orderedSame }) else { return nil }
            result.replaceCharacters(in: match.range, with: names.remove(at: index))
        }
        return names.isEmpty ? result as String : nil
    }

    private static func isTag(_ token: Substring) -> Bool {
        guard token.first == "@" || token.first == "#" else { return false }
        let value = String(token), range = NSRange(value.startIndex..., in: value)
        return identifier.firstMatch(in: value, range: range)?.range == range
    }

    private static func section(_ text: String) -> String? {
        let value = String(text.drop(while: { !$0.isLetter })).trimmingCharacters(in: .whitespacesAndNewlines)
        return ["Messages", "Suggested", "Suggestions", "Öneriler", "Mesajlar"].contains(value) ? value : nil
    }

    private static func looksLikeName(_ text: String) -> Bool {
        let label = String(text.prefix(while: { $0 != "(" })).trimmingCharacters(in: .whitespacesAndNewlines)
        let words = label.split(separator: " ")
        guard section(label) == nil, (1...4).contains(words.count), words[0].first?.isUppercase == true else { return false }
        return words.allSatisfy { word in
            (word.first?.isUppercase == true || ["de","du","of"].contains(String(word))) &&
            word.allSatisfy { $0.isLetter || $0 == "-" || $0 == "'" }
        }
    }

    static func isMessagePreview(_ text: String) -> Bool {
        text.contains(where: \.isWhitespace) && section(text) == nil && !looksLikeName(text)
    }

    private static func layout(_ line: TextLine) -> TextBox? { line.phoneBox ?? line.box }

    private static func isContactPair(_ name: TextLine, _ preview: TextLine) -> Bool {
        let label = name.text.trimmingCharacters(in: .whitespacesAndNewlines)
        let lowerName = (2...18).contains(label.count) && label.allSatisfy(\.isLowercase)
        guard looksLikeName(label) || lowerName, preview.text.trimmingCharacters(in: .whitespacesAndNewlines).contains(" "),
              let a = layout(name), let b = layout(preview) else { return false }
        let h = a.bottom - a.top
        return a.left > 0.1 && a.left < 0.48 && abs(a.left - b.left) <= 0.06 &&
            b.top > a.top && b.top - a.bottom < h * 0.7 && b.bottom - b.top < h * 1.8
    }

    private static func previewPunctuation(_ line: TextLine) -> TextLine {
        guard let box = layout(line), box.right >= 0.76 else { return line }
        let tail = line.text.reversed().prefix { ".,„".contains($0) }
        let stem = line.text.dropLast(tail.count)
        guard (2...3).contains(tail.count), tail.last == ".", stem.last?.isLetter == true,
              !line.text.hasSuffix("...") else { return line }
        var copy = line
        copy.text = String(stem) + "..."
        return copy
    }

    static func forTranslation(_ reading: Reading) -> Reading {
        var lines = reading.lines.filter { line in
            let words = line.text.split(whereSeparator: \.isWhitespace)
            return !(words.contains(where: isTag) && words.allSatisfy { isTag($0) || !$0.contains(where: { $0.isLetter || $0.isNumber }) })
        }
        let headings = Set(lines.compactMap { section($0.text) })
        guard !headings.isDisjoint(with: ["Suggested","Suggestions","Öneriler"]),
              !headings.isDisjoint(with: ["Messages","Mesajlar"]) else { return Reading(lines: lines) }
        for i in lines.indices { if let heading = section(lines[i].text) { lines[i].text = heading } }
        let order = lines.indices.filter { layout(lines[$0]) != nil }.sorted { layout(lines[$0])!.top < layout(lines[$1])!.top }
        var names: [Int] = [], previews: Set<Int> = []
        var i = 0
        while i + 1 < order.count {
            let name = order[i], preview = order[i + 1]
            if isContactPair(lines[name], lines[preview]) {
                names.append(name)
                previews.insert(preview)
                i += 2
            } else { i += 1 }
        }
        guard names.count >= 3, let first = layout(lines[names[0]]), let last = layout(lines[names[names.count - 1]]),
              last.top > first.top else { return Reading(lines: lines) }
        let slope = (last.left - first.left) / (last.top - first.top)
        func inColumn(_ line: TextLine) -> Bool {
            guard let box = layout(line) else { return false }
            return abs(box.left - (first.left + slope * (box.top - first.top))) <= 0.06
        }
        guard names.allSatisfy({ inColumn(lines[$0]) }) else { return Reading(lines: lines) }
        return Reading(lines: lines.indices.compactMap { index in
            let line = lines[index]
            if previews.contains(index) { return previewPunctuation(line) }
            return inColumn(line) && (names.contains(index) || looksLikeName(line.text)) ? nil : line
        })
    }

    static func groups(_ reading: Reading) -> [ScreenTranslation.Group] {
        let flat = reading.lines.allSatisfy { $0.phoneBox != nil }
        func box(_ line: TextLine) -> TextBox { (flat ? line.phoneBox : line.box)! }
        var groups: [ScreenTranslation.Group] = [], run: [TextLine] = []
        func finish() {
            guard !run.isEmpty else { return }
            defer { run.removeAll(keepingCapacity: true) }
            if !flat {
                groups += ScreenTranslation.groups(Reading(lines: run), keepIdentifiers: true)
                return
            }
            let clean = Reading(lines: run).withButtonIcons().withoutButtonPrompts().withoutOcrNoise(keepIdentifiers: true)
                .lines.filter { $0.box != nil && ScreenTranslation.isWords($0.text) }
            guard let first = clean.first?.box else { return }
            let place = clean.dropFirst().reduce(first) { $0.union($1.box!) }
            let heights = run.map { box($0).bottom - box($0).top }.sorted()
            let icons = clean.flatMap { $0.icons ?? [] }
            groups.append(.init(paragraphs: [clean.map(\.text).joined(separator: " ")], box: place,
                                lineHeight: heights[heights.count / 2], icons: icons.isEmpty ? nil : [icons]))
        }
        for line in reading.lines.filter({ $0.box != nil }).sorted(by: { box($0).top < box($1).top }) {
            if let previous = run.last, breaksBlock(box(previous), box(line)) { finish() }
            run.append(line)
        }
        finish()
        return Array(groups.prefix(ScreenTranslation.maxGroups))
    }

    private static func breaksBlock(_ previous: TextBox, _ next: TextBox) -> Bool {
        if next.top - previous.bottom > max(previous.bottom - previous.top, next.bottom - next.top) { return true }
        let alignedLeft = abs(previous.left - next.left) <= 0.08
        let alignedRight = min(previous.left, next.left) > 0.25 && abs(previous.right - next.right) <= 0.045
        return !alignedLeft && !alignedRight
    }
}
