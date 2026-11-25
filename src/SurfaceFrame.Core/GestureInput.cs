namespace SurfaceFrame;

public enum Page
{
    Photos,
    Ha,
    Info
}

public enum UserAction
{
    None,
    Tap,
    EnterHa,
    BackToPhotos
}

public enum MaintCommand
{
    None,
    TogglePage,
    Exit,
    Wake
}

public readonly record struct PointerOutcome(Page? GoPage, int PhotoDelta, string? Control, bool GuardWake);

public static class PointerPolicy
{
    public static PointerOutcome Interpret(UserAction action, double x, double width, Page page, bool shield, string? hitTag)
    {
        if (action == UserAction.Tap && hitTag != null && hitTag.StartsWith("demo:", StringComparison.Ordinal))
            return new PointerOutcome(null, 0, hitTag, false);

        if (shield)
            return new PointerOutcome(null, 0, null, action != UserAction.None);

        switch (action)
        {
            case UserAction.EnterHa when page == Page.Photos:
                return new PointerOutcome(Page.Ha, 0, null, false);
            case UserAction.EnterHa when page == Page.Info:
                return new PointerOutcome(Page.Photos, 0, null, false);
            case UserAction.BackToPhotos when page == Page.Ha:
                return new PointerOutcome(Page.Photos, 0, null, false);
            case UserAction.BackToPhotos when page == Page.Photos:
                return new PointerOutcome(Page.Info, 0, null, false);
            case UserAction.Tap when page is Page.Ha or Page.Info:
                return new PointerOutcome(null, 0, IsCard(hitTag) ? hitTag : null, false);
            case UserAction.Tap:
                return new PointerOutcome(null, x < width / 2.0 ? -1 : 1, null, false);
            default:
                return new PointerOutcome(null, 0, null, false);
        }
    }

    public static bool IsCard(string? tag)
        => tag != null && (
            tag.StartsWith("on:", StringComparison.Ordinal)
            || tag.StartsWith("off:", StringComparison.Ordinal)
            || tag.StartsWith("retry:", StringComparison.Ordinal)
            || tag.StartsWith("toggle:", StringComparison.Ordinal)
            || tag.StartsWith("open:", StringComparison.Ordinal)
            || tag.StartsWith("close:", StringComparison.Ordinal)
            || tag.StartsWith("scene:", StringComparison.Ordinal)
            || tag.StartsWith("nav:", StringComparison.Ordinal)
            || tag.StartsWith("album:", StringComparison.Ordinal));

    public static bool TryParseScene(string tag, out string sceneId)
    {
        if (tag.StartsWith("scene:", StringComparison.Ordinal) && tag.Length > 6)
        {
            sceneId = tag[6..];
            return true;
        }
        sceneId = "";
        return false;
    }

    public static bool TryParseCard(string tag, out string entityId, out bool turnOn)
    {
        if (tag.StartsWith("on:", StringComparison.Ordinal))
        {
            turnOn = true;
            entityId = tag[3..];
            return entityId.Length > 0;
        }
        if (tag.StartsWith("off:", StringComparison.Ordinal))
        {
            turnOn = false;
            entityId = tag[4..];
            return entityId.Length > 0;
        }
        turnOn = false;
        entityId = "";
        return false;
    }

    public static bool TryParseToggle(string tag, out string entityId)
    {
        entityId = tag.StartsWith("toggle:", StringComparison.Ordinal) ? tag[7..] : "";
        return entityId.Length > 0;
    }

    public static bool TryParseCover(string tag, out string entityId, out bool open)
    {
        if (tag.StartsWith("open:", StringComparison.Ordinal))
        {
            open = true;
            entityId = tag[5..];
            return entityId.Length > 0;
        }
        if (tag.StartsWith("close:", StringComparison.Ordinal))
        {
            open = false;
            entityId = tag[6..];
            return entityId.Length > 0;
        }
        open = false;
        entityId = "";
        return false;
    }

    public static bool TryParseNav(string tag, out Page target)
    {
        target = Page.Photos;
        if (!tag.StartsWith("nav:", StringComparison.Ordinal))
            return false;
        switch (tag[4..])
        {
            case "photos":
                target = Page.Photos;
                return true;
            case "ha":
                target = Page.Ha;
                return true;
            case "info":
                target = Page.Info;
                return true;
            default:
                return false;
        }
    }

    public static bool TryParseAlbum(string tag, out string action)
    {
        if (tag.StartsWith("album:", StringComparison.Ordinal))
        {
            action = tag[6..];
            return action.Length > 0;
        }
        action = "";
        return false;
    }

    public static bool TryParseRetry(string tag, out string entityId)
    {
        entityId = tag.StartsWith("retry:", StringComparison.Ordinal) ? tag[6..] : "";
        return entityId.Length > 0;
    }

