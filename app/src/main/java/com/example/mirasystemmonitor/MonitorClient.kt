package com.example.mirasystemmonitor

import android.util.Log
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import okhttp3.OkHttpClient
import okhttp3.Request
import org.json.JSONObject
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.util.concurrent.TimeUnit

class MonitorClient {
    private val tag = "MiraMonitorClient"
    private var cachedApiUrl: String? = null
    private var lastSnapshot: MonitorSnapshot? = null
    private var lastSuccessAt = 0L
    private val http = OkHttpClient.Builder()
        .connectTimeout(900, TimeUnit.MILLISECONDS)
        .readTimeout(900, TimeUnit.MILLISECONDS)
        .build()

    suspend fun discoverAndRead(): MonitorSnapshot = withContext(Dispatchers.IO) {
        cachedApiUrl?.let { url ->
            read(url)?.let { return@withContext remember(it) }
            cachedApiUrl = null
        }

        val apiUrl = discover()
        if (apiUrl != null) {
            read(apiUrl)?.let {
                cachedApiUrl = apiUrl
                Log.i(tag, "computer connected: $apiUrl (${it.networkType})")
                return@withContext remember(it)
            }
        }

        val now = System.currentTimeMillis()
        lastSnapshot?.takeIf { now - lastSuccessAt < STALE_GRACE_MS }
            ?: MonitorSnapshot.Disconnected
    }

    private fun remember(value: MonitorSnapshot): MonitorSnapshot {
        lastSnapshot = value
        lastSuccessAt = System.currentTimeMillis()
        return value
    }

    private fun discover(): String? {
        val magic = "MIRA_MONITOR_DISCOVER_V1".toByteArray(Charsets.UTF_8)
        return runCatching {
            DatagramSocket().use { socket ->
                socket.broadcast = true
                socket.soTimeout = 1100
                val request = DatagramPacket(
                    magic,
                    magic.size,
                    InetAddress.getByName("255.255.255.255"),
                    DISCOVERY_PORT
                )
                socket.send(request)
                val buffer = ByteArray(2048)
                val response = DatagramPacket(buffer, buffer.size)
                socket.receive(response)
                val json = JSONObject(String(response.data, 0, response.length, Charsets.UTF_8))
                val port = json.optInt("port", 8789).coerceIn(1, 65535)
                val advertisedIp = json.optString("ip").trim()
                val host = when {
                    advertisedIp.isNotEmpty() -> advertisedIp
                    json.has("ip") -> null
                    else -> response.address.hostAddress
                }
                host?.let { "http://$it:$port/api/v1/status" }
            }
        }.onFailure { Log.w(tag, "computer discovery failed: ${it.message}") }.getOrNull()
    }

    private fun read(apiUrl: String): MonitorSnapshot? {
        val request = Request.Builder().url(apiUrl).get().build()
        return runCatching {
            http.newCall(request).execute().use { response ->
                if (!response.isSuccessful) return null
                val json = JSONObject(response.body?.string().orEmpty())
                val memory = json.optJSONObject("memory") ?: JSONObject()
                val network = json.optJSONObject("network") ?: JSONObject()
                MonitorSnapshot(
                    connected = json.optBoolean("ok", true),
                    host = json.optString("host", "MIRA-PC"),
                    cpuPercent = json.optDouble("cpuPercent", 0.0).toFloat(),
                    memoryPercent = memory.optDouble("percent", 0.0).toFloat(),
                    memoryUsedBytes = memory.optLong("usedBytes", 0L),
                    memoryTotalBytes = memory.optLong("totalBytes", 0L),
                    downloadBytesPerSecond = network.optLong("downloadBytesPerSecond", 0L),
                    uploadBytesPerSecond = network.optLong("uploadBytesPerSecond", 0L),
                    networkType = network.optString("type", "UNKNOWN"),
                    networkName = network.optString("name", ""),
                    temperatureC = json.optNullableDouble("temperatureC")?.toFloat(),
                    fanRpm = json.optNullableDouble("fanRpm")?.toInt(),
                    fanPercent = json.optNullableDouble("fanPercent")?.toFloat(),
                    chassisRpm = json.optNullableDouble("chassisRpm")?.toInt(),
                    psuRpm = json.optNullableDouble("psuRpm")?.toInt(),
                    gpuRpm = json.optNullableDouble("gpuRpm")?.toInt(),
                    gpuPercent = json.optNullableDouble("gpuPercent")?.toFloat(),
                    timestampMs = json.optLong("timestamp", System.currentTimeMillis())
                )
            }
        }.onFailure { Log.w(tag, "computer status read failed: ${it.message}") }.getOrNull()
    }

    private fun JSONObject.optNullableDouble(name: String): Double? {
        if (!has(name) || isNull(name)) return null
        return optDouble(name).takeUnless { it.isNaN() }
    }

    private companion object {
        const val DISCOVERY_PORT = 38901
        const val STALE_GRACE_MS = 8_000L
    }
}
