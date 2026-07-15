// About window — version + backend URL with a reachability dot, daemon-derived (the app can't
// read the root-owned appsettings.json), mirroring the Linux tray's About.

import SwiftUI

struct AboutView: View {
    let version: String
    let backendUrl: String
    let reachable: Bool?
    var onSendLogs: () -> Void

    @State private var logsSent = false

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack(spacing: 12) {
                BrandAssets.logo(size: 48)
                VStack(alignment: .leading) {
                    Text("Valenius").font(.title2).bold()
                    Text("Version \(version)").font(.caption).foregroundColor(.secondary)
                }
            }
            Divider()
            HStack(spacing: 8) {
                Circle().fill(dotColor).frame(width: 9, height: 9)
                Text(backendUrl).font(.callout).textSelection(.enabled)
            }
            Text(reachableText).font(.caption).foregroundColor(.secondary)
            Divider()
            HStack {
                Button {
                    onSendLogs()
                    logsSent = true
                } label: {
                    Label("Send diagnostic logs", systemImage: "paperplane")
                }
                if logsSent {
                    Text("Sent").font(.caption).foregroundColor(.secondary)
                }
            }
            Text("Sends a redacted, Valenius-only log bundle for support.")
                .font(.caption2).foregroundColor(.secondary)
        }
        .padding(20)
        .frame(width: 340)
    }

    private var dotColor: Color {
        switch reachable {
        case .some(true): return Color(red: 0, green: 0.78, blue: 0.45)
        case .some(false): return Color(red: 0.9, green: 0.3, blue: 0.3)
        case .none: return .gray
        }
    }
    private var reachableText: String {
        switch reachable {
        case .some(true): return "Backend reachable"
        case .some(false): return "Backend unreachable"
        case .none: return "Checking backend…"
        }
    }
}
