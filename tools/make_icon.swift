// アプリのアイコン(src/AppLock/Assets/AppLock.ico)を作る。Mac で: swift tools/make_icon.swift
// 藍(Mac 版 Janus のロックの色 #5C6BF5)の角丸の板に、白い錠前。.ico には PNG を大きさごとに入れる。
import CoreGraphics
import Foundation
import ImageIO
import UniformTypeIdentifiers

let sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]

func color(_ hex: UInt32, _ alpha: CGFloat = 1) -> CGColor {
    CGColor(srgbRed: CGFloat((hex >> 16) & 0xFF) / 255, green: CGFloat((hex >> 8) & 0xFF) / 255, blue: CGFloat(hex & 0xFF) / 255, alpha: alpha)
}

func render(_ size: Int) -> Data {
    let s = CGFloat(size)
    let space = CGColorSpace(name: CGColorSpace.sRGB)!
    let context = CGContext(data: nil, width: size, height: size, bitsPerComponent: 8, bytesPerRow: 0, space: space,
                            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
    context.setAllowsAntialiasing(true)
    context.setShouldAntialias(true)

    // 板(小さいときは余白を詰める)
    let inset = size <= 24 ? 0.5 : s * 0.055
    let plate = CGRect(x: inset, y: inset, width: s - inset * 2, height: s - inset * 2)
    let radius = plate.width * 0.23
    let platePath = CGPath(roundedRect: plate, cornerWidth: radius, cornerHeight: radius, transform: nil)
    context.saveGState()
    context.addPath(platePath)
    context.clip()
    let gradient = CGGradient(colorsSpace: space, colors: [color(0x7381F8), color(0x4D5AE6)] as CFArray, locations: [0, 1])!
    context.drawLinearGradient(gradient, start: CGPoint(x: 0, y: plate.maxY), end: CGPoint(x: 0, y: plate.minY), options: [])
    context.restoreGState()

    // 錠前(体と、上の輪)
    let unit = plate.width
    let bodyWidth = unit * (size <= 16 ? 0.53 : (size <= 24 ? 0.56 : 0.50))
    let bodyHeight = unit * (size <= 24 ? 0.40 : 0.37)
    let body = CGRect(x: plate.midX - bodyWidth / 2, y: plate.minY + unit * 0.19, width: bodyWidth, height: bodyHeight)
    let small = size <= 24
    let shackleWidth = size <= 16 ? 1.3 : (small ? max(unit * 0.105, 1.5) : unit * 0.115)
    let shackleRadius = bodyWidth * (small ? 0.36 : 0.30)
    let shackleTop = body.maxY + unit * (size <= 16 ? 0.27 : (small ? 0.24 : 0.15))

    context.setStrokeColor(color(0xFFFFFF))
    context.setLineWidth(shackleWidth)
    context.setLineCap(.butt)
    let shackle = CGMutablePath()
    let centerY = shackleTop - shackleRadius
    shackle.move(to: CGPoint(x: plate.midX - shackleRadius, y: body.maxY - shackleWidth))
    shackle.addLine(to: CGPoint(x: plate.midX - shackleRadius, y: centerY))
    shackle.addArc(center: CGPoint(x: plate.midX, y: centerY), radius: shackleRadius, startAngle: .pi, endAngle: 0, clockwise: true)
    shackle.addLine(to: CGPoint(x: plate.midX + shackleRadius, y: body.maxY - shackleWidth))
    context.addPath(shackle)
    context.strokePath()

    context.setFillColor(color(0xFFFFFF))
    let bodyRadius = bodyWidth * 0.16
    context.addPath(CGPath(roundedRect: body, cornerWidth: bodyRadius, cornerHeight: bodyRadius, transform: nil))
    context.fillPath()

    // 鍵穴(大きいときだけ)
    if size >= 32 {
        context.setFillColor(color(0x4D5AE6))
        let hole = bodyWidth * 0.11
        let holeCenter = CGPoint(x: plate.midX, y: body.minY + bodyHeight * 0.56)
        context.fillEllipse(in: CGRect(x: holeCenter.x - hole, y: holeCenter.y - hole, width: hole * 2, height: hole * 2))
        let stem = CGRect(x: holeCenter.x - hole * 0.45, y: body.minY + bodyHeight * 0.24, width: hole * 0.9, height: holeCenter.y - body.minY - bodyHeight * 0.24)
        context.fill(stem)
    }

    let image = context.makeImage()!
    let data = NSMutableData()
    let destination = CGImageDestinationCreateWithData(data, UTType.png.identifier as CFString, 1, nil)!
    CGImageDestinationAddImage(destination, image, nil)
    CGImageDestinationFinalize(destination)
    return data as Data
}

let output = URL(fileURLWithPath: CommandLine.arguments.count > 1 ? CommandLine.arguments[1] : "src/AppLock/Assets/AppLock.ico")
let images = sizes.map(render)

var file = Data()
func append16(_ value: Int) { var v = UInt16(value).littleEndian; file.append(Data(bytes: &v, count: 2)) }
func append32(_ value: Int) { var v = UInt32(value).littleEndian; file.append(Data(bytes: &v, count: 4)) }
append16(0); append16(1); append16(images.count)
var offset = 6 + 16 * images.count
for (size, png) in zip(sizes, images) {
    file.append(UInt8(size >= 256 ? 0 : size))
    file.append(UInt8(size >= 256 ? 0 : size))
    file.append(0); file.append(0)
    append16(1); append16(32)
    append32(png.count); append32(offset)
    offset += png.count
}
for png in images { file.append(png) }
try FileManager.default.createDirectory(at: output.deletingLastPathComponent(), withIntermediateDirectories: true)
try file.write(to: output)
// 見て確かめるための大きな PNG
if CommandLine.arguments.count > 2 {
    try images.last!.write(to: URL(fileURLWithPath: CommandLine.arguments[2]))
    try images[3].write(to: URL(fileURLWithPath: CommandLine.arguments[2].replacingOccurrences(of: ".png", with: "-32.png")))
    try images[0].write(to: URL(fileURLWithPath: CommandLine.arguments[2].replacingOccurrences(of: ".png", with: "-16.png")))
}
print("wrote \(output.path) (\(file.count) bytes, \(sizes.count) sizes)")
