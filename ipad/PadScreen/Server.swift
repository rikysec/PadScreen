import Foundation
import Network

/// Server TCP dell'iPad. Il PC si collega qui via Wi-Fi o tramite USB (usbmuxd → 127.0.0.1).
/// Tutte le callback vengono chiamate sulla coda `queue`.
final class Server {
    let queue = DispatchQueue(label: "padscreen.net", qos: .userInteractive)

    var onListenerState: ((_ text: String, _ ok: Bool) -> Void)?
    var onConnected: ((_ peer: String, _ viaUSB: Bool) -> Void)?
    var onDisconnected: ((_ reason: String) -> Void)?
    var onMessage: ((_ type: MsgType, _ payload: Data) -> Void)?

    private var listener: NWListener?
    private var connection: NWConnection?
    private var connectionID = 0
    private var lastReceive = DispatchTime.now()
    private var timer: DispatchSourceTimer?
    private var helloPayload = Data()
    private var serviceName = "iPad"

    private let rttLock = NSLock()
    private var _rttMs: Double = 0
    var rttMs: Double {
        rttLock.lock(); defer { rttLock.unlock() }
        return _rttMs
    }

    func configure(serviceName: String) {
        queue.async { self.serviceName = serviceName }
    }

    func setHello(_ data: Data) {
        queue.async { self.helloPayload = data }
    }

    /// Avvia (o riavvia, se è caduto) il listener. Idempotente.
    func start() {
        queue.async {
            self.startListenerIfNeeded()
            self.startTimerIfNeeded()
        }
    }

    /// Invia un messaggio al PC (thread-safe).
    func send(_ type: MsgType, _ payload: Data = Data()) {
        queue.async { self.sendNow(type, payload) }
    }

    func disconnect() {
        queue.async {
            guard self.connection != nil else { return }
            self.sendNow(.bye, Data())
            self.closeConnection(self.connectionID, reason: "Disconnesso dall'iPad")
        }
    }

    // MARK: - Listener

    private func startListenerIfNeeded() {
        if let l = listener {
            switch l.state {
            case .ready, .setup, .waiting:
                return
            default:
                l.stateUpdateHandler = nil
                l.cancel()
                listener = nil
            }
        }

        let tcp = NWProtocolTCP.Options()
        tcp.noDelay = true
        tcp.enableKeepalive = true
        tcp.keepaliveIdle = 5
        let params = NWParameters(tls: nil, tcp: tcp)
        params.allowLocalEndpointReuse = true
        params.includePeerToPeer = false

        let l: NWListener
        do {
            l = try NWListener(using: params, on: NWEndpoint.Port(rawValue: Proto.port)!)
        } catch {
            onListenerState?("Impossibile aprire la porta \(Proto.port): \(error.localizedDescription)", false)
            queue.asyncAfter(deadline: .now() + 3) { self.startListenerIfNeeded() }
            return
        }
        l.service = NWListener.Service(name: serviceName, type: Proto.bonjourType)
        l.stateUpdateHandler = { [weak self, weak l] state in
            guard let self, let l, self.listener === l else { return }
            switch state {
            case .ready:
                self.onListenerState?("In attesa del PC (porta \(Proto.port))", true)
            case .waiting(let err):
                self.onListenerState?("Rete non disponibile: \(err.localizedDescription)", false)
            case .failed(let err):
                self.onListenerState?("Errore di rete: \(err.localizedDescription) — riprovo…", false)
                l.stateUpdateHandler = nil
                l.cancel()
                self.listener = nil
                self.queue.asyncAfter(deadline: .now() + 2) { self.startListenerIfNeeded() }
            default:
                break
            }
        }
        l.newConnectionHandler = { [weak self] c in self?.accept(c) }
        listener = l
        l.start(queue: queue)
    }

    private func startTimerIfNeeded() {
        guard timer == nil else { return }
        let t = DispatchSource.makeTimerSource(queue: queue)
        t.schedule(deadline: .now() + 1, repeating: 1)
        t.setEventHandler { [weak self] in self?.tick() }
        t.resume()
        timer = t
    }

    private func tick() {
        guard connection != nil else { return }
        let now = DispatchTime.now()
        if now.uptimeNanoseconds > lastReceive.uptimeNanoseconds + 10_000_000_000 {
            closeConnection(connectionID, reason: "Il PC non risponde")
            return
        }
        var payload = Data(capacity: 8)
        payload.appendBE(now.uptimeNanoseconds)
        sendNow(.ping, payload)
    }

    // MARK: - Connessione

