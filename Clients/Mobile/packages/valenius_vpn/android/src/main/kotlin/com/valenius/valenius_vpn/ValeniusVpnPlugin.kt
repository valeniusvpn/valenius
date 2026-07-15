package com.valenius.valenius_vpn

import android.app.Activity
import android.content.Context
import android.content.Intent
import android.os.Handler
import android.os.Looper
import com.wireguard.android.backend.GoBackend
import com.wireguard.android.backend.Tunnel
import com.wireguard.config.Config
import io.flutter.embedding.engine.plugins.FlutterPlugin
import io.flutter.embedding.engine.plugins.activity.ActivityAware
import io.flutter.embedding.engine.plugins.activity.ActivityPluginBinding
import io.flutter.plugin.common.EventChannel
import io.flutter.plugin.common.MethodCall
import io.flutter.plugin.common.MethodChannel
import io.flutter.plugin.common.MethodChannel.MethodCallHandler
import io.flutter.plugin.common.MethodChannel.Result
import io.flutter.plugin.common.PluginRegistry
import java.io.BufferedReader
import java.io.StringReader
import java.util.concurrent.Executors

/**
 * Single-tunnel WireGuard backend exposed to Flutter. Android allows one system
 * VPN with one interface identity, so only one profile is active at a time;
 * bringing a new one up replaces the current one. The Dart side keeps the
 * per-profile UI; this plugin just owns the live tunnel.
 */
