// The popover content — visual/behavioral parity with the Windows TrayPopupForm and Linux
// popup: header with backend-reachability dot, a "Register Client" row when inactive, MFA
// rows, per-profile rows (indicator + Connected/Verified tag + delete × on deletable rows),
// "Disconnect all" when >1 tunnel is up, and footer actions.

import SwiftUI
import ValeniusShared

struct PopupView: View {
    @ObservedObject var state: AppState
    var onUploadConfig: () -> Void
    var onAbout: () -> Void
    var onUnlockMfa: (String) -> Void
    var onQuit: () -> Void

    private var status: TunnelStatus { state.status }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            header
            Divider().overlay(Color.white.opacity(0.08))

            if !state.daemonReachable {
                infoRow("The Valenius service isn't running.", systemImage: "exclamationmark.triangle")
            } else if status.registrationIsActive != true {
                registerRow
            }

            if status.mfaEnrollmentOpen {
                mfaEnrollRow
            } else if status.mfaRequired {
                mfaUnlockRow
            } else if let remaining = mfaSessionRemaining {
                mfaSessionRow(remaining)
            }

            profileList

            if status.connectedTunnels.count > 1 {
                actionRow("Disconnect all", systemImage: "bolt.slash") { state.disconnectAll() }
            }

            if status.updateAvailable {
                updateRow
            }

            if let err = state.lastError {
                Text(err)
                    .font(.caption)
                    .foregroundColor(.orange)
                    .padding(.horizontal, 14).padding(.vertical, 6)
                    .fixedSize(horizontal: false, vertical: true)
            }

            Divider().overlay(Color.white.opacity(0.08))
            footer
        }
        .frame(width: 300)
        .background(Color(red: 0.11, green: 0.12, blue: 0.14))
        .preferredColorScheme(.dark)
        .alert(
            "Network conflict — can't connect",
            isPresented: Binding(
                get: { state.lanConflictMessage != nil },
                set: { if !$0 { state.lanConflictMessage = nil } }
            )
        ) {
            Button("OK") { state.lanConflictMessage = nil }
        } message: {
            Text(state.lanConflictMessage ?? "")
        }
    }

    // MARK: Header

    private var header: some View {
        HStack(spacing: 8) {
            BrandAssets.logo(size: 20)
            Text("Valenius").font(.headline).foregroundColor(.white)
            Spacer()
            Circle()
                .fill(backendDotColor)
                .frame(width: 8, height: 8)
                .help(backendDotHelp)
        }
        .padding(.horizontal, 14).padding(.vertical, 12)
    }

    private var backendDotColor: Color {
        switch status.backendReachable {
        case .some(true): return Color(red: 0, green: 0.78, blue: 0.45)
        case .some(false): return Color(red: 0.9, green: 0.3, blue: 0.3)
        case .none: return Color.gray
        }
    }
    private var backendDotHelp: String {
        switch status.backendReachable {
        case .some(true): return "Backend reachable"
        case .some(false): return "Backend unreachable"
        case .none: return "Checking backend…"
        }
    }

    // MARK: Rows

    private var registerRow: some View {
        Button(action: { state.register() }) {
            HStack {
                Image(systemName: "person.badge.key")
                Text("Register Client")
                Spacer()
            }
            .contentShape(Rectangle())
            .padding(.horizontal, 14).padding(.vertical, 9)
        }
        .buttonStyle(.plain)
        .foregroundColor(.white)
    }

    private var mfaUnlockRow: some View {
        // Push-to-approve (cross-device): show the match number; else the TOTP unlock deep link.
        let label = status.mfaApproveNumber.map { "Approve \($0) on your phone" } ?? "Unlock VPN"
        return Button(action: { if let url = status.mfaAuthorizeUrl { onUnlockMfa(url) } }) {
            HStack {
                Image(systemName: "lock.shield")
                Text(label)
                Spacer()
            }
            .contentShape(Rectangle())
            .padding(.horizontal, 14).padding(.vertical, 9)
        }
        .buttonStyle(.plain)
        .foregroundColor(Color(red: 1.0, green: 0.8, blue: 0.2))
    }

    private var mfaEnrollRow: some View {
        infoRow("Two-factor setup required — scan the QR in the dialog.", systemImage: "qrcode")
    }

    /// macOS updates install automatically; this row is transient feedback (the daemon starts
    /// downloading + installing as soon as it detects a new version), so it reads "installing".
    private var updateRow: some View {
        HStack(spacing: 8) {
            ProgressView().controlSize(.small).scaleEffect(0.7)
            Text("Update available — installing…").font(.caption).foregroundColor(.secondary)
            Spacer()
        }
        .padding(.horizontal, 14).padding(.vertical, 7)
    }

    private func mfaSessionRow(_ remaining: String) -> some View {
        HStack(spacing: 8) {
            Image(systemName: "lock.open.fill").foregroundColor(Color(red: 0, green: 0.78, blue: 0.42))
            Text("MFA session active").font(.caption).foregroundColor(.secondary)
            Spacer()
            Text(remaining).font(.caption2).foregroundColor(.secondary)
        }
        .padding(.horizontal, 14).padding(.vertical, 7)
    }

    /// Human "expires in Xm"/"Xs" from the ISO-8601 MfaSessionExpiresAt, or nil if absent/past.
    /// Refreshes each 5 s poll (a live tick is M8 polish).
    private var mfaSessionRemaining: String? {
        guard let iso = status.mfaSessionExpiresAt,
              let date = isoDate(iso) else { return nil }
        let secs = Int(date.timeIntervalSinceNow)
        guard secs > 0 else { return nil }
        if secs >= 3600 { return "expires in \(secs / 3600)h \((secs % 3600) / 60)m" }
        if secs >= 60 { return "expires in \(secs / 60)m" }
        return "expires in \(secs)s"
    }

    private func isoDate(_ s: String) -> Date? {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        if let d = f.date(from: s) { return d }
        f.formatOptions = [.withInternetDateTime]
        return f.date(from: s)
    }

    private var profileList: some View {
        VStack(spacing: 0) {
            if status.availableProfiles.isEmpty && state.daemonReachable {
                infoRow("No VPN profiles yet. Upload a .conf to get started.", systemImage: "tray")
            } else {
                ForEach(status.availableProfiles, id: \.self) { profile in
                    ProfileRow(
                        profile: profile,
                        state: state,
                        isConnected: connected(profile),
                        isVerified: verified(profile),
                        isDeletable: status.deletableProfiles.contains(profile)
                    )
                }
            }
        }
    }

    // MARK: Footer

    private var footer: some View {
        HStack(spacing: 16) {
            footerButton("Upload…", systemImage: "square.and.arrow.up", action: onUploadConfig)
            footerButton("About", systemImage: "info.circle", action: onAbout)
            Spacer()
            Text("v\(appVersion)").font(.caption2).foregroundColor(.secondary.opacity(0.7))
            footerButton("Quit", systemImage: "power", action: onQuit)
        }
        .padding(.horizontal, 14).padding(.vertical, 8)
    }

    private func footerButton(_ title: String, systemImage: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Label(title, systemImage: systemImage).font(.caption)
        }
        .buttonStyle(.plain)
        .foregroundColor(.white.opacity(0.85))
    }

    private func actionRow(_ title: String, systemImage: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            HStack {
                Image(systemName: systemImage)
                Text(title)
                Spacer()
            }
            .contentShape(Rectangle())
            .padding(.horizontal, 14).padding(.vertical, 8)
        }
        .buttonStyle(.plain)
        .foregroundColor(.white.opacity(0.85))
    }

    private func infoRow(_ text: String, systemImage: String) -> some View {
        HStack(spacing: 8) {
            Image(systemName: systemImage).foregroundColor(.secondary)
            Text(text).font(.caption).foregroundColor(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            Spacer()
        }
        .padding(.horizontal, 14).padding(.vertical, 9)
    }

    // MARK: Helpers

    private func connected(_ profile: String) -> Bool {
        status.connectedTunnels.contains { $0.name == profile }
    }
    private func verified(_ profile: String) -> Bool {
        status.connectedTunnels.first { $0.name == profile }?.isVerified ?? false
    }
}

