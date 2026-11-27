using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

namespace DSHVoiceSystem
{
    /// <summary>
    /// 16 kHz mono capture through winmm waveIn (no external audio package).
    /// Returns float samples, which is what the DSH speech route expects.
    /// </summary>
    public sealed class Recorder : IDisposable
    {
        private const int WAVE_MAPPER = -1;
        private const int WAVE_FORMAT_PCM = 1;
        private const int CALLBACK_FUNCTION = 0x00030000;
        private const int WIM_DATA = 0x3C0;
        private const int MMSYSERR_NOERROR = 0;

        private delegate void WaveInProc(IntPtr hwi, uint uMsg, IntPtr dwInstance, IntPtr dwParam1, IntPtr dwParam2);

        [StructLayout(LayoutKind.Sequential)]
        private struct WAVEFORMATEX
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WAVEHDR
        {
            public IntPtr lpData;
            public uint dwBufferLength;
            public uint dwBytesRecorded;
            public IntPtr dwUser;
            public uint dwFlags;
            public uint dwLoops;
            public IntPtr lpNext;
            public IntPtr reserved;
        }

        [DllImport("winmm.dll")]
        private static extern int waveInOpen(out IntPtr hwi, int deviceId, ref WAVEFORMATEX format, WaveInProc callback, IntPtr instance, int flags);
        [DllImport("winmm.dll")]
        private static extern int waveInPrepareHeader(IntPtr hwi, IntPtr header, int headerSize);
        [DllImport("winmm.dll")]
        private static extern int waveInUnprepareHeader(IntPtr hwi, IntPtr header, int headerSize);
        [DllImport("winmm.dll")]
        private static extern int waveInAddBuffer(IntPtr hwi, IntPtr header, int headerSize);
        [DllImport("winmm.dll")]
        private static extern int waveInStart(IntPtr hwi);
        [DllImport("winmm.dll")]
        private static extern int waveInStop(IntPtr hwi);
        [DllImport("winmm.dll")]
        private static extern int waveInReset(IntPtr hwi);
        [DllImport("winmm.dll")]
        private static extern int waveInClose(IntPtr hwi);

        private sealed class Buffer
        {
            public byte[] Data;
            public IntPtr DataPtr;
            public IntPtr HeaderPtr;
            public WAVEHDR Header;
        }

        private readonly object _gate = new object();
        private readonly List<byte> _pcm = new List<byte>();
        private readonly List<Buffer> _buffers = new List<Buffer>();
        private IntPtr _handle;
        private WaveInProc _callback;
        private int _sampleRate;
        private bool _recording;
        private bool _stopping;
        private DateTime _startedAt;

        public bool IsRecording
        {
            get { return _recording; }
        }

        public TimeSpan Duration
        {
            get
            {
                if (!_recording) return TimeSpan.Zero;
                return DateTime.UtcNow - _startedAt;
            }
        }

        public void Start()
        {
            if (_recording) return;
            _pcm.Clear();
            _stopping = false;
            _callback = OnWaveIn;

            int[] rates = new int[] { 16000, 44100, 48000 };
            string lastError = "";
            foreach (int rate in rates)
            {
                if (TryOpen(rate, out lastError))
                {
                    _recording = true;
                    _startedAt = DateTime.UtcNow;
                    int result = waveInStart(_handle);
                    if (result != MMSYSERR_NOERROR)
                    {
                        Close();
                        throw new InvalidOperationException("无法开始录音（waveInStart=" + result + "）");
                    }
                    Log.Write("recording at " + rate + " Hz");
                    return;
                }
            }
            throw new InvalidOperationException("无法打开麦克风：" + lastError);
        }

