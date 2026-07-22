// First-run server-address setup — shown when the daemon reports BackendUnconfigured (fresh
// install/dev-run with no BackendUrl in appsettings.json and no persisted backend.dat
// override). Mirrors the Windows BackendUrlForm: a fixed, non-editable "https://" prefix, the
// user types only the host. Also carries a hidden triple-click-the-logo shortcut that applies
// the build-time quick-config default (QuickConfig.swift) — the macOS analogue of the mobile
// client's triple-tap-the-logo (see Clients/Mobile/lib/ui/setup_screen.dart).

import SwiftUI

struct BackendUrlView: View {
    @ObservedObject var state: AppState
    var onDone: () -> Void

    @State private var host = ""
    @State private var error: String?
    @State private var submitting = false
    @State private var logoTaps = 0
    @State private var lastTap = Date.distantPast

    var body: some View {
        VStack(spacing: 16) {
            BrandAssets.logo(size: 48)
                .onTapGesture { onLogoTap() }

            Text("Connect to your Valenius server")
                .font(.headline)
            Text("Enter the address of your Valenius server. Your administrator provided this — just the server name, for example vpn.company.com.")
                .font(.caption)
                .foregroundColor(.secondary)
                .multilineTextAlignment(.center)
                .fixedSize(horizontal: false, vertical: true)

            HStack(spacing: 0) {
                Text("https://").foregroundColor(.secondary)
                TextField("vpn.company.com", text: $host)
                    .textFieldStyle(.plain)
                    .disableAutocorrection(true)
                    .onSubmit { submit() }
            }
            .padding(8)
            .background(RoundedRectangle(cornerRadius: 6).stroke(Color.secondary.opacity(0.4)))
            .frame(width: 260)

            Text("Do not include https:// or any path — just the server name.")
                .font(.caption2)
                .foregroundColor(.secondary)

            if let error {
                Text(error).font(.caption).foregroundColor(.red)
            }

            HStack {
                Button("Cancel", action: onDone)
                Button(submitting ? "Saving…" : "Save") { submit() }
                    .buttonStyle(.borderedProminent)
                    .disabled(submitting || host.trimmingCharacters(in: .whitespaces).isEmpty)
            }
        }
        .padding(24)
        .frame(width: 340)
    }

    /// Three taps within 1.5 s of each other apply the build-time quick-config default.
    /// Silent no-op in builds that didn't stamp one in (dev/OSS).
    private func onLogoTap() {
        let now = Date()
        logoTaps = now.timeIntervalSince(lastTap) > 1.5 ? 1 : logoTaps + 1
        lastTap = now
        if logoTaps >= 3 {
            logoTaps = 0
            applyQuickConfig()
        }
    }

    private func applyQuickConfig() {
        guard !quickConfigUrl.isEmpty, !submitting else { return }
        host = quickConfigUrl
        submit()
    }

    private func submit() {
        let trimmed = host.trimmingCharacters(in: .whitespaces)
        guard !trimmed.isEmpty, !submitting else { return }
        submitting = true
        error = nil
        Task {
            let failure = await state.setBackendUrl(dns: trimmed)
            submitting = false
            if let failure {
                error = failure
            } else {
                onDone()
            }
        }
    }
}
