using System.Runtime.InteropServices;

namespace CantoTranscribe;

internal sealed record NativeTranscript(int Kind, long StartMs, long EndMs, string Text);

internal sealed class NativeEngine : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct EngineConfig
    {
        public uint StructSize;
        public uint SampleRateHz;
        public uint RingCapacitySamples;
        public uint ResultQueueCapacity;
        public uint ChunkSamples;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeResult
    {
        public uint StructSize;
        public int Kind;
        public long StartMs;
        public long EndMs;
        public IntPtr Text;
        public nuint TextLength;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    internal struct Capabilities
    {
        public uint StructSize;
        public uint AbiVersion;
        public byte SupportsStreaming;
        public byte SupportsTimestamps;
        public byte SupportsHotwords;
        public byte SupportsVad;
        public uint MaxSampleRateHz;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Backend;
    }

    [DllImport("canto_core.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int canto_engine_create(ref EngineConfig config, out IntPtr engine);
    [DllImport("canto_core.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void canto_engine_destroy(IntPtr engine);
    [DllImport("canto_core.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern int canto_model_load(IntPtr engine, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);
    [DllImport("canto_core.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int canto_engine_set_timestamp_mode(IntPtr engine, byte enabled);
    [DllImport("canto_core.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int canto_engine_cancel(IntPtr engine);
    [DllImport("canto_core.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe int canto_push_pcm16(IntPtr engine, short* samples, nuint count, byte endOfStream);
    [DllImport("canto_core.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int canto_poll_result(IntPtr engine, ref NativeResult result);
    [DllImport("canto_core.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void canto_result_free(ref NativeResult result);
    [DllImport("canto_core.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int canto_engine_get_capabilities(IntPtr engine, ref Capabilities capabilities);
    [DllImport("canto_core.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern long canto_engine_last_inference_ms(IntPtr engine);
    [DllImport("canto_core.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr canto_status_message(int status);

    private IntPtr _engine;
    private readonly object _lifetime = new();
    private IDisposable? _modelLease;
    public string LoadedModelPath { get; private set; } = "";

    public NativeEngine(string modelPath, bool timestampMode)
    {
        var config = new EngineConfig
        {
            StructSize = (uint)Marshal.SizeOf<EngineConfig>(), SampleRateHz = 16_000,
            RingCapacitySamples = 16_000 * 30, ResultQueueCapacity = 128, ChunkSamples = 16_000 * 10,
        };
        try
        {
            _modelLease = ModelUseGate.Acquire(modelPath);
            Check(canto_engine_create(ref config, out _engine));
            Check(canto_engine_set_timestamp_mode(_engine, timestampMode ? (byte)1 : (byte)0));
            Check(canto_model_load(_engine, modelPath));
            LoadedModelPath = Path.GetFullPath(modelPath);
        }
        catch { Dispose(); throw; }
    }

    public void RequestCancel()
    {
        if (_engine != IntPtr.Zero) _ = canto_engine_cancel(_engine);
    }

    public Capabilities GetCapabilities()
    {
        var capabilities = new Capabilities { StructSize = (uint)Marshal.SizeOf<Capabilities>(), Backend = string.Empty };
        Check(canto_engine_get_capabilities(_engine, ref capabilities));
        return capabilities;
    }

    public long LastInferenceMs => _engine == IntPtr.Zero
        ? -1
        : canto_engine_last_inference_ms(_engine);

    public unsafe int Push(short[] samples, int count, bool endOfStream)
    {
        if (count < 0 || count > samples.Length) throw new ArgumentOutOfRangeException(nameof(count));
        fixed (short* pointer = samples)
            return canto_push_pcm16(_engine, count == 0 ? null : pointer, (nuint)count,
                endOfStream ? (byte)1 : (byte)0);
    }

    public NativeTranscript? Poll()
    {
        var native = new NativeResult { StructSize = (uint)Marshal.SizeOf<NativeResult>() };
        var status = canto_poll_result(_engine, ref native);
        if (status == 5) return null;
        Check(status);
        try
        {
            var length = checked((int)native.TextLength);
            var text = native.Text == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUTF8(native.Text, length) ?? string.Empty;
            return new NativeTranscript(native.Kind, native.StartMs, native.EndMs, text);
        }
        finally { canto_result_free(ref native); }
    }

    public static void Check(int status)
    {
        if (status == 0) return;
        var pointer = canto_status_message(status);
        var message = pointer == IntPtr.Zero ? "native error" : Marshal.PtrToStringUTF8(pointer);
        throw new InvalidOperationException($"{message} ({status})");
    }

    public void Dispose()
    {
        var (engine, lease) = Detach();
        try { if (engine != IntPtr.Zero) canto_engine_destroy(engine); }
        finally { lease?.Dispose(); }
    }

    public Task DisposeAsync()
    {
        var (engine, lease) = Detach();
        return Task.Run(() =>
        {
            try { if (engine != IntPtr.Zero) canto_engine_destroy(engine); }
            finally { lease?.Dispose(); }
        });
    }

    private (IntPtr, IDisposable?) Detach()
    {
        lock (_lifetime)
        {
            var state = (_engine, _modelLease);
            _engine = IntPtr.Zero;
            _modelLease = null;
            return state;
        }
    }
}