class ValeniusVpnPlugin :
    FlutterPlugin,
    ActivityAware,
    MethodCallHandler,
    EventChannel.StreamHandler,
    PluginRegistry.ActivityResultListener {

    private companion object {
        const val VPN_REQUEST_CODE = 0x7F01 // arbitrary, stable
    }

    private lateinit var methodChannel: MethodChannel
    private lateinit var eventChannel: EventChannel
    private lateinit var context: Context

    private var activity: Activity? = null
    private var events: EventChannel.EventSink? = null
    private var pendingPermission: Result? = null

    private val main = Handler(Looper.getMainLooper())
    private val io = Executors.newSingleThreadExecutor()
    private val backend: GoBackend by lazy { GoBackend(context) }

    private var current: WgTunnel? = null

    override fun onAttachedToEngine(binding: FlutterPlugin.FlutterPluginBinding) {
        context = binding.applicationContext
        methodChannel = MethodChannel(binding.binaryMessenger, "valenius_vpn")
        methodChannel.setMethodCallHandler(this)
        eventChannel = EventChannel(binding.binaryMessenger, "valenius_vpn/states")
        eventChannel.setStreamHandler(this)
    }

    override fun onDetachedFromEngine(binding: FlutterPlugin.FlutterPluginBinding) {
        methodChannel.setMethodCallHandler(null)
        eventChannel.setStreamHandler(null)
    }

    // --- MethodChannel ---

    override fun onMethodCall(call: MethodCall, result: Result) {
        when (call.method) {
            "hasPermission" -> result.success(GoBackend.VpnService.prepare(context) == null)
            "requestPermission" -> requestPermission(result)
            "up" -> up(call.argument<String>("name")!!, call.argument<String>("config")!!, result)
            "down" -> down(result)
            "stats" -> stats(call.argument<String>("name")!!, result)
            // No Android equivalent of iOS NEOnDemandRule; auto-connect on Android
            // is a separate (not-yet-built) mechanism. Accept and no-op so the
            // shared Dart seam stays uniform.
            "setOnDemand" -> result.success(null)
            else -> result.notImplemented()
        }
    }

    private fun requestPermission(result: Result) {
        val intent: Intent? = GoBackend.VpnService.prepare(context)
        if (intent == null) {
            result.success(true)
            return
        }
        val act = activity
        if (act == null) {
            result.error("no_activity", "VPN consent needs a foreground activity", null)
            return
        }
        pendingPermission = result
        act.startActivityForResult(intent, VPN_REQUEST_CODE)
    }

    private fun up(name: String, configText: String, result: Result) {
        io.execute {
            try {
                val config = Config.parse(BufferedReader(StringReader(configText)))
                current?.let { backend.setState(it, Tunnel.State.DOWN, null) }
                val tunnel = WgTunnel(name) { state -> emitState(name, state) }
                backend.setState(tunnel, Tunnel.State.UP, config)
                current = tunnel
                startForegroundService(name)
                emitState(name, Tunnel.State.UP)
                main.post { result.success(null) }
            } catch (e: Exception) {
                android.util.Log.e("ValeniusVpn", "up($name) failed", e)
                main.post {
                    result.error("up_failed", "${e.javaClass.simpleName}: ${e.message}", null)
                }
            }
        }
    }

    private fun down(result: Result) {
        io.execute {
            try {
                current?.let { backend.setState(it, Tunnel.State.DOWN, null) }
                val name = current?.name
                current = null
                stopForegroundService()
                if (name != null) emitState(name, Tunnel.State.DOWN)
                main.post { result.success(null) }
            } catch (e: Exception) {
                main.post { result.error("down_failed", e.message, null) }
            }
        }
    }

    private fun stats(name: String, result: Result) {
        io.execute {
            try {
                val tunnel = current
                if (tunnel == null || tunnel.name != name) {
                    main.post { result.success(null) }
                    return@execute
                }
                val s = backend.getStatistics(tunnel)
                var rx = 0L
                var tx = 0L
                var lastHandshake = 0L
                for (key in s.peers()) {
                    val p = s.peer(key) ?: continue
                    rx += p.rxBytes
                    tx += p.txBytes
                    if (p.latestHandshakeEpochMillis > lastHandshake) {
                        lastHandshake = p.latestHandshakeEpochMillis
                    }
                }
                val payload = mapOf(
                    "lastHandshakeEpochSec" to lastHandshake / 1000,
                    "rxBytes" to rx,
                    "txBytes" to tx,
                )
                main.post { result.success(payload) }
            } catch (e: Exception) {
                main.post { result.error("stats_failed", e.message, null) }
            }
        }
    }

    private fun startForegroundService(profile: String) {
        val intent = Intent(context, TunnelForegroundService::class.java)
            .putExtra(TunnelForegroundService.EXTRA_PROFILE, profile)
        if (android.os.Build.VERSION.SDK_INT >= 26) {
            context.startForegroundService(intent)
        } else {
            context.startService(intent)
        }
    }

    private fun stopForegroundService() {
        context.stopService(Intent(context, TunnelForegroundService::class.java))
    }

    private fun emitState(name: String, state: Tunnel.State) {
        val value = when (state) {
            Tunnel.State.UP -> "connected"
            Tunnel.State.TOGGLE -> "connecting"
            Tunnel.State.DOWN -> "down"
        }
        main.post { events?.success(mapOf("name" to name, "state" to value)) }
    }

    // --- EventChannel ---

    override fun onListen(arguments: Any?, sink: EventChannel.EventSink?) {
        events = sink
    }

    override fun onCancel(arguments: Any?) {
        events = null
    }

    // --- ActivityAware ---

    override fun onAttachedToActivity(binding: ActivityPluginBinding) {
        activity = binding.activity
        binding.addActivityResultListener(this)
    }

    override fun onReattachedToActivityForConfigChanges(binding: ActivityPluginBinding) =
        onAttachedToActivity(binding)

    override fun onDetachedFromActivityForConfigChanges() {
        activity = null
    }

    override fun onDetachedFromActivity() {
        activity = null
    }

    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?): Boolean {
        if (requestCode != VPN_REQUEST_CODE) return false
        pendingPermission?.success(resultCode == Activity.RESULT_OK)
        pendingPermission = null
        return true
    }
}

private class WgTunnel(
    private val tunnelName: String,
    private val onState: (Tunnel.State) -> Unit,
) : Tunnel {
    override fun getName(): String = tunnelName
    override fun onStateChange(newState: Tunnel.State) = onState(newState)
}
