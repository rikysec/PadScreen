import SwiftUI
import UIKit

@MainActor
final class AppModel: ObservableObject {
    @Published private(set) var listening = false
    @Published private(set) var listenerText = "Avvio…"
    @Published private(set) var connected = false
    @Published private(set) var peerText = ""
    @Published private(set) var viaUSB = false
    @Published private(set) var hostStatus = ""
    @Published private(set) var config: ConfigInfo?
    @Published private(set) var hasVideo = false
    @Published private(set) var statsText = ""
    @Published private(set) var addresses: [String] = []
    @Published private(set) var lastDisconnectReason = ""
    @Published private(set) var screenPixels: CGSize = .zero

    let server = Server()
    let video = VideoPipeline()

    private var statsTimer: Timer?
    private let deviceName = UIDevice.current.name
    private let modelIdentifier = AppModel.machineIdentifier()
    private let appVersion = Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "1.0"

    init() {
        let screen = UIScreen.main
        screenPixels = CGSize(width: (screen.bounds.width * screen.nativeScale).rounded(),
                              height: (screen.bounds.height * screen.nativeScale).rounded())

        let server = self.server
        let video = self.video

        server.configure(serviceName: deviceName)
        server.setHello(helloJSON())

        server.onListenerState = { [weak self] text, ok in
            Task { @MainActor in
                self?.listenerText = text
                self?.listening = ok
            }
        }
        server.onConnected = { [weak self] peer, usb in
            video.reset()
            Task { @MainActor in self?.didConnect(peer: peer, usb: usb) }
        }
        server.onDisconnected = { [weak self] reason in
            video.reset()
            Task { @MainActor in self?.didDisconnect(reason: reason) }
        }
        server.onMessage = { [weak self] type, payload in
            switch type {
            case .videoHeader:
                video.handleHeader(payload)
            case .videoFrame:
                video.handleFrame(payload)
            case .config:
                if let c = try? JSONDecoder().decode(ConfigInfo.self, from: payload) {
                    Task { @MainActor in self?.config = c }
                }
            case .status:
                let text = String(decoding: payload, as: UTF8.self)
                Task { @MainActor in self?.hostStatus = text }
            default:
                break
            }
        }
        video.requestKeyframe = { server.send(.keyframeRequest) }
        video.onFirstFrame = { [weak self] in
            Task { @MainActor in self?.hasVideo = true }
        }

        server.start()
        refreshAddresses()

        statsTimer = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.updateStats() }
        }
    }

    // MARK: - Eventi

    private func didConnect(peer: String, usb: Bool) {
        connected = true
        viaUSB = usb
        peerText = usb ? "USB" : peer
        hostStatus = ""
        config = nil
        hasVideo = false
        lastDisconnectReason = ""
        UIApplication.shared.isIdleTimerDisabled = true
    }

    private func didDisconnect(reason: String) {
        connected = false
        hasVideo = false
        config = nil
        // Se il PC ha segnalato un errore prima di chiudere, mostriamolo.
        lastDisconnectReason = hostStatus.hasPrefix("Errore") ? "\(reason). \(hostStatus)" : reason
        hostStatus = ""
        UIApplication.shared.isIdleTimerDisabled = false
        refreshAddresses()
    }

    func scenePhaseChanged(_ phase: ScenePhase) {
        if phase == .active {
            server.start()
            refreshAddresses()
        }
    }

    func updateScreen(points: CGSize, scale: CGFloat) {
        guard points.width > 0, points.height > 0 else { return }
        let px = CGSize(width: (points.width * scale).rounded(), height: (points.height * scale).rounded())
        guard px != screenPixels else { return }
        screenPixels = px
        let hello = helloJSON()
        server.setHello(hello)
        if connected { server.send(.size, hello) }
    }

    func sendTouches(_ samples: [TouchSample]) {
        guard connected else { return }
        server.send(.touch, Wire.touches(samples))
    }

    func sendScroll(dx: CGFloat, dy: CGFloat) {
        guard connected else { return }
        server.send(.scroll, Wire.scroll(dx: Float(dx), dy: Float(dy)))
    }

    func refreshAddresses() {
        addresses = NetworkInfo.ipv4Addresses()
    }

    private func updateStats() {
        let s = video.takeStats()
        guard connected else { statsText = ""; return }
        let mbps = Double(s.bytes) * 8 / 1_000_000
        var parts = ["\(s.frames) fps", String(format: "%.1f Mbit/s", mbps), String(format: "RTT %.0f ms", server.rttMs)]
        if let c = config {
            parts.append("\(c.width)×\(c.height)")
            if let e = c.encoder, !e.isEmpty { parts.append(e) }
        }
        parts.append(viaUSB ? "USB" : "Wi-Fi")
        if s.errors > 0 { parts.append("errori: \(s.errors)") }
        statsText = parts.joined(separator: " · ")
    }

    // MARK: - HELLO

    private func helloJSON() -> Data {
        let hello = HelloInfo(
            name: deviceName,
            model: modelIdentifier,
            width: Int(screenPixels.width),
            height: Int(screenPixels.height),
            scale: Double(UIScreen.main.nativeScale),
            maxFps: UIScreen.main.maximumFramesPerSecond,
            appVersion: appVersion)
        return (try? JSONEncoder().encode(hello)) ?? Data()
    }

    static func machineIdentifier() -> String {
        if let sim = ProcessInfo.processInfo.environment["SIMULATOR_MODEL_IDENTIFIER"] { return sim }
        var u = utsname()
        uname(&u)
        return withUnsafeBytes(of: &u.machine) { raw in
            String(decoding: raw.prefix { $0 != 0 }, as: UTF8.self)
        }
    }
}
