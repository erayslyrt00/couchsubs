// SPDX-License-Identifier: AGPL-3.0-only

@testable import CouchSubsCore
import XCTest

/// Oyuna göre alan profilinin ne zaman uygulandığı ve `GameAreas` akışı (Windows AreaProfilesTests/AreaProfileTests ve
/// Android AreaProfilesTest ile aynı örnekler).
final class AreaProfilesTests: XCTestCase {
    private let farCry = "far cry 3|stream"
    private let general = CropRegion(top: 0.62, bottom: 0.97, left: 0.14, right: 0.86)
    private let farCryArea = CropRegion(top: 0.70, bottom: 0.95, left: 0.20, right: 0.80)

    private func run(_ active: String?, _ game: String?, _ source: GameSense.Source?, area: AreaSource = .stream,
                     saved: [String]? = nil, declined: [String] = []) -> AreaProfiles.Step {
        let saved = saved ?? [farCry]
        return AreaProfiles.decide(active: active, game: game, source: source, areaSource: area,
                                   exists: { saved.contains($0) }, declined: { declined.contains($0) })
    }

    func testKeyUsesTheServersGameKeyAndTheSource() {
        XCTAssertEqual(AreaProfiles.key("Grand Theft Auto VI", source: .stream), "gta 6|stream")
        XCTAssertEqual(AreaProfiles.key("GTA 6", source: .window), "gta 6|window")
        XCTAssertNil(AreaProfiles.key("", source: .stream))
        XCTAssertNil(AreaProfiles.key(nil, source: .stream))
    }

    func testACertainGameAppliesAndAPackGuessIsOffered() {
        XCTAssertEqual(run(nil, "Far Cry 3", .console), .apply(farCry))
        XCTAssertEqual(run(nil, "Far Cry 3", .user), .apply(farCry))
        XCTAssertEqual(run(nil, "Far Cry 3", .pack), .offer(farCry))
    }

    func testTheSameProfileOrAnUnknownGameChangesNothing() {
        XCTAssertEqual(run(farCry, "Far Cry III", .console), AreaProfiles.Step.none)
        XCTAssertEqual(run(farCry, nil, nil), AreaProfiles.Step.none)
        XCTAssertEqual(run(nil, nil, nil), AreaProfiles.Step.none)
    }

    func testAnotherGameSwitchesOrGoesBackToTheGeneralAreas() {
        XCTAssertEqual(run(farCry, "Hades", .console), .useGeneral)
        XCTAssertEqual(run(nil, "Hades", .console), AreaProfiles.Step.none)
        XCTAssertEqual(run(farCry, "Hades", .user, saved: [farCry, "hades|stream"]), .apply("hades|stream"))
    }

    func testADeclinedProfileIsNotAppliedOrOfferedAgain() {
        XCTAssertEqual(run(nil, "Far Cry 3", .console, declined: [farCry]), AreaProfiles.Step.none)
        XCTAssertEqual(run(nil, "Far Cry 3", .pack, declined: [farCry]), AreaProfiles.Step.none)
        XCTAssertEqual(run("hades|stream", "Far Cry 3", .console, declined: [farCry]), .useGeneral)
    }

    func testTheWindowSourceHasItsOwnProfile() {
        XCTAssertEqual(run(nil, "Far Cry 3", .console, area: .window), AreaProfiles.Step.none)
        XCTAssertEqual(run(nil, "Far Cry 3", .console, area: .window, saved: [farCry, "far cry 3|window"]),
                       .apply("far cry 3|window"))
    }

    func testSavingMovesTheProfileToTheEndAndKeepsTwenty() {
        XCTAssertEqual(AreaProfiles.saved(["a", "b", "c"], "b") { $0 }, ["a", "c", "b"])
        let full = (0..<AreaProfiles.maxCount).map { "g\($0)" }
        let next = AreaProfiles.saved(full, "new") { $0 }
        XCTAssertEqual(next.count, AreaProfiles.maxCount)
        XCTAssertEqual(next.first, "g1")
        XCTAssertEqual(next.last, "new")
    }

    // MARK: - GameAreas

