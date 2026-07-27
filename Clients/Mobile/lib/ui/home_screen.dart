import 'dart:async';

import 'package:firebase_messaging/firebase_messaging.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../api/backend_client.dart';
import '../app/theme.dart';
import '../state/app_state.dart';
import '../state/registration.dart';
import 'about_dialog.dart';
import 'approve_banner.dart';
import 'mfa_banner.dart';
import 'pair_screen.dart';
import 'widgets/profile_row.dart';

/// The tray popup, expanded into a screen. Shows activation state until the
/// device is approved, then the heartbeat-driven profile list.
class HomeScreen extends ConsumerStatefulWidget {
  const HomeScreen({super.key});

  @override
  ConsumerState<HomeScreen> createState() => _HomeScreenState();
}

class _HomeScreenState extends ConsumerState<HomeScreen> with WidgetsBindingObserver {
  StreamSubscription<RemoteMessage>? _fgMsg;
  StreamSubscription<RemoteMessage>? _openMsg;

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addObserver(this);
    // FCM wake → pull pending approvals immediately (vs. waiting for the poll).
    try {
      _fgMsg = FirebaseMessaging.onMessage.listen(_onPush);
      _openMsg = FirebaseMessaging.onMessageOpenedApp.listen(_onPush);
    } catch (_) {
      // Firebase not configured — approvals still arrive via the heartbeat poll.
    }
  }

  void _onPush(RemoteMessage message) {
    if (!mounted) return;
    if (message.data['type'] == 'mfa_approve') {
      ref.read(registrationControllerProvider.notifier).refresh();
    }
  }

  @override
  void didChangeAppLifecycleState(AppLifecycleState state) {
    // Re-opening the app (e.g. after the phone was locked) prompts a fresh
    // heartbeat right away rather than waiting for the next poll interval —
    // RegistrationController's own retry-with-backoff absorbs the radio still
    // reconnecting from the resume.
    if (state == AppLifecycleState.resumed) {
      ref.read(registrationControllerProvider.notifier).refresh();
    }
  }

  @override
  void dispose() {
    WidgetsBinding.instance.removeObserver(this);
    _fgMsg?.cancel();
    _openMsg?.cancel();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final reg = ref.watch(registrationControllerProvider);
    final conn = ref.watch(connectionControllerProvider);

    ref.listen(connectionControllerProvider, (prev, next) {
      if (next.error != null && next.errorAt != prev?.errorAt) {
        if (next.isLanConflict) {
          // Blocking alert, not a snackbar — this refusal must not be missed or
          // auto-dismissed like a transient connect error.
          showDialog<void>(
            context: context,
            builder: (ctx) => AlertDialog(
              backgroundColor: ValeniusColors.hover,
              icon: const Icon(Icons.error, color: Colors.red, size: 40),
              title: const Text(
                "Network conflict — can't connect",
                style: TextStyle(color: Colors.white),
              ),
              content: Text(
                next.error!,
                style: const TextStyle(color: ValeniusColors.actionText),
              ),
              actions: [
                TextButton(
                  onPressed: () => Navigator.of(ctx).pop(),
                  child: const Text('OK'),
                ),
              ],
            ),
          );
        } else {
          ScaffoldMessenger.of(context)
            ..hideCurrentSnackBar()
            ..showSnackBar(SnackBar(
              content: Text(next.error!),
              backgroundColor: ValeniusColors.deleteHot,
              duration: const Duration(seconds: 6),
            ));
        }
      }
    });

    ref.listen(registrationControllerProvider, (prev, next) {
      if (next.status == RegStatus.error && next.errorAt != prev?.errorAt) {
        final hasCachedProfiles =
            ref.read(profilesControllerProvider).isNotEmpty;
        ScaffoldMessenger.of(context)
          ..hideCurrentSnackBar()
          ..showSnackBar(SnackBar(
            content: Text(hasCachedProfiles
                ? "Couldn't reach the server — showing cached profiles."
                : "Couldn't reach the server."),
            backgroundColor: ValeniusColors.deleteHot,
            duration: const Duration(seconds: 6),
          ));
      }
    });

    ref.listen(profileChangeEventProvider, (prev, next) {
      if (next == null) return;
      final String verb;
      final Color color;
      switch (next.kind) {
        case ProfileChangeKind.added:
          verb = 'added';
          color = ValeniusColors.verifiedPill;
          break;
        case ProfileChangeKind.updated:
          verb = 'updated';
          color = ValeniusColors.verifiedPill;
          break;
        case ProfileChangeKind.deleted:
          verb = 'removed';
          color = ValeniusColors.deleteHot;
          break;
      }
      ScaffoldMessenger.of(context)
        ..hideCurrentSnackBar()
        ..showSnackBar(SnackBar(
          content: Text("Profile '${next.name}' $verb"),
          backgroundColor: color,
          duration: const Duration(seconds: 4),
        ));
    });

    ref.listen(mfaStateProvider, (prev, next) {
      // Fire only on an actual gated -> cleared transition, not on every
      // heartbeat — otherwise this would also fire (wrongly) on first load
      // for a device that was never gated to begin with.
      final wasPending =
          prev != null && (prev.required || prev.enrollmentOpen);
      final nowClear = !next.required && !next.enrollmentOpen;
      if (wasPending && nowClear) {
        ScaffoldMessenger.of(context)
          ..hideCurrentSnackBar()
          ..showSnackBar(const SnackBar(
            content: Text('Authenticated successfully'),
            backgroundColor: ValeniusColors.verifiedPill,
            duration: Duration(seconds: 4),
          ));
      }
    });

    return Scaffold(
      backgroundColor: ValeniusColors.bg,
      body: SafeArea(
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            _Header(active: conn.activeProfile),
            const Divider(height: 1, color: ValeniusColors.sep),
            const MfaBanner(),
            const ApproveBanner(),
            Expanded(child: _body(context, ref, reg, conn)),
          ],
        ),
      ),
    );
  }

  Widget _body(
    BuildContext context,
    WidgetRef ref,
    RegistrationState reg,
    VpnConnectionState conn,
  ) {
    switch (reg.status) {
      case RegStatus.loading:
        return const Center(
          child: CircularProgressIndicator(color: ValeniusColors.dotOn),
        );
      case RegStatus.error:
        // A heartbeat failure doesn't necessarily mean we have nothing to show:
        // the profile list is locally cached and doesn't need the network, so
        // keep it up (with a snackbar noting the sync failure, see build())
        // rather than blanking the whole screen for what's often a transient
        // blip (e.g. the radio still reconnecting after the phone was locked).
        final cachedProfiles = ref.watch(profilesControllerProvider);
        if (cachedProfiles.isNotEmpty) {
          return _profileList(ref, conn, cachedProfiles);
        }
        return _Message(
          title: 'Cannot reach the server',
          detail: reg.error ?? '',
          onRetry: () => ref.read(registrationControllerProvider.notifier).refresh(),
        );
      case RegStatus.awaitingActivation:
        return _AwaitingActivation(
          clientKey: reg.clientKey ?? '',
          title: 'Waiting for activation',
          detail:
              'Ask an administrator to approve this device, or pair it with a QR code.',
        );
      case RegStatus.duplicate:
        return _AwaitingActivation(
          clientKey: reg.clientKey ?? '',
          title: 'Device already exists',
          detail:
              'A device with this name is already registered. Ask an administrator to reassign or keep both.',
        );
      case RegStatus.active:
        final profiles = ref.watch(profilesControllerProvider);
        if (profiles.isEmpty) {
          return const _Message(
            title: 'No profiles yet',
            detail: 'No VPN profiles have been delivered to this device yet.',
          );
        }
        return _profileList(ref, conn, profiles);
    }
  }

  Widget _profileList(
    WidgetRef ref,
    VpnConnectionState conn,
    List<ProfileMeta> profiles,
  ) {
    final controller = ref.read(connectionControllerProvider.notifier);
    final store = ref.read(configStoreProvider);
    final gateways = ref.watch(gatewayTargetsProvider);
    return ListView.separated(
      itemCount: profiles.length,
      separatorBuilder: (_, __) =>
          const Divider(height: 1, color: ValeniusColors.sep),
      itemBuilder: (context, i) {
        final p = profiles[i];
        final connected = conn.isConnected(p.name);
        return ProfileRow(
          name: p.name,
          connected: connected,
          verified: connected && conn.verified,
          mfaGated: p.mfaGated,
          deletable: p.deletable,
          onTap: conn.busy
              ? null
              : () async {
                  final cfg = await store.read(p.name) ?? '';
                  await controller.toggle(p.name, cfg, gateways[p.name]);
                },
        );
      },
    );
  }
}

