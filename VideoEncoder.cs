// H.264 인코더 — Android MediaCodec + MediaMuxer. ffmpeg 없이 MP4 를 만든다.
//   SKBitmap(BGRA) → NV12/I420 ByteBuffer → MediaCodec → MediaMuxer(MP4)
using System;
using System.Collections.Generic;
using Android.Media;
using SkiaSharp;

namespace ReplayVideoCore
{
    public sealed class H264Encoder : IDisposable
    {
        private readonly int _w, _h, _fps;
        private readonly string _path;
        private MediaCodec _codec;
        private MediaMuxer _muxer;
        private MediaCodec.BufferInfo _info = new MediaCodec.BufferInfo();
        private int _track = -1;
        private bool _muxStarted, _eosQueued, _done;
        private byte[] _pixels;
        private byte[] _yuv;
        private int _stride = -1;
        private bool _semiPlanar = true;
        private long _frames;

        public int Frames { get { return (int)_frames; } }
        public string Path { get { return _path; } }

        public H264Encoder(int w, int h, int fps, string outPath)
        {
            _w = w; _h = h; _fps = fps; _path = outPath;
            _pixels = new byte[w * h * 4];
            _yuv = new byte[w * h * 3 / 2];

            try { if (System.IO.File.Exists(outPath)) System.IO.File.Delete(outPath); } catch { }
            _codec = MediaCodec.CreateEncoderByType("video/avc");
            var fmt = MediaFormat.CreateVideoFormat("video/avc", w, h);
            fmt.SetInteger(MediaFormat.KeyBitRate, BitrateFor(w, h));
            fmt.SetInteger(MediaFormat.KeyFrameRate, fps);
            fmt.SetInteger(MediaFormat.KeyIFrameInterval, 2);
            fmt.SetInteger(MediaFormat.KeyColorFormat, 21);   // COLOR_FormatYUV420SemiPlanar (NV12)
            try
            {
                _codec.Configure(fmt, null, null, MediaCodecConfigFlags.Encode);
            }
            catch
            {
                _semiPlanar = false;
                fmt.SetInteger(MediaFormat.KeyColorFormat, 19);   // COLOR_FormatYUV420Planar (I420)
                _codec.Configure(fmt, null, null, MediaCodecConfigFlags.Encode);
            }
            _codec.Start();
            _muxer = new MediaMuxer(outPath, MuxerOutputType.Mpeg4);
        }

        private static int BitrateFor(int w, int h)
        {
            long px = (long)w * h;
            int bps = (int)(px * 4);            // 대략 4bit/px/frame 기준
            if (bps < 2500000) bps = 2500000;
            if (bps > 12000000) bps = 12000000;
            return bps;
        }

        public static Action<string> Log;

        /// <summary>한 프레임 넣기. ptsUs = 표시 시각(마이크로초).</summary>
        public void AddFrame(SKBitmap bmp, long ptsUs)
        {
            if (_done) return;
            CopyPixels(bmp);
            ToYuv(_pixels, _w, _h, _yuv, _semiPlanar, ref _stride);
            int inIdx = _codec.DequeueInputBuffer(2000000);
            if (inIdx >= 0)
            {
                Java.Nio.ByteBuffer buf = _codec.GetInputBuffer(inIdx);
                buf.Clear();
                buf.Put(_yuv, 0, _yuv.Length);
                _codec.QueueInputBuffer(inIdx, 0, _yuv.Length, ptsUs, MediaCodecBufferFlags.None);
                _frames++;
            }
            Drain(false);
        }

        /// <summary>비트맵 픽셀을 BGRA 연속 버퍼로 복사(행 간격 고려).</summary>
        private void CopyPixels(SKBitmap bmp)
        {
            int rowBytes = bmp.RowBytes;
            int need = _w * 4;
            var span = bmp.GetPixelSpan();
            if (rowBytes == need)
            {
                span.Slice(0, need * _h).CopyTo(new Span<byte>(_pixels));
                return;
            }
            for (int y = 0; y < _h; y++)
                span.Slice(y * rowBytes, need).CopyTo(new Span<byte>(_pixels, y * need, need));
        }

        public void Finish()
        {
            if (_done) return;
            try
            {
                int inIdx = _codec.DequeueInputBuffer(1000000);
                if (inIdx >= 0)
                    _codec.QueueInputBuffer(inIdx, 0, 0, _frames * 1000000L / _fps, MediaCodecBufferFlags.EndOfStream);
                _eosQueued = true;
                Drain(true);
            }
            catch { }
            try { _codec.Stop(); } catch { }
            try { _codec.Release(); } catch { }
            try { if (_muxStarted) _muxer.Stop(); } catch { }
            try { _muxer.Release(); } catch { }
            _codec = null; _muxer = null;
            _done = true;
        }

