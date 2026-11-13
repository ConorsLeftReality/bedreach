package com.bedreach.app

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import com.bedreach.app.net.PairedPc
import org.json.JSONArray
import org.json.JSONObject
import java.security.KeyStore
import java.util.Base64
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

/**
 * Saved PCs. Each pairing secret is encrypted with a key held in the Android Keystore,
 * so it can't be copied out of the app's storage.
 */
class PcStore(context: Context) {
    private val prefs = context.getSharedPreferences("pcs", Context.MODE_PRIVATE)

    fun all(): List<PairedPc> {
        val arr = JSONArray(prefs.getString("list", "[]"))
        return (0 until arr.length()).mapNotNull { i ->
            val o = arr.getJSONObject(i)
            runCatching {
                PairedPc(
                    o.getString("serverId"), o.getString("name"), o.getString("host"), o.getInt("port"),
                    o.getString("deviceId"), decrypt(o.getString("secret")),
                )
            }.getOrNull()
        }
    }

    fun get(serverId: String) = all().firstOrNull { it.serverId == serverId }

    fun save(pc: PairedPc) {
        val list = all().filter { it.serverId != pc.serverId } + pc
        write(list)
    }

    fun remove(serverId: String) = write(all().filter { it.serverId != serverId })

    var lastUsed: String?
        get() = prefs.getString("last", null)
        set(v) = prefs.edit().putString("last", v).apply()

    private fun write(list: List<PairedPc>) {
        val arr = JSONArray()
        list.forEach { pc ->
            arr.put(
                JSONObject().put("serverId", pc.serverId).put("name", pc.name).put("host", pc.host)
                    .put("port", pc.port).put("deviceId", pc.deviceId).put("secret", encrypt(pc.secret))
            )
        }
        prefs.edit().putString("list", arr.toString()).apply()
    }

    // ---- Android Keystore AES-GCM wrapping ----
    private val alias = "bedreach-secrets"

    private fun key(): SecretKey {
        val ks = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        (ks.getKey(alias, null) as? SecretKey)?.let { return it }
        return KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore").run {
            init(
                KeyGenParameterSpec.Builder(alias, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
                    .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                    .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                    .setKeySize(256)
                    .build()
            )
            generateKey()
        }
    }

    private fun encrypt(plain: ByteArray): String {
        val c = Cipher.getInstance("AES/GCM/NoPadding")
        c.init(Cipher.ENCRYPT_MODE, key())
        return Base64.getEncoder().encodeToString(c.iv + c.doFinal(plain))
    }

    private fun decrypt(s: String): ByteArray {
        val b = Base64.getDecoder().decode(s)
        val c = Cipher.getInstance("AES/GCM/NoPadding")
        c.init(Cipher.DECRYPT_MODE, key(), GCMParameterSpec(128, b, 0, 12))
        return c.doFinal(b, 12, b.size - 12)
    }
}
