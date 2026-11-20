// SPDX-License-Identifier: AGPL-3.0-only

import Foundation

/// İngilizce tuş çubuğundaki kısa komutlar (GameTranslator UiPrompts, bkz. THIRD-PARTY-NOTICES.md).
enum UiCommands {
    private static let commands: [String: String] = [
        "ADD MARKER": "İşaret ekle", "SET DESTINATION": "Hedef belirle", "REMOVE DESTINATION": "Hedefi kaldır",
        "RECENTER": "Ortala", "2D VIEW": "2B görünüm", "3D VIEW": "3B görünüm",
        "ZOOM": "Yakınlaştır", "EXIT": "Çıkış", "BACK": "Geri",
        "SELECT": "Seç", "CLOSE": "Kapat", "CLOSE MENU": "Menüyü kapat",
        "MENU": "Menü", "OPEN": "Aç", "NEXT PAGE": "Sonraki sayfa",
        "PREVIOUS PAGE": "Önceki sayfa", "SHOW TEXT": "Metni göster", "SHOW": "Göster",
        "FLIP": "Çevir", "PUT DOWN": "Bırak", "PICK UP": "Al",
        "DRINK": "İç", "EAT": "Ye", "WEAR": "Giy",
        "TAKE OFF": "Çıkar", "BUY": "Satın al", "SELL": "Sat",
        "PAY": "Öde", "PAY LATER": "Sonra öde", "PAY NOW": "Şimdi öde",
        "ACCEPT": "Kabul et", "DECLINE": "Reddet", "YES": "Evet",
        "NO": "Hayır", "CONFIRM": "Onayla", "CANCEL": "İptal",
        "GOODBYE": "Hoşça kal", "GREET": "Selamla", "TALK": "Konuş",
        "DEFUSE": "Yatıştır", "PROVOKE": "Kışkırt", "WARN": "Uyar",
        "OFFER RIDE": "Arabaya çağır", "ENTER": "Gir", "LEAVE": "Ayrıl",
        "SKIP": "Geç", "CONTINUE": "Devam et", "RETRY": "Tekrar dene",
        "RESTORE ALL": "Hepsini geri yükle", "RESTORE DEFAULT": "Varsayılana döndür", "PREVIEW": "Önizle",
        "PAUSE PREVIEW": "Önizlemeyi durdur", "ADJUST": "Ayarla", "SCROLL": "Kaydır",
        "REFUEL": "Yakıt al", "LIFT": "Kaldır", "ORDER": "Sipariş ver",
        "MAYBE LATER": "Belki sonra", "PHOTO STUDIO": "Fotoğraf stüdyosu", "REMINDERS": "Hatırlatıcılar",
        "ROTATE": "Döndür", "INTERACT": "Etkileşim", "USE": "Kullan",
        "KNOCK": "Kapıyı çal", "CALL": "Ara", "HANG UP": "Telefonu kapat",
        "ANSWER": "Cevapla", "IGNORE": "Yoksay", "REPLY": "Yanıtla",
        "DELETE": "Sil", "SAVE": "Kaydet", "VIEW": "Görüntüle",
        "INSPECT": "İncele", "SEARCH": "Ara", "HIDE": "Saklan",
        "JOIN": "Katıl", "TERMS": "Koşullar", "MARKERS": "İşaretler",
        "DROP": "Bırak", "EQUIP": "Kuşan", "UNEQUIP": "Çıkar",
        "STORE": "Dükkân", "HELP": "Yardım", "OPTIONS": "Seçenekler",
        "CAMERA": "Kamera", "TOGGLE": "Aç / kapat", "GRAB": "Tut",
        "SIT": "Otur", "STAND UP": "Kalk", "SLEEP": "Uyu",
        "SHOWER": "Duş al", "TURN OFF": "Kapat", "TURN ON": "Aç",
        "WATCH": "İzle", "LISTEN": "Dinle", "DRIVE": "Sür",
        "GET IN": "Bin", "GET OUT": "İn", "STEAL": "Çal",
        "HACK": "Hackle", "LOOT": "Yağmala", "COLLECT": "Topla",
    ]

    /// Bütün parçalar bilinen büyük harfli komutlarsa çevirir; aralarındaki ve sondaki simgeleri korur.
    static func translate(_ paragraph: String) -> String? {
        let marker = String(ButtonIcons.marker)
        var translated: [String] = []
        var found = false
        for part in paragraph.components(separatedBy: marker) {
            let text = part.trimmingCharacters(in: .whitespacesAndNewlines)
                .trimmingCharacters(in: CharacterSet(charactersIn: ".,:!?"))
                .trimmingCharacters(in: .whitespacesAndNewlines)
            if text.isEmpty {
                translated.append("")
            } else {
                guard let command = commands[text] else { return nil }
                translated.append(command)
                found = true
            }
        }
        guard found else { return nil }
        return translated.joined(separator: " \(marker) ").split(separator: " ").joined(separator: " ")
    }
}
