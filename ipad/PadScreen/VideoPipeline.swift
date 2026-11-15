import AVFoundation
import CoreMedia

/// Riceve header e fotogrammi (sulla coda di rete) e li accoda al renderer hardware.
final class VideoPipeline {
    private let lock = NSLock()
    private var renderer: AVSampleBufferVideoRenderer?
    private var depacketizer = H264Depacketizer()
    private var waitingForKeyframe = true
    private var lastKeyframeRequest: UInt64 = 0
    private var announcedFirstFrame = false

    private var frames = 0
    private var bytes = 0
    private var errors = 0

    /// Chiamata (sulla coda di rete) quando serve un keyframe dal PC.
    var requestKeyframe: (() -> Void)?
    /// Chiamata (sulla coda di rete) al primo fotogramma visualizzato dopo un reset.
    var onFirstFrame: (() -> Void)?

    func attach(_ r: AVSampleBufferVideoRenderer?) {
        lock.lock()
        renderer?.flush()
        renderer = r
        waitingForKeyframe = true
        lock.unlock()
        if r != nil { maybeRequestKeyframe(force: true) }
    }

    /// Nuova sessione: dimentica formato e stato del decoder.
    func reset() {
        lock.lock()
        depacketizer = H264Depacketizer()
        waitingForKeyframe = true
        announcedFirstFrame = false
        renderer?.flush()
        lock.unlock()
    }

    func handleHeader(_ data: Data) {
        lock.lock(); defer { lock.unlock() }
        if depacketizer.applyConfig([UInt8](data)) {
            renderer?.flush()
            waitingForKeyframe = true
        }
    }

    func handleFrame(_ payload: Data) {
        guard payload.count > 5 else { return }
        let d = [UInt8](payload)
        let flagKey = d[0] & 0x01 != 0
        let annexB = d[0] & 0x02 != 0

        lock.lock()
        bytes += d.count
        guard let renderer else {
            waitingForKeyframe = true
            lock.unlock()
            return
        }
        if renderer.requiresFlushToResumeDecoding || renderer.status == .failed {
            renderer.flush()
            waitingForKeyframe = true
            errors += 1
        }
        let frame = depacketizer.makeFrame(d, from: 5, annexB: annexB)
        if frame.formatChanged {
            renderer.flush()
        }
        let isKey = flagKey || frame.isIDR
        var needKeyframe = false
        var first = false
        if waitingForKeyframe && !isKey {
            needKeyframe = true
        } else if let sample = frame.sample {
            renderer.enqueue(sample)
            waitingForKeyframe = false
            frames += 1
            if !announcedFirstFrame {
                announcedFirstFrame = true
                first = true
            }
        } else {
            waitingForKeyframe = true
            errors += 1
            needKeyframe = true
        }
        lock.unlock()

        if needKeyframe { maybeRequestKeyframe(force: false) }
        if first { onFirstFrame?() }
    }

    /// Statistiche dall'ultima chiamata: fotogrammi, byte, errori.
    func takeStats() -> (frames: Int, bytes: Int, errors: Int) {
        lock.lock(); defer { lock.unlock() }
        let r = (frames, bytes, errors)
        frames = 0; bytes = 0; errors = 0
        return r
    }

    private func maybeRequestKeyframe(force: Bool) {
        let now = DispatchTime.now().uptimeNanoseconds
        lock.lock()
        let ok = force || now > lastKeyframeRequest + 1_500_000_000
        if ok { lastKeyframeRequest = now }
        lock.unlock()
        if ok { requestKeyframe?() }
    }
}
