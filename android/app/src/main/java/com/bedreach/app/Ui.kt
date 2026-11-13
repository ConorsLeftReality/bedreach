package com.bedreach.app

import android.annotation.SuppressLint
import android.content.Context
import android.content.res.ColorStateList
import android.graphics.Color
import android.graphics.Typeface
import android.graphics.drawable.Drawable
import android.graphics.drawable.GradientDrawable
import android.graphics.drawable.RippleDrawable
import android.os.Handler
import android.os.Looper
import android.view.Gravity
import android.view.HapticFeedbackConstants
import android.view.MotionEvent
import android.view.View
import android.view.ViewGroup
import android.widget.FrameLayout
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.TextView

/** Colours and small view builders. The UI is built in code so it stays in one place. */
object C {
    const val BG = 0xFF0E0E18.toInt()
    const val SURFACE = 0xFF1A1A2B.toInt()
    const val SURFACE2 = 0xFF25253B.toInt()
    const val STROKE = 0xFF2F2F48.toInt()
    const val TEXT = 0xFFEDEDF7.toInt()
    const val MUTED = 0xFF9494B0.toInt()
    const val ACCENT = 0xFF8B5CF6.toInt()
    const val ACCENT2 = 0xFFEC4899.toInt()
    const val GOOD = 0xFF34D399.toInt()
    const val WARN = 0xFFFBBF24.toInt()
    const val BAD = 0xFFF87171.toInt()
}

fun Context.dp(v: Int) = (v * resources.displayMetrics.density).toInt()
fun Context.dpf(v: Float) = v * resources.displayMetrics.density

fun rounded(color: Int, radius: Float, stroke: Int = 0, strokeColor: Int = C.STROKE) = GradientDrawable().apply {
    setColor(color); cornerRadius = radius
    if (stroke > 0) setStroke(stroke, strokeColor)
}

fun gradient(radius: Float, oval: Boolean = false) = GradientDrawable(
    GradientDrawable.Orientation.TL_BR, intArrayOf(C.ACCENT, C.ACCENT2)
).apply { if (oval) shape = GradientDrawable.OVAL else cornerRadius = radius }

fun oval(color: Int, stroke: Int = 0) = GradientDrawable().apply {
    shape = GradientDrawable.OVAL; setColor(color)
    if (stroke > 0) setStroke(stroke, C.STROKE)
}

fun withRipple(bg: Drawable, mask: Drawable? = null) =
    RippleDrawable(ColorStateList.valueOf(0x33FFFFFF), bg, mask ?: bg)

fun View.haptic() = performHapticFeedback(HapticFeedbackConstants.VIRTUAL_KEY)

fun Context.text(s: String, sizeSp: Float = 15f, color: Int = C.TEXT, bold: Boolean = false) = TextView(this).apply {
    text = s; textSize = sizeSp; setTextColor(color)
    if (bold) typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
}

fun lp(w: Int, h: Int, weight: Float = 0f) = LinearLayout.LayoutParams(w, h, weight)
const val MATCH = ViewGroup.LayoutParams.MATCH_PARENT
const val WRAP = ViewGroup.LayoutParams.WRAP_CONTENT

fun LinearLayout.LayoutParams.margins(ctx: Context, l: Int = 0, t: Int = 0, r: Int = 0, b: Int = 0) = apply {
    setMargins(ctx.dp(l), ctx.dp(t), ctx.dp(r), ctx.dp(b))
}

/**
 * A round (or rounded) icon button with ripple + haptics. If [repeat] is true, holding it
 * repeats the action (for volume and seeking).
 */
@SuppressLint("ClickableViewAccessibility")
fun Context.iconButton(
    icon: Int,
    sizeDp: Int,
    bg: Drawable = oval(C.SURFACE2),
    iconSizeDp: Int = sizeDp / 2,
    tint: Int = C.TEXT,
    desc: String = "",
    repeat: Boolean = false,
    onClick: () -> Unit,
): FrameLayout {
    val ctx = this
    return FrameLayout(this).apply {
        layoutParams = ViewGroup.LayoutParams(dp(sizeDp), dp(sizeDp))
        background = withRipple(bg)
        contentDescription = desc
        isClickable = true; isFocusable = true
        addView(ImageView(ctx).apply {
            setImageResource(icon)
            imageTintList = ColorStateList.valueOf(tint)
        }, FrameLayout.LayoutParams(dp(iconSizeDp), dp(iconSizeDp), Gravity.CENTER))
        if (!repeat) {
            setOnClickListener { haptic(); onClick() }
        } else {
            val h = Handler(Looper.getMainLooper())
            var r: Runnable? = null
            setOnTouchListener { v, e ->
                when (e.actionMasked) {
                    MotionEvent.ACTION_DOWN -> {
                        v.isPressed = true; v.haptic(); onClick()
                        r = object : Runnable {
                            override fun run() { onClick(); v.haptic(); h.postDelayed(this, 110) }
                        }.also { h.postDelayed(it, 420) }
                    }
                    MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {
                        v.isPressed = false; r?.let { h.removeCallbacks(it) }; r = null
                    }
                }
                true
            }
        }
    }
}

/** A pill-shaped labelled button. */
fun Context.pillButton(label: String, bg: Drawable, textColor: Int = Color.WHITE, onClick: () -> Unit) = TextView(this).apply {
    text = label; textSize = 15f; setTextColor(textColor); gravity = Gravity.CENTER
    typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
    background = withRipple(bg)
    setPadding(dp(20), dp(14), dp(20), dp(14))
    isClickable = true
    setOnClickListener { haptic(); onClick() }
}