    public static bool TryParseDemo(string tag, out string action)
    {
        if (tag.StartsWith("demo:", StringComparison.Ordinal))
        {
            action = tag[5..];
            return action.Length > 0;
        }
        action = "";
        return false;
    }
}

public static class MaintKeys
{
    public static MaintCommand Map(bool ctrl, bool shift, string key)
    {
        if (ctrl && shift && string.Equals(key, "X", StringComparison.OrdinalIgnoreCase))
            return MaintCommand.Exit;
        if (ctrl && shift && string.Equals(key, "M", StringComparison.OrdinalIgnoreCase))
            return MaintCommand.Wake;
        if (string.Equals(key, "F12", StringComparison.OrdinalIgnoreCase))
            return MaintCommand.TogglePage;
        return MaintCommand.None;
    }
}

public sealed class IdleReturn
{
    DateTime _last;
    bool _started;

    public void Touch(DateTime now)
    {
        _last = now;
        _started = true;
    }

    public bool Due(DateTime now, bool onHaPage)
    {
        if (!onHaPage || !_started)
            return false;
        return now - _last >= FrameTiming.HaIdleReturn;
    }
}

public sealed class GestureRecognizer
{
    const double MinSwipeDistance = 100;
    const double MaxVerticalDrift = 60;
    const double MaxTapSlop = 28;
    static readonly TimeSpan MaxTapDuration = TimeSpan.FromMilliseconds(550);
    static readonly TimeSpan MaxSwipeDuration = TimeSpan.FromMilliseconds(900);
    static readonly TimeSpan ContactJoinWindow = TimeSpan.FromMilliseconds(180);
    static readonly TimeSpan ContactReleaseWindow = TimeSpan.FromMilliseconds(240);

    sealed class Contact(double startX, double startY)
    {
        public double Dx { get; private set; }
        public double Dy { get; private set; }
        public double MaxDistanceSquared { get; private set; }
        public double VerticalDrift { get; private set; }

        public void Move(double x, double y)
        {
            Dx = x - startX;
            Dy = y - startY;
            MaxDistanceSquared = Math.Max(MaxDistanceSquared, Dx * Dx + Dy * Dy);
            VerticalDrift = Math.Max(VerticalDrift, Math.Abs(Dy));
        }

        public int SwipeDirection()
        {
            var x = Math.Abs(Dx);
            return x >= MinSwipeDistance && VerticalDrift <= MaxVerticalDrift && x >= 2 * Math.Abs(Dy)
                ? Math.Sign(Dx)
                : 0;
        }
    }

    readonly Dictionary<int, Contact> _active = new();
    readonly List<Contact> _released = new(3);
    DateTime _started;
    DateTime _firstRelease;
    bool _invalid;

    public bool Suppressing => _invalid || _active.Count + _released.Count > 1;

    public void TouchDown(int id, double x, double y, DateTime now)
    {
        if (_active.Count == 0)
            _started = now;
        if (!_active.TryAdd(id, new Contact(x, y)))
            return;
        if (_released.Count > 0 || _active.Count > 3 || now - _started > ContactJoinWindow)
            _invalid = true;
    }

    public void TouchMove(int id, double x, double y)
    {
        if (_active.TryGetValue(id, out var contact))
            contact.Move(x, y);
    }

    public void CancelContact(int id)
    {
        if (!_active.Remove(id))
            return;
        _invalid = true;
        if (_active.Count == 0)
            Reset();
    }

    public void Invalidate() => _invalid = _active.Count != 0;

    public UserAction TouchUp(int id, double x, double y, DateTime now)
    {
        if (!_active.Remove(id, out var contact))
            return UserAction.None;

        contact.Move(x, y);
        if (_released.Count == 0)
            _firstRelease = now;
        if (_released.Count < 3)
            _released.Add(contact);
        if (_active.Count > 0)
            return UserAction.None;

        var action = Classify(now);
        Reset();
        return action;
    }

    UserAction Classify(DateTime now)
    {
        if (_invalid)
            return UserAction.None;
        var duration = now - _started;
        if (_released.Count == 1 && duration <= MaxTapDuration
            && _released[0].MaxDistanceSquared <= MaxTapSlop * MaxTapSlop)
            return UserAction.Tap;
        if (_released.Count != 3 || duration > MaxSwipeDuration || now - _firstRelease > ContactReleaseWindow)
            return UserAction.None;
        var direction = _released[0].SwipeDirection();
        if (direction == 0 || _released.Any(contact => contact.SwipeDirection() != direction))
            return UserAction.None;
        return direction < 0 ? UserAction.EnterHa : UserAction.BackToPhotos;
    }

    void Reset()
    {
        _released.Clear();
        _invalid = false;
    }
}
