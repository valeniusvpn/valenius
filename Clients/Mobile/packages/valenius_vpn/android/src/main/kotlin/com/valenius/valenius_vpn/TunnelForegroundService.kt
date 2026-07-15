package com.valenius.valenius_vpn

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.IBinder

/**
 * Keeps the app process at foreground importance while a tunnel is up, so the
 * OS doesn't reclaim it (and with it GoBackend's VpnService, which runs in the
 * same process) when the app is backgrounded. Shows an ongoing "Connected"
 * notification.
 */
class TunnelForegroundService : Service() {
    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        val profile = intent?.getStringExtra(EXTRA_PROFILE) ?: "VPN"
        ensureChannel(this)
        val notification = buildNotification(this, profile)
        if (Build.VERSION.SDK_INT >= 34) {
            startForeground(
                NOTIFICATION_ID,
                notification,
                ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE,
            )
        } else {
            startForeground(NOTIFICATION_ID, notification)
        }
        return START_STICKY
    }

    companion object {
        const val CHANNEL_ID = "valenius_vpn"
        const val NOTIFICATION_ID = 1001
        const val EXTRA_PROFILE = "profile"

        fun ensureChannel(context: Context) {
            if (Build.VERSION.SDK_INT < 26) return
            val mgr = context.getSystemService(NotificationManager::class.java)
            if (mgr.getNotificationChannel(CHANNEL_ID) == null) {
                mgr.createNotificationChannel(
                    NotificationChannel(
                        CHANNEL_ID,
                        "VPN status",
                        NotificationManager.IMPORTANCE_LOW,
                    ),
                )
            }
        }

        private fun buildNotification(context: Context, profile: String): Notification {
            val builder = if (Build.VERSION.SDK_INT >= 26) {
                Notification.Builder(context, CHANNEL_ID)
            } else {
                @Suppress("DEPRECATION")
                Notification.Builder(context)
            }
            return builder
                .setContentTitle("Valenius VPN")
                .setContentText("Connected to $profile")
                .setSmallIcon(context.applicationInfo.icon)
                .setOngoing(true)
                .build()
        }
    }
}