    private func accept(_ c: NWConnection) {
        if let old = connection {
            // Una nuova connessione sostituisce quella vecchia (es. riconnessione del PC).
            old.stateUpdateHandler = nil
            old.cancel()
            connection = nil
        }
        connectionID &+= 1
        let id = connectionID
        connection = c
        c.stateUpdateHandler = { [weak self] st in
            guard let self, id == self.connectionID else { return }
            switch st {
            case .ready:
                self.lastReceive = .now()
                self.onConnected?(Self.hostString(c.endpoint), Self.isLoopback(c.endpoint))
                self.sendNow(.hello, self.helloPayload)
                self.receiveHeader(c, id)
            case .failed(let e):
                self.closeConnection(id, reason: "Connessione interrotta: \(e.localizedDescription)")
            case .cancelled:
                self.closeConnection(id, reason: "Disconnesso")
            default:
                break
            }
        }
        c.start(queue: queue)
    }

    private func closeConnection(_ id: Int, reason: String) {
        guard id == connectionID, let c = connection else { return }
        connection = nil
        c.stateUpdateHandler = nil
        c.cancel()
        onDisconnected?(reason)
    }

    private func sendNow(_ type: MsgType, _ payload: Data) {
        guard let c = connection else { return }
        c.send(content: Wire.frame(type, payload), completion: .contentProcessed { _ in })
    }

    private func receiveHeader(_ c: NWConnection, _ id: Int) {
        c.receive(minimumIncompleteLength: 5, maximumLength: 5) { [weak self] data, _, isComplete, error in
            guard let self, id == self.connectionID else { return }
            if let error {
                self.closeConnection(id, reason: "Errore di rete: \(error.localizedDescription)")
                return
            }
            guard let data, data.count == 5 else {
                self.closeConnection(id, reason: isComplete ? "Il PC ha chiuso la connessione" : "Dati non validi")
                return
            }
            let b = [UInt8](data)
            let len = Int(UInt32(b[1]) << 24 | UInt32(b[2]) << 16 | UInt32(b[3]) << 8 | UInt32(b[4]))
            guard len <= Proto.maxMessageSize else {
                self.closeConnection(id, reason: "Messaggio troppo grande")
                return
            }
            if len == 0 {
                self.handle(b[0], Data())
                self.receiveHeader(c, id)
            } else {
                self.receiveBody(c, id, b[0], len)
            }
        }
    }

    private func receiveBody(_ c: NWConnection, _ id: Int, _ type: UInt8, _ len: Int) {
        c.receive(minimumIncompleteLength: len, maximumLength: len) { [weak self] data, _, isComplete, error in
            guard let self, id == self.connectionID else { return }
            if let error {
                self.closeConnection(id, reason: "Errore di rete: \(error.localizedDescription)")
                return
            }
            guard let data, data.count == len else {
                self.closeConnection(id, reason: isComplete ? "Il PC ha chiuso la connessione" : "Dati non validi")
                return
            }
            self.handle(type, data)
            self.receiveHeader(c, id)
        }
    }

    private func handle(_ typeRaw: UInt8, _ payload: Data) {
        lastReceive = .now()
        guard let type = MsgType(rawValue: typeRaw) else { return }
        switch type {
        case .ping:
            sendNow(.pong, payload)
        case .pong:
            if let sent = payload.readBE64(at: 0) {
                let now = DispatchTime.now().uptimeNanoseconds
                if now >= sent {
                    rttLock.lock(); _rttMs = Double(now - sent) / 1_000_000; rttLock.unlock()
                }
            }
        case .bye:
            closeConnection(connectionID, reason: "Il PC ha terminato la sessione")
        default:
            onMessage?(type, payload)
        }
    }

    // MARK: - Utilità

    static func hostString(_ ep: NWEndpoint) -> String {
        if case .hostPort(let host, _) = ep {
            switch host {
            case .ipv4(let a): return "\(a)"
            case .ipv6(let a):
                if let v4 = a.asIPv4 { return "\(v4)" }
                return "\(a)"
            case .name(let n, _): return n
            @unknown default: return "\(host)"
            }
        }
        return "\(ep)"
    }

    static func isLoopback(_ ep: NWEndpoint) -> Bool {
        guard case .hostPort(let host, _) = ep else { return false }
        switch host {
        case .ipv4(let a):
            return a == .loopback || a.rawValue.first == 127
        case .ipv6(let a):
            if a == .loopback { return true }
            if let v4 = a.asIPv4 { return v4.rawValue.first == 127 }
            return false
        default:
            return false
        }
    }
}
