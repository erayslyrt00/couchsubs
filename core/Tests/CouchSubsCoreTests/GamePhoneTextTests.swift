// SPDX-License-Identifier: AGPL-3.0-only

import XCTest
@testable import CouchSubsCore

final class GamePhoneTextTests: XCTestCase {
    private func line(_ text: String, _ y: Double, _ left: Double = 0.2, _ right: Double = 0.8) -> TextLine {
        TextLine(text, box: TextBox(left: left, top: y, right: right, bottom: y + 0.031))
    }

    func testCaptionsRemainTranslatableButStandaloneTagsAndUserNamesStayInTheGame() {
        let reading = Reading(lines: [line("@vice.305", 0.63), line("Meet me #NightRide", 0.68),
                                      line("#sunset #leonida", 0.73), line("Tell @vice.305 to wait.", 0.80)])
        let kept = GamePhoneText.forTranslation(reading)
        XCTAssertEqual(GamePhoneText.groups(kept).flatMap(\.paragraphs), ["Meet me #NightRide", "Tell @vice.305 to wait."])
        let source = "Send the photo to @KaterinaDiva."
        XCTAssertEqual(GamePhoneText.keepIdentifiers(source, translation: "Fotoğrafı @katerinadiva'ya gönder."),
                       "Fotoğrafı @KaterinaDiva'ya gönder.")
        XCTAssertNil(GamePhoneText.keepIdentifiers(source, translation: "Fotoğrafı ona gönder."))
        XCTAssertNil(GamePhoneText.keepIdentifiers(source, translation: "Fotoğrafı @Joey'e gönder."))
    }

    func testEnglishShorthandAndNumericOcrCorrectionRespectNamesAndCodes() {
        let text = "We're in this 2gether. I left it 4u."
        XCTAssertEqual(GamePhoneText.englishModelInput(text), "We're in this together. I left it for you.")
        XCTAssertEqual(GamePhoneText.groups(Reading(lines: [line(text, 0.5)])).flatMap(\.paragraphs), [text])
        let codes = "Use B4, @4u, #2gether or gate C4."
        XCTAssertEqual(GamePhoneText.englishModelInput(codes), codes)
        XCTAssertEqual(GamePhoneText.englishModelInput("IO min out."), "10 min out.")
        XCTAssertEqual(GamePhoneText.englishModelInput("IO port. I owe him."), "IO port. I owe him.")
    }

    func testContactsOnlyRemovedInRecognizableMessageListAndCutWordsAreNeverCompleted() {
        let rows = [line("Suggestions", 0.04), line("Chase", 0.12), line("call me after ...", 0.16),
                    line("May", 0.25), line("Don't leave yet.", 0.29), line("Messages", 0.42),
                    line("Rose", 0.54), line("Meet me at 06:40.", 0.58)]
        let kept = GamePhoneText.forTranslation(Reading(lines: rows))
        XCTAssertEqual(kept.lines.map(\.text), ["Suggestions", "call me after ...", "Don't leave yet.", "Messages", "Meet me at 06:40."])
        XCTAssertTrue(GamePhoneText.forTranslation(Reading(lines: Array(rows.dropFirst()))).lines.map(\.text).contains("May"))
    }

    func testOpposingRepliesAndWrappedMessagesUseFlatDisplayCoordinates() {
        let rows = [line("Find someone who", 0.30, 0.10, 0.69), line("knows this road.", 0.334, 0.10, 0.64),
                    line("on my way", 0.372, 0.38, 0.93)]
        XCTAssertEqual(GamePhoneText.groups(Reading(lines: rows)).flatMap(\.paragraphs), ["Find someone who knows this road.", "on my way"])
        func tilted(_ text: String, _ x: Double, _ y: Double, _ right: Double) -> TextLine {
            var row = TextLine(text, box: TextBox(left: x, top: y, right: right, bottom: y + 0.068))
            row.phoneBox = TextBox(left: x, top: y, right: right, bottom: y + 0.026)
            return row
        }
        let angled = [tilted("That was quite a", 0.1, 0.20, 0.75), tilted("surprise!", 0.1, 0.237, 0.35),
                      tilted("Did you see it?", 0.1, 0.325, 0.70), tilted("Ask later", 0.52, 0.78, 0.88),
                      tilted("No problem", 0.50, 0.862, 0.88)]
        let groups = GamePhoneText.groups(Reading(lines: angled))
        XCTAssertEqual(groups.flatMap(\.paragraphs), ["That was quite a surprise!", "Did you see it?", "Ask later", "No problem"])
        XCTAssertEqual(groups.first?.box, angled[0].box!.union(angled[1].box!))
    }
}
