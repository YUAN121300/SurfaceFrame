namespace SurfaceFrame;

public readonly record struct LayoutBox(double X, double Y, double Width, double Height);

public static class CenterFillLayout
{
    public static LayoutBox Compute(double containerW, double containerH, double contentW, double contentH, bool crop)
    {
        if (containerW <= 0 || containerH <= 0 || contentW <= 0 || contentH <= 0)
            return new LayoutBox(0, 0, Math.Max(0, containerW), Math.Max(0, containerH));

        var scale = crop
            ? Math.Max(containerW / contentW, containerH / contentH)
            : Math.Min(containerW / contentW, containerH / contentH);
        var rw = contentW * scale;
        var rh = contentH * scale;
        return new LayoutBox((containerW - rw) / 2.0, (containerH - rh) / 2.0, rw, rh);
    }
}

public sealed record PhotoSlide(string Primary, string? Secondary = null);

public static class ImageDimensions
{
    public static bool IsPortrait(string path)
        => TryRead(path, out var width, out var height) && height > width;

    public static bool TryRead(string path, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return false;
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[24];
            var read = stream.Read(header);
            if (read >= 24 && IsPng(header))
            {
                width = ReadInt32BigEndian(header.Slice(16, 4));
                height = ReadInt32BigEndian(header.Slice(20, 4));
                return width > 0 && height > 0;
            }
            if (read >= 2 && header[0] == 0xFF && header[1] == 0xD8)
            {
                stream.Position = 2;
                return TryReadJpeg(stream, out width, out height);
            }
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    static bool IsPng(ReadOnlySpan<byte> b)
        => b[0] == 137 && b[1] == 80 && b[2] == 78 && b[3] == 71
        && b[4] == 13 && b[5] == 10 && b[6] == 26 && b[7] == 10
        && b[12] == (byte)'I' && b[13] == (byte)'H' && b[14] == (byte)'D' && b[15] == (byte)'R';

    static int ReadInt32BigEndian(ReadOnlySpan<byte> b)
        => (b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3];

    static bool TryReadJpeg(Stream stream, out int width, out int height)
    {
        width = 0;
        height = 0;
        Span<byte> lenBuf = stackalloc byte[2];
        Span<byte> sofBuf = stackalloc byte[5];
        while (true)
        {
            var next = stream.ReadByte();
            if (next < 0)
                return false;
            if (next != 0xFF)
                continue;
            int marker;
            do
            {
                marker = stream.ReadByte();
                if (marker < 0)
                    return false;
            } while (marker == 0xFF);

            if (marker == 0x00 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD8))
                continue;
            if (marker == 0xD9 || marker == 0xDA)
                return false;

            if (stream.Read(lenBuf) != 2)
                return false;
            var segLen = (lenBuf[0] << 8) | lenBuf[1];
            if (segLen < 2)
                return false;

            if (IsSofMarker(marker))
            {
                if (segLen < 7 || stream.Read(sofBuf) != 5)
                    return false;
                height = (sofBuf[1] << 8) | sofBuf[2];
                width = (sofBuf[3] << 8) | sofBuf[4];
                return width > 0 && height > 0;
            }

            if (stream.CanSeek)
                stream.Seek(segLen - 2, SeekOrigin.Current);
            else
                return false;
        }
    }

    static bool IsSofMarker(int m)
        => m is (>= 0xC0 and <= 0xC3) or (>= 0xC5 and <= 0xC7) or (>= 0xC9 and <= 0xCB) or (>= 0xCD and <= 0xCF);
}

public sealed class SlideDeck
{
    readonly List<PhotoSlide> _slides = new();
    readonly HashSet<PhotoSlide> _bad = new();
    int _index = -1;

    public PhotoSlide? Current { get; private set; }
    public int Count => _slides.Count;
    public int CurrentIndex => _index;

    public static List<T> Shuffle<T>(IReadOnlyList<T> items, Random? rng = null)
    {
        var r = rng ?? Random.Shared;
        var list = new List<T>(items);
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = r.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
        return list;
    }
    public bool IsEmpty => _slides.Count == 0 || _slides.All(_bad.Contains);

