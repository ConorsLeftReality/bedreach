package com.bedreach.app

import android.annotation.SuppressLint
import android.app.Activity
import android.app.AlertDialog
import android.content.Context
import android.graphics.Color
import android.graphics.Typeface
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.provider.Settings
import android.text.InputType
import android.view.Gravity
import android.view.KeyEvent
import android.view.MotionEvent
import android.view.View
import android.view.inputmethod.EditorInfo
import android.view.inputmethod.InputMethodManager
import android.widget.EditText
import android.widget.FrameLayout
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.ProgressBar
import android.widget.ScrollView
import android.widget.TextView
import android.widget.Toast
import com.bedreach.app.net.Cmd
import com.bedreach.app.net.Discovery
import com.bedreach.app.net.FoundPc
import com.bedreach.app.net.NotPairedException
import com.bedreach.app.net.PairedPc
import com.bedreach.app.net.Pairing
import com.bedreach.app.net.Service
import com.bedreach.app.net.Session
import org.json.JSONObject
import java.util.concurrent.Executors
import kotlin.math.abs
import kotlin.math.min
import kotlin.math.sign

class MainActivity : Activity() {
    private lateinit var store: PcStore
    private val main = Handler(Looper.getMainLooper())
    private val io = Executors.newCachedThreadPool()

    private enum class Screen { CONNECT, REMOTE }
    private var screen = Screen.CONNECT

    // ---- connection state (main thread only) ----
    private var current: PairedPc? = null
    private var session: Session? = null
    private var connId = 0
    private var connecting = false
    private var wantConnected = false
    private var retryDelay = 1000L
    private val retry = Runnable { connectNow() }

