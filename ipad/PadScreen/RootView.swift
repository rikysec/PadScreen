import SwiftUI

struct RootView: View {
    @EnvironmentObject private var model: AppModel
    @Environment(\.displayScale) private var displayScale

    var body: some View {
        GeometryReader { geo in
            ZStack {
                Color.black
                if model.connected {
                    StreamScreen()
                } else {
                    WaitingView()
                }
            }
            .onAppear { model.updateScreen(points: geo.size, scale: displayScale) }
            .onChange(of: geo.size) { _, size in model.updateScreen(points: size, scale: displayScale) }
        }
        .ignoresSafeArea()
        .statusBarHidden(true)
        .persistentSystemOverlays(.hidden)
        .defersSystemGestures(on: .all)
    }
}

/// Schermata durante la trasmissione: solo il desktop del PC, più eventuali statistiche.
struct StreamScreen: View {
    @EnvironmentObject private var model: AppModel
    @AppStorage("showStats") private var showStats = false

    var body: some View {
        ZStack(alignment: .topLeading) {
            StreamViewRepresentable(model: model)

            if !model.hasVideo {
                VStack(spacing: 16) {
                    ProgressView().controlSize(.large)
                    Text(model.hostStatus.isEmpty ? "Collegato al PC, in attesa dell'immagine…" : model.hostStatus)
                        .font(.title3)
                        .multilineTextAlignment(.center)
                        .foregroundStyle(.secondary)
                        .padding(.horizontal, 40)
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                .allowsHitTesting(false)
            }

            if showStats && !model.statsText.isEmpty {
                Text(model.statsText)
                    .font(.caption.monospacedDigit())
                    .padding(.horizontal, 8)
                    .padding(.vertical, 4)
                    .background(.black.opacity(0.55), in: RoundedRectangle(cornerRadius: 6))
                    .foregroundStyle(.white)
                    .padding(10)
                    .allowsHitTesting(false)
            }
        }
    }
}

struct WaitingView: View {
    @EnvironmentObject private var model: AppModel
    @AppStorage("showStats") private var showStats = false

    var body: some View {
        ScrollView {
            VStack(spacing: 28) {
                VStack(spacing: 10) {
                    Image(systemName: "rectangle.on.rectangle")
                        .font(.system(size: 64, weight: .light))
                        .foregroundStyle(.tint)
                    Text("PadScreen")
                        .font(.largeTitle.bold())
                    Text("Usa questo iPad come secondo schermo del PC Windows")
                        .foregroundStyle(.secondary)
                }

                HStack(spacing: 12) {
                    if model.listening {
                        ProgressView()
                    } else {
                        Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(.yellow)
                    }
                    Text(model.listenerText)
                }
                .padding(.horizontal, 18)
                .padding(.vertical, 12)
                .background(.thinMaterial, in: RoundedRectangle(cornerRadius: 14))

                if !model.lastDisconnectReason.isEmpty {
                    Text("Ultima sessione: \(model.lastDisconnectReason)")
                        .font(.footnote)
                        .foregroundStyle(.secondary)
                }

                VStack(alignment: .leading, spacing: 20) {
                    Text("Come collegarsi").font(.headline)

                    Label {
                        VStack(alignment: .leading, spacing: 4) {
                            Text("Con il cavo USB").bold()
                            Text("Collega l'iPad al PC, sbloccalo e tocca «Autorizza» se compare la richiesta. Sul PC serve l'app «Dispositivi Apple» (o iTunes). In PadScreen per Windows scegli l'iPad indicato come USB e premi Connetti.")
                                .foregroundStyle(.secondary)
                        }
                    } icon: {
                        Image(systemName: "cable.connector")
                    }

                    Label {
                        VStack(alignment: .leading, spacing: 4) {
                            Text("In Wi-Fi").bold()
                            Text("PC e iPad devono essere sulla stessa rete. L'iPad compare da solo nell'elenco del programma Windows; in alternativa inserisci l'indirizzo:")
                                .foregroundStyle(.secondary)
                            if model.addresses.isEmpty {
                                Text("Nessuna rete Wi-Fi attiva").foregroundStyle(.orange)
                            } else {
                                ForEach(model.addresses, id: \.self) { ip in
                                    Text(ip)
                                        .font(.title3.monospaced().bold())
                                        .textSelection(.enabled)
                                }
                            }
                        }
                    } icon: {
                        Image(systemName: "wifi")
                    }
                }
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(20)
                .background(.thinMaterial, in: RoundedRectangle(cornerRadius: 16))

                Toggle("Mostra statistiche durante la trasmissione", isOn: $showStats)
                    .padding(.horizontal, 4)

                Text(verbatim: "Risoluzione di questo schermo: \(Int(model.screenPixels.width)) × \(Int(model.screenPixels.height)) pixel")
                    .font(.footnote)
                    .foregroundStyle(.secondary)
            }
            .padding(48)
            .frame(maxWidth: 680)
            .frame(maxWidth: .infinity)
        }
        .scrollBounceBehavior(.basedOnSize)
    }
}
