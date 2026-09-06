package hk.canto.canto_meet

import android.Manifest
import android.app.ActivityManager
import android.content.pm.PackageManager
import android.icu.text.Transliterator
import android.media.AudioFormat
import android.media.AudioRecord
import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaFormat
import android.media.MediaExtractor
import android.media.MediaMuxer
import android.media.MediaRecorder
import android.os.Handler
import android.os.Looper
import android.os.Build
import androidx.core.content.ContextCompat
import io.flutter.embedding.android.FlutterActivity
import io.flutter.embedding.engine.FlutterEngine
import io.flutter.plugin.common.EventChannel
import io.flutter.plugin.common.MethodCall
import io.flutter.plugin.common.MethodChannel
import java.io.File
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.concurrent.atomic.AtomicBoolean
import kotlin.math.min

class MainActivity : FlutterActivity() {
    private lateinit var audioBridge: MeetingAudioBridge

    override fun configureFlutterEngine(flutterEngine: FlutterEngine) {
        super.configureFlutterEngine(flutterEngine)
        audioBridge = MeetingAudioBridge(this, flutterEngine)
    }

    override fun onDestroy() {
        if (::audioBridge.isInitialized) audioBridge.close()
        super.onDestroy()
    }
}

private class MeetingAudioBridge(
    private val activity: FlutterActivity,
    flutterEngine: FlutterEngine,
) : MethodChannel.MethodCallHandler, EventChannel.StreamHandler {
    private val mainHandler = Handler(Looper.getMainLooper())
    private var eventSink: EventChannel.EventSink? = null
    private var capture: MeetingAudioCapture? = null
    private val decoding = AtomicBoolean(false)
    private val hongKongConverter = HongKongChineseConverter(activity.assets)
    private val cantoneseCleaner = CantoneseCleaner(activity.assets)

    init {
        MethodChannel(flutterEngine.dartExecutor.binaryMessenger, "hk.canto.canto_meet/audio_control")
            .setMethodCallHandler(this)
        EventChannel(flutterEngine.dartExecutor.binaryMessenger, "hk.canto.canto_meet/audio_events")
            .setStreamHandler(this)
    }

    override fun onListen(arguments: Any?, events: EventChannel.EventSink) {
        eventSink = events
    }

    override fun onCancel(arguments: Any?) {
        eventSink = null
    }

    override fun onMethodCall(call: MethodCall, result: MethodChannel.Result) {
        when (call.method) {
            "start" -> start(call, result)
            "stop" -> stop(result)
            "decode" -> decode(call, result)
            "hardwareProfile" -> hardwareProfile(result)
            "modelCatalog" -> {
                try {
                    result.success(activity.assets.open("model-catalog/catalog.v1.json").bufferedReader().use { it.readText() })
                } catch (error: Exception) {
                    result.error("model_catalog", "未能讀取模型目錄", error.message)
                }
            }
            "convertChinese" -> convertChinese(call, result)
            else -> result.notImplemented()
        }
    }

    private fun hardwareProfile(result: MethodChannel.Result) {
        val manager = activity.getSystemService(ActivityManager::class.java)
        val memoryInfo = ActivityManager.MemoryInfo()
        manager.getMemoryInfo(memoryInfo)
        val memoryMiB = memoryInfo.totalMem / (1024L * 1024L)
        val recommendation = when {
            memoryMiB >= 4096 && Runtime.getRuntime().availableProcessors() >= 8 -> "high"
            memoryMiB >= 2048 && Runtime.getRuntime().availableProcessors() >= 4 -> "standard"
            else -> "light"
        }
        result.success(
            mapOf(
                "memoryMiB" to memoryMiB,
                "largeMemoryMiB" to manager.largeMemoryClass,
                "processors" to Runtime.getRuntime().availableProcessors(),
                "recommendation" to recommendation,
            ),
        )
    }

    private fun convertChinese(call: MethodCall, result: MethodChannel.Result) {
        val text = call.argument<String>("text") ?: ""
        val simplified = call.argument<Boolean>("simplified") ?: false
        val clean = call.argument<Boolean>("clean") ?: false
        val normalized = if (simplified && Build.VERSION.SDK_INT >= Build.VERSION_CODES.N) {
            Transliterator.getInstance("Traditional-Simplified").transliterate(text)
        } else if (simplified) text else hongKongConverter.convert(text)
        result.success(if (clean) cantoneseCleaner.clean(normalized) else normalized)
    }

    private fun start(call: MethodCall, result: MethodChannel.Result) {
        if (capture != null) {
            result.error("already_recording", "錄音已經開始", null)
            return
        }
        if (ContextCompat.checkSelfPermission(activity, Manifest.permission.RECORD_AUDIO) !=
            PackageManager.PERMISSION_GRANTED
        ) {
            result.error("microphone_permission", "需要咪高峰權限先可以錄音", null)
            return
        }
        val directory = call.argument<String>("directory")
        val meetingId = call.argument<String>("meetingId")
        val segmentDurationMs = call.argument<Number>("segmentDurationMs")?.toLong() ?: 300_000L
        if (directory.isNullOrBlank() || meetingId.isNullOrBlank()) {
            result.error("invalid_arguments", "錄音目錄或會議編號無效", null)
            return
        }
        try {
            capture = MeetingAudioCapture(
                File(directory), meetingId, segmentDurationMs, onEvent = ::emit,
            ).also { it.start() }
            result.success(null)
        } catch (error: Throwable) {
            capture = null
            result.error("audio_start_failed", error.message, null)
        }
    }

    private fun stop(result: MethodChannel.Result) {
        val active = capture
        if (active == null) {
            result.success(emptyList<String>())
            return
        }
        capture = null
        Thread {
            try {
                val paths = active.stop()
                mainHandler.post { result.success(paths) }
            } catch (error: Throwable) {
                mainHandler.post { result.error("audio_stop_failed", error.message, null) }
            }
        }.start()
    }

    private fun decode(call: MethodCall, result: MethodChannel.Result) {
        val path = call.argument<String>("path")
        if (path.isNullOrBlank() || !File(path).isFile) {
            result.error("invalid_media", "錄音片段不存在", null)
            return
        }
        if (!decoding.compareAndSet(false, true)) {
            result.error("decode_busy", "另一個錄音片段正在處理", null)
            return
        }
        result.success(null)
        Thread({
            try {
                decodeAudio(File(path), ::emit)
                emit(mapOf("type" to "decode_done", "path" to path))
            } catch (error: Throwable) {
                emit(mapOf("type" to "decode_error", "message" to (error.message ?: error.toString())))
            } finally {
                decoding.set(false)
            }
        }, "canto-meet-quality-decode").start()
    }

    private fun emit(event: Map<String, Any?>) {
        mainHandler.post { eventSink?.success(event) }
    }

    fun close() {
        capture?.stop()
        capture = null
    }

    private fun decodeAudio(file: File, onEvent: (Map<String, Any?>) -> Unit) {
        val extractor = MediaExtractor()
        var decoder: MediaCodec? = null
        try {
            extractor.setDataSource(file.absolutePath)
            var track = -1
            var format: MediaFormat? = null
            for (index in 0 until extractor.trackCount) {
                val candidate = extractor.getTrackFormat(index)
                if (candidate.getString(MediaFormat.KEY_MIME)?.startsWith("audio/") == true) {
                    track = index
                    format = candidate
                    break
                }
            }
            check(track >= 0 && format != null) { "錄音片段未有可解碼音訊" }
            extractor.selectTrack(track)
            decoder = MediaCodec.createDecoderByType(checkNotNull(format.getString(MediaFormat.KEY_MIME)))
            decoder.configure(format, null, null, 0)
            decoder.start()
            val info = MediaCodec.BufferInfo()
            var inputEnded = false
            var outputEnded = false
            var sampleRate = format.getInteger(MediaFormat.KEY_SAMPLE_RATE)
            var channels = format.getInteger(MediaFormat.KEY_CHANNEL_COUNT)
            while (!outputEnded) {
                if (!inputEnded) {
                    val inputIndex = decoder.dequeueInputBuffer(10_000)
                    if (inputIndex >= 0) {
                        val input = checkNotNull(decoder.getInputBuffer(inputIndex))
                        val size = extractor.readSampleData(input, 0)
                        if (size < 0) {
                            decoder.queueInputBuffer(inputIndex, 0, 0, 0, MediaCodec.BUFFER_FLAG_END_OF_STREAM)
                            inputEnded = true
                        } else {
                            decoder.queueInputBuffer(inputIndex, 0, size, extractor.sampleTime, 0)
                            extractor.advance()
                        }
                    }
                }
                when (val outputIndex = decoder.dequeueOutputBuffer(info, 10_000)) {
                    MediaCodec.INFO_OUTPUT_FORMAT_CHANGED -> {
                        val outputFormat = decoder.outputFormat
                        sampleRate = outputFormat.getInteger(MediaFormat.KEY_SAMPLE_RATE)
                        channels = outputFormat.getInteger(MediaFormat.KEY_CHANNEL_COUNT)
                    }
                    MediaCodec.INFO_TRY_AGAIN_LATER -> Unit
                    else -> if (outputIndex >= 0) {
                        if (info.size > 0) {
                            val output = checkNotNull(decoder.getOutputBuffer(outputIndex)).duplicate()
                            output.position(info.offset)
                            output.limit(info.offset + info.size)
                            val data = ByteArray(info.size)
                            output.get(data)
                            emitDecodedPcm(data, sampleRate, channels, onEvent)
                        }
                        outputEnded = info.flags and MediaCodec.BUFFER_FLAG_END_OF_STREAM != 0
                        decoder.releaseOutputBuffer(outputIndex, false)
                    }
                }
            }
        } finally {
            try { decoder?.stop() } catch (_: Throwable) {}
            decoder?.release()
            extractor.release()
        }
    }

    private fun emitDecodedPcm(
        data: ByteArray,
        sampleRate: Int,
        channels: Int,
        onEvent: (Map<String, Any?>) -> Unit,
    ) {
        if (data.size < channels * 2 || sampleRate <= 0 || channels <= 0) return
        val shorts = ByteBuffer.wrap(data).order(ByteOrder.LITTLE_ENDIAN).asShortBuffer()
        val frames = shorts.remaining() / channels
        val step = sampleRate.toDouble() / 16_000.0
        val outputFrames = (frames / step).toInt()
        if (outputFrames <= 0) return
        val output = ByteArray(outputFrames * 2)
        for (index in 0 until outputFrames) {
            val sourceFrame = min(frames - 1, (index * step).toInt())
            var sum = 0
            for (channel in 0 until channels) sum += shorts.get(sourceFrame * channels + channel).toInt()
            val sample = (sum / channels).toShort().toInt()
            output[index * 2] = (sample and 0xff).toByte()
            output[index * 2 + 1] = ((sample ushr 8) and 0xff).toByte()
        }
        onEvent(mapOf("type" to "decode_pcm", "sampleRate" to 16_000, "data" to output))
    }
}

