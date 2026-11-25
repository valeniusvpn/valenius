package com.valenius.valenius_vpn

import android.content.Context
import android.content.SharedPreferences
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Log
import androidx.security.crypto.EncryptedSharedPreferences
import androidx.security.crypto.MasterKey

/**
 * One-time reader for the data the app stored with flutter_secure_storage **v9**
 * (`AndroidOptions(encryptedSharedPreferences: true)`).
 *
 * flutter_secure_storage v11 dropped the Jetpack Security (EncryptedSharedPreferences)
 * backend entirely and cannot read v9 data -- upstream's only path is "install v10 first",
 * which Play Store users skipping releases can't be relied on to do. Losing that store
 * would wipe the device's ClientKey and its individual device credential (F02), forcing an
 * administrator recovery QR for every phone. So the app reads the v9 store itself, exactly
 * the way v9 opened it, and the Dart side (SecureKeyStore) copies every entry into the v11
 * store before the app touches any secret.
 *
 * Only reads; [delete] is called by Dart after the copy has been verified.
 */
internal object LegacySecureStorage {
    private const val TAG = "ValeniusLegacyStorage"

    // Must match flutter_secure_storage 9.2.4's FlutterSecureStorage.java exactly.
    private const val PREFS_NAME = "FlutterSecureStorage"
    private const val KEY_PREFIX = "VGhpcyBpcyB0aGUgcHJlZml4IGZvciBhIHNlY3VyZSBzdG9yYWdlCg"
    private const val ESP_KEYSET_KEY = "__androidx_security_crypto_encrypted_prefs_key_keyset__"
    private const val ESP_VALUE_KEYSET = "__androidx_security_crypto_encrypted_prefs_value_keyset__"

    // Plain bookkeeping for failed attempts (no secrets).
    private const val STATE_PREFS = "ValeniusLegacyStorageMigration"
    private const val FAILED_ATTEMPTS = "failedAttempts"

    /**
     * After this many app launches in a row that could not decrypt the v9 store, give up
     * and let the app start fresh. The data is then unreadable on this device for good
     * (typically: restored from a backup onto a new phone, where the Keystore master key
     * doesn't exist) -- exactly the case where v9 itself would have started fresh. A
     * transient Keystore error (e.g. right after boot) gets retried on the next launches.
     */
    private const val MAX_FAILED_ATTEMPTS = 3

    /**
     * Returns `{status, entries?, skipped?, error?}`:
     *  - `none`      -- no v9 data on this device (fresh install / already migrated)
     *  - `ok`        -- `entries` holds every v9 key -> value (prefix stripped)
     *  - `failed`    -- v9 data exists but couldn't be decrypted now; retry next launch
     *  - `abandoned` -- failed [MAX_FAILED_ATTEMPTS] launches running; treat as unreadable
     */
    fun read(context: Context): Map<String, Any?> {
        val raw = context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
        val all = raw.all
        if (all.isEmpty()) return mapOf("status" to "none")

        // v9 falls back to plain prefs with its own cipher when EncryptedSharedPreferences
        // couldn't be created; those entries keep their key names in clear text. Not
        // supported here (it needs the deprecated RSA/PKCS1 key) -- counted for diagnostics.
        val plainEntries = all.keys.count { it.contains(KEY_PREFIX) }
        val hasEsp = all.containsKey(ESP_KEYSET_KEY) || all.containsKey(ESP_VALUE_KEYSET)
        if (!hasEsp) {
            return mapOf("status" to "none", "skipped" to plainEntries)
        }

        return try {
            val esp = open(context)
            val entries = HashMap<String, String>()
            for ((key, value) in esp.all) {
                if (value is String && key.contains(KEY_PREFIX)) {
                    entries[key.replaceFirst("${KEY_PREFIX}_", "")] = value
                }
            }
            state(context).edit().remove(FAILED_ATTEMPTS).commit()
            mapOf("status" to "ok", "entries" to entries, "skipped" to plainEntries)
        } catch (e: Throwable) {
            if (e is VirtualMachineError) throw e
            val attempts = state(context).getInt(FAILED_ATTEMPTS, 0) + 1
            state(context).edit().putInt(FAILED_ATTEMPTS, attempts).commit()
            Log.w(TAG, "Could not read v9 secure storage (attempt $attempts)", e)
            val status = if (attempts >= MAX_FAILED_ATTEMPTS) "abandoned" else "failed"
            mapOf(
                "status" to status,
                "error" to "${e.javaClass.simpleName}: ${e.message}",
                "attempts" to attempts,
            )
        }
    }

    /** Removes the v9 data file once its contents are safely in the v11 store. */
    fun delete(context: Context) {
        context.deleteSharedPreferences(PREFS_NAME)
        state(context).edit().clear().commit()
    }

    private fun state(context: Context): SharedPreferences =
        context.getSharedPreferences(STATE_PREFS, Context.MODE_PRIVATE)

    // Same master key + schemes as flutter_secure_storage 9.2.4
    // initializeEncryptedSharedPreferencesManager().
    private fun open(context: Context): SharedPreferences {
        val masterKey = MasterKey.Builder(context)
            .setKeyGenParameterSpec(
                KeyGenParameterSpec.Builder(
                    MasterKey.DEFAULT_MASTER_KEY_ALIAS,
                    KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT,
                )
                    .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                    .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                    .setKeySize(256)
                    .build(),
            )
            .build()
        return EncryptedSharedPreferences.create(
            context,
            PREFS_NAME,
            masterKey,
            EncryptedSharedPreferences.PrefKeyEncryptionScheme.AES256_SIV,
            EncryptedSharedPreferences.PrefValueEncryptionScheme.AES256_GCM,
        )
    }
}
