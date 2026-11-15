import AVFoundation
import SwiftUI
import UIKit

/// Vista a tutto schermo che mostra il video del PC e inoltra tocchi, Apple Pencil e trackpad.
final class StreamView: UIView {
    override class var layerClass: AnyClass { AVSampleBufferDisplayLayer.self }

    var displayLayer: AVSampleBufferDisplayLayer { layer as! AVSampleBufferDisplayLayer }
    var renderer: AVSampleBufferVideoRenderer { displayLayer.sampleBufferRenderer }

    /// Dimensione del video in pixel (dal messaggio CONFIG), per mappare le coordinate.
    var videoPixelSize: CGSize = .zero

    var onSamples: (([TouchSample]) -> Void)?
    var onScroll: ((CGFloat, CGFloat) -> Void)?

    private var fingerSlots: [ObjectIdentifier: UInt32] = [:]
    private var lastScroll: CGPoint = .zero
    private var hoverIsPencil = false

    override init(frame: CGRect) {
        super.init(frame: frame)
        backgroundColor = .black
        isMultipleTouchEnabled = true
        displayLayer.videoGravity = .resizeAspect
        displayLayer.backgroundColor = UIColor.black.cgColor

        let hover = UIHoverGestureRecognizer(target: self, action: #selector(hovered(_:)))
        hover.cancelsTouchesInView = false
        addGestureRecognizer(hover)

        // Solo eventi di scorrimento (trackpad / rotella): nessun tocco diretto.
        let scroll = UIPanGestureRecognizer(target: self, action: #selector(scrolled(_:)))
        scroll.allowedScrollTypesMask = .all
        scroll.maximumNumberOfTouches = 0
        scroll.cancelsTouchesInView = false
        scroll.delaysTouchesBegan = false
        scroll.delaysTouchesEnded = false
        addGestureRecognizer(scroll)
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) non supportato") }

    var videoRect: CGRect {
        guard videoPixelSize.width > 0, videoPixelSize.height > 0 else { return bounds }
        return AVMakeRect(aspectRatio: videoPixelSize, insideRect: bounds)
    }

    // MARK: - Tocchi

    override func touchesBegan(_ touches: Set<UITouch>, with event: UIEvent?) { process(touches, event, .down) }
    override func touchesMoved(_ touches: Set<UITouch>, with event: UIEvent?) { process(touches, event, .move) }
    override func touchesEnded(_ touches: Set<UITouch>, with event: UIEvent?) { process(touches, event, .up) }
    override func touchesCancelled(_ touches: Set<UITouch>, with event: UIEvent?) { process(touches, event, .cancel) }

    private func process(_ touches: Set<UITouch>, _ event: UIEvent?, _ phase: TouchPhase) {
        let rect = videoRect
        var out: [TouchSample] = []
        for t in touches {
            switch t.type {
            case .pencil:
                // Con la Pencil inviamo anche i campioni intermedi (fino a 240 Hz) per tratti più precisi.
                let list = phase == .move ? (event?.coalescedTouches(for: t) ?? [t]) : [t]
                for c in list {
                    out.append(sample(c, id: 0, kind: .pencil, phase: phase, rect: rect))
                }
            case .indirectPointer:
                out.append(sample(t, id: 0, kind: .mouse, phase: phase, rect: rect))
            default:
                let key = ObjectIdentifier(t)
                var id = fingerSlots[key]
                if id == nil && phase == .down {
                    id = freeSlot()
                    if let id { fingerSlots[key] = id }
                }
                guard let id else { continue }
                out.append(sample(t, id: id, kind: .finger, phase: phase, rect: rect))
                if phase == .up || phase == .cancel { fingerSlots[key] = nil }
            }
        }
        if !out.isEmpty { onSamples?(out) }
    }

    private func freeSlot() -> UInt32? {
        let used = Set(fingerSlots.values)
        return (0..<10).map(UInt32.init).first { !used.contains($0) }
    }

    private func sample(_ t: UITouch, id: UInt32, kind: TouchKind, phase: TouchPhase, rect: CGRect) -> TouchSample {
        let (x, y) = normalize(t.preciseLocation(in: self), rect)
        var s = TouchSample(id: id, phase: phase, kind: kind, x: x, y: y, pressure: 0.5)
        if kind == .pencil {
            let maxForce = t.maximumPossibleForce
            s.pressure = maxForce > 0 ? Float(min(1, max(0, t.force / maxForce))) : 0.5
            s.altitude = Float(t.altitudeAngle)
            s.azimuth = Float(t.azimuthAngle(in: self))
        }
        return s
    }

    private func normalize(_ p: CGPoint, _ r: CGRect) -> (Float, Float) {
        guard r.width > 0, r.height > 0 else { return (0, 0) }
        let x = min(max((p.x - r.minX) / r.width, 0), 1)
        let y = min(max((p.y - r.minY) / r.height, 0), 1)
        return (Float(x), Float(y))
    }

    // MARK: - Hover (Apple Pencil o puntatore del trackpad)

    @objc private func hovered(_ g: UIHoverGestureRecognizer) {
        let (x, y) = normalize(g.location(in: self), videoRect)
        switch g.state {
        case .began, .changed:
            // La Pencil sospesa ha zOffset > 0; il puntatore del trackpad ha sempre 0.
            hoverIsPencil = g.zOffset > 0
            var s = TouchSample(id: 0, phase: .hover, kind: hoverIsPencil ? .pencil : .mouse, x: x, y: y)
            if hoverIsPencil {
                s.altitude = Float(g.altitudeAngle)
                s.azimuth = Float(g.azimuthAngle(in: self))
            }
            onSamples?([s])
        case .ended, .cancelled, .failed:
            // A fine hover zOffset vale 0: usiamo il tipo dell'ultimo campione.
            onSamples?([TouchSample(id: 0, phase: .hoverExit, kind: hoverIsPencil ? .pencil : .mouse, x: x, y: y)])
            hoverIsPencil = false
        default:
            return
        }
    }

    @objc private func scrolled(_ g: UIPanGestureRecognizer) {
        switch g.state {
        case .began:
            lastScroll = .zero
        case .changed:
            let t = g.translation(in: self)
            let dx = t.x - lastScroll.x
            let dy = t.y - lastScroll.y
            lastScroll = t
            if dx != 0 || dy != 0 { onScroll?(dx, dy) }
        default:
            lastScroll = .zero
        }
    }
}

struct StreamViewRepresentable: UIViewRepresentable {
    @ObservedObject var model: AppModel

    final class Coordinator {
        weak var model: AppModel?
        init(model: AppModel) { self.model = model }
    }

    func makeCoordinator() -> Coordinator { Coordinator(model: model) }

    func makeUIView(context: Context) -> StreamView {
        let v = StreamView()
        let coordinator = context.coordinator
        v.onSamples = { samples in coordinator.model?.sendTouches(samples) }
        v.onScroll = { dx, dy in coordinator.model?.sendScroll(dx: dx, dy: dy) }
        model.video.attach(v.renderer)
        return v
    }

    func updateUIView(_ v: StreamView, context: Context) {
        if let c = model.config {
            v.videoPixelSize = CGSize(width: c.width, height: c.height)
        }
    }

    static func dismantleUIView(_ v: StreamView, coordinator: Coordinator) {
        coordinator.model?.video.attach(nil)
    }
}
