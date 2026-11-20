// SPDX-License-Identifier: AGPL-3.0-only

import CoreGraphics
import Foundation

enum GamePhoneReader {
    /// Read the deskewed screen once. Keep its flat coordinates for grouping, and return real game coordinates for drawing.
    static func read(_ frame: CGImage, region: CropRegion, pose: GamePhoneGeometry.Pose, reader: any TextReader) throws -> Reading {
        let topX = pose.topX * Double(frame.width), topY = pose.topY * Double(frame.height)
        let dx = (pose.bottomX - pose.topX) * Double(frame.width), dy = (pose.bottomY - pose.topY) * Double(frame.height)
        guard dy > 0, region.right > region.left, region.bottom > region.top else { return .empty }
        func point(_ u: Double, _ v: Double) -> CGPoint {
            let t = 0.04 + 0.915 * v, side = (u - 0.5) * 0.52
            return CGPoint(x: topX + dx * t + side * dy,
                           y: topY + dy * t - side * dx * (0.52 + v * 0.30))
        }
        func rect(_ box: TextBox) -> CGRect {
            let a = point(box.left, box.top), b = point(box.right, box.top)
            let c = point(box.left, box.bottom), d = point(box.right, box.bottom)
            let x = min(a.x,b.x,c.x,d.x), y = min(a.y,b.y,c.y,d.y)
            return CGRect(x: x, y: y, width: max(a.x,b.x,c.x,d.x) - x, height: max(a.y,b.y,c.y,d.y) - y)
        }
        let whole = TextBox(left: 0, top: 0, right: 1, bottom: 1)
        guard let pixels = GamePhonePixels(frame, in: rect(whole).insetBy(dx: -2, dy: -2)) else { return .empty }
        let scale = min(dy * 0.5 < 300 ? 1.5 : 1, 960 / (dy * 0.9))
        let width = max(1, Int((dy * 0.5 * scale).rounded(.toNearestOrEven)))
        let height = max(1, Int((dy * 0.9 * scale).rounded(.toNearestOrEven)))
        var rgba = Data(count: width * height * 4)
        rgba.withUnsafeMutableBytes { raw in
            let output = raw.bindMemory(to: UInt8.self)
            pixels.bytes.withUnsafeBufferPointer { input in
                for y in 0..<height {
                    let v = (Double(y) + 0.5) / Double(height)
                    let left = point(0, v), right = point(1, v)
                    let stepX = (right.x - left.x) / Double(width), stepY = (right.y - left.y) / Double(width)
                    var sx = left.x + stepX * 0.5 - 0.5, sy = left.y + stepY * 0.5 - 0.5
                    for x in 0..<width {
                        let px = min(Double(pixels.width - 1), max(0, sx - Double(pixels.x)))
                        let py = min(Double(pixels.height - 1), max(0, sy - Double(pixels.y)))
                        let x0 = Int(px), y0 = Int(py), wx = Int((px - Double(x0)) * 256), wy = Int((py - Double(y0)) * 256)
                        let a = (y0 * pixels.width + x0) * 4, b = x0 + 1 < pixels.width ? a + 4 : a
                        let row = y0 + 1 < pixels.height ? pixels.width * 4 : 0, out = (y * width + x) * 4
                        for channel in 0..<3 {
                            let upper = Int(input[a+channel]) * (256-wx) + Int(input[b+channel]) * wx
                            let lower = Int(input[a+row+channel]) * (256-wx) + Int(input[b+row+channel]) * wx
                            output[out+channel] = UInt8((upper * (256-wy) + lower * wy + 32768) >> 16)
                        }
                        output[out+3] = 255
                        sx += stepX; sy += stepY
                    }
                }
            }
        }
        guard let provider = CGDataProvider(data: rgba as CFData),
              let flat = CGImage(width: width, height: height, bitsPerComponent: 8, bitsPerPixel: 32, bytesPerRow: width * 4,
                                 space: frame.colorSpace.flatMap { $0.model == .rgb ? $0 : nil } ?? CGColorSpaceCreateDeviceRGB(),
                                 bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.premultipliedLast.rawValue | CGBitmapInfo.byteOrder32Big.rawValue),
                                 provider: provider, decode: nil, shouldInterpolate: false, intent: .defaultIntent) else { return .empty }
        func map(_ box: TextBox) -> TextBox {
            let q = rect(box)
            func x(_ n: CGFloat) -> Double { min(1,max(0,(n / Double(frame.width) - region.left) / (region.right - region.left))) }
            func y(_ n: CGFloat) -> Double { min(1,max(0,(n / Double(frame.height) - region.top) / (region.bottom - region.top))) }
            return TextBox(left: x(q.minX), top: y(q.minY), right: x(q.maxX), bottom: y(q.maxY))
        }
        let reading = try reader.readLines(flat, deep: false)
        return Reading(lines: reading.lines.map { original in
            var line = original
            line.phoneBox = original.box
            line.box = original.box.map(map)
            line.words = original.words?.map { TextWord($0.text, box: map($0.box)) }
            line.icons = original.icons?.map { IconSlot(box: map($0.box), text: $0.text, image: $0.image) }
            return line
        })
    }
}
