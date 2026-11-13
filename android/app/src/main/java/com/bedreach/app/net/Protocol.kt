package com.bedreach.app.net

import org.json.JSONArray
import org.json.JSONObject
import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.IOException
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.Socket
import java.net.SocketTimeoutException
import java.util.Base64
import java.util.concurrent.Executors
import java.util.concurrent.ScheduledExecutorService
import java.util.concurrent.TimeUnit

/** A PC this phone has paired with. */
data class PairedPc(
    val serverId: String,
    val name: String,
    val host: String,
    val port: Int,
    val deviceId: String,
    val secret: ByteArray,
)

data class FoundPc(val serverId: String, val name: String, val host: String, val port: Int)

data class Service(val id: String, val name: String, val color: String, val installedApp: Boolean = false)

class ProtocolException(msg: String) : IOException(msg)

private fun b64(b: ByteArray): String = Base64.getEncoder().encodeToString(b)
private fun unb64(s: String): ByteArray = Base64.getDecoder().decode(s)

private class Framed(val socket: Socket) {
    private val input = DataInputStream(socket.getInputStream().buffered())
    private val output = DataOutputStream(socket.getOutputStream().buffered())

    fun read(): ByteArray {
        val len = input.readInt()
        if (len <= 0 || len > 64 * 1024) throw ProtocolException("bad frame length")
        return ByteArray(len).also { input.readFully(it) }
    }

    @Synchronized
    fun write(b: ByteArray) {
        output.writeInt(b.size)
        output.write(b)
        output.flush()
    }

    fun readJson() = JSONObject(String(read(), Charsets.UTF_8))
    fun writeJson(o: JSONObject) = write(o.toString().toByteArray(Charsets.UTF_8))
}

private fun open(host: String, port: Int, timeoutMs: Int): Socket =
    Socket().apply {
        tcpNoDelay = true
        connect(InetSocketAddress(host, port), timeoutMs)
    }

object Discovery {
    const val MAGIC = "BEDREACH_DISCOVER_V1"
    const val PORT = 47801

    /** Broadcasts on the LAN and collects replies for [durationMs]. Blocking — call off the main thread. */
    fun scan(durationMs: Int = 1800, extraTargets: List<InetAddress> = emptyList()): List<FoundPc> {
        val found = LinkedHashMap<String, FoundPc>()
        DatagramSocket().use { sock ->
            sock.broadcast = true
            sock.soTimeout = 250
            val msg = MAGIC.toByteArray()
            val targets = listOf(InetAddress.getByName("255.255.255.255")) + extraTargets
            val end = System.currentTimeMillis() + durationMs
            var nextSend = 0L
            val buf = ByteArray(2048)
            while (System.currentTimeMillis() < end) {
                if (System.currentTimeMillis() >= nextSend) {
                    targets.forEach { t -> runCatching { sock.send(DatagramPacket(msg, msg.size, t, PORT)) } }
                    nextSend = System.currentTimeMillis() + 600
                }
                try {
                    val p = DatagramPacket(buf, buf.size)
                    sock.receive(p)
                    val o = JSONObject(String(p.data, 0, p.length, Charsets.UTF_8))
                    if (o.optString("app") == "bedreach") {
                        val id = o.getString("id")
                        found[id] = FoundPc(id, o.optString("name", "PC"), p.address.hostAddress ?: continue, o.optInt("port", 47800))
                    }
                } catch (_: SocketTimeoutException) {
                } catch (_: Exception) {
                }
            }
        }
        return found.values.toList()
    }
}

/**
 * Pairing with numeric comparison. The phone commits to its key first, so a man-in-the-middle
 * can't choose keys that make the two codes match.
 */
class Pairing(private val host: String, private val port: Int, private val deviceName: String) {
    @Volatile private var socket: Socket? = null

    fun cancel() = runCatching { socket?.close() }

    /** Blocking. [onCode] is called (on this thread) with the 6-digit code to show the user. */
    fun run(onCode: (code: String, pcName: String) -> Unit): PairedPc {
        val s = open(host, port, 5000)
        socket = s
        s.use {
            it.soTimeout = 15_000
            val f = Framed(it)
            val kp = Crypto.newEcKeyPair()
            val myPub = kp.public.encoded
            val myNonce = Crypto.random(16)
            f.writeJson(JSONObject().put("t", "pair_hello").put("device", deviceName).put("commit", b64(Crypto.sha256(myPub, myNonce))))

            val srv = f.readJson()
            when (srv.optString("t")) {
                "pair_server" -> {}
                "error" -> throw ProtocolException(if (srv.optString("reason") == "busy") "The PC is already pairing another phone" else "PC refused")
                else -> throw ProtocolException("Unexpected reply")
            }
            val pcPub = unb64(srv.getString("pub"))
            val pcNonce = unb64(srv.getString("nonce"))
            val shared = Crypto.ecdh(kp, pcPub)
            val pr = Crypto.derivePairing(shared, myPub, myNonce, pcPub, pcNonce)

            f.writeJson(JSONObject().put("t", "pair_reveal").put("pub", b64(myPub)).put("nonce", b64(myNonce)))
            onCode(pr.code, srv.optString("name", "PC"))

            it.soTimeout = 100_000 // the person is walking over to click Allow
            val res = f.readJson()
            when (res.optString("t")) {
                "pair_ok" -> {
                    val deviceId = res.getString("deviceId")
                    val mac = Crypto.hmac(pr.secret, Crypto.utf8("paired"), Crypto.utf8(deviceId))
                    if (!Crypto.constantTimeEquals(mac, unb64(res.getString("mac")))) throw ProtocolException("PC failed verification")
                    return PairedPc(srv.getString("id"), srv.optString("name", "PC"), host, port, deviceId, pr.secret)
                }
                "pair_denied" -> throw ProtocolException("Pairing was denied on the PC")
                else -> throw ProtocolException("Unexpected reply")
            }
        }
    }
}