        private void Drain(bool untilEos)
        {
            while (true)
            {
                int outIdx;
                try { outIdx = _codec.DequeueOutputBuffer(_info, untilEos ? 1000000 : 0); }
                catch { return; }

                if (outIdx == (int)MediaCodecInfoState.OutputFormatChanged)
                {
                    MediaFormat of = _codec.OutputFormat;
                    _track = _muxer.AddTrack(of);
                    _muxer.Start();
                    _muxStarted = true;
                    continue;
                }
                if (outIdx == (int)MediaCodecInfoState.OutputBuffersChanged) continue;
                if (outIdx == (int)MediaCodecInfoState.TryAgainLater)
                {
                    if (!untilEos) return;
                    continue;
                }
                if (outIdx < 0) return;

                Java.Nio.ByteBuffer ob = _codec.GetOutputBuffer(outIdx);
                bool eos = (_info.Flags & MediaCodecBufferFlags.EndOfStream) != 0;
                if (_info.Size > 0 && _muxStarted)
                {
                    ob.Position(_info.Offset);
                    ob.Limit(_info.Offset + _info.Size);
                    _muxer.WriteSampleData(_track, ob, _info);
                }
                _codec.ReleaseOutputBuffer(outIdx, false);
                if (eos) return;
                if (!untilEos) return;
            }
        }

        // ── BGRA → YUV420 ─────────────────────────────────────
        private static void ToYuv(byte[] bgra, int w, int h, byte[] yuv, bool semiPlanar, ref int stride)
        {
            if (stride < w) stride = w;
            int cw = w / 2, ch = h / 2;
            int ySize = stride * h;

            // Y
            for (int y = 0; y < h; y++)
            {
                int src = y * w * 4;
                int dst = y * stride;
                for (int x = 0; x < w; x++)
                {
                    int b = bgra[src], g = bgra[src + 1], r = bgra[src + 2];
                    int v = ((66 * r + 129 * g + 25 * b + 128) >> 8) + 16;
                    yuv[dst + x] = (byte)(v < 0 ? 0 : (v > 255 ? 255 : v));
                    src += 4;
                }
            }

            if (semiPlanar)
            {
                for (int y = 0; y < ch; y++)
                {
                    int s0 = (y * 2) * w * 4;
                    int s1 = (y * 2 + 1) * w * 4;
                    int d = ySize + y * stride;
                    for (int x = 0; x < cw; x++)
                    {
                        int i0 = s0 + x * 8, i1 = s1 + x * 8;
                        int b = (bgra[i0] + bgra[i0 + 4] + bgra[i1] + bgra[i1 + 4]) / 4;
                        int g = (bgra[i0 + 1] + bgra[i0 + 5] + bgra[i1 + 1] + bgra[i1 + 5]) / 4;
                        int r = (bgra[i0 + 2] + bgra[i0 + 6] + bgra[i1 + 2] + bgra[i1 + 6]) / 4;
                        int u = ((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128;
                        int vv = ((112 * r - 94 * g - 18 * b + 128) >> 8) + 128;
                        yuv[d + x * 2] = (byte)Clamp(u);
                        yuv[d + x * 2 + 1] = (byte)Clamp(vv);
                    }
                }
            }
            else
            {
                int uOff = ySize, vOff = ySize + cw * ch;
                for (int y = 0; y < ch; y++)
                {
                    int s0 = (y * 2) * w * 4;
                    int s1 = (y * 2 + 1) * w * 4;
                    for (int x = 0; x < cw; x++)
                    {
                        int i0 = s0 + x * 8, i1 = s1 + x * 8;
                        int b = (bgra[i0] + bgra[i0 + 4] + bgra[i1] + bgra[i1 + 4]) / 4;
                        int g = (bgra[i0 + 1] + bgra[i0 + 5] + bgra[i1 + 1] + bgra[i1 + 5]) / 4;
                        int r = (bgra[i0 + 2] + bgra[i0 + 6] + bgra[i1 + 2] + bgra[i1 + 6]) / 4;
                        int u = ((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128;
                        int vv = ((112 * r - 94 * g - 18 * b + 128) >> 8) + 128;
                        yuv[uOff + y * cw + x] = (byte)Clamp(u);
                        yuv[vOff + y * cw + x] = (byte)Clamp(vv);
                    }
                }
            }
        }

        private static int Clamp(int v) { return v < 0 ? 0 : (v > 255 ? 255 : v); }

        public void Dispose() { Finish(); }
    }
}
