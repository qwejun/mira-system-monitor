package com.example.mirasystemmonitor

import android.content.Context
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.Path
import android.graphics.RectF
import android.graphics.Rect
import android.graphics.LinearGradient
import android.graphics.Shader
import android.graphics.Typeface
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.util.AttributeSet
import android.view.View
import kotlin.math.cos
import kotlin.math.min
import kotlin.math.sin
import kotlin.math.sqrt

class MonitorDialView @JvmOverloads constructor(
    context: Context,
    attrs: AttributeSet? = null
) : View(context, attrs) {
    private val design = 360f
    private val paint = Paint(Paint.ANTI_ALIAS_FLAG)
    private val textPaint = Paint(Paint.ANTI_ALIAS_FLAG)
    private val backgroundPaint = Paint(Paint.ANTI_ALIAS_FLAG or Paint.FILTER_BITMAP_FLAG)
    private val background: Bitmap
    private var snapshot = MonitorSnapshot.Disconnected
    private var displayedCpu = 0f
    private var displayedMemory = 0f
    private var elapsedMs = 0L
    private var lastFrameMs = 0L
    private val downloadWave = Path()
    private val uploadWave = Path()
    private val downloadHistory = FloatArray(WAVE_SAMPLES)
    private val uploadHistory = FloatArray(WAVE_SAMPLES)
    private val ticker = object : Runnable {
        override fun run() {
            if (!isAttachedToWindow || visibility != VISIBLE) return
            val now = android.os.SystemClock.uptimeMillis()
            elapsedMs += if (lastFrameMs == 0L) 33L else (now - lastFrameMs).coerceIn(0L, 100L)
            lastFrameMs = now
            displayedCpu = approach(displayedCpu, snapshot.cpuPercent, 0.12f)
            displayedMemory = approach(displayedMemory, snapshot.memoryPercent, 0.12f)
            invalidate()
            postDelayed(this, 33L)
        }
    }

    init {
        // Software rendering keeps the neon halo consistent on Android 5.1.
        setLayerType(View.LAYER_TYPE_SOFTWARE, null)
        background = BitmapFactory.decodeResource(resources, R.drawable.monitor_background)
    }

    fun setSnapshot(value: MonitorSnapshot) {
        if (!value.connected) {
            snapshot = MonitorSnapshot.Disconnected
            displayedCpu = 0f
            displayedMemory = 0f
            downloadHistory.fill(0f)
            uploadHistory.fill(0f)
            invalidate()
            return
        }
        val calculatedMemoryPercent = if (value.memoryTotalBytes > 0L) {
            value.memoryUsedBytes * 100f / value.memoryTotalBytes
        } else {
            value.memoryPercent
        }
        snapshot = value.copy(memoryPercent = calculatedMemoryPercent.coerceIn(0f, 100f))
        appendNetworkSample(downloadHistory, value.downloadBytesPerSecond)
        appendNetworkSample(uploadHistory, value.uploadBytesPerSecond)
        invalidate()
    }

    override fun onAttachedToWindow() {
        super.onAttachedToWindow()
        lastFrameMs = 0L
        post(ticker)
    }

    override fun onDetachedFromWindow() {
        removeCallbacks(ticker)
        super.onDetachedFromWindow()
    }

    override fun onDraw(canvas: Canvas) {
        super.onDraw(canvas)
        val scale = min(width / design, height / design)
        canvas.save()
        canvas.translate((width - design * scale) / 2f, (height - design * scale) / 2f)
        canvas.scale(scale, scale)
        drawDashboard(canvas)
        canvas.restore()
    }

    private fun drawDashboard(canvas: Canvas) {
        canvas.drawColor(Color.rgb(15, 15, 15))
        // The supplied plate already contains the circular bezel and divider lines.
        canvas.drawBitmap(background, Rect(0, 6, background.width, 489), RectF(0f, 0f, design, design), backgroundPaint)
        drawNetworkPanel(canvas)
        drawGauge(canvas, 87f, 182f, 68f, displayedCpu, Color.rgb(101, 240, 112), "CPU", if (snapshot.connected) "${percent(displayedCpu)}%" else "--")
        drawGauge(canvas, 273f, 182f, 68f, displayedMemory, Color.rgb(255, 170, 57), "RAM", if (snapshot.connected) "${percent(displayedMemory)}%" else "--")
        drawConnectionMark(canvas)
        drawBottomPanel(canvas)
    }

    private fun drawNetworkPanel(canvas: Canvas) {
        val cyan = Color.rgb(56, 207, 255)
        val red = Color.rgb(255, 92, 100)
        // These accents follow the same circular boundary as the supplied plate.
        // Using two small ellipses made them look like detached eyebrows.
        val rim = RectF(4f, 4f, 356f, 356f)
        glowArc(canvas, rim, 205f, 61f, cyan, 2.5f)
        glowArc(canvas, rim, 274f, 61f, red, 2.5f)
        drawSpeed(canvas, 101f, 69f, "↓", if (snapshot.connected) speed(snapshot.downloadBytesPerSecond) else "--", snapshot.downloadBytesPerSecond, cyan)
        drawSpeed(canvas, 259f, 69f, "↑", if (snapshot.connected) speed(snapshot.uploadBytesPerSecond) else "--", snapshot.uploadBytesPerSecond, red)
        drawWave(canvas, downloadWave, downloadHistory, 37f, 82f, 122f, cyan, snapshot.downloadBytesPerSecond)
        drawWave(canvas, uploadWave, uploadHistory, 201f, 82f, 122f, red, snapshot.uploadBytesPerSecond)
    }

    private fun drawGauge(canvas: Canvas, cx: Float, cy: Float, radius: Float, value: Float, color: Int, label: String, valueText: String) {
        val start = 140f
        val sweep = 260f
        stroke(Color.argb(95, Color.red(color), Color.green(color), Color.blue(color)), 3.8f)
        canvas.drawArc(RectF(cx - radius, cy - radius, cx + radius, cy + radius), start, sweep, false, paint)
        val progressSweep = sweep * (value / 100f).coerceIn(0f, 1f)
        glowArc(canvas, RectF(cx - radius, cy - radius, cx + radius, cy + radius), start, progressSweep, color, 4.2f)
        drawGaugeEndpoint(canvas, cx, cy, radius, start + progressSweep, color)
        drawTicks(canvas, cx, cy, radius, value, color)
        drawChipIcon(canvas, cx, cy - 31f, color, label == "CPU")
        text(canvas, label, cx, cy + 2f, 14f, Color.WHITE, Paint.Align.CENTER)
        glowingText(canvas, valueText, cx, cy + 20f, 16f, Color.WHITE, Paint.Align.CENTER)
        if (label == "RAM") {
            val memoryText = if (snapshot.connected && snapshot.memoryTotalBytes > 0L) {
                formatBytes(snapshot.memoryUsedBytes) + " / " + formatBytes(snapshot.memoryTotalBytes)
            } else {
                "-- / --"
            }
            text(canvas, memoryText, cx, cy + 31f, 6.5f, color, Paint.Align.CENTER)
        }
    }

    private fun drawTicks(canvas: Canvas, cx: Float, cy: Float, radius: Float, value: Float, color: Int) {
        val total = 34
        for (i in 0 until total) {
            val angle = Math.toRadians((140f + i * (260f / (total - 1))).toDouble())
            val r1 = radius - 10f
            val r2 = radius - if (i % 5 == 0) 2f else 6f
            val x1 = cx + cos(angle).toFloat() * r1
            val y1 = cy + sin(angle).toFloat() * r1
            val x2 = cx + cos(angle).toFloat() * r2
            val y2 = cy + sin(angle).toFloat() * r2
            if (i <= (total - 1) * value / 100f) glowLine(canvas, color, 2.2f, x1, y1, x2, y2, 3f)
            else line(canvas, Color.rgb(26, 56, 51), 2f, x1, y1, x2, y2)
        }
    }

    private fun drawConnectionMark(canvas: Canvas) {
        val color = if (snapshot.connected) Color.rgb(173, 202, 224) else Color.rgb(90, 95, 106)
        stroke(color, 1.1f)
        canvas.drawRoundRect(RectF(169f, 122f, 181f, 130f), 0.8f, 0.8f, paint)
        line(canvas, color, 1f, 175f, 130f, 175f, 132f)
        line(canvas, color, 1f, 172f, 132f, 178f, 132f)
        canvas.drawRoundRect(RectF(184f, 123f, 190f, 132f), 0.7f, 0.7f, paint)
        fillCircle(canvas, color, 187f, 129.8f, 0.65f, 0f)
        text(canvas, "SYSTEM", 180f, 244f, 6.5f, color, Paint.Align.CENTER)
        text(canvas, "STATUS", 180f, 252f, 6.5f, color, Paint.Align.CENTER)
    }

    private fun drawBottomPanel(canvas: Canvas) {
        val cyan = Color.rgb(75, 217, 255)
        val orange = Color.rgb(255, 137, 68)
        val coolPath = Path().apply {
            moveTo(29f, 276f)
            cubicTo(52f, 302f, 77f, 329f, 99f, 329f)
            cubicTo(116f, 329f, 122f, 304f, 141f, 304f)
            cubicTo(153f, 304f, 168f, 304f, 180f, 304f)
        }
        val warmPath = Path().apply {
            moveTo(180f, 304f)
            cubicTo(192f, 304f, 207f, 304f, 219f, 304f)
            cubicTo(238f, 304f, 244f, 329f, 261f, 329f)
            cubicTo(283f, 329f, 308f, 302f, 331f, 276f)
        }
        drawNeonPath(canvas, coolPath, cyan)
        drawNeonPath(canvas, warmPath, orange)
        val orangeIcon = Color.rgb(255, 170, 57)
        drawThermometerIcon(canvas, 72f, 287f, cyan, snapshot.temperatureC)
        val fanRpmForIcon = snapshot.chassisRpm ?: snapshot.psuRpm ?: snapshot.gpuRpm
        val fanPercentForIcon = if (fanRpmForIcon == null) snapshot.gpuPercent else null
        drawFanIcon(canvas, 208f, 286f, orangeIcon, fanRpmForIcon, fanPercentForIcon)
        val temperature = if (snapshot.connected) {
            snapshot.temperatureC?.let { "%.0f°C".format(it) } ?: "--"
        } else {
            "--"
        }
        val fans = listOf(
            "机箱" to snapshot.chassisRpm,
            "电源" to snapshot.psuRpm,
            "GPU" to snapshot.gpuRpm
        )
        glowingText(canvas, temperature, 107f, 291f, 8.5f, Color.rgb(188, 211, 225), Paint.Align.CENTER)
        fans.forEachIndexed { index, (label, rpm) ->
            val y = 284f + index * 13f
            text(canvas, label, 239f, y, 5.2f, Color.rgb(190, 166, 122), Paint.Align.LEFT)
            glowingText(canvas, if (snapshot.connected) rpm?.let { "$it" } ?: "--" else "--", 281f, y, 6.2f, Color.rgb(236, 198, 132), Paint.Align.RIGHT)
        }
        text(canvas, "MONITORING", 180f, 319f, 7.5f, Color.rgb(143, 158, 177), Paint.Align.CENTER)
        val clock = java.text.SimpleDateFormat("HH:mm", java.util.Locale.US).format(java.util.Date())
        glowingText(canvas, clock, 180f, 343f, 20f, Color.WHITE, Paint.Align.CENTER)
    }

    private fun drawNeonPath(canvas: Canvas, path: Path, color: Int) {
        // Layered strokes create the soft tube glow and a crisp luminous core.
        glowPath(canvas, path, color, 2.8f, 8f)
        glowPath(canvas, path, color, 1.9f, 4f)
        glowPath(canvas, path, color, 0.9f, 1.5f)
    }

    private fun drawThermometerIcon(canvas: Canvas, cx: Float, cy: Float, color: Int, temperatureC: Float?) {
        val temperature = temperatureC?.coerceIn(0f, 100f)
        val pulse = if (temperature != null) {
            0.82f + 0.18f * ((sin(elapsedMs / 520f) + 1f) / 2f)
        } else 0f
        if (temperature != null) {
            val fillHeight = 7f + temperature / 100f * 10f
            fillCircle(canvas, color, cx, cy + 5f, 3.2f, 2.5f * pulse)
            stroke(color, 2.1f)
            paint.setShadowLayer(3f * pulse, 0f, 0f, color)
            line(canvas, color, 2.1f, cx, cy + 4f, cx, cy - 9f + fillHeight)
            paint.clearShadowLayer()
        }
        stroke(color, 1.3f)
        paint.setShadowLayer(4f, 0f, 0f, color)
        canvas.drawRoundRect(RectF(cx - 2.2f, cy - 9f, cx + 2.2f, cy + 3f), 2.2f, 2.2f, paint)
        canvas.drawCircle(cx, cy + 5f, 4.3f, paint)
        paint.clearShadowLayer()
        line(canvas, color, 1.4f, cx, cy - 5.5f, cx, cy + 5f)
        line(canvas, color, 0.9f, cx + 4.5f, cy - 5f, cx + 7f, cy - 5f)
        line(canvas, color, 0.9f, cx + 4.5f, cy - 1f, cx + 7f, cy - 1f)
    }

    private fun drawFanIcon(canvas: Canvas, cx: Float, cy: Float, color: Int, fanRpm: Int?, fanPercent: Float?) {
        stroke(color, 1.2f)
        paint.setShadowLayer(4f, 0f, 0f, color)
        canvas.drawCircle(cx, cy, 8.5f, paint)
        canvas.drawCircle(cx, cy, 1.7f, paint)
        paint.clearShadowLayer()
        val visibleRevolutionsPerSecond = when {
            fanRpm != null && fanRpm > 0 -> fanRpm.coerceIn(300, 2400) / 1200f
            fanPercent != null && fanPercent > 0f -> (fanPercent.coerceIn(1f, 100f) / 100f) * 1.8f
            else -> 0f
        }
        val rotation = (elapsedMs / 1000f * visibleRevolutionsPerSecond * 360f) % 360f
        canvas.save()
        canvas.rotate(rotation, cx, cy)
        for (i in 0 until 4) {
            val angle = Math.toRadians((i * 90f - 20f).toDouble())
            val tipX = cx + cos(angle).toFloat() * 6.8f
            val tipY = cy + sin(angle).toFloat() * 6.8f
            val sideAngle = angle + Math.toRadians(62.0)
            val sideX = cx + cos(sideAngle).toFloat() * 3.2f
            val sideY = cy + sin(sideAngle).toFloat() * 3.2f
            val path = Path().apply {
                moveTo(cx, cy)
                quadTo(sideX, sideY, tipX, tipY)
            }
            glowPath(canvas, path, color, 1.2f, 3f)
        }
        canvas.restore()
    }

    private fun drawGaugeEndpoint(canvas: Canvas, cx: Float, cy: Float, radius: Float, angleDegrees: Float, color: Int) {
        val angle = Math.toRadians(angleDegrees.toDouble())
        val x = cx + cos(angle).toFloat() * radius
        val y = cy + sin(angle).toFloat() * radius
        fillCircle(canvas, color, x, y, 2.2f, 5f)
        fillCircle(canvas, Color.WHITE, x, y, 0.75f, 2f)
    }

    private fun drawChipIcon(canvas: Canvas, cx: Float, cy: Float, color: Int, cpu: Boolean) {
        stroke(color, 1.25f)
        if (cpu) {
            val half = 4.5f
            canvas.drawRoundRect(RectF(cx - half, cy - half, cx + half, cy + half), 1f, 1f, paint)
            canvas.drawRect(cx - 2.4f, cy - 2.4f, cx + 2.4f, cy + 2.4f, paint)
            for (offset in floatArrayOf(-3f, 0f, 3f)) {
                line(canvas, color, 1f, cx - half - 2.5f, cy + offset, cx - half, cy + offset)
                line(canvas, color, 1f, cx + half, cy + offset, cx + half + 2.5f, cy + offset)
                line(canvas, color, 1f, cx + offset, cy - half - 2.5f, cx + offset, cy - half)
                line(canvas, color, 1f, cx + offset, cy + half, cx + offset, cy + half + 2.5f)
            }
        } else {
            canvas.drawRoundRect(RectF(cx - 6.5f, cy - 3.8f, cx + 6.5f, cy + 3.8f), 0.8f, 0.8f, paint)
            for (offset in floatArrayOf(-4f, -2f, 0f, 2f, 4f)) {
                line(canvas, color, 0.9f, cx + offset, cy - 2.4f, cx + offset, cy + 2.4f)
            }
            for (offset in floatArrayOf(-4f, 0f, 4f)) {
                line(canvas, color, 0.9f, cx + offset, cy - 3.8f, cx + offset, cy - 6f)
                line(canvas, color, 0.9f, cx + offset, cy + 3.8f, cx + offset, cy + 6f)
            }
        }
    }

    private fun drawWave(
        canvas: Canvas,
        path: Path,
        history: FloatArray,
        left: Float,
        top: Float,
        width: Float,
        color: Int,
        amount: Long
    ) {
        path.reset()
        val baseline = top + 24f
        val isIdle = amount < IDLE_BYTES_PER_SECOND
        val points = ArrayList<Pair<Float, Float>>()
        for (i in history.indices) {
            val x = left + width * i / history.lastIndex
            val level = if (isIdle) 0f else history[i]
            val y = baseline - level * 18f
            points += x to y
        }
        path.moveTo(points.first().first, points.first().second)
        for (i in 0 until points.lastIndex) {
            val p0 = points[(i - 1).coerceAtLeast(0)]
            val p1 = points[i]
            val p2 = points[i + 1]
            val p3 = points[(i + 2).coerceAtMost(points.lastIndex)]
            path.cubicTo(
                p1.first + (p2.first - p0.first) / 6f,
                p1.second + (p2.second - p0.second) / 6f,
                p2.first - (p3.first - p1.first) / 6f,
                p2.second - (p3.second - p1.second) / 6f,
                p2.first,
                p2.second
            )
        }
        val fillBottom = baseline + 7f
        val fill = Path(path).apply {
            lineTo(left + width, fillBottom)
            lineTo(left, fillBottom)
            close()
        }
        paint.reset()
        paint.isAntiAlias = true
        paint.style = Paint.Style.FILL
        paint.shader = LinearGradient(
            0f, top + 3f, 0f, fillBottom,
            Color.argb(150, Color.red(color), Color.green(color), Color.blue(color)),
            Color.TRANSPARENT,
            Shader.TileMode.CLAMP
        )
        canvas.drawPath(fill, paint)
        paint.shader = null
        glowPath(canvas, path, color, 1.5f, 6f)
    }

    private fun appendNetworkSample(history: FloatArray, bytesPerSecond: Long) {
        val displayedAsZero = bytesPerSecond < IDLE_BYTES_PER_SECOND
        if (displayedAsZero) {
            history.fill(0f)
            return
        }
        for (i in 0 until history.lastIndex) history[i] = history[i + 1]
        val megabytesPerSecond = bytesPerSecond.coerceAtLeast(0L) / 1_000_000f
        // A square-root scale keeps small LAN traffic visible without making high-speed traffic too tall.
        history[history.lastIndex] = sqrt(megabytesPerSecond.coerceIn(0f, 1f)).coerceIn(0.08f, 1f)
    }

    private fun drawSpeed(canvas: Canvas, centerX: Float, baseline: Float, arrow: String, value: String, bytesPerSecond: Long, color: Int) {
        textPaint.reset()
        textPaint.isAntiAlias = true
        textPaint.typeface = Typeface.create("sans-serif", Typeface.NORMAL)
        textPaint.textSize = 16f
        val main = "$arrow $value"
        val mainWidth = textPaint.measureText(main)
        textPaint.textSize = 7.5f
        val unit = if (value == "--") "MB/s"
        else if (bytesPerSecond < 1_000_000L) "KB/s" else "MB/s"
        val unitWidth = textPaint.measureText(unit)
        val gap = 4f
        val start = centerX - (mainWidth + gap + unitWidth) / 2f
        glowingText(canvas, main, start, baseline, 16f, color, Paint.Align.LEFT)
        text(canvas, unit, start + mainWidth + gap, baseline, 7.5f, Color.rgb(166, 178, 190), Paint.Align.LEFT)
    }

    private fun text(canvas: Canvas, value: String, x: Float, y: Float, size: Float, color: Int, align: Paint.Align) {
        textPaint.reset()
        textPaint.isAntiAlias = true
        textPaint.typeface = Typeface.create("sans-serif", Typeface.NORMAL)
        textPaint.textSize = size
        textPaint.color = color
        textPaint.textAlign = align
        canvas.drawText(value, x, y, textPaint)
    }

    private fun glowingText(canvas: Canvas, value: String, x: Float, y: Float, size: Float, color: Int, align: Paint.Align) {
        textPaint.reset()
        textPaint.isAntiAlias = true
        textPaint.typeface = Typeface.create("sans-serif", Typeface.NORMAL)
        textPaint.textSize = size
        textPaint.color = color
        textPaint.textAlign = align
        textPaint.setShadowLayer(5f, 0f, 0f, color)
        canvas.drawText(value, x, y, textPaint)
        textPaint.clearShadowLayer()
    }

    private fun stroke(color: Int, width: Float) {
        paint.reset()
        paint.isAntiAlias = true
        paint.style = Paint.Style.STROKE
        paint.strokeWidth = width
        paint.strokeCap = Paint.Cap.ROUND
        paint.color = color
    }

    private fun line(canvas: Canvas, color: Int, width: Float, x1: Float, y1: Float, x2: Float, y2: Float) {
        stroke(color, width)
        canvas.drawLine(x1, y1, x2, y2, paint)
    }

    private fun glowArc(canvas: Canvas, bounds: RectF, start: Float, sweep: Float, color: Int, width: Float) {
        stroke(color, width)
        paint.setShadowLayer(6f, 0f, 0f, color)
        canvas.drawArc(bounds, start, sweep, false, paint)
        paint.clearShadowLayer()
    }

    private fun glowPath(canvas: Canvas, path: Path, color: Int, width: Float, glow: Float) {
        stroke(color, width)
        paint.setShadowLayer(glow, 0f, 0f, color)
        canvas.drawPath(path, paint)
        paint.clearShadowLayer()
    }

    private fun glowLine(canvas: Canvas, color: Int, width: Float, x1: Float, y1: Float, x2: Float, y2: Float, glow: Float) {
        stroke(color, width)
        paint.setShadowLayer(glow, 0f, 0f, color)
        canvas.drawLine(x1, y1, x2, y2, paint)
        paint.clearShadowLayer()
    }

    private fun fillCircle(canvas: Canvas, color: Int, x: Float, y: Float, radius: Float, glow: Float) {
        paint.reset()
        paint.isAntiAlias = true
        paint.style = Paint.Style.FILL
        paint.color = color
        if (glow > 0f) paint.setShadowLayer(glow, 0f, 0f, color)
        canvas.drawCircle(x, y, radius, paint)
        paint.clearShadowLayer()
    }

    private fun percent(value: Float): String = value.coerceIn(0f, 100f).toInt().toString()
    private fun approach(current: Float, target: Float, factor: Float): Float {
        val next = current + (target - current) * factor
        return if (kotlin.math.abs(target - next) < 0.05f) target else next
    }
    private fun speed(bytes: Long): String {
        val safeBytes = bytes.coerceAtLeast(0L)
        return if (safeBytes < 1_000_000L) {
            val kilobytesPerSecond = safeBytes / 1_000f
            if (kilobytesPerSecond < 1f) "0" else "%.0f".format(kilobytesPerSecond)
        } else {
            "%.1f".format(safeBytes / 1_000_000f)
        }
    }
    private fun formatBytes(bytes: Long): String = if (bytes >= 1_000_000_000L) "%.1f GB".format(bytes / 1_000_000_000f) else "%.0f MB".format(bytes / 1_000_000f)

    private companion object {
        const val WAVE_SAMPLES = 13
        const val IDLE_BYTES_PER_SECOND = 1_000L
    }
}