/**
 * A live, authenticated, encrypted connection. Commands are queued on a single background
 * thread so the UI never blocks; if the link drops, [Listener.onClosed] fires and the
 * owner reconnects.
 */
class Session private constructor(
    private val framed: Framed,
    private val channel: SecureChannel,
    private val listener: Listener,
) {
    interface Listener {
        fun onInfo(pcName: String, services: List<Service>)
        fun onClosed(error: Throwable?)
    }

    private val sender: ScheduledExecutorService = Executors.newSingleThreadScheduledExecutor()
    @Volatile private var closed = false

    companion object {
        /** Blocking connect + mutual authentication. */
        fun connect(host: String, port: Int, deviceId: String, secret: ByteArray, listener: Listener): Session {
            val s = open(host, port, 3000)
            try {
                s.soTimeout = 10_000
                val f = Framed(s)
                val myNonce = Crypto.random(16)
                f.writeJson(JSONObject().put("t", "auth").put("deviceId", deviceId).put("nonce", b64(myNonce)))
                val ch = f.readJson()
                if (ch.optString("t") == "auth_fail") throw NotPairedException()
                if (ch.optString("t") != "challenge") throw ProtocolException("Unexpected reply")
                val pcNonce = unb64(ch.getString("nonce"))
                f.writeJson(JSONObject().put("t", "auth_proof").put("mac", b64(Crypto.hmac(secret, Crypto.utf8("client"), myNonce, pcNonce))))
                val ok = f.readJson()
                if (ok.optString("t") == "auth_fail") throw NotPairedException()
                val expected = Crypto.hmac(secret, Crypto.utf8("server"), myNonce, pcNonce)
                if (ok.optString("t") != "auth_ok" || !Crypto.constantTimeEquals(expected, unb64(ok.getString("mac"))))
                    throw ProtocolException("PC failed verification")
                val key = Crypto.hkdf(secret, myNonce + pcNonce, "bedreach-session-v1")
                s.soTimeout = 20_000
                return Session(f, SecureChannel(key, isServer = false), listener).also { it.start() }
            } catch (e: Exception) {
                runCatching { s.close() }
                throw e
            }
        }
    }

    private fun start() {
        Thread({ readLoop() }, "bedreach-reader").apply { isDaemon = true }.start()
        sender.scheduleWithFixedDelay({ sendNow(JSONObject().put("t", "ping")) }, 5, 5, TimeUnit.SECONDS)
    }

    private fun readLoop() {
        try {
            while (!closed) {
                val o = JSONObject(String(channel.open(framed.read()), Charsets.UTF_8))
                if (o.optString("t") == "info") {
                    val arr: JSONArray = o.optJSONArray("services") ?: JSONArray()
                    val list = (0 until arr.length()).map { i ->
                        val x = arr.getJSONObject(i)
                        Service(x.getString("id"), x.optString("name"), x.optString("color", "#444444"), x.optBoolean("app", false))
                    }
                    listener.onInfo(o.optString("name", "PC"), list)
                }
            }
        } catch (e: Exception) {
            close(e)
        }
    }

    private fun sendNow(o: JSONObject) {
        if (closed) return
        try {
            framed.write(channel.seal(o.toString().toByteArray(Charsets.UTF_8)))
        } catch (e: Exception) {
            close(e)
        }
    }

    /** Queue a command; never blocks. */
    fun send(o: JSONObject) {
        if (!closed) runCatching { sender.execute { sendNow(o) } }
    }

    fun close(error: Throwable? = null) {
        if (closed) return
        closed = true
        sender.shutdownNow()
        runCatching { framed.socket.close() }
        listener.onClosed(error)
    }

    val isOpen get() = !closed
}

class NotPairedException : IOException("This phone is no longer paired with that PC")

/** Command builders — the full list of things the phone can ask for. */
object Cmd {
    fun key(k: String) = JSONObject().put("t", "key").put("k", k)
    fun text(s: String) = JSONObject().put("t", "text").put("s", s)
    fun move(dx: Int, dy: Int) = JSONObject().put("t", "move").put("dx", dx).put("dy", dy)
    fun click(b: String = "left") = JSONObject().put("t", "click").put("b", b)
    fun scroll(d: Int) = JSONObject().put("t", "scroll").put("d", d)
    fun launch(id: String) = JSONObject().put("t", "launch").put("id", id)
    fun power(a: String) = JSONObject().put("t", "power").put("a", a)
}