/// One profile row: click body to connect/disconnect; hover shows a delete × on user-uploaded
/// (deletable) profiles. Indicator = green ✓ (verified), green dot (connected), grey (available).
private struct ProfileRow: View {
    let profile: String
    @ObservedObject var state: AppState
    let isConnected: Bool
    let isVerified: Bool
    let isDeletable: Bool

    @State private var hovering = false

    private var busy: Bool { state.busyProfiles.contains(profile) }

    var body: some View {
        HStack(spacing: 10) {
            indicator
            Text(profile).foregroundColor(.white).lineLimit(1)
            Spacer()
            if busy {
                ProgressView().controlSize(.small).scaleEffect(0.7)
            } else if isVerified {
                tag("Verified", color: Color(red: 0, green: 0.78, blue: 0.42), filled: true)
            } else if isConnected {
                tag("Connected", color: Color(red: 0, green: 0.86, blue: 0.45), filled: false)
            }
            if isDeletable && hovering && !busy {
                Button(action: { state.deleteProfile(profile) }) {
                    Image(systemName: "xmark.circle.fill")
                        .foregroundColor(hovering ? .red.opacity(0.9) : .secondary)
                }
                .buttonStyle(.plain)
                .help("Delete this profile")
            }
        }
        .padding(.horizontal, 14).padding(.vertical, 9)
        .contentShape(Rectangle())
        .background(hovering ? Color.white.opacity(0.05) : Color.clear)
        .onHover { hovering = $0 }
        .onTapGesture {
            guard !busy else { return }
            if isConnected { state.disconnect(profile: profile) } else { state.connect(profile: profile) }
        }
    }

    @ViewBuilder private var indicator: some View {
        if isVerified {
            Image(systemName: "checkmark.circle.fill")
                .foregroundColor(Color(red: 0, green: 0.78, blue: 0.42))
        } else {
            Circle()
                .fill(isConnected ? Color(red: 0, green: 0.86, blue: 0.45) : Color.gray.opacity(0.6))
                .frame(width: 10, height: 10)
        }
    }

    private func tag(_ text: String, color: Color, filled: Bool) -> some View {
        Text(text)
            .font(.caption2).bold()
            .foregroundColor(filled ? .white : color)
            .padding(.horizontal, filled ? 7 : 0).padding(.vertical, filled ? 2 : 0)
            .background(filled ? color : Color.clear)
            .clipShape(Capsule())
    }
}
