package com.example.mirasystemmonitor

data class MonitorSnapshot(
    val connected: Boolean = false,
    val host: String = "MIRA-PC",
    val cpuPercent: Float = 54f,
    val memoryPercent: Float = 49f,
    val memoryUsedBytes: Long = 7_800_000_000L,
    val memoryTotalBytes: Long = 16_000_000_000L,
    val downloadBytesPerSecond: Long = 38_200_000L,
    val uploadBytesPerSecond: Long = 5_100_000L,
    val networkType: String = "WI-FI",
    val networkName: String = "",
    val temperatureC: Float? = 34f,
    val fanRpm: Int? = 1250,
    val fanPercent: Float? = null,
    val chassisRpm: Int? = 1018,
    val psuRpm: Int? = 1257,
    val gpuRpm: Int? = 0,
    val gpuPercent: Float? = 0f,
    val timestampMs: Long = System.currentTimeMillis()
) {
    companion object {
        val Demo = MonitorSnapshot()

        // Demo values are only for development previews, never for a lost connection.
        val Disconnected = MonitorSnapshot(
            connected = false,
            host = "MIRA-PC",
            cpuPercent = 0f,
            memoryPercent = 0f,
            memoryUsedBytes = 0L,
            memoryTotalBytes = 0L,
            downloadBytesPerSecond = 0L,
            uploadBytesPerSecond = 0L,
            networkType = "UNKNOWN",
            networkName = "",
            temperatureC = null,
            fanRpm = null,
            fanPercent = null,
            chassisRpm = null,
            psuRpm = null,
            gpuRpm = null,
            gpuPercent = null
        )
    }
}
