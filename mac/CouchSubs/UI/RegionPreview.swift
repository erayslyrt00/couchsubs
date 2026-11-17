// SPDX-License-Identifier: AGPL-3.0-only
// Mac ve iPhone uygulamalarının ortak parçası (iPhone projesi de bu dosyayı derler).

import CouchSubsCore
import SwiftUI

/// Alanın canlı önizlemesi: son PS5 karesi (yoksa yer tutucu), üstünde alanın çerçevesi; alanın dışı karartılır, okunacak
/// yer belirgin olur. Çerçeve içinden sürüklenerek taşınır, sekiz tutamacından (köşeler ve kenar ortaları) sürüklenerek
/// boyutlandırılır. `centered` (altyazı alanı): yatay boyut ortadan değişir, alan ortalı kalır. Kurallar `RegionEdit`'tedir
/// (Windows ve Android ile aynı); her değişiklik `onChange` ile bildirilir.
struct RegionPreview: View {
    let region: CropRegion
    let frame: CGImage?
    /// Yatay boyut ortadan değişir (altyazı alanı); ek alanlarda kapalı.
    var centered = false
    /// Kare yokken gösterilen yazı.
    var placeholder: LocalizedStringKey = "The game picture appears here once translating starts."
    let onChange: (CropRegion) -> Void

    /// Sürükleme başladığında bölge (sürükleme hep bu bölgeye göre hesaplanır).
    @State private var dragStart: CropRegion? = nil

    /// Görünen tutamaç ve parmakla tutma alanı (dokunmatikte küçük kare tutulmaz).
    private static let handle: CGFloat = 10
    private static let grip: CGFloat = 28

    var body: some View {
        GeometryReader { geometry in
            let size = geometry.size
            let box = CGRect(x: size.width * region.left, y: size.height * region.top,
                             width: size.width * (region.right - region.left), height: size.height * (region.bottom - region.top))
            ZStack(alignment: .topLeading) {
                if let frame {
                    Image(decorative: frame, scale: 1).resizable()
                } else {
                    RoundedRectangle(cornerRadius: Theme.radius("inner")).fill(Color.csBackground)
                    Text(placeholder)
                        .font(.csCaption).foregroundStyle(Color.csSecondary)
                        .multilineTextAlignment(.center)
                        .padding(Theme.space("m"))
                        .frame(maxWidth: .infinity, maxHeight: .infinity)
                }
                Path { path in
                    path.addRect(CGRect(origin: .zero, size: size))
                    path.addRect(box)
                }
                .fill(Color.black.opacity(0.5), style: FillStyle(eoFill: true))
                .allowsHitTesting(false)
                Rectangle()
                    .fill(Color.clear)
                    .contentShape(Rectangle())
                    .overlay(Rectangle().strokeBorder(Color.csAccent, lineWidth: 2))
                    .frame(width: box.width, height: box.height)
                    .offset(x: box.minX, y: box.minY)
                    .gesture(drag(.move, in: size))
                    .accessibilityLabel(Text("Subtitle area"))
                ForEach(Array(RegionEdit.handles(region).enumerated()), id: \.offset) { _, spot in
                    Rectangle()
                        .fill(Color.csAccent)
                        .frame(width: Self.handle, height: Self.handle)
                        .frame(width: Self.grip, height: Self.grip)
                        .contentShape(Rectangle())
                        .offset(x: size.width * spot.x - Self.grip / 2, y: size.height * spot.y - Self.grip / 2)
                        .gesture(drag(spot.handle, in: size))
                        .accessibilityHidden(true)
                }
            }
        }
        .aspectRatio(16 / 9, contentMode: .fit)
        .clipShape(RoundedRectangle(cornerRadius: Theme.radius("inner")))
    }

    private func drag(_ handle: RegionEdit.Handle, in size: CGSize) -> some Gesture {
        DragGesture(minimumDistance: 1)
            .onChanged { value in
                let start = dragStart ?? region
                dragStart = start
                onChange(RegionEdit.dragged(start, handle: handle, dx: value.translation.width / max(1, size.width),
                                            dy: value.translation.height / max(1, size.height), centered: centered))
            }
            .onEnded { _ in dragStart = nil }
    }
}

extension AreaPreset {
    /// Seçimin ekrandaki adı.
    var title: LocalizedStringKey {
        switch self {
        case .bottom: "Bottom area"
        case .top: "Top area"
        case .full: "Full screen"
        case .custom: "Custom"
        }
    }
}

/// Yüzde değeri ("40%"; Türkçede "%40").
func percentText(_ value: Double) -> String {
    String(format: String(localized: "%lld%%"), Int(value.rounded()))
}
