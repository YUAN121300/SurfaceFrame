using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SurfaceFrame;

static unsafe class LivePhotoPlayer
{
    const uint MF_VERSION = 0x00020070;
    const uint MFSTARTUP_NOSOCKET = 1;
    const uint MF_SOURCE_READER_ALL_STREAMS = 0xFFFFFFFE;
    const uint MF_SOURCE_READER_FIRST_VIDEO_STREAM = 0xFFFFFFFC;
    const uint MF_SOURCE_READERF_ERROR = 0x1;
    const uint MF_SOURCE_READERF_ENDOFSTREAM = 0x2;
    const uint MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED = 0x20;

    static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00aa00389b71");
    static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    static readonly Guid MFVideoFormat_RGB32 = new("00000016-0000-0010-8000-00aa00389b71");
    static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    static readonly Guid MF_MT_DEFAULT_STRIDE = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    static readonly Guid MF_MT_VIDEO_ROTATION = new("c380465d-2271-428c-9b83-ecea3b4a85c1");
    static readonly Guid MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING = new("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");
    static readonly Guid MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS = new("a634a91c-822b-41b9-a494-4de4643612b0");

    static int _started;

    public static void EnsureStartup()
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) == 0)
        {
            try
            {
                var hr = MFStartup(MF_VERSION, MFSTARTUP_NOSOCKET);
                if (hr < 0)
                    Log.Info($"livephoto startup 0x{hr:X8}");
            }
            catch (Exception ex)
            {
                Log.Info("livephoto startup " + ex.GetType().Name);
            }
        }
    }

    public static void Shutdown()
    {
        if (Interlocked.Exchange(ref _started, 0) == 1)
        {
            try { MFShutdown(); } catch { }
        }
    }

    public static Task PlayAsync(
        string videoPath,
        Dispatcher dispatcher,
        System.Windows.Controls.Image targetImage,
        CancellationToken ct)
    {
        return Task.Run(() => PlayWorker(videoPath, dispatcher, targetImage, ct), ct);
    }

    static void PlayWorker(
        string videoPath,
        Dispatcher dispatcher,
        System.Windows.Controls.Image targetImage,
        CancellationToken ct)
    {
        EnsureStartup();
        IntPtr attrs = IntPtr.Zero;
        IntPtr reader = IntPtr.Zero;
        IntPtr outType = IntPtr.Zero;
        try
        {
            var hr = MFCreateAttributes(out attrs, 2);
            if (hr < 0) { Log.Info($"livephoto err 0x{hr:X8} attrs"); return; }

            var videoProc = MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING;
            AttrSetUINT32(attrs, ref videoProc, 1);
            var hwTrans = MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS;
            AttrSetUINT32(attrs, ref hwTrans, 1);

            hr = MFCreateSourceReaderFromURL(videoPath, attrs, out reader);
            if (hr < 0 || reader == IntPtr.Zero)
            {
                Log.Info($"livephoto err 0x{hr:X8} reader");
                return;
            }

            // Select ONLY first video stream; audio stays completely disabled (muted).
            hr = ReaderSetStreamSelection(reader, MF_SOURCE_READER_ALL_STREAMS, 0);
            if (hr < 0) { Log.Info($"livephoto err 0x{hr:X8} deselect"); return; }
            hr = ReaderSetStreamSelection(reader, MF_SOURCE_READER_FIRST_VIDEO_STREAM, 1);
            if (hr < 0) { Log.Info($"livephoto err 0x{hr:X8} select"); return; }

            var rotation = ReadRotation(reader);

            hr = MFCreateMediaType(out outType);
            if (hr < 0 || outType == IntPtr.Zero) { Log.Info($"livephoto err 0x{hr:X8} mtype"); return; }
            var majorKey = MF_MT_MAJOR_TYPE;
            var majorVal = MFMediaType_Video;
            AttrSetGUID(outType, ref majorKey, ref majorVal);
            var subKey = MF_MT_SUBTYPE;
            var subVal = MFVideoFormat_RGB32;
            AttrSetGUID(outType, ref subKey, ref subVal);

            hr = ReaderSetCurrentMediaType(reader, MF_SOURCE_READER_FIRST_VIDEO_STREAM, IntPtr.Zero, outType);
            if (hr < 0) { Log.Info($"livephoto err 0x{hr:X8} settype"); return; }

            if (!ReadGeometry(reader, out var width, out var height, out var stride))
            {
                Log.Info("livephoto err geometry");
                return;
            }
            if (rotation == 0)
                rotation = ReadRotation(reader);

            var frameBytes = width * 4;
            var bufferSize = frameBytes * height;
            var managed = new byte[bufferSize];
            WriteableBitmap? bitmap = null;

            dispatcher.Invoke(() =>
            {
                if (ct.IsCancellationRequested)
                    return;
                bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr32, null);
                targetImage.LayoutTransform = rotation != 0 ? new RotateTransform(rotation) : Transform.Identity;
                targetImage.Source = bitmap;
                targetImage.Visibility = Visibility.Visible;
            }, DispatcherPriority.Send, ct);

            if (bitmap == null || ct.IsCancellationRequested)
                return;

            var sw = Stopwatch.StartNew();
            long? firstTs = null;
            var frames = 0;

            while (!ct.IsCancellationRequested)
            {
                hr = ReaderReadSample(reader, MF_SOURCE_READER_FIRST_VIDEO_STREAM, 0, out _, out var flags, out var ts, out var sample);
                if (hr < 0 || (flags & MF_SOURCE_READERF_ERROR) != 0)
                {
                    if (sample != IntPtr.Zero) ReleaseCom(sample);
                    Log.Info($"livephoto err 0x{hr:X8} read");
                    break;
                }
                if ((flags & MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED) != 0)
                {
                    if (ReadGeometry(reader, out var newW, out var newH, out var newStride)
                        && (newW != width || newH != height || newStride != stride))
                    {
                        width = newW;
                        height = newH;
                        stride = newStride;
                        frameBytes = width * 4;
                        bufferSize = frameBytes * height;
                        managed = new byte[bufferSize];
                        dispatcher.Invoke(() =>
                        {
                            if (ct.IsCancellationRequested) return;
                            bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr32, null);
                            targetImage.Source = bitmap;
                        }, DispatcherPriority.Send, ct);
                    }
                }
                if ((flags & MF_SOURCE_READERF_ENDOFSTREAM) != 0)
                {
                    if (sample != IntPtr.Zero) ReleaseCom(sample);
                    break;
                }
                if (sample == IntPtr.Zero)
                    continue;

                IntPtr mfBuf = IntPtr.Zero;
                var locked = false;
                try
                {
                    hr = SampleConvertToContiguousBuffer(sample, out mfBuf);
                    if (hr < 0 || mfBuf == IntPtr.Zero)
                        continue;
                    hr = BufferLock(mfBuf, out var dataPtr, out _, out var curLen);
                    if (hr < 0 || dataPtr == IntPtr.Zero)
                        continue;
                    locked = true;

                    var absStride = Math.Abs(stride);
                    if (absStride < frameBytes || curLen < (uint)(absStride * height))
                        continue;

                    var srcBase = (byte*)dataPtr;
                    for (var y = 0; y < height; y++)
                    {
                        var dstRow = stride < 0 ? (height - 1 - y) : y;
                        Marshal.Copy((IntPtr)(srcBase + y * absStride), managed, dstRow * frameBytes, frameBytes);
                    }
                }
                finally
                {
                    if (locked && mfBuf != IntPtr.Zero) BufferUnlock(mfBuf);
                    if (mfBuf != IntPtr.Zero) ReleaseCom(mfBuf);
                    ReleaseCom(sample);
                }

                firstTs ??= ts;
                var relMs = (ts - firstTs.Value) / 10_000.0;
                if (relMs > 8000)
                    break;

                var waitMs = relMs - sw.Elapsed.TotalMilliseconds;
                if (waitMs > 2)
                {
                    var sleepMs = (int)Math.Min(waitMs, 250);
                    if (ct.WaitHandle.WaitOne(sleepMs))
                        break;
                }

                if (ct.IsCancellationRequested)
                    break;

                var captureW = width;
                var captureH = height;
                var captureStride = frameBytes;
                dispatcher.Invoke(() =>
                {
                    if (ct.IsCancellationRequested || bitmap == null)
                        return;
                    bitmap.WritePixels(new Int32Rect(0, 0, captureW, captureH), managed, captureStride, 0);
                }, DispatcherPriority.Render, ct);

                frames++;
            }

            if (frames > 0 && !ct.IsCancellationRequested)
                Log.Info($"livephoto played {frames}f {width}x{height} rot={rotation}");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Info("livephoto " + ex.GetType().Name);
        }
        finally
        {
            if (outType != IntPtr.Zero) ReleaseCom(outType);
            if (reader != IntPtr.Zero) ReleaseCom(reader);
            if (attrs != IntPtr.Zero) ReleaseCom(attrs);
        }
    }

    static int ReadRotation(IntPtr reader)
    {
        IntPtr nativeType = IntPtr.Zero;
        try
        {
            if (ReaderGetNativeMediaType(reader, MF_SOURCE_READER_FIRST_VIDEO_STREAM, 0, out nativeType) >= 0
                && nativeType != IntPtr.Zero)
            {
                var rotKey = MF_MT_VIDEO_ROTATION;
                if (AttrGetUINT32(nativeType, ref rotKey, out var rot) >= 0)
                    return (int)(rot % 360);
            }
        }
        finally
        {
            if (nativeType != IntPtr.Zero) ReleaseCom(nativeType);
        }
        return 0;
    }

    static bool ReadGeometry(IntPtr reader, out int width, out int height, out int stride)
    {
        width = 0;
        height = 0;
        stride = 0;
        IntPtr curType = IntPtr.Zero;
        try
        {
            if (ReaderGetCurrentMediaType(reader, MF_SOURCE_READER_FIRST_VIDEO_STREAM, out curType) < 0
                || curType == IntPtr.Zero)
                return false;
            var sizeKey = MF_MT_FRAME_SIZE;
            if (AttrGetUINT64(curType, ref sizeKey, out var packed) < 0)
                return false;
            width = (int)(packed >> 32);
            height = (int)(packed & 0xFFFFFFFF);
            if (width <= 0 || height <= 0 || width > 8192 || height > 8192)
                return false;
            var strideKey = MF_MT_DEFAULT_STRIDE;
            stride = AttrGetUINT32(curType, ref strideKey, out var uStride) >= 0
                ? unchecked((int)uStride)
                : width * 4;
            return true;
        }
        finally
        {
            if (curType != IntPtr.Zero) ReleaseCom(curType);
        }
    }

    static void** VTable(IntPtr obj) => *(void***)obj;

    static void ReleaseCom(IntPtr obj)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint>)VTable(obj)[2];
        fn(obj);
    }

    static int AttrGetUINT32(IntPtr obj, ref Guid key, out uint value)
    {
        fixed (Guid* pKey = &key)
        fixed (uint* pVal = &value)
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint*, int>)VTable(obj)[7];
            return fn(obj, pKey, pVal);
        }
    }

    static int AttrGetUINT64(IntPtr obj, ref Guid key, out ulong value)
    {
        fixed (Guid* pKey = &key)
        fixed (ulong* pVal = &value)
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, ulong*, int>)VTable(obj)[8];
            return fn(obj, pKey, pVal);
        }
    }

    static int AttrSetUINT32(IntPtr obj, ref Guid key, uint value)
    {
        fixed (Guid* pKey = &key)
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint, int>)VTable(obj)[21];
            return fn(obj, pKey, value);
        }
    }

    static int AttrSetGUID(IntPtr obj, ref Guid key, ref Guid value)
    {
        fixed (Guid* pKey = &key)
        fixed (Guid* pVal = &value)
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, Guid*, int>)VTable(obj)[24];
            return fn(obj, pKey, pVal);
        }
    }

    static int ReaderSetStreamSelection(IntPtr reader, uint streamIndex, int selected)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint, int, int>)VTable(reader)[4];
        return fn(reader, streamIndex, selected);
    }

    static int ReaderGetNativeMediaType(IntPtr reader, uint streamIndex, uint mediaTypeIndex, out IntPtr ppMediaType)
    {
        fixed (IntPtr* pType = &ppMediaType)
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, int>)VTable(reader)[5];
            return fn(reader, streamIndex, mediaTypeIndex, pType);
        }
    }

    static int ReaderGetCurrentMediaType(IntPtr reader, uint streamIndex, out IntPtr ppMediaType)
    {
        fixed (IntPtr* pType = &ppMediaType)
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)VTable(reader)[6];
            return fn(reader, streamIndex, pType);
        }
    }

    static int ReaderSetCurrentMediaType(IntPtr reader, uint streamIndex, IntPtr reserved, IntPtr mediaType)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, int>)VTable(reader)[7];
        return fn(reader, streamIndex, reserved, mediaType);
    }

    static int ReaderReadSample(
        IntPtr reader,
        uint streamIndex,
        uint controlFlags,
        out uint actualStreamIndex,
        out uint streamFlags,
        out long timestamp,
        out IntPtr sample)
    {
        fixed (uint* pIdx = &actualStreamIndex)
        fixed (uint* pFlags = &streamFlags)
        fixed (long* pTs = &timestamp)
        fixed (IntPtr* pSample = &sample)
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint*, uint*, long*, IntPtr*, int>)VTable(reader)[9];
            return fn(reader, streamIndex, controlFlags, pIdx, pFlags, pTs, pSample);
        }
    }

    static int SampleConvertToContiguousBuffer(IntPtr sample, out IntPtr buffer)
    {
        fixed (IntPtr* pBuf = &buffer)
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)VTable(sample)[41];
            return fn(sample, pBuf);
        }
    }

    static int BufferLock(IntPtr buffer, out IntPtr data, out uint maxLen, out uint curLen)
    {
        fixed (IntPtr* pData = &data)
        fixed (uint* pMax = &maxLen)
        fixed (uint* pCur = &curLen)
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, uint*, uint*, int>)VTable(buffer)[3];
            return fn(buffer, pData, pMax, pCur);
        }
    }

    static int BufferUnlock(IntPtr buffer)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, int>)VTable(buffer)[4];
        return fn(buffer);
    }

    [DllImport("mfplat.dll")]
    static extern int MFStartup(uint version, uint dwFlags);

    [DllImport("mfplat.dll")]
    static extern int MFShutdown();

    [DllImport("mfplat.dll")]
    static extern int MFCreateAttributes(out IntPtr ppMFAttributes, uint cInitialSize);

    [DllImport("mfplat.dll")]
    static extern int MFCreateMediaType(out IntPtr ppMFType);

    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)]
    static extern int MFCreateSourceReaderFromURL(string pwszURL, IntPtr pAttributes, out IntPtr ppSourceReader);
}
