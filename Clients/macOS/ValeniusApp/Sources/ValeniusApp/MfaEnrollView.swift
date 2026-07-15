// MFA TOTP enrollment dialog — shows the QR for the otpauth URI (CoreImage), a manual-entry
// secret fallback, and a code field that confirms via the MfaEnrollConfirm IPC command.
// Mirrors the Windows MfaEnrollForm / Linux _MfaEnrollDialog.

import SwiftUI

struct MfaEnrollView: View {
    @ObservedObject var state: AppState
    var onDone: () -> Void

    @State private var code = ""
    @State private var error: String?
    @State private var submitting = false

    private var otpauthUri: String? { state.status.mfaEnrollmentUri }

    var body: some View {
        VStack(spacing: 16) {
            Text("Set up two-factor authentication")
                .font(.headline)

            if let uri = otpauthUri, let qr = QRCode.image(from: uri) {
                Image(nsImage: qr)
                    .interpolation(.none)
                    .resizable()
                    .frame(width: 180, height: 180)
                Text("Scan with your authenticator app, then enter the 6-digit code.")
                    .font(.caption).foregroundColor(.secondary)
                    .multilineTextAlignment(.center)
                if let secret = QRCode.secret(fromOtpauth: uri) {
                    HStack(spacing: 6) {
                        Text("Manual key:").font(.caption).foregroundColor(.secondary)
                        Text(secret).font(.system(.caption, design: .monospaced)).textSelection(.enabled)
                    }
                }
            } else {
                Text("Waiting for the enrollment code from the server…")
                    .font(.caption).foregroundColor(.secondary)
            }

            TextField("6-digit code", text: $code)
                .textFieldStyle(.roundedBorder)
                .frame(width: 140)
                .multilineTextAlignment(.center)
                .onSubmit { submit() }

            if let error {
                Text(error).font(.caption).foregroundColor(.red)
            }

            HStack {
                Button("Cancel", action: onDone)
                Button(submitting ? "Verifying…" : "Confirm") { submit() }
                    .buttonStyle(.borderedProminent)
                    .disabled(submitting || code.trimmingCharacters(in: .whitespaces).count < 6)
            }
        }
        .padding(24)
        .frame(width: 320)
    }

    private func submit() {
        let trimmed = code.trimmingCharacters(in: .whitespaces)
        guard trimmed.count >= 6, !submitting else { return }
        submitting = true
        error = nil
        Task {
            let failure = await state.mfaEnrollConfirm(code: trimmed)
            submitting = false
            if let failure {
                error = failure
                code = ""
            } else {
                onDone() // success — heartbeat clears the enrollment window
            }
        }
    }
}