class _Header extends ConsumerWidget {
  const _Header({required this.active});

  final String? active;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final online = active != null;
    return Padding(
      padding: const EdgeInsets.fromLTRB(16, 6, 4, 6),
      child: Row(
        children: [
          Image.asset('assets/icon/valenius.png', height: 28),
          const SizedBox(width: 10),
          const Text(
            'Valenius',
            style: TextStyle(
              color: Colors.white,
              fontSize: 18,
              fontWeight: FontWeight.w600,
            ),
          ),
          const Spacer(),
          Container(
            width: 8,
            height: 8,
            decoration: BoxDecoration(
              shape: BoxShape.circle,
              color: online ? ValeniusColors.dotOn : ValeniusColors.dotOff,
            ),
          ),
          const SizedBox(width: 6),
          Text(
            online ? 'Connected' : 'Not connected',
            style: const TextStyle(color: ValeniusColors.actionText, fontSize: 12),
          ),
          PopupMenuButton<String>(
            icon: const Icon(Icons.menu, color: Colors.white),
            color: ValeniusColors.hover,
            onSelected: (value) {
              if (value == 'about') {
                showValeniusAbout(context);
              } else if (value == 'backend_check') {
                _runBackendCheck(context, ref);
              } else if (value == 'sync') {
                _runSync(context, ref);
              } else if (value == 'send_logs') {
                _runSendLogs(context, ref);
              }
            },
            itemBuilder: (_) => const [
              PopupMenuItem<String>(
                value: 'sync',
                child: Text('Sync',
                    style: TextStyle(color: ValeniusColors.profileText)),
              ),
              PopupMenuItem<String>(
                value: 'send_logs',
                child: Text('Send logs',
                    style: TextStyle(color: ValeniusColors.profileText)),
              ),
              PopupMenuItem<String>(
                value: 'backend_check',
                child: Text('Backend check',
                    style: TextStyle(color: ValeniusColors.profileText)),
              ),
              PopupMenuItem<String>(
                value: 'about',
                child: Text('About',
                    style: TextStyle(color: ValeniusColors.profileText)),
              ),
            ],
          ),
        ],
      ),
    );
  }

  /// "Sync" menu action — forces an immediate heartbeat so pending changes,
  /// staged/foreign profiles and MFA state are pulled now instead of waiting for
  /// the next poll tick. Reuses the registration refresh (register + ingestConfigs).
  Future<void> _runSync(BuildContext context, WidgetRef ref) async {
    final messenger = ScaffoldMessenger.maybeOf(context);
    messenger?.showSnackBar(const SnackBar(
      content: Text('Syncing…'),
      duration: Duration(seconds: 1),
    ));
    await ref.read(registrationControllerProvider.notifier).refresh();
    if (!context.mounted) return;
    final ok = ref.read(registrationControllerProvider).status != RegStatus.error;
    messenger?.showSnackBar(SnackBar(
      content: Text(ok ? 'Sync complete' : 'Sync failed — backend unreachable'),
      duration: const Duration(seconds: 2),
    ));
  }

  /// "Send logs" menu action — uploads the app's (redacted, app-only) diagnostic
  /// log to the backend for support. What's collected: the in-app event log only.
  Future<void> _runSendLogs(BuildContext context, WidgetRef ref) async {
    final messenger = ScaffoldMessenger.maybeOf(context);
    messenger?.showSnackBar(const SnackBar(
      content: Text('Sending diagnostic logs…'),
      duration: Duration(seconds: 1),
    ));
    await ref.read(registrationControllerProvider.notifier).sendLogs();
    if (!context.mounted) return;
    messenger?.showSnackBar(const SnackBar(
      content: Text('Diagnostic logs sent to your administrator.'),
      duration: Duration(seconds: 2),
    ));
  }

  /// "Backend check" menu action — probes the backend and reports reachability
  /// in a dialog (the mobile analogue of the Windows tray About-dialog check).
  Future<void> _runBackendCheck(BuildContext context, WidgetRef ref) async {
    ScaffoldMessenger.maybeOf(context)?.showSnackBar(
      const SnackBar(
        content: Text('Checking backend…'),
        duration: Duration(seconds: 1),
      ),
    );

    // clientKey is irrelevant to the public /api/version probe — pass empty.
    final api = ref.read(backendFactoryProvider)(
      ref.read(backendBaseUrlProvider),
      '',
    );
    BackendCheckResult result;
    try {
      result = await api.checkBackend();
    } finally {
      api.close();
    }

    if (!context.mounted) return;
    await showDialog<void>(
      context: context,
      builder: (ctx) => AlertDialog(
        backgroundColor: ValeniusColors.hover,
        icon: Icon(
          result.reachable ? Icons.check_circle : Icons.error,
          color: result.reachable ? ValeniusColors.dotOn : Colors.red,
          size: 40,
        ),
        title: Text(
          result.reachable ? 'Backend reachable' : 'Backend unreachable',
          style: const TextStyle(color: Colors.white),
        ),
        content: Text(
          result.detail,
          style: const TextStyle(color: ValeniusColors.actionText),
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(ctx).pop(),
            child: const Text('OK'),
          ),
        ],
      ),
    );
  }
}