    @MainActor
    func testSavedAreasComeBackWithTheGameAndTheSessionEndsWithTheGeneralAreas() {
        var store = UserSettings()
        store.setCustomRegion(general)
        let areas = GameAreas(read: { store }, write: { store = $0 })
        XCTAssertNil(areas.target)
        areas.save()
        XCTAssertTrue(store.areaProfiles.isEmpty)

        areas.start()
        areas.follow(game: RunningGame(name: "Far Cry 3"), source: .user)
        XCTAssertEqual(areas.target, "Far Cry 3")
        store.setCustomRegion(farCryArea)
        store.extraAreas = [ExtraArea(name: "Görev", region: ExtraAreaPreset.quest.region)]
        areas.save()
        XCTAssertEqual(store.areaProfiles.map(\.key), [farCry])
        XCTAssertEqual(areas.activeGame, "Far Cry 3")
        XCTAssertFalse(areas.unsaved)
        areas.stop()

        // Profil yokken yapılan değişiklik genel ayardı: genel alanlar yeniden ayarlanır.
        store.setCustomRegion(general)
        store.extraAreas = []
        areas.start()
        areas.follow(game: RunningGame(name: "Far Cry III"), source: .console)
        XCTAssertEqual(store.region, farCryArea)
        XCTAssertEqual(store.extraAreas.count, 1)
        XCTAssertEqual(store.generalAreas?.region, general)

        store.setCustomRegion(general)
        XCTAssertTrue(areas.unsaved)
        areas.stop()
        XCTAssertEqual(store.region, general)
        XCTAssertTrue(store.extraAreas.isEmpty)
        XCTAssertNil(store.activeAreaProfile)
        XCTAssertNil(store.generalAreas)
        XCTAssertEqual(store.areaProfiles.first?.areas.region, farCryArea)
    }

    @MainActor
    func testUndoOfferAndAnotherGame() {
        var store = UserSettings()
        store.setCustomRegion(general)
        store.areaProfiles = [AreaProfile(key: farCry, game: "Far Cry 3",
                                          areas: AreaSet(region: farCryArea, areaPreset: .custom, extraAreas: []))]
        let areas = GameAreas(read: { store }, write: { store = $0 })
        areas.start()

        // Paketten tanınan oyunda sorulur; "Hayır" bu oturumda yeniden sormaz.
        areas.follow(game: RunningGame(name: "Far Cry 3"), source: .pack)
        XCTAssertEqual(areas.offeredGame, "Far Cry 3")
        XCTAssertEqual(store.region, general)
        areas.declineOffered()
        areas.follow(game: RunningGame(name: "Far Cry 3"), source: .pack)
        XCTAssertNil(areas.offered)
        areas.stop()

        // "Kullan".
        areas.start()
        areas.follow(game: RunningGame(name: "Far Cry 3"), source: .pack)
        areas.useOffered()
        XCTAssertEqual(store.region, farCryArea)
        // Elle seçilince aynı profil yeniden uygulanmaz.
        areas.follow(game: RunningGame(name: "Far Cry 3"), source: .user)
        XCTAssertEqual(store.activeAreaProfile, farCry)

        // "Geri al": genel alanlar, aynı oturumda yeniden uygulanmaz.
        areas.undo()
        XCTAssertEqual(store.region, general)
        areas.follow(game: RunningGame(name: "Far Cry 3"), source: .console)
        XCTAssertEqual(store.region, general)
        areas.stop()

        // Başka (profilsiz) oyun genel alanlara döndürür; silme de.
        areas.start()
        areas.follow(game: RunningGame(name: "Far Cry 3"), source: .console)
        XCTAssertEqual(store.region, farCryArea)
        areas.follow(game: RunningGame(name: "Hades"), source: .console)
        XCTAssertEqual(store.region, general)
        areas.follow(game: RunningGame(name: "Far Cry 3"), source: .console)
        areas.delete()
        XCTAssertTrue(store.areaProfiles.isEmpty)
        XCTAssertEqual(store.region, general)
    }

    @MainActor
    func testAProfileLeftOpenIsClosedAtStartupAndOldSettingsHaveNone() throws {
        var open = UserSettings()
        open.setCustomRegion(general)
        open.saveAreaProfile(for: "Far Cry 3")
        open.setCustomRegion(farCryArea)
        let restored = GameAreas.restored(open)
        XCTAssertEqual(restored.region, general)
        XCTAssertNil(restored.activeAreaProfile)
        XCTAssertEqual(restored.areaProfiles.count, 1)

        let old = try JSONDecoder().decode(UserSettings.self, from: Data(#"{"region":{"top":0.5,"bottom":0.9,"left":0.1,"right":0.9}}"#.utf8))
        XCTAssertTrue(old.areaProfiles.isEmpty)
        XCTAssertNil(old.activeAreaProfile)
        let copy = try JSONDecoder().decode(UserSettings.self, from: JSONEncoder().encode(restored))
        XCTAssertEqual(copy.areaProfiles, restored.areaProfiles)
    }
}