private class MeetingAudioCapture(
    private val directory: File,
    private val meetingId: String,
    segmentDurationMs: Long,
    private val onEvent: (Map<String, Any?>) -> Unit,
) {
    companion object {
        private const val ARCHIVE_SAMPLE_RATE = 48_000
        private const val ASR_SAMPLE_RATE = 16_000
        private const val AAC_BIT_RATE = 64_000
        private const val MIME = MediaFormat.MIMETYPE_AUDIO_AAC
    }

    private val running = AtomicBoolean(false)
    private val segmentSamples = ARCHIVE_SAMPLE_RATE * (segmentDurationMs.coerceAtLeast(30_000L) / 1000L)
    private val completedPaths = mutableListOf<String>()
    private var captureThread: Thread? = null
    private lateinit var recorder: AudioRecord
    private var encoder: MediaCodec? = null
    private var muxer: MediaMuxer? = null
    private var muxerTrack = -1
    private var muxerStarted = false
    private var samplesInSegment = 0L
    private var sequence = 0
    private lateinit var partFile: File

    fun start() {
        directory.mkdirs()
        val minimum = AudioRecord.getMinBufferSize(
            ARCHIVE_SAMPLE_RATE, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT,
        )
        check(minimum > 0) { "裝置唔支援 48 kHz 單聲道錄音" }
        recorder = AudioRecord.Builder()
            .setAudioSource(MediaRecorder.AudioSource.VOICE_RECOGNITION)
            .setAudioFormat(
                AudioFormat.Builder()
                    .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                    .setSampleRate(ARCHIVE_SAMPLE_RATE)
                    .setChannelMask(AudioFormat.CHANNEL_IN_MONO)
                    .build(),
            )
            .setBufferSizeInBytes(maxOf(minimum * 2, 16_384))
            .build()
        check(recorder.state == AudioRecord.STATE_INITIALIZED) { "咪高峰初始化失敗" }
        openSegment()
        recorder.startRecording()
        check(recorder.recordingState == AudioRecord.RECORDSTATE_RECORDING) { "咪高峰未能開始錄音" }
        running.set(true)
        captureThread = Thread(::captureLoop, "canto-meet-audio").also { it.start() }
    }

    fun stop(): List<String> {
        running.set(false)
        captureThread?.join(5_000)
        if (captureThread?.isAlive == true) {
            recorder.stop()
            captureThread?.join(2_000)
        }
        return synchronized(completedPaths) { completedPaths.toList() }
    }

    private fun captureLoop() {
        val buffer = ShortArray(4_800)
        try {
            while (running.get()) {
                val read = recorder.read(buffer, 0, buffer.size, AudioRecord.READ_BLOCKING)
                if (read < 0) throw IllegalStateException("咪高峰讀取失敗：$read")
                if (read == 0) continue
                feedEncoder(buffer, read)
                emitAsrPcm(buffer, read)
                samplesInSegment += read
                if (samplesInSegment >= segmentSamples) {
                    closeSegment()
                    sequence += 1
                    openSegment()
                }
            }
        } catch (error: Throwable) {
            onEvent(mapOf("type" to "error", "message" to (error.message ?: error.toString())))
        } finally {
            try {
                if (recorder.recordingState == AudioRecord.RECORDSTATE_RECORDING) recorder.stop()
            } catch (_: Throwable) {
            }
            recorder.release()
            try {
                closeSegment()
            } catch (error: Throwable) {
                onEvent(mapOf("type" to "error", "message" to (error.message ?: error.toString())))
            }
        }
    }

    private fun openSegment() {
        samplesInSegment = 0
        partFile = File(directory, "segment_${sequence.toString().padStart(5, '0')}.m4a.part")
        if (partFile.exists()) partFile.delete()
        muxer = MediaMuxer(partFile.absolutePath, MediaMuxer.OutputFormat.MUXER_OUTPUT_MPEG_4)
        muxerTrack = -1
        muxerStarted = false
        encoder = MediaCodec.createEncoderByType(MIME).also { codec ->
            val format = MediaFormat.createAudioFormat(MIME, ARCHIVE_SAMPLE_RATE, 1).apply {
                setInteger(MediaFormat.KEY_AAC_PROFILE, MediaCodecInfo.CodecProfileLevel.AACObjectLC)
                setInteger(MediaFormat.KEY_BIT_RATE, AAC_BIT_RATE)
                setInteger(MediaFormat.KEY_MAX_INPUT_SIZE, 16_384)
            }
            codec.configure(format, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE)
            codec.start()
        }
    }

    private fun feedEncoder(samples: ShortArray, count: Int) {
        val codec = checkNotNull(encoder)
        var offset = 0
        while (offset < count) {
            drainEncoder(false)
            val inputIndex = codec.dequeueInputBuffer(10_000)
            if (inputIndex < 0) continue
            val input = checkNotNull(codec.getInputBuffer(inputIndex))
            input.clear()
            input.order(ByteOrder.LITTLE_ENDIAN)
            val writable = min(count - offset, input.remaining() / 2)
            input.asShortBuffer().put(samples, offset, writable)
            input.position(writable * 2)
            val presentationUs = (samplesInSegment + offset) * 1_000_000L / ARCHIVE_SAMPLE_RATE
            codec.queueInputBuffer(inputIndex, 0, writable * 2, presentationUs, 0)
            offset += writable
        }
        drainEncoder(false)
    }

    private fun emitAsrPcm(samples: ShortArray, count: Int) {
        val outputCount = count / 3
        if (outputCount == 0) return
        val bytes = ByteArray(outputCount * 2)
        for (index in 0 until outputCount) {
            val source = index * 3
            val averaged = ((samples[source].toInt() + samples[source + 1] + samples[source + 2]) / 3).toShort()
            bytes[index * 2] = (averaged.toInt() and 0xff).toByte()
            bytes[index * 2 + 1] = ((averaged.toInt() ushr 8) and 0xff).toByte()
        }
        onEvent(mapOf("type" to "pcm", "sampleRate" to ASR_SAMPLE_RATE, "data" to bytes))
    }

    private fun closeSegment() {
        val codec = encoder ?: return
        var inputQueued = false
        while (!inputQueued) {
            val index = codec.dequeueInputBuffer(10_000)
            if (index >= 0) {
                val presentationUs = samplesInSegment * 1_000_000L / ARCHIVE_SAMPLE_RATE
                codec.queueInputBuffer(index, 0, 0, presentationUs, MediaCodec.BUFFER_FLAG_END_OF_STREAM)
                inputQueued = true
            } else {
                drainEncoder(false)
            }
        }
        drainEncoder(true)
        codec.stop()
        codec.release()
        encoder = null
        if (muxerStarted) muxer?.stop()
        muxer?.release()
        muxer = null
        if (partFile.length() > 0 && muxerStarted) {
            val finalFile = File(partFile.absolutePath.removeSuffix(".part"))
            if (finalFile.exists()) finalFile.delete()
            check(partFile.renameTo(finalFile)) { "錄音片段提交失敗" }
            synchronized(completedPaths) { completedPaths.add(finalFile.absolutePath) }
            onEvent(
                mapOf(
                    "type" to "segment", "meetingId" to meetingId,
                    "sequence" to sequence, "path" to finalFile.absolutePath,
                ),
            )
        } else {
            partFile.delete()
        }
    }

    private fun drainEncoder(waitForEnd: Boolean) {
        val codec = encoder ?: return
        val info = MediaCodec.BufferInfo()
        while (true) {
            val outputIndex = codec.dequeueOutputBuffer(info, if (waitForEnd) 10_000 else 0)
            when {
                outputIndex == MediaCodec.INFO_TRY_AGAIN_LATER -> if (!waitForEnd) return
                outputIndex == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED -> {
                    check(!muxerStarted) { "AAC 輸出格式重複改變" }
                    muxerTrack = checkNotNull(muxer).addTrack(codec.outputFormat)
                    checkNotNull(muxer).start()
                    muxerStarted = true
                }
                outputIndex >= 0 -> {
                    val output = checkNotNull(codec.getOutputBuffer(outputIndex))
                    if (info.flags and MediaCodec.BUFFER_FLAG_CODEC_CONFIG != 0) info.size = 0
                    if (info.size > 0) {
                        check(muxerStarted) { "AAC 封裝器未開始" }
                        output.position(info.offset)
                        output.limit(info.offset + info.size)
                        checkNotNull(muxer).writeSampleData(muxerTrack, output, info)
                    }
                    val ended = info.flags and MediaCodec.BUFFER_FLAG_END_OF_STREAM != 0
                    codec.releaseOutputBuffer(outputIndex, false)
                    if (ended) return
                }
            }
        }
    }
}