    // ---- remote screen views ----
    private var statusDot: View? = null
    private var statusText: TextView? = null
    private var titleText: TextView? = null
    private var appsGrid: LinearLayout? = null
    private var services: List<Service> = emptyList()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.statusBarColor = C.BG
        window.navigationBarColor = C.BG
        store = PcStore(this)
        val last = store.lastUsed?.let { store.get(it) }
        if (last != null) showRemote(last) else showConnect()
    }

    override fun onStart() {
        super.onStart()
        if (screen == Screen.REMOTE) { wantConnected = true; retryDelay = 1000; connectNow() }
    }

    override fun onStop() {
        super.onStop()
        disconnect()
    }

    @Deprecated("Deprecated in Java")
    override fun onBackPressed() {
        if (screen == Screen.REMOTE) showConnect() else @Suppress("DEPRECATION") super.onBackPressed()
    }

    override fun onDestroy() {
        super.onDestroy()
        io.shutdownNow()
    }

    /** Phone volume buttons control the PC's volume while the remote is open and connected. */
    override fun dispatchKeyEvent(event: KeyEvent): Boolean {
        val s = session
        if (screen == Screen.REMOTE && s != null && s.isOpen &&
            (event.keyCode == KeyEvent.KEYCODE_VOLUME_UP || event.keyCode == KeyEvent.KEYCODE_VOLUME_DOWN)
        ) {
            if (event.action == KeyEvent.ACTION_DOWN)
                s.send(Cmd.key(if (event.keyCode == KeyEvent.KEYCODE_VOLUME_UP) "volup" else "voldown"))
            return true
        }
        return super.dispatchKeyEvent(event)
    }

    // =====================================================================================
    // Connect / pair screen
    // =====================================================================================

    private fun showConnect() {
        screen = Screen.CONNECT
        disconnect()
        current = null
        store.lastUsed = null

        val col = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(24), dp(40), dp(24), dp(32))
        }
        col.addView(ImageView(this).apply { setImageResource(R.drawable.ic_logo) }, lp(dp(72), dp(72)))
        col.addView(text("BedReach", 30f, bold = true), lp(WRAP, WRAP).margins(this, t = 18))
        col.addView(text("Control your PC from bed or the sofa.", 15f, C.MUTED), lp(WRAP, WRAP).margins(this, t = 4))

        val saved = store.all()
        if (saved.isNotEmpty()) {
            col.addView(sectionTitle("Your PCs"))
            saved.forEach { pc ->
                col.addView(pcCard(pc.name, pc.host, paired = true, onClick = { showRemote(pc) }, onLong = { confirmForget(pc) }))
            }
        }

        col.addView(sectionTitle("On this Wi-Fi"))
        val foundBox = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        col.addView(foundBox)

        val row = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
        row.addView(pillButton("Scan again", rounded(C.SURFACE2, dpf(24f))) { scan(foundBox) }, lp(0, WRAP, 1f).margins(this, r = 6))
        row.addView(pillButton("Enter IP", rounded(C.SURFACE2, dpf(24f))) { manualAdd() }, lp(0, WRAP, 1f).margins(this, l = 6))
        col.addView(row, lp(MATCH, WRAP).margins(this, t = 12))

        col.addView(text(
            "Don't see your PC? Make sure the BedReach app is running on it (look for the purple icon in the system tray) and that both are on the same Wi-Fi.",
            13f, C.MUTED
        ), lp(MATCH, WRAP).margins(this, t = 24))

        setContentView(ScrollView(this).apply { setBackgroundColor(C.BG); addView(col) })
        scan(foundBox)
    }

    private fun sectionTitle(s: String) = text(s.uppercase(), 12f, C.MUTED, bold = true).apply {
        letterSpacing = 0.1f
        layoutParams = lp(WRAP, WRAP).margins(this@MainActivity, t = 32, b = 10)
    }

    private fun pcCard(name: String, sub: String, paired: Boolean, onClick: () -> Unit, onLong: (() -> Unit)? = null): View {
        val card = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            background = withRipple(rounded(C.SURFACE, dpf(18f), 1, C.STROKE))
            setPadding(dp(16), dp(16), dp(16), dp(16))
            isClickable = true
            setOnClickListener { it.haptic(); onClick() }
            onLong?.let { l -> setOnLongClickListener { it.haptic(); l(); true } }
        }
        card.addView(FrameLayout(this).apply {
            background = if (paired) gradient(dpf(14f)) else rounded(C.SURFACE2, dpf(14f))
            addView(ImageView(context).apply { setImageResource(R.drawable.ic_laptop); setColorFilter(Color.WHITE) },
                FrameLayout.LayoutParams(dp(24), dp(24), Gravity.CENTER))
        }, lp(dp(48), dp(48)))
        val texts = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        texts.addView(text(name, 17f, bold = true))
        texts.addView(text(sub, 13f, C.MUTED))
        card.addView(texts, lp(0, WRAP, 1f).margins(this, l = 14))
        card.addView(text(if (paired) "Open" else "Pair", 14f, if (paired) C.ACCENT2 else C.ACCENT, bold = true))
        card.layoutParams = lp(MATCH, WRAP).margins(this, b = 10)
        return card
    }

    private fun scan(box: LinearLayout) {
        box.removeAllViews()
        val searching = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL
            addView(ProgressBar(context).apply { isIndeterminate = true }, lp(dp(22), dp(22)))
            addView(text("Looking for PCs…", 14f, C.MUTED), lp(WRAP, WRAP).margins(context, l = 10))
        }
        box.addView(searching)
        io.execute {
            val found = runCatching { Discovery.scan() }.getOrDefault(emptyList())
            main.post {
                if (screen != Screen.CONNECT) return@post
                box.removeAllViews()
                val saved = store.all().associateBy { it.serverId }
                // Keep saved PCs' addresses fresh (laptops change IP).
                found.forEach { f -> saved[f.serverId]?.let { s -> if (s.host != f.host || s.port != f.port) store.save(s.copy(host = f.host, port = f.port)) } }
                val fresh = found.filter { it.serverId !in saved }
                if (fresh.isEmpty()) {
                    box.addView(text(if (found.isEmpty()) "No PCs found yet." else "All PCs on this Wi-Fi are already paired.", 14f, C.MUTED))
                } else fresh.forEach { f ->
                    box.addView(pcCard(f.name, "${f.host} · tap to pair", paired = false, onClick = { startPairing(f.host, f.port) }))
                }
            }
        }
    }

    private fun confirmForget(pc: PairedPc) {
        AlertDialog.Builder(this, android.R.style.Theme_Material_Dialog_Alert)
            .setTitle("Forget ${pc.name}?")
            .setMessage("You'll need to pair again to use it. To fully revoke access, also remove this phone from \"Paired phones\" on the PC.")
            .setPositiveButton("Forget") { _, _ -> store.remove(pc.serverId); showConnect() }
            .setNegativeButton("Cancel", null)
            .show()
    }

    private fun manualAdd() {
        val box = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setPadding(dp(24), dp(8), dp(24), 0) }
        val host = EditText(this).apply { hint = "PC address, e.g. 192.168.1.20"; inputType = InputType.TYPE_CLASS_PHONE; setTextColor(C.TEXT); setHintTextColor(C.MUTED) }
        val port = EditText(this).apply { setText("47800"); inputType = InputType.TYPE_CLASS_NUMBER; setTextColor(C.TEXT) }
        box.addView(host); box.addView(port)
        AlertDialog.Builder(this, android.R.style.Theme_Material_Dialog_Alert)
            .setTitle("Add PC by address")
            .setMessage("Click the BedReach tray icon on the PC to see its address.")
            .setView(box)
            .setPositiveButton("Pair") { _, _ ->
                val h = host.text.toString().trim()
                if (h.isNotEmpty()) startPairing(h, port.text.toString().toIntOrNull() ?: 47800)
            }
            .setNegativeButton("Cancel", null)
            .show()
    }

    private fun deviceName(): String =
        Settings.Global.getString(contentResolver, "device_name")?.takeIf { it.isNotBlank() }
            ?: "${Build.MANUFACTURER.replaceFirstChar { it.uppercase() }} ${Build.MODEL}"

    private fun startPairing(host: String, port: Int) {
        val view = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL; gravity = Gravity.CENTER_HORIZONTAL
            setPadding(dp(24), dp(24), dp(24), dp(8))
        }
        val title = text("Connecting…", 18f, bold = true).apply { gravity = Gravity.CENTER }
        val code = text("", 44f, Color.WHITE, bold = true).apply { gravity = Gravity.CENTER; letterSpacing = 0.08f; visibility = View.GONE }
        val hint = text("", 14f, C.MUTED).apply { gravity = Gravity.CENTER }
        val spinner = ProgressBar(this)
        view.addView(title, lp(MATCH, WRAP))
        view.addView(code, lp(MATCH, WRAP).margins(this, t = 16))
        view.addView(hint, lp(MATCH, WRAP).margins(this, t = 12))
        view.addView(spinner, lp(WRAP, WRAP).margins(this, t = 16))

        val pairing = Pairing(host, port, deviceName())
        val dlg = AlertDialog.Builder(this, android.R.style.Theme_Material_Dialog_Alert)
            .setView(view)
            .setNegativeButton("Cancel") { _, _ -> io.execute { pairing.cancel() } }
            .setCancelable(false)
            .show()

        io.execute {
            try {
                val pc = pairing.run { c, pcName ->
                    main.post {
                        title.text = "Check your PC"
                        code.text = "${c.substring(0, 3)} ${c.substring(3)}"
                        code.visibility = View.VISIBLE
                        hint.text = "If $pcName shows this same code, click Allow on the PC."
                    }
                }
                store.save(pc)
                main.post { if (dlg.isShowing) dlg.dismiss(); showRemote(pc) }
            } catch (e: Exception) {
                main.post {
                    if (dlg.isShowing) {
                        dlg.dismiss()
                        AlertDialog.Builder(this, android.R.style.Theme_Material_Dialog_Alert)
                            .setTitle("Couldn't pair")
                            .setMessage(friendlyError(e))
                            .setPositiveButton("OK", null).show()
                    }
                }
            }
        }
    }

    private fun friendlyError(e: Exception): String = when {
        e is com.bedreach.app.net.ProtocolException -> e.message ?: "Something went wrong."
        e is java.net.SocketTimeoutException -> "The PC didn't answer in time. Is BedReach running on it?"
        e is java.net.ConnectException -> "Couldn't reach the PC. Check it's on, BedReach is running, and Windows Firewall allows it."
        else -> e.message ?: e.javaClass.simpleName
    }

    // =====================================================================================
    // Connection
    // =====================================================================================

    private inner class Conn(val id: Int) : Session.Listener {
        override fun onInfo(pcName: String, services: List<Service>) {
            main.post {
                if (id != connId) return@post
                titleText?.text = pcName
                this@MainActivity.services = services
                renderApps()
            }
        }

        override fun onClosed(error: Throwable?) {
            main.post {
                if (id != connId) return@post
                session = null
                setStatus(Status.CONNECTING)
                scheduleRetry(300)
            }
        }
    }

    private fun connectNow() {
        val pc = current ?: return
        if (!wantConnected || connecting || session?.isOpen == true) return
        connecting = true
        val id = ++connId
        setStatus(Status.CONNECTING)
        io.execute {
            try {
                val s = Session.connect(pc.host, pc.port, pc.deviceId, pc.secret, Conn(id))
                main.post {
                    connecting = false
                    if (!wantConnected || id != connId) { s.close(); return@post }
                    session = s
                    retryDelay = 1000
                    setStatus(Status.CONNECTED)
                }
            } catch (e: NotPairedException) {
                main.post { connecting = false; if (id == connId) notPaired(pc) }
            } catch (e: Exception) {
                // The laptop may have a new IP address — look for it by its ID.
                val found: FoundPc? = runCatching { Discovery.scan(1200) }.getOrDefault(emptyList()).firstOrNull { it.serverId == pc.serverId }
                main.post {
                    connecting = false
                    if (id != connId) return@post
                    if (found != null && (found.host != pc.host || found.port != pc.port)) {
                        val upd = pc.copy(host = found.host, port = found.port)
                        store.save(upd)
                        if (current?.serverId == upd.serverId) current = upd
                        scheduleRetry(0)
                    } else {
                        setStatus(Status.OFFLINE)
                        scheduleRetry()
                    }
                }
            }
        }
    }

    private fun scheduleRetry(delay: Long = retryDelay) {
        if (!wantConnected) return
        main.removeCallbacks(retry)
        main.postDelayed(retry, delay)
        retryDelay = min(retryDelay * 2, 8000)
    }

    private fun disconnect() {
        wantConnected = false
        main.removeCallbacks(retry)
        connId++
        connecting = false
        session?.close()
        session = null
    }

    private fun notPaired(pc: PairedPc) {
        store.remove(pc.serverId)
        AlertDialog.Builder(this, android.R.style.Theme_Material_Dialog_Alert)
            .setTitle("Not paired any more")
            .setMessage("${pc.name} doesn't recognise this phone (it may have been removed on the PC). Pair again to keep using it.")
            .setPositiveButton("OK") { _, _ -> showConnect() }
            .setCancelable(false)
            .show()
    }

    private fun send(o: JSONObject) {
        val s = session
        if (s != null && s.isOpen) s.send(o)
        else Toast.makeText(this, "Not connected yet", Toast.LENGTH_SHORT).show()
    }

    private enum class Status { CONNECTING, CONNECTED, OFFLINE }

    private fun setStatus(s: Status) {
        val (color, label) = when (s) {
            Status.CONNECTED -> C.GOOD to "Connected"
            Status.CONNECTING -> C.WARN to "Connecting…"
            Status.OFFLINE -> C.BAD to "Can't reach PC. Is it on?"
        }
        statusDot?.background = oval(color)
        statusText?.text = label
    }

    // =====================================================================================
    // Remote screen
    // =====================================================================================

    private fun showRemote(pc: PairedPc) {
        disconnect()
        screen = Screen.REMOTE
        current = pc
        store.lastUsed = pc.serverId
        services = emptyList()

        val root = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setBackgroundColor(C.BG) }

        // --- top bar
        val top = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL
            setPadding(dp(12), dp(14), dp(12), dp(6))
        }
        top.addView(iconButton(R.drawable.ic_arrow_back, 44, oval(Color.TRANSPARENT), 24, desc = "PCs") { showConnect() })
        val titles = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        titleText = text(pc.name, 19f, bold = true).apply { maxLines = 1 }
        titles.addView(titleText)
        val statusRow = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL }
        statusDot = View(this)
        statusRow.addView(statusDot, lp(dp(8), dp(8)))
        statusText = text("", 13f, C.MUTED)
        statusRow.addView(statusText, lp(WRAP, WRAP).margins(this, l = 6))
        titles.addView(statusRow)
        top.addView(titles, lp(0, WRAP, 1f).margins(this, l = 6))

        val keyboardBar = buildKeyboardBar()
        top.addView(iconButton(R.drawable.ic_keyboard, 44, oval(C.SURFACE2), 22, desc = "Keyboard") { toggleKeyboard(keyboardBar) })
        top.addView(iconButton(R.drawable.ic_power, 44, oval(C.SURFACE2), 22, desc = "Power") { powerMenu() }, lp(dp(44), dp(44)).margins(this, l = 8))
        root.addView(top)
        root.addView(keyboardBar)

        // --- tabs
        val content = FrameLayout(this)
        root.addView(content, lp(MATCH, 0, 1f))
        val tabs = listOf(
            Triple("Remote", R.drawable.ic_remote, buildRemoteTab()),
            Triple("Touchpad", R.drawable.ic_mouse, buildTouchpadTab()),
            Triple("Apps", R.drawable.ic_apps, buildAppsTab()),
        )
        tabs.forEach { content.addView(it.third, FrameLayout.LayoutParams(MATCH, MATCH)) }

        val nav = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            background = rounded(C.SURFACE, dpf(26f), 1, C.STROKE)
            setPadding(dp(6), dp(6), dp(6), dp(6))
        }
        val pages = tabs.map { it.third }
        tabs.forEachIndexed { i, (label, icon, _) ->
            nav.addView(LinearLayout(this).apply {
                orientation = LinearLayout.VERTICAL; gravity = Gravity.CENTER
                setPadding(0, dp(8), 0, dp(8))
                isClickable = true
                addView(ImageView(context).apply { setImageResource(icon) }, lp(dp(22), dp(22)))
                addView(text(label, 12f, bold = true), lp(WRAP, WRAP).margins(context, t = 2))
                setOnClickListener { it.haptic(); selectTab(i, pages, nav) }
            }, lp(0, WRAP, 1f))
        }
        root.addView(nav, lp(MATCH, WRAP).margins(this, 16, 6, 16, 16))
        selectTab(0, pages, nav)

        setContentView(root)
        setStatus(Status.CONNECTING)
        wantConnected = true
        retryDelay = 1000
        connectNow()
    }

    private fun selectTab(i: Int, pages: List<View>, nav: LinearLayout) {
        pages.forEachIndexed { j, v -> v.visibility = if (i == j) View.VISIBLE else View.GONE }
        for (j in 0 until nav.childCount) {
            val item = nav.getChildAt(j) as LinearLayout
            val on = i == j
            item.background = if (on) rounded(C.SURFACE2, dpf(20f)) else null
            (item.getChildAt(0) as ImageView).setColorFilter(if (on) C.ACCENT2 else C.MUTED)
            (item.getChildAt(1) as TextView).setTextColor(if (on) C.TEXT else C.MUTED)
        }
    }

    // ---------------- Remote tab ----------------

    private fun buildRemoteTab(): View {
        val col = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL; gravity = Gravity.CENTER_HORIZONTAL
            setPadding(dp(20), dp(8), dp(20), dp(8))
        }

        // quick keys
        val chips = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER }
        listOf("Esc" to "esc", "Fullscreen" to "f", "Space" to "space").forEach { (label, key) ->
            chips.addView(chip(label) { send(Cmd.key(key)) }, lp(WRAP, WRAP).margins(this, 4, 0, 4, 0))
        }
        col.addView(chips)

        col.addView(View(this), lp(1, 0, 1f))

        // d-pad
        val pad = FrameLayout(this).apply { background = oval(C.SURFACE, 1) }
        fun arrow(icon: Int, key: String, gravity: Int) = iconButton(icon, 76, oval(Color.TRANSPARENT), 34, C.TEXT, key, repeat = true) { send(Cmd.key(key)) }
            .also { pad.addView(it, FrameLayout.LayoutParams(dp(76), dp(76), gravity)) }
        arrow(R.drawable.ic_chevron_up, "up", Gravity.TOP or Gravity.CENTER_HORIZONTAL)
        arrow(R.drawable.ic_chevron_down, "down", Gravity.BOTTOM or Gravity.CENTER_HORIZONTAL)
        arrow(R.drawable.ic_chevron_left, "left", Gravity.START or Gravity.CENTER_VERTICAL)
        arrow(R.drawable.ic_chevron_right, "right", Gravity.END or Gravity.CENTER_VERTICAL)
        val ok = TextView(this).apply {
            text = "OK"; textSize = 20f; setTextColor(Color.WHITE); gravity = Gravity.CENTER
            typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
            background = withRipple(gradient(0f, oval = true))
            isClickable = true
            setOnClickListener { it.haptic(); send(Cmd.key("enter")) }
        }
        pad.addView(ok, FrameLayout.LayoutParams(dp(96), dp(96), Gravity.CENTER))
        col.addView(pad, lp(dp(256), dp(256)))

        col.addView(View(this), lp(1, 0, 1f))

        // media
        val media = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER }
        media.addView(iconButton(R.drawable.ic_prev, 60, desc = "Previous") { send(Cmd.key("prev")) })
        media.addView(iconButton(R.drawable.ic_play_pause, 84, gradient(0f, oval = true), 40, Color.WHITE, "Play/pause") { send(Cmd.key("playpause")) },
            lp(dp(84), dp(84)).margins(this, 28, 0, 28, 0))
        media.addView(iconButton(R.drawable.ic_next, 60, desc = "Next") { send(Cmd.key("next")) })
        col.addView(media, lp(MATCH, WRAP))

        col.addView(View(this), lp(1, 0, 1f))

        // volume pill
        val vol = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL
            background = rounded(C.SURFACE, dpf(32f), 1, C.STROKE)
            setPadding(dp(6), dp(6), dp(6), dp(6))
        }
        vol.addView(iconButton(R.drawable.ic_vol_down, 56, oval(Color.TRANSPARENT), 26, desc = "Volume down", repeat = true) { send(Cmd.key("voldown")) })
        vol.addView(iconButton(R.drawable.ic_vol_mute, 56, oval(C.SURFACE2), 24, desc = "Mute") { send(Cmd.key("mute")) },
            lp(dp(56), dp(56)).margins(this, 22, 0, 22, 0))
        vol.addView(iconButton(R.drawable.ic_vol_up, 56, oval(Color.TRANSPARENT), 26, desc = "Volume up", repeat = true) { send(Cmd.key("volup")) })
        col.addView(vol, lp(WRAP, WRAP)) // pill hugs its three buttons and sits centred
        col.addView(text("Tip: your phone's volume buttons change the PC's volume too.", 12f, C.MUTED).apply { gravity = Gravity.CENTER },
            lp(MATCH, WRAP).margins(this, t = 10))

        return col
    }

    private fun chip(label: String, onClick: () -> Unit) = TextView(this).apply {
        text = label; textSize = 13f; setTextColor(C.TEXT); gravity = Gravity.CENTER
        typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
        background = withRipple(rounded(C.SURFACE2, dpf(18f)))
        setPadding(dp(16), dp(9), dp(16), dp(9))
        isClickable = true
        setOnClickListener { it.haptic(); onClick() }
    }

    // ---------------- Touchpad tab ----------------

    @SuppressLint("ClickableViewAccessibility")
    private fun buildTouchpadTab(): View {
        val col = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setPadding(dp(16), dp(8), dp(16), dp(8)) }
        val pad = FrameLayout(this).apply { background = rounded(C.SURFACE, dpf(28f), 1, C.STROKE) }
        pad.addView(text("Drag to move  ·  Tap to click\nTwo fingers: drag to scroll, tap to right-click", 13f, C.MUTED).apply {
            gravity = Gravity.CENTER; alpha = 0.8f
        }, FrameLayout.LayoutParams(WRAP, WRAP, Gravity.CENTER))

        val density = resources.displayMetrics.density
        var lastX = 0f; var lastY = 0f
        var accX = 0f; var accY = 0f; var scrollAcc = 0f
        var maxPointers = 0; var moved = false; var downAt = 0L; var travel = 0f
        var lastSend = 0L

        pad.setOnTouchListener { v, e ->
            when (e.actionMasked) {
                MotionEvent.ACTION_DOWN -> {
                    lastX = e.x; lastY = e.y; maxPointers = 1; moved = false; travel = 0f
                    downAt = e.eventTime; accX = 0f; accY = 0f; scrollAcc = 0f
                }
                MotionEvent.ACTION_POINTER_DOWN -> {
                    maxPointers = maxOf(maxPointers, e.pointerCount)
                    lastX = e.getX(0); lastY = e.getY(0)
                }
                MotionEvent.ACTION_MOVE -> {
                    val x = e.getX(0); val y = e.getY(0)
                    val dx = (x - lastX) / density; val dy = (y - lastY) / density
                    lastX = x; lastY = y
                    travel += abs(dx) + abs(dy)
                    if (travel > 6) moved = true
                    if (e.pointerCount >= 2) {
                        scrollAcc += dy
                        while (abs(scrollAcc) >= 14) {
                            send(Cmd.scroll((120 * sign(scrollAcc)).toInt()))
                            scrollAcc -= 14 * sign(scrollAcc)
                        }
                    } else if (maxPointers == 1) {
                        // simple pointer acceleration
                        val speed = kotlin.math.sqrt(dx * dx + dy * dy)
                        val gain = 1.6f + min(speed / 6f, 2.4f)
                        accX += dx * gain; accY += dy * gain
                        val now = e.eventTime
                        if (now - lastSend >= 12 && (abs(accX) >= 1 || abs(accY) >= 1)) {
                            val ix = accX.toInt(); val iy = accY.toInt()
                            send(Cmd.move(ix, iy)); accX -= ix; accY -= iy; lastSend = now
                        }
                    }
                }
                MotionEvent.ACTION_UP -> {
                    if (!moved && e.eventTime - downAt < 300) {
                        v.haptic()
                        send(Cmd.click(if (maxPointers >= 2) "right" else "left"))
                    }
                }
            }
            true
        }
        col.addView(pad, lp(MATCH, 0, 1f))

        val btns = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
        btns.addView(pillButton("Left click", rounded(C.SURFACE2, dpf(22f))) { send(Cmd.click("left")) }, lp(0, WRAP, 1f).margins(this, r = 6))
        btns.addView(pillButton("Right click", rounded(C.SURFACE2, dpf(22f))) { send(Cmd.click("right")) }, lp(0, WRAP, 1f).margins(this, l = 6))
        col.addView(btns, lp(MATCH, WRAP).margins(this, t = 12))
        return col
    }

    // ---------------- Apps tab ----------------

    private fun buildAppsTab(): View {
        val col = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setPadding(dp(16), dp(4), dp(16), dp(16)) }
        col.addView(sectionTitle("Open on your PC").apply { (layoutParams as LinearLayout.LayoutParams).topMargin = dp(8) })
        appsGrid = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        col.addView(appsGrid)
        renderApps()

        col.addView(sectionTitle("Windows"))
        val row1 = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
        row1.addView(pillButton("Switch app", rounded(C.SURFACE2, dpf(18f))) { send(Cmd.key("alttab")) }, lp(0, WRAP, 1f).margins(this, r = 5))
        row1.addView(pillButton("Browser back", rounded(C.SURFACE2, dpf(18f))) { send(Cmd.key("browser_back")) }, lp(0, WRAP, 1f).margins(this, l = 5))
        col.addView(row1)
        val row2 = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
        row2.addView(pillButton("Show desktop", rounded(C.SURFACE2, dpf(18f))) { send(Cmd.key("win_d")) }, lp(0, WRAP, 1f).margins(this, r = 5))
        row2.addView(pillButton("Close window", rounded(C.SURFACE2, dpf(18f))) {
            AlertDialog.Builder(this, android.R.style.Theme_Material_Dialog_Alert)
                .setTitle("Close the current window on the PC?")
                .setPositiveButton("Close") { _, _ -> send(Cmd.key("alt_f4")) }
                .setNegativeButton("Cancel", null).show()
        }, lp(0, WRAP, 1f).margins(this, l = 5))
        col.addView(row2, lp(MATCH, WRAP).margins(this, t = 10))
        col.addView(text("You can change the app shortcuts from the BedReach tray icon on the PC.", 12f, C.MUTED),
            lp(MATCH, WRAP).margins(this, t = 20))

        return ScrollView(this).apply { addView(col) }
    }

    private fun renderApps() {
        val grid = appsGrid ?: return
        grid.removeAllViews()
        if (services.isEmpty()) {
            grid.addView(text("Connect to your PC to see its apps.", 14f, C.MUTED))
            return
        }
        services.chunked(2).forEach { pair ->
            val row = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
            pair.forEachIndexed { i, s ->
                val color = runCatching { Color.parseColor(s.color) }.getOrDefault(C.SURFACE2)
                val tile = FrameLayout(this).apply {
                    background = withRipple(android.graphics.drawable.GradientDrawable(
                        android.graphics.drawable.GradientDrawable.Orientation.TL_BR,
                        intArrayOf(color, darken(color))
                    ).apply { cornerRadius = dpf(20f) })
                    isClickable = true
                    contentDescription = "${s.name}, opens the ${if (s.installedApp) "app" else "website"}"
                    setOnClickListener {
                        it.haptic(); send(Cmd.launch(s.id))
                        Toast.makeText(context, "Opening ${s.name}${if (s.installedApp) " app" else ""}…", Toast.LENGTH_SHORT).show()
                    }
                    addView(TextView(context).apply {
                        text = s.name; textSize = 17f; setTextColor(Color.WHITE)
                        typeface = Typeface.create("sans-serif-medium", Typeface.BOLD)
                    }, FrameLayout.LayoutParams(WRAP, WRAP, Gravity.START or Gravity.BOTTOM).apply { setMargins(dp(16), 0, dp(12), dp(14)) })
                    // Badge: does this open the installed app on the PC, or the website?
                    addView(TextView(context).apply {
                        text = if (s.installedApp) "APP" else "WEB"
                        textSize = 10f; letterSpacing = 0.08f
                        setTextColor(Color.WHITE)
                        typeface = Typeface.create("sans-serif-medium", Typeface.BOLD)
                        background = rounded(if (s.installedApp) 0x33FFFFFF else 0x1AFFFFFF, dpf(8f))
                        alpha = if (s.installedApp) 1f else 0.75f
                        setPadding(dp(7), dp(3), dp(7), dp(3))
                    }, FrameLayout.LayoutParams(WRAP, WRAP, Gravity.END or Gravity.TOP).apply { setMargins(0, dp(10), dp(10), 0) })
                }
                row.addView(tile, lp(0, dp(96), 1f).margins(this, l = if (i == 1) 5 else 0, r = if (i == 0) 5 else 0))
            }
            if (pair.size == 1) row.addView(View(this), lp(0, dp(96), 1f).margins(this, l = 5))
            grid.addView(row, lp(MATCH, WRAP).margins(this, b = 10))
        }
    }

    private fun darken(c: Int): Int {
        val f = 0.55f
        return Color.rgb((Color.red(c) * f).toInt(), (Color.green(c) * f).toInt(), (Color.blue(c) * f).toInt())
    }

    // ---------------- Keyboard + power ----------------

    private fun buildKeyboardBar(): LinearLayout {
        val bar = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL
            setPadding(dp(16), dp(4), dp(16), dp(8))
            visibility = View.GONE
        }
        val input = EditText(this).apply {
            hint = "Type to send to PC"; setTextColor(C.TEXT); setHintTextColor(C.MUTED)
            background = rounded(C.SURFACE2, dpf(22f))
            setPadding(dp(16), dp(12), dp(16), dp(12))
            isSingleLine = true
            imeOptions = EditorInfo.IME_ACTION_GO
            setOnEditorActionListener { v, _, _ ->
                val s = v.text.toString()
                if (s.isNotEmpty()) send(Cmd.text(s))
                send(Cmd.key("enter"))
                v.text = ""
                true
            }
        }
        bar.addView(input, lp(0, WRAP, 1f))
        bar.addView(iconButton(R.drawable.ic_send, 44, gradient(0f, oval = true), 20, Color.WHITE, "Send") {
            val s = input.text.toString(); if (s.isNotEmpty()) { send(Cmd.text(s)); input.setText("") }
        }, lp(dp(44), dp(44)).margins(this, l = 8))
        bar.addView(iconButton(R.drawable.ic_backspace, 44, oval(C.SURFACE2), 20, desc = "Backspace", repeat = true) { send(Cmd.key("backspace")) },
            lp(dp(44), dp(44)).margins(this, l = 8))
        return bar
    }

    private fun toggleKeyboard(bar: LinearLayout) {
        val imm = getSystemService(Context.INPUT_METHOD_SERVICE) as InputMethodManager
        val input = bar.getChildAt(0) as EditText
        if (bar.visibility == View.VISIBLE) {
            imm.hideSoftInputFromWindow(input.windowToken, 0)
            bar.visibility = View.GONE
        } else {
            bar.visibility = View.VISIBLE
            input.requestFocus()
            input.post { imm.showSoftInput(input, InputMethodManager.SHOW_IMPLICIT) }
        }
    }

    private fun powerMenu() {
        val items = arrayOf("Sleep", "Lock", "Shut down (in 60 s)", "Cancel shutdown")
        AlertDialog.Builder(this, android.R.style.Theme_Material_Dialog_Alert)
            .setTitle("Power")
            .setItems(items) { _, which ->
                when (which) {
                    0 -> send(Cmd.power("sleep"))
                    1 -> send(Cmd.power("lock"))
                    2 -> AlertDialog.Builder(this, android.R.style.Theme_Material_Dialog_Alert)
                        .setTitle("Shut down the PC?")
                        .setMessage("It will shut down in 60 seconds. You can cancel from this menu until then.")
                        .setPositiveButton("Shut down") { _, _ -> send(Cmd.power("shutdown")) }
                        .setNegativeButton("Cancel", null).show()
                    3 -> send(Cmd.power("cancel"))
                }
            }
            .show()
    }
}
