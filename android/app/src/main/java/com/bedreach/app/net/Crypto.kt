package com.bedreach.app.net

import java.nio.ByteBuffer
import java.security.KeyFactory
import java.security.KeyPair
import java.security.KeyPairGenerator
import java.security.MessageDigest
import java.security.SecureRandom
import java.security.spec.ECGenParameterSpec
import java.security.spec.X509EncodedKeySpec
import javax.crypto.Cipher
import javax.crypto.KeyAgreement
import javax.crypto.Mac
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec

/**
 * Mirror of windows/Crypto.cs — the two must stay byte-for-byte compatible. See PROTOCOL.md.
 */
object Crypto {
    private val rng = SecureRandom()

    fun random(n: Int) = ByteArray(n).also { rng.nextBytes(it) }

    fun sha256(vararg parts: ByteArray): ByteArray {
        val md = MessageDigest.getInstance("SHA-256")
        parts.forEach { md.update(it) }
        return md.digest()
    }

    fun hmac(key: ByteArray, vararg parts: ByteArray): ByteArray {
        val mac = Mac.getInstance("HmacSHA256")
        mac.init(SecretKeySpec(key, "HmacSHA256"))
        parts.forEach { mac.update(it) }
        return mac.doFinal()
    }

    /** HKDF-SHA256 (RFC 5869), 32-byte output. */
    fun hkdf(ikm: ByteArray, salt: ByteArray, info: String): ByteArray {
        val prk = hmac(salt, ikm)
        return hmac(prk, info.toByteArray(Charsets.UTF_8), byteArrayOf(1))
    }

    fun utf8(s: String) = s.toByteArray(Charsets.UTF_8)

    fun newEcKeyPair(): KeyPair =
        KeyPairGenerator.getInstance("EC").apply { initialize(ECGenParameterSpec("secp256r1")) }.generateKeyPair()

    /** Raw ECDH shared secret (x-coordinate, 32 bytes) — same as .NET DeriveRawSecretAgreement. */
    fun ecdh(mine: KeyPair, peerSpki: ByteArray): ByteArray {
        val peer = KeyFactory.getInstance("EC").generatePublic(X509EncodedKeySpec(peerSpki))
        return KeyAgreement.getInstance("ECDH").run {
            init(mine.private)
            doPhase(peer, true)
            generateSecret()
        }
    }

    class PairResult(val secret: ByteArray, val code: String)

    fun derivePairing(shared: ByteArray, phonePub: ByteArray, phoneNonce: ByteArray, pcPub: ByteArray, pcNonce: ByteArray): PairResult {
        val transcript = phonePub + phoneNonce + pcPub + pcNonce
        val secret = hkdf(shared, sha256(transcript), "bedreach-pair-v1")
        val h = sha256(utf8("bedreach-code-v1"), transcript, shared)
        val n = ByteBuffer.wrap(h, 0, 4).int.toLong() and 0xffffffffL
        return PairResult(secret, "%06d".format(n % 1_000_000L))
    }

    fun constantTimeEquals(a: ByteArray, b: ByteArray) = MessageDigest.isEqual(a, b)
}

/** AES-256-GCM framing, mirror of SecureChannel in Crypto.cs. */
class SecureChannel(key: ByteArray, isServer: Boolean) {
    private val keySpec = SecretKeySpec(key, "AES")
    private val sendDir: Byte = if (isServer) 1 else 0
    private val recvDir: Byte = if (isServer) 0 else 1
    private var sendCounter = 0L
    private var lastRecv = 0L
    private var anyRecv = false

    private fun nonce(dir: Byte, counter: Long) = ByteBuffer.allocate(12).put(dir).put(byteArrayOf(0, 0, 0)).putLong(counter).array()

    @Synchronized
    fun seal(plain: ByteArray): ByteArray {
        val c = ++sendCounter
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.ENCRYPT_MODE, keySpec, GCMParameterSpec(128, nonce(sendDir, c)))
        val ct = cipher.doFinal(plain) // ciphertext || tag
        return ByteBuffer.allocate(8 + ct.size).putLong(c).put(ct).array()
    }

    fun open(frame: ByteArray): ByteArray {
        require(frame.size >= 24) { "short frame" }
        val c = ByteBuffer.wrap(frame, 0, 8).long
        require(!anyRecv || java.lang.Long.compareUnsigned(c, lastRecv) > 0) { "replayed frame" }
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.DECRYPT_MODE, keySpec, GCMParameterSpec(128, nonce(recvDir, c)))
        val plain = cipher.doFinal(frame, 8, frame.size - 8)
        lastRecv = c; anyRecv = true
        return plain
    }
}
