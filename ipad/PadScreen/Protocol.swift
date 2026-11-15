import Foundation

/// Tipi di messaggio del protocollo PadScreen (vedi PROTOCOL.md).
enum MsgType: UInt8 {
    // iPad -> PC
    case hello = 0x01
    case size = 0x02
    case touch = 0x10
    case scroll = 0x11
    case keyframeRequest = 0x20
    // entrambe le direzioni
    case ping = 0x05
    case pong = 0x06
    case bye = 0x07
    // PC -> iPad
    case config = 0x81
    case videoHeader = 0x82
    case videoFrame = 0x83
    case status = 0x91
}

enum Proto {
    static let version = 1
    static let port: UInt16 = 47800
    static let bonjourType = "_padscreen._tcp"
    static let maxMessageSize = 32 * 1024 * 1024
}

struct HelloInfo: Codable {
    var proto: Int = Proto.version
    var name: String
    var model: String
    var width: Int
    var height: Int
    var scale: Double
    var maxFps: Int
    var appVersion: String
}

struct ConfigInfo: Codable, Equatable {
    var width: Int
    var height: Int
    var fps: Int
    var encoder: String?
    var host: String?
}

enum TouchPhase: UInt8 {
    case down = 0, move, up, cancel, hover, hoverExit
}

enum TouchKind: UInt8 {
    case finger = 0, pencil, mouse
}

struct TouchSample {
    var id: UInt32
    var phase: TouchPhase
    var kind: TouchKind
    var x: Float
    var y: Float
    var pressure: Float = 0
    var altitude: Float = 0
    var azimuth: Float = 0
}

extension Data {
    mutating func appendBE(_ v: UInt32) {
        append(UInt8(truncatingIfNeeded: v >> 24))
        append(UInt8(truncatingIfNeeded: v >> 16))
        append(UInt8(truncatingIfNeeded: v >> 8))
        append(UInt8(truncatingIfNeeded: v))
    }

    mutating func appendBE(_ v: UInt64) {
        appendBE(UInt32(truncatingIfNeeded: v >> 32))
        appendBE(UInt32(truncatingIfNeeded: v))
    }

    mutating func appendBE(_ v: Float) {
        appendBE(v.bitPattern)
    }

    func readBE64(at offset: Int) -> UInt64? {
        guard offset >= 0, offset + 8 <= count else { return nil }
        var v: UInt64 = 0
        for i in 0..<8 {
            v = (v << 8) | UInt64(self[startIndex + offset + i])
        }
        return v
    }
}

enum Wire {
    static func frame(_ type: MsgType, _ payload: Data) -> Data {
        var d = Data(capacity: 5 + payload.count)
        d.append(type.rawValue)
        d.appendBE(UInt32(payload.count))
        d.append(payload)
        return d
    }

    static func touches(_ samples: [TouchSample]) -> Data {
        let list = samples.prefix(255)
        var d = Data(capacity: 1 + list.count * 26)
        d.append(UInt8(list.count))
        for s in list {
            d.appendBE(s.id)
            d.append(s.phase.rawValue)
            d.append(s.kind.rawValue)
            d.appendBE(s.x)
            d.appendBE(s.y)
            d.appendBE(s.pressure)
            d.appendBE(s.altitude)
            d.appendBE(s.azimuth)
        }
        return d
    }

    static func scroll(dx: Float, dy: Float) -> Data {
        var d = Data(capacity: 8)
        d.appendBE(dx)
        d.appendBE(dy)
        return d
    }
}
