package com.bedreach.app.net

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Assume.assumeTrue
import org.junit.Test
import java.io.File
import java.net.InetAddress
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit

/** Pure-JVM tests of the channel crypto. */
class SecureChannelTest {
    @Test
    fun roundTripAndReplayRejected() {
        val key = Crypto.random(32)
        val phone = SecureChannel(key, isServer = false)
        val pc = SecureChannel(key, isServer = true)
        val f1 = phone.seal("hello".toByteArray())
        assertEquals("hello", String(pc.open(f1)))
        try { pc.open(f1); fail("replay accepted") } catch (_: IllegalArgumentException) {}
        val f2 = phone.seal("x".toByteArray())
        f2[f2.size - 1] = (f2[f2.size - 1].toInt() xor 1).toByte()
        try { pc.open(f2); fail("tampered frame accepted") } catch (_: Exception) {}
    }

    @Test
    fun hkdfMatchesRfc5869Case1() {
        // RFC 5869 test case 1 (first 32 bytes of OKM), info = 0xf0..0xf9
        // (Crypto.hkdf takes a UTF-8 string, and these info bytes aren't valid UTF-8, so the
        //  same extract/expand steps are checked directly, then hkdf() is checked against them.)
        val ikm = ByteArray(22) { 0x0b }
        val salt = ByteArray(13) { it.toByte() }
        val prk = Crypto.hmac(salt, ikm)
        val okm = Crypto.hmac(prk, ByteArray(10) { (0xf0 + it).toByte() }, byteArrayOf(1))
        assertEquals("3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf", okm.joinToString("") { "%02x".format(it) })
        val viaHelper = Crypto.hkdf(ikm, salt, "bedreach")
        val manual = Crypto.hmac(prk, "bedreach".toByteArray(), byteArrayOf(1))
        assertTrue(viaHelper.contentEquals(manual))
    }
}

/**
 * End-to-end against the real Windows app running in Debug test mode:
 *   set BEDREACH_TEST_DIR=<dir> and start windows/bin/Debug/.../BedReach.exe first.
 * Skipped when the variable isn't set.
 */
class EndToEndTest {
    private val dir = System.getenv("BEDREACH_TEST_DIR")

    @Test
    fun pairConnectCommandAndRejections() {
        assumeTrue("BEDREACH_TEST_DIR not set", dir != null)
        val host = "127.0.0.1"

        // 1. discovery
        val found = Discovery.scan(1200, listOf(InetAddress.getLoopbackAddress()))
        assertEquals(1, found.size)
        val pcInfo = found[0]

        // 2. pairing — the code we show must equal the code the PC shows
        var phoneCode = ""
        val pc = Pairing(host, pcInfo.port, "Test Phone").run { code, _ -> phoneCode = code }
        assertEquals(File(dir, "pair-code.txt").readText(), phoneCode)
        assertEquals(pcInfo.serverId, pc.serverId)

        // 3. authenticated session + commands
        val gotInfo = CountDownLatch(1)
        val closed = CountDownLatch(1)
        val s = Session.connect(host, pc.port, pc.deviceId, pc.secret, object : Session.Listener {
            override fun onInfo(pcName: String, services: List<Service>) { servicesHolder = services; gotInfo.countDown() }
            override fun onClosed(error: Throwable?) { closed.countDown() }
        })
        assertTrue("no info message", gotInfo.await(5, TimeUnit.SECONDS))
        val services = servicesHolder
        assertTrue("expected default services", services.any { it.id == "netflix" } && services.any { it.id == "spotify" })
        // Optional: BEDREACH_EXPECT_APP=netflix asserts that the PC detected that app as installed.
        System.getenv("BEDREACH_EXPECT_APP")?.let { id ->
            assertTrue("$id should be detected as an installed app: $services", services.first { it.id == id }.installedApp)
        }
        s.send(Cmd.key("playpause"))
        s.send(Cmd.launch("netflix"))
        s.send(Cmd.move(5, -3))
        s.send(Cmd.text("héllo"))
        Thread.sleep(800)
        s.close()
        val log = File(dir, "commands.log").readText()
        listOf("\"playpause\"", "\"netflix\"", "\"dx\":5", "h\\u00E9llo" /* .NET's JSON escapes é */).forEach { assertTrue("missing $it in log:\n$log", log.contains(it)) }

        // 4. wrong secret is rejected
        try {
            Session.connect(host, pc.port, pc.deviceId, Crypto.random(32), NoopListener).close()
            fail("wrong secret accepted")
        } catch (_: NotPairedException) {}

        // 5. unknown device is rejected
        try {
            Session.connect(host, pc.port, "nope", pc.secret, NoopListener).close()
            fail("unknown device accepted")
        } catch (_: NotPairedException) {}

        // 6. denied pairing reports cleanly
        try {
            Pairing(host, pc.port, "DENY-ME").run { _, _ -> }
            fail("denied pairing succeeded")
        } catch (e: ProtocolException) {
            assertTrue(e.message!!.contains("denied"))
        }
    }

    private object NoopListener : Session.Listener {
        override fun onInfo(pcName: String, services: List<Service>) {}
        override fun onClosed(error: Throwable?) {}
    }

    @Volatile private var servicesHolder: List<Service> = emptyList()
}