class _AwaitingActivation extends StatelessWidget {
  const _AwaitingActivation({
    required this.clientKey,
    required this.title,
    required this.detail,
  });

  final String clientKey;
  final String title;
  final String detail;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.all(24),
      child: Column(
        mainAxisAlignment: MainAxisAlignment.center,
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          const Icon(Icons.hourglass_empty, color: ValeniusColors.lockAmber, size: 40),
          const SizedBox(height: 16),
          Text(
            title,
            textAlign: TextAlign.center,
            style: const TextStyle(color: Colors.white, fontSize: 18, fontWeight: FontWeight.w600),
          ),
          const SizedBox(height: 8),
          Text(
            detail,
            textAlign: TextAlign.center,
            style: const TextStyle(color: ValeniusColors.actionText, fontSize: 13),
          ),
          const SizedBox(height: 20),
          Center(
            child: OutlinedButton.icon(
              onPressed: () => Navigator.of(context).push(
                MaterialPageRoute<void>(builder: (_) => const PairScreen()),
              ),
              icon: const Icon(Icons.qr_code_scanner, size: 18),
              label: const Text('Scan pairing code'),
              style: OutlinedButton.styleFrom(
                foregroundColor: ValeniusColors.dotOn,
                side: const BorderSide(color: ValeniusColors.sep),
              ),
            ),
          ),
          const SizedBox(height: 20),
          const Text(
            'Device ID',
            textAlign: TextAlign.center,
            style: TextStyle(color: ValeniusColors.dimText, fontSize: 11),
          ),
          const SizedBox(height: 4),
          SelectableText(
            clientKey,
            textAlign: TextAlign.center,
            style: const TextStyle(
              color: ValeniusColors.profileText,
              fontSize: 13,
              fontFamily: 'monospace',
            ),
          ),
        ],
      ),
    );
  }
}

class _Message extends StatelessWidget {
  const _Message({required this.title, required this.detail, this.onRetry});

  final String title;
  final String detail;
  final VoidCallback? onRetry;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.all(24),
      child: Column(
        mainAxisAlignment: MainAxisAlignment.center,
        children: [
          Text(
            title,
            textAlign: TextAlign.center,
            style: const TextStyle(color: Colors.white, fontSize: 16, fontWeight: FontWeight.w600),
          ),
          if (detail.isNotEmpty) ...[
            const SizedBox(height: 8),
            Text(
              detail,
              textAlign: TextAlign.center,
              style: const TextStyle(color: ValeniusColors.dimText, fontSize: 12),
            ),
          ],
          if (onRetry != null) ...[
            const SizedBox(height: 16),
            TextButton(onPressed: onRetry, child: const Text('Retry')),
          ],
        ],
      ),
    );
  }
}