        private bool TryOpen(int rate, out string error)
        {
            error = "";
            WAVEFORMATEX format = new WAVEFORMATEX();
            format.wFormatTag = WAVE_FORMAT_PCM;
            format.nChannels = 1;
            format.nSamplesPerSec = (uint)rate;
            format.wBitsPerSample = 16;
            format.nBlockAlign = 2;
            format.nAvgBytesPerSec = (uint)(rate * 2);
            format.cbSize = 0;

            IntPtr handle;
            int result = waveInOpen(out handle, WAVE_MAPPER, ref format, _callback, IntPtr.Zero, CALLBACK_FUNCTION);
            if (result != MMSYSERR_NOERROR)
            {
                error = "waveInOpen=" + result;
                return false;
            }
            _handle = handle;
            _sampleRate = rate;

            int bufferBytes = Math.Max(3200, rate / 10 * 2); // ~100 ms
            try
            {
                for (int i = 0; i < 8; i++)
                {
                    Buffer buffer = new Buffer();
                    buffer.Data = new byte[bufferBytes];
                    buffer.DataPtr = Marshal.AllocHGlobal(bufferBytes);
                    buffer.Header = new WAVEHDR();
                    buffer.Header.lpData = buffer.DataPtr;
                    buffer.Header.dwBufferLength = (uint)bufferBytes;
                    buffer.HeaderPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WAVEHDR)));
                    Marshal.StructureToPtr(buffer.Header, buffer.HeaderPtr, false);
                    int prep = waveInPrepareHeader(_handle, buffer.HeaderPtr, Marshal.SizeOf(typeof(WAVEHDR)));
                    if (prep != MMSYSERR_NOERROR) throw new InvalidOperationException("waveInPrepareHeader=" + prep);
                    int add = waveInAddBuffer(_handle, buffer.HeaderPtr, Marshal.SizeOf(typeof(WAVEHDR)));
                    if (add != MMSYSERR_NOERROR) throw new InvalidOperationException("waveInAddBuffer=" + add);
                    _buffers.Add(buffer);
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Close();
                return false;
            }
        }

        private void OnWaveIn(IntPtr hwi, uint message, IntPtr instance, IntPtr param1, IntPtr param2)
        {
            if (message != WIM_DATA || param1 == IntPtr.Zero) return;
            try
            {
                WAVEHDR header = (WAVEHDR)Marshal.PtrToStructure(param1, typeof(WAVEHDR));
                if (header.dwBytesRecorded > 0)
                {
                    byte[] copy = new byte[header.dwBytesRecorded];
                    Marshal.Copy(header.lpData, copy, 0, copy.Length);
                    lock (_gate) _pcm.AddRange(copy);
                }
                if (!_stopping && _handle != IntPtr.Zero)
                    waveInAddBuffer(_handle, param1, Marshal.SizeOf(typeof(WAVEHDR)));
            }
            catch (Exception ex)
            {
                Log.Write("waveIn callback: " + ex.Message);
            }
        }

        /// <summary>Stop and return 16 kHz mono float samples.</summary>
        public float[] Stop()
        {
            if (!_recording && !_stopping) return new float[0];
            _stopping = true;
            _recording = false;
            byte[] raw;
            lock (_gate)
            {
                raw = _pcm.ToArray();
                _pcm.Clear();
            }
            Close();
            if (raw.Length < 2) return new float[0];
            int samples = raw.Length / 2;
            float[] input = new float[samples];
            for (int i = 0; i < samples; i++)
            {
                short value = (short)(raw[i * 2] | (raw[i * 2 + 1] << 8));
                input[i] = value / 32768f;
            }
            if (_sampleRate == 16000) return input;
            return Resample(input, _sampleRate, 16000);
        }

        public void Abort()
        {
            _stopping = true;
            _recording = false;
            lock (_gate) _pcm.Clear();
            Close();
        }

        private void Close()
        {
            if (_handle != IntPtr.Zero)
            {
                try { waveInStop(_handle); } catch { }
                try { waveInReset(_handle); } catch { }
                Thread.Sleep(80);
                for (int i = 0; i < _buffers.Count; i++)
                {
                    try { waveInUnprepareHeader(_handle, _buffers[i].HeaderPtr, Marshal.SizeOf(typeof(WAVEHDR))); } catch { }
                    try { Marshal.FreeHGlobal(_buffers[i].DataPtr); } catch { }
                    try { Marshal.FreeHGlobal(_buffers[i].HeaderPtr); } catch { }
                }
                _buffers.Clear();
                try { waveInClose(_handle); } catch { }
                _handle = IntPtr.Zero;
            }
        }

        public static float[] Resample(float[] input, int fromRate, int toRate)
        {
            if (fromRate == toRate || input.Length == 0) return input;
            double ratio = (double)fromRate / toRate;
            int outputLength = (int)Math.Floor(input.Length / ratio);
            if (outputLength <= 0) return new float[0];
            float[] output = new float[outputLength];
            for (int i = 0; i < outputLength; i++)
            {
                double position = i * ratio;
                int left = (int)Math.Floor(position);
                int right = Math.Min(left + 1, input.Length - 1);
                double fraction = position - left;
                output[i] = (float)(input[left] * (1.0 - fraction) + input[right] * fraction);
            }
            return output;
        }

        public static double Rms(float[] samples)
        {
            if (samples == null || samples.Length == 0) return 0;
            double sum = 0;
            for (int i = 0; i < samples.Length; i++) sum += samples[i] * samples[i];
            return Math.Sqrt(sum / samples.Length);
        }

        public void Dispose()
        {
            Abort();
        }
    }
}