    public static IReadOnlyList<PhotoSlide> BuildSlides(
        IReadOnlyList<string> files,
        Func<string, bool>? isPortrait = null)
    {
        var portraitCheck = isPortrait ?? ImageDimensions.IsPortrait;
        var slides = new List<PhotoSlide>(files.Count);
        string? pendingPortrait = null;
        string? firstPortrait = null;

        foreach (var file in files)
        {
            if (portraitCheck(file))
            {
                firstPortrait ??= file;
                if (pendingPortrait == null)
                {
                    pendingPortrait = file;
                }
                else
                {
                    slides.Add(new PhotoSlide(pendingPortrait, file));
                    pendingPortrait = null;
                }
            }
            else
            {
                slides.Add(new PhotoSlide(file));
            }
        }

        if (pendingPortrait != null)
        {
            var partner = firstPortrait != null && !string.Equals(firstPortrait, pendingPortrait, StringComparison.OrdinalIgnoreCase)
                ? firstPortrait
                : null;
            slides.Add(new PhotoSlide(pendingPortrait, partner));
        }

        return slides;
    }

    public void Reset(IEnumerable<PhotoSlide> slides)
    {
        _slides.Clear();
        _slides.AddRange(slides);
        _bad.Clear();
        _index = -1;
        Current = null;
    }

    public PhotoSlide? Go(int direction, Func<PhotoSlide, bool>? canShow = null)
    {
        if (direction == 0)
            direction = 1;
        var count = _slides.Count;
        if (count == 0)
        {
            Current = null;
            return null;
        }

        var index = _index;
        for (var tried = 0; tried < count; tried++)
        {
            index = index < 0
                ? (direction > 0 ? 0 : count - 1)
                : Mod(index + direction, count);
            var slide = _slides[index];
            if (_bad.Contains(slide))
                continue;
            var ok = true;
            if (canShow != null)
            {
                try { ok = canShow(slide); }
                catch { ok = false; }
            }
            if (!ok)
            {
                _bad.Add(slide);
                continue;
            }
            _index = index;
            Current = slide;
            return slide;
        }

        _index = -1;
        Current = null;
        return null;
    }

    static int Mod(int value, int count)
    {
        var mod = value % count;
        return mod < 0 ? mod + count : mod;
    }
}

public sealed class PhotoDeck
{
    static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png"
    };

    readonly List<string> _files = new();
    readonly HashSet<string> _bad = new(StringComparer.OrdinalIgnoreCase);
    int _index = -1;

    public string? Current { get; private set; }
    public int Count => _files.Count;
    public bool IsEmpty => _files.Count == 0 || _files.All(_bad.Contains);

    public static IReadOnlyList<string> List(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return Array.Empty<string>();
        try
        {
            return Directory.EnumerateFiles(folder)
                .Where(path => Extensions.Contains(Path.GetExtension(path)))
                .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    public void Reset(IEnumerable<string> files)
    {
        _files.Clear();
        _files.AddRange(files);
        _bad.Clear();
        _index = -1;
        Current = null;
    }

    public string? Go(int direction, Func<string, bool>? canShow = null)
    {
        if (direction == 0)
            direction = 1;
        var count = _files.Count;
        if (count == 0)
        {
            Current = null;
            return null;
        }

        var index = _index;
        for (var tried = 0; tried < count; tried++)
        {
            index = index < 0
                ? (direction > 0 ? 0 : count - 1)
                : Mod(index + direction, count);
            var path = _files[index];
            if (_bad.Contains(path))
                continue;
            var ok = true;
            if (canShow != null)
            {
                try
                {
                    ok = canShow(path);
                }
                catch
                {
                    ok = false;
                }
            }
            if (!ok)
            {
                _bad.Add(path);
                continue;
            }
            _index = index;
            Current = path;
            return path;
        }

        _index = -1;
        Current = null;
        return null;
    }

    static int Mod(int value, int count)
    {
        var mod = value % count;
        return mod < 0 ? mod + count : mod;
    }
}

public sealed class SlideClock
{
    readonly TimeSpan _interval;
    DateTime _due;
    bool _armed;

    public SlideClock(TimeSpan interval) => _interval = interval;

    public void Arm(DateTime now)
    {
        _due = now + _interval;
        _armed = true;
    }

    public bool Due(DateTime now)
    {
        if (!_armed)
        {
            Arm(now);
            return false;
        }
        if (now < _due)
            return false;
        _due = now + _interval;
        return true;
    }
}
