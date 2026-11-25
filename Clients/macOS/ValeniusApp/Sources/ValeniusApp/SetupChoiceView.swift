// First-run setup choice — shown before BackendUrlView when the daemon reports
// BackendUnconfigured and standalone mode hasn't been chosen yet: "Connect to a server"
// (the existing DNS-prompt flow) or "Use standalone" (local .conf profiles only, connect/
// upload/delete all work, nothing is ever contacted except optionally for auto-update).
// Mirrors the Windows SetupChoiceForm.

import SwiftUI

struct SetupChoiceView: View {
    @ObservedObject var state: AppState
    var onConnectToServer: () -> Void
    var onStandalone: () -> Void

    @State private var submitting = false

    var body: some View {
        VStack(spacing: 16) {
            BrandAssets.logo(size: 48)

            Text("Set up Valenius")
                .font(.headline)
            Text("Connect to a Valenius server your administrator runs, or use Valenius standalone with your own WireGuard configuration files. An administrator must provision the initial choice.")
                .font(.caption)
                .foregroundColor(.secondary)
                .multilineTextAlignment(.center)
                .fixedSize(horizontal: false, vertical: true)

            VStack(spacing: 8) {
                Button(action: onConnectToServer) {
                    HStack {
                        Image(systemName: "server.rack")
                        Text("Connect to a server")
                        Spacer()
                    }
                }
                .buttonStyle(.bordered)

                Button(action: chooseStandalone) {
                    HStack {
                        Image(systemName: "shield.lefthalf.filled")
                        Text(submitting ? "Setting up…" : "Use standalone")
                        Spacer()
                    }
                }
                .buttonStyle(.bordered)
                .disabled(submitting)
            }
            .frame(width: 260)

            Text("An administrator can enroll a standalone client later. Changing an existing backend requires a separate migration.")
                .font(.caption2)
                .foregroundColor(.secondary)
                .multilineTextAlignment(.center)
                .fixedSize(horizontal: false, vertical: true)
        }
        .padding(24)
        .frame(width: 340)
    }

    private func chooseStandalone() {
        guard !submitting else { return }
        submitting = true
        Task {
            await state.setStandaloneMode()
            submitting = false
            onStandalone()
        }
    }
}
