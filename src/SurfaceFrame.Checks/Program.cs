using System.Net.Http;
using System.Text;
using System.Text.Json;
using SurfaceFrame;

static class Program
{
    static readonly Check C = new();

    static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        C.Run("spec", SpecConstants);
        C.Run("gesture", Gestures);
        C.Run("policy", Policy);
        C.Run("photos", Photos);
        C.Run("presence", Presence);
        C.Run("ha", HomeAssistant);
        C.Run("demo-pace", DemoPace);
        C.Run("presence-reconnect", PresenceReconnect);
        C.Run("config", Config);
        C.Run("immich", Immich);
        C.Run("v5-homekit-utility", V5HomeKitUtility);
        C.Run("v6-random-pin", V6RandomAndPin);
        C.Run("v7-photo-meta", V7PhotoMeta);
        C.Run("project", ProjectShape);
        Console.WriteLine(C.Failed == 0 ? "全部通过" : "失败 " + C.Failed + " 项");
        return C.Failed == 0 ? 0 : 1;
    }

    static void SpecConstants()
    {
        C.Equal(FrameTiming.CommandTimeout, TimeSpan.FromSeconds(5), "spec.command-timeout-5s");
        C.Equal(FrameTiming.AwayBeforeSleep, TimeSpan.FromMinutes(10), "spec.away-10min");
        C.Equal(FrameTiming.HaIdleReturn, TimeSpan.FromMinutes(2), "spec.ha-idle-2min");
        C.Equal(FrameTiming.SlideshowDefault, TimeSpan.FromSeconds(30), "spec.slideshow-30s");
        C.Equal(ReconnectBackoff.Next(1000), 2000, "reconnect.1s");
        C.Equal(ReconnectBackoff.Next(8000), 15000, "reconnect.cap");
        var idle = new IdleReturn();
        var t = T0();
        idle.Touch(t);
        C.True(!idle.Due(t.AddMinutes(2).AddSeconds(-1), true), "idle.before-2min");
        C.True(idle.Due(t.AddMinutes(2), true), "idle.at-2min");
        C.True(!idle.Due(t.AddMinutes(5), false), "idle.photos-stay");
    }

    static void Gestures()
    {
        var t = T0();
        C.Equal(Swipe(new GestureRecognizer(), t, -180, -175, -190), UserAction.EnterHa, "gesture.three-left");
        C.Equal(Swipe(new GestureRecognizer(), t, 180, 175, 190), UserAction.BackToPhotos, "gesture.three-right");
        C.Equal(Swipe(new GestureRecognizer(), t, -180, -175), UserAction.None, "gesture.two");
        C.Equal(Swipe(new GestureRecognizer(), t, -180, -175, -190, -160), UserAction.None, "gesture.four");
        C.Equal(Swipe(new GestureRecognizer(), t, -180, 175, -190), UserAction.None, "gesture.mixed-direction");
        C.Equal(Swipe(new GestureRecognizer(), t, -180, -175, -25), UserAction.None, "gesture.short-finger");

        var singles = new GestureRecognizer();
        for (var i = 0; i < 3; i++)
            C.Equal(Swipe(singles, t.AddMilliseconds(250 * i), -180), UserAction.None, "gesture.sequential-one-finger");

        var late = new GestureRecognizer();
        late.TouchDown(1, 500, 40, t);
        late.TouchDown(2, 540, 40, t.AddMilliseconds(30));
        late.TouchDown(3, 580, 40, t.AddMilliseconds(240));
        late.TouchUp(1, 300, 40, t.AddMilliseconds(350));
        late.TouchUp(2, 340, 40, t.AddMilliseconds(360));
        C.Equal(late.TouchUp(3, 380, 40, t.AddMilliseconds(370)), UserAction.None, "gesture.late-third-finger");

        var tap = new GestureRecognizer();
        tap.TouchDown(1, 30, 40, t);
        C.Equal(tap.TouchUp(1, 34, 42, t.AddMilliseconds(90)), UserAction.Tap, "gesture.brief-tap");
        tap.TouchDown(1, 30, 40, t);
        C.Equal(tap.TouchUp(1, 34, 42, t.AddSeconds(1)), UserAction.None, "gesture.long-press");
        tap.TouchDown(1, 30, 40, t);
        tap.TouchMove(1, 55, 100);
        C.Equal(tap.TouchUp(1, 32, 42, t.AddMilliseconds(180)), UserAction.None, "gesture.drag-returning-to-origin");
        tap.TouchDown(1, 30, 40, t);
        tap.CancelContact(1);
        C.Equal(tap.TouchUp(1, 32, 42, t.AddMilliseconds(180)), UserAction.None, "gesture.lost-capture");
    }

    static void Policy()
    {
        var left = PointerPolicy.Interpret(UserAction.Tap, 100, 1000, Page.Photos, false, null);
        C.Equal(left.PhotoDelta, -1, "policy.tap-left-previous");
        C.Equal(left.Control, null, "policy.tap-left-no-control");
        var right = PointerPolicy.Interpret(UserAction.Tap, 800, 1000, Page.Photos, false, null);
        C.Equal(right.PhotoDelta, 1, "policy.tap-right-next");

        var swipe = PointerPolicy.Interpret(UserAction.None, 10, 1000, Page.Photos, false, null);
        C.Equal(swipe.PhotoDelta, 0, "policy.single-motion-does-not-advance");
        var enter = PointerPolicy.Interpret(UserAction.EnterHa, 10, 1000, Page.Photos, false, null);
        C.Equal(enter.GoPage, Page.Ha, "policy.enter-ha");
        C.Equal(enter.PhotoDelta, 0, "policy.enter-does-not-advance-photo");
        C.Equal(PointerPolicy.Interpret(UserAction.BackToPhotos, 10, 1000, Page.Ha, false, null).GoPage, Page.Photos, "policy.back");

        var shielded = PointerPolicy.Interpret(UserAction.EnterHa, 10, 1000, Page.Photos, true, null);
        C.Equal(shielded.GoPage, null, "policy.shield-blocks-page");
        C.True(shielded.GuardWake, "policy.shield-guards-wake");
        var shieldedTap = PointerPolicy.Interpret(UserAction.Tap, 10, 1000, Page.Photos, true, "on:light.a");
        C.Equal(shieldedTap.PhotoDelta, 0, "policy.shield-blocks-advance");
        C.Equal(shieldedTap.Control, null, "policy.shield-blocks-control");
        var demo = PointerPolicy.Interpret(UserAction.Tap, 10, 1000, Page.Photos, true, "demo:home");
        C.Equal(demo.Control, "demo:home", "policy.demo-under-shield");
        C.True(!demo.GuardWake, "policy.demo-does-not-guard");

        var card = PointerPolicy.Interpret(UserAction.Tap, 10, 1000, Page.Ha, false, "on:light.a");
        C.Equal(card.Control, "on:light.a", "policy.card-tap");
        C.Equal(card.PhotoDelta, 0, "policy.card-does-not-advance-photo");
        var miss = PointerPolicy.Interpret(UserAction.Tap, 10, 1000, Page.Ha, false, null);
        C.Equal(miss.Control, null, "policy.ha-empty-tap");
        C.Equal(miss.PhotoDelta, 0, "policy.ha-empty-no-photo");
        C.Equal(PointerPolicy.Interpret(UserAction.EnterHa, 10, 1000, Page.Ha, false, null).GoPage,
            null, "policy.left-swipe-already-on-ha");
        C.Equal(PointerPolicy.Interpret(UserAction.Tap, 10, 1000, Page.Ha, false, "retry:light.a").Control,
            "retry:light.a", "policy.retry-button-tap");
        var mouse = PointerPolicy.Interpret(UserAction.None, 10, 1000, Page.Ha, false, "off:light.a");
        C.Equal(mouse.Control, null, "policy.synthetic-mouse-does-not-trigger");

        C.True(PointerPolicy.TryParseCard("on:light.a", out var id, out var turnOn) && turnOn && id == "light.a", "policy.parse-on");
        C.True(PointerPolicy.TryParseCard("off:switch.a", out var offId, out var off) && !off && offId == "switch.a", "policy.parse-off");
        C.True(PointerPolicy.TryParseToggle("toggle:light.a", out var togId) && togId == "light.a", "policy.parse-toggle");
        C.True(PointerPolicy.TryParseCover("open:cover.a", out var covId, out var covOpen) && covOpen && covId == "cover.a", "policy.parse-open");
        C.True(PointerPolicy.TryParseCover("close:cover.a", out var covCloseId, out var covClose) && !covClose && covCloseId == "cover.a", "policy.parse-close");
        C.True(PointerPolicy.TryParseNav("nav:info", out var navTarget) && navTarget == Page.Info, "policy.parse-nav-info");
        C.True(PointerPolicy.TryParseAlbum("album:open", out var albAct) && albAct == "open", "policy.parse-album");
        C.Equal(PointerPolicy.Interpret(UserAction.BackToPhotos, 10, 1000, Page.Photos, false, null).GoPage, Page.Info, "policy.right-from-photos-to-info");
        C.Equal(PointerPolicy.Interpret(UserAction.EnterHa, 10, 1000, Page.Info, false, null).GoPage, Page.Photos, "policy.left-from-info-to-photos");
        C.Equal(PointerPolicy.Interpret(UserAction.BackToPhotos, 10, 1000, Page.Info, false, null).GoPage, null, "policy.right-from-info-noop");
        C.Equal(PointerPolicy.Interpret(UserAction.Tap, 10, 1000, Page.Info, false, "album:open").Control, "album:open", "policy.info-album-tap");
        C.Equal(MaintKeys.Map(true, true, "X"), MaintCommand.Exit, "keys.exit");
        C.Equal(MaintKeys.Map(true, true, "M"), MaintCommand.Wake, "keys.wake");
        C.Equal(MaintKeys.Map(false, false, "F12"), MaintCommand.TogglePage, "keys.switch");
        C.Equal(MaintKeys.Map(false, false, "X"), MaintCommand.None, "keys.plain-x");

        var clock = new SlideClock(TimeSpan.FromSeconds(30));
        var t = T0();
        clock.Arm(t);
        C.True(!clock.Due(t.AddSeconds(29)), "slideshow.before-30s");
        C.True(clock.Due(t.AddSeconds(30)), "slideshow.at-30s");
        C.True(!clock.Due(t.AddSeconds(30)), "slideshow.rearms");
    }

    static void Photos()
    {
        var dir = Scratch("check-album");
        File.WriteAllBytes(Path.Combine(dir, "a.jpg"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(dir, "b.PNG"), new byte[] { 2 });
        File.WriteAllBytes(Path.Combine(dir, "c.jpeg"), new byte[] { 3 });
        File.WriteAllBytes(Path.Combine(dir, "d.JPEG"), new byte[] { 4 });
        File.WriteAllBytes(Path.Combine(dir, "e.gif"), new byte[] { 5 });
        File.WriteAllBytes(Path.Combine(dir, "f.webp"), new byte[] { 6 });
        File.WriteAllBytes(Path.Combine(dir, "notes.txt"), new byte[] { 7 });
        File.WriteAllBytes(Path.Combine(dir, "g.jpg.partial"), new byte[] { 8 });
        var listed = PhotoDeck.List(dir).Select(Path.GetFileName).ToList();
        C.Equal(string.Join(",", listed), "a.jpg,b.PNG,c.jpeg,d.JPEG", "photos.jpeg-png-only");

        var deck = new PhotoDeck();
        deck.Reset(new[] { Path.Combine(dir, "a.jpg"), Path.Combine(dir, "bad.jpg"), Path.Combine(dir, "c.jpeg") });
        C.Equal(Name(deck.Go(1, CanShow)), "a.jpg", "photos.first");
        C.Equal(Name(deck.Go(1, CanShow)), "c.jpeg", "photos.skip-broken");
        C.Equal(Name(deck.Go(1, CanShow)), "a.jpg", "photos.wrap");
        C.Equal(Name(deck.Go(-1, CanShow)), "c.jpeg", "photos.previous");
        var broken = new PhotoDeck();
        broken.Reset(new[] { Path.Combine(dir, "bad.jpg") });
        C.Equal(broken.Go(1, CanShow), null, "photos.all-broken");
        C.True(broken.IsEmpty, "photos.empty-when-all-broken");
        var none = new PhotoDeck();
        C.True(none.IsEmpty, "photos.empty-folder");

        var pngPort = Path.Combine(dir, "dim-p.png");
        File.WriteAllBytes(pngPort, new byte[]
        {
            137, 80, 78, 71, 13, 10, 26, 10,
            0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R',
            0, 0, 0x05, 0xA0, // 1440
            0, 0, 0x07, 0x80  // 1920
        });
        C.True(ImageDimensions.TryRead(pngPort, out var pw, out var ph) && pw == 1440 && ph == 1920, "photos.dim-png");
        C.True(ImageDimensions.IsPortrait(pngPort), "photos.dim-png-portrait");

        var jpgLand = Path.Combine(dir, "dim-l.jpg");
        File.WriteAllBytes(jpgLand, new byte[]
        {
            0xFF, 0xD8,
            0xFF, 0xE0, 0x00, 0x04, 0x00, 0x00,
            0xFF, 0xC0, 0x00, 0x0B, 0x08,
            0x05, 0xA0, // 1440 height
            0x07, 0x80, // 1920 width
            0x03, 0x01, 0x11, 0x00
        });
        C.True(ImageDimensions.TryRead(jpgLand, out var lw, out var lh) && lw == 1920 && lh == 1440, "photos.dim-jpg");
        C.True(!ImageDimensions.IsPortrait(jpgLand), "photos.dim-jpg-landscape");

        var paired = SlideDeck.BuildSlides(
            new[] { "L1", "P1", "L2", "P2", "P3" },
            p => p.StartsWith("P", StringComparison.Ordinal));
        C.Equal(paired.Count, 4, "slides.mixed-count");
        C.Equal(paired[0].Primary, "L1", "slides.l1");
        C.Equal(paired[0].Secondary, null, "slides.l1-single");
        C.Equal(paired[1].Primary, "L2", "slides.l2");
        C.Equal(paired[2].Primary, "P1", "slides.p12-left");
        C.Equal(paired[2].Secondary, "P2", "slides.p12-right");
        C.Equal(paired[3].Primary, "P3", "slides.p3-left");
        C.Equal(paired[3].Secondary, "P1", "slides.p3-wraps-first");

        var soloPortrait = SlideDeck.BuildSlides(new[] { "P1" }, _ => true);
        C.Equal(soloPortrait.Count, 1, "slides.solo-portrait");
        C.Equal(soloPortrait[0].Secondary, null, "slides.solo-no-partner");

        var sdeck = new SlideDeck();
        sdeck.Reset(paired);
        C.Equal(sdeck.Go(1)?.Primary, "L1", "slides.go-first");
        C.Equal(sdeck.Go(1)?.Primary, "L2", "slides.go-second");
        C.Equal(sdeck.Go(-1)?.Primary, "L1", "slides.go-back");

        var b1 = CenterFillLayout.Compute(1200, 800, 1920, 1440, true);
        C.True(Math.Abs(b1.X - 0) < 0.01 && Math.Abs(b1.Y - (-50)) < 0.01
            && Math.Abs(b1.Width - 1200) < 0.01 && Math.Abs(b1.Height - 900) < 0.01, "layout.landscape-4x3");

        var b2 = CenterFillLayout.Compute(599, 800, 1440, 1920, true);
        C.True(Math.Abs(b2.X - (-0.5)) < 0.01 && Math.Abs(b2.Y - 0) < 0.01
            && Math.Abs(b2.Width - 600) < 0.01 && Math.Abs(b2.Height - 800) < 0.01, "layout.portrait-3x4");

        var b3 = CenterFillLayout.Compute(599, 800, 1440, 2160, true);
        C.True(Math.Abs(b3.X - 0) < 0.01 && Math.Abs(b3.Y - (-49.25)) < 0.01
            && Math.Abs(b3.Width - 599) < 0.01 && Math.Abs(b3.Height - 898.5) < 0.01, "layout.portrait-2x3");

        var b4 = CenterFillLayout.Compute(1200, 800, 1920, 1440, false);
        C.True(Math.Abs(b4.X - 66.666) < 0.05 && Math.Abs(b4.Y - 0) < 0.01
            && Math.Abs(b4.Width - 1066.666) < 0.05 && Math.Abs(b4.Height - 800) < 0.01, "layout.fit-4x3");
    }

    static void Presence()
    {
        var t = T0();
        var fresh = new PresenceDirector();
        C.True(fresh.Shield, "presence.initially-black");
        C.True(!fresh.MonitorLatched, "presence.initial-display-not-forced-off");
        C.Equal(fresh.ConsumeCommand(), DisplayCommand.None, "presence.initial-command");

        fresh.Sample(PresenceKind.Away, true, t);
        C.True(fresh.Shield, "presence.initial-away-black");
        C.True(!fresh.EverHome, "presence.initial-away-not-home");
        C.True(fresh.MonitorLatched, "presence.initial-away-latched-off");
        C.Equal(fresh.ConsumeCommand(), DisplayCommand.TurnOff, "presence.initial-away-power-off");

        var d = new PresenceDirector();
        d.Sample(PresenceKind.Home, true, t);
        C.True(!d.Shield, "presence.home-clears-shield");
        C.True(d.ConsumeArrivedHome(), "presence.home-wakes-album");
        C.Equal(d.ConsumeCommand(), DisplayCommand.TurnOn, "presence.home-display-on");

        d.Sample(PresenceKind.Away, true, t);
        d.Sample(PresenceKind.Away, true, t.AddMinutes(9));
        C.Equal(d.AwayAccumulated, TimeSpan.FromMinutes(9), "presence.nine-minutes");
        C.True(!d.MonitorLatched, "presence.nine-minutes-stays-on");
        d.Sample(PresenceKind.Away, true, t.AddMinutes(10));
        C.True(d.Shield, "presence.ten-minutes-shield-first");
        C.True(d.MonitorLatched, "presence.ten-minutes-latched");
        C.Equal(d.ConsumeCommand(), DisplayCommand.TurnOff, "presence.ten-minutes-display-off");
        C.Equal(d.OnAccidentalWake(t.AddMinutes(10)), DisplayCommand.KeepOff, "presence.accidental-wake");
        d.BeginMaintenance(t.AddMinutes(10));
        C.Equal(d.OnAccidentalWake(t.AddMinutes(12)), DisplayCommand.None, "presence.maintenance-allows-wake");
        C.Equal(d.OnAccidentalWake(t.AddMinutes(13)), DisplayCommand.KeepOff, "presence.maintenance-ends");

        d.Sample(PresenceKind.Home, true, t.AddMinutes(14));
        C.True(!d.Shield && !d.MonitorLatched, "presence.return-home-clears-latch");
        C.Equal(d.AwayAccumulated, TimeSpan.Zero, "presence.return-home-resets");
        C.Equal(d.ConsumeCommand(), DisplayCommand.TurnOn, "presence.return-home-display-on");
        C.True(d.ConsumeArrivedHome(), "presence.return-home-album");

        var pause = new PresenceDirector();
        pause.Sample(PresenceKind.Home, true, t);
        pause.ConsumeCommand();
        pause.ConsumeArrivedHome();
        pause.Sample(PresenceKind.Away, true, t);
        pause.Sample(PresenceKind.Away, true, t.AddMinutes(4));
        pause.Sample(PresenceKind.Unknown, true, t.AddMinutes(4));
        pause.Sample(PresenceKind.Unknown, true, t.AddMinutes(34));
        C.Equal(pause.AwayAccumulated, TimeSpan.FromMinutes(4), "presence.pause-unknown");
        pause.Sample(PresenceKind.Away, true, t.AddMinutes(34));
        pause.Sample(PresenceKind.Away, true, t.AddMinutes(40));
        C.Equal(pause.AwayAccumulated, TimeSpan.FromMinutes(10), "presence.resume-after-unknown");

        var link = new PresenceDirector();
        link.Sample(PresenceKind.Home, true, t);
        link.ConsumeCommand();
        link.Sample(PresenceKind.Away, true, t);
        link.Sample(PresenceKind.Away, true, t.AddMinutes(4));
        link.Sample(PresenceKind.Away, false, t.AddMinutes(4));
        link.Sample(PresenceKind.Away, false, t.AddMinutes(34));
        C.Equal(link.AwayAccumulated, TimeSpan.FromMinutes(4), "presence.pause-disconnected");
        C.True(!link.MonitorLatched, "presence.disconnect-does-not-sleep");
    }

    static void HomeAssistant()
    {
        var t = T0();
        var logic = NewLogic();
        var sent = new List<string>();
        logic.BeginSession(t);
        sent.AddRange(logic.OnMessage("{\"type\":\"auth_required\",\"ha_version\":\"demo\"}", t));
        using (var auth = JsonDocument.Parse(sent[0]))
        {
            C.Equal(auth.RootElement.GetProperty("type").GetString(), "auth", "ha.auth-type");
            C.Equal(auth.RootElement.GetProperty("access_token").GetString(), "placeholder-token", "ha.auth-token");
        }
        sent.AddRange(logic.OnMessage("{\"type\":\"auth_ok\",\"ha_version\":\"demo\"}", t));
        using (var subscribe = JsonDocument.Parse(sent[^1]))
        {
            C.Equal(subscribe.RootElement.GetProperty("type").GetString(), "subscribe_events", "ha.subscribe-before-get-states");
            C.Equal(subscribe.RootElement.GetProperty("event_type").GetString(), "state_changed", "ha.subscribe-state-changed");
            C.True(!sent[^1].Contains("get_states"), "ha.auth-ok-does-not-get-states");
        }
        var subscribeId = JsonDocument.Parse(sent[^1]).RootElement.GetProperty("id").GetInt32();
        C.True(!logic.TryCommand("light.demo_ceiling", true, t, out _), "ha.no-command-before-snapshot");
        logic.OnMessage(Changed("light.demo_ceiling", "on", "2026-01-01T00:00:03+00:00"), t);
        logic.OnMessage(Changed("switch.demo_plug", "off", "2026-01-01T00:00:01+00:00"), t);
        C.Equal(logic.StateOf("light.demo_ceiling"), null, "ha.buffer-not-applied-yet");
        C.True(!logic.ControlsEnabled, "ha.controls-off-while-snapshotting");
        var getStates = logic.OnMessage(Ok(subscribeId), t);
        using (var get = JsonDocument.Parse(getStates[0]))
            C.Equal(get.RootElement.GetProperty("type").GetString(), "get_states", "ha.get-states-after-subscribe-result");
        var getId = JsonDocument.Parse(getStates[0]).RootElement.GetProperty("id").GetInt32();
        C.True(getId > subscribeId, "ha.get-states-id-after-subscribe");
        C.Equal(logic.Phase, ConnPhase.Snapshotting, "ha.snapshot-phase");
        C.True(!logic.TryCommand("light.demo_ceiling", true, t, out _), "ha.no-command-during-snapshot");
        logic.OnMessage(States(getId,
            ("light.demo_ceiling", "off", "2026-01-01T00:00:02+00:00"),
            ("switch.demo_plug", "on", "2026-01-01T00:00:05+00:00"),
            ("sensor.demo_temp", "22.5", "2026-01-01T00:00:02+00:00"),
            ("person.demo", "unknown", "2026-01-01T00:00:02+00:00")), t);
        C.Equal(logic.Phase, ConnPhase.Live, "ha.live-after-reconcile");
        C.True(logic.ControlsEnabled, "ha.controls-after-snapshot");
        C.Equal(logic.StateOf("light.demo_ceiling"), "on", "ha.reconcile-newer-buffer");
        C.Equal(logic.StateOf("switch.demo_plug"), "on", "ha.reconcile-drops-older-buffer");
        C.Equal(logic.Presence, PresenceKind.Unknown, "ha.unknown-presence");

        logic.OnMessage(Changed("person.demo", "away", "2026-01-01T00:00:06+00:00"), t);
        C.Equal(logic.Presence, PresenceKind.Unknown, "ha.explicit-away-value");
        logic.OnMessage(Changed("person.other", "home", "2026-01-01T00:00:07+00:00"), t);
        C.Equal(logic.Presence, PresenceKind.Unknown, "ha.only-configured-presence");
        logic.OnMessage(Changed("person.demo", "not_home", "2026-01-01T00:00:08+00:00"), t);
        C.Equal(logic.Presence, PresenceKind.Away, "ha.presence-away");
        logic.OnMessage(Changed("person.demo", "home", "2026-01-01T00:00:09+00:00"), t);
        C.Equal(logic.Presence, PresenceKind.Home, "ha.presence-home");

        C.True(logic.TryCommand("light.demo_ceiling", true, t, out var first) && first != null, "command.accepted");
        C.Equal(logic.PendingOf("light.demo_ceiling"), PendingKind.TurningOn, "command.pending-immediate");
        C.Equal(logic.Cards().First(c => c.EntityId == "light.demo_ceiling").Pending, PendingKind.TurningOn, "command.card-pending");
        var call = ReadCall(first!);
        C.Equal(call.Service, "turn_on", "command.explicit-turn-on");
        C.Equal(call.Domain, "light", "command.light-domain");
        C.Equal(call.Entity, "light.demo_ceiling", "command.entity");
        C.True(!logic.TryCommand("light.demo_ceiling", true, t.AddSeconds(1), out _), "command.duplicate-same-target");
        C.True(!logic.TryCommand("light.demo_ceiling", false, t.AddSeconds(1), out _), "command.opposite-blocked");
        C.True(logic.TryCommand("switch.demo_plug", false, t.AddSeconds(1), out var other), "command.other-card-available");
        C.Equal(ReadCall(other!).Service, "turn_off", "command.explicit-turn-off");
        C.True(!logic.OnTimer(t.AddSeconds(4.9)).Any(IsCall), "command.no-send-before-5s");
        C.True(!logic.TryRetry("light.demo_ceiling", t.AddSeconds(4.9), out _), "command.no-retry-before-timeout");
        C.True(!logic.OnTimer(t.AddSeconds(5)).Any(IsCall), "command.no-automatic-retry");
        C.Equal(logic.PendingOf("light.demo_ceiling"), PendingKind.Unconfirmed, "command.unconfirmed-at-five-seconds");
        logic.OnMessage(Changed("light.demo_ceiling", "off", "2026-01-01T00:00:10+00:00"), t.AddSeconds(5));
        C.Equal(logic.PendingOf("light.demo_ceiling"), PendingKind.Unconfirmed, "command.opposite-state-keeps-retry");
        C.True(logic.TryRetry("light.demo_ceiling", t.AddSeconds(6), out var retried), "command.manual-retry");
        var again = ReadCall(retried!);
        C.Equal(again.Service, "turn_on", "command.retry-same-service");
        C.Equal(again.Entity, "light.demo_ceiling", "command.retry-same-entity");
        C.True(again.Id != call.Id, "command.retry-new-id");
        C.Equal(logic.PendingOf("light.demo_ceiling"), PendingKind.TurningOn, "command.retried-pending");
        logic.OnMessage(Changed("light.demo_ceiling", "on", "2026-01-01T00:00:12+00:00"), t.AddSeconds(7));
        C.Equal(logic.StateOf("light.demo_ceiling"), "on", "command.late-update");
        C.Equal(logic.PendingOf("light.demo_ceiling"), PendingKind.None, "command.late-update-clears-pending");

        C.True(!logic.TryCommand("sensor.demo_temp", true, t, out _), "command.sensor-readonly");
        logic.OnTransportClosed(t.AddSeconds(8));
        C.True(!logic.ControlsEnabled, "command.disconnected-disables-controls");
        C.Equal(logic.PendingOf("switch.demo_plug"), PendingKind.Unconfirmed, "command.disconnect-keeps-target");
        C.True(!logic.TryRetry("switch.demo_plug", t.AddSeconds(9), out _), "command.no-retry-while-disconnected");
        C.True(!logic.TryCommand("light.demo_ceiling", false, t.AddSeconds(9), out _), "command.no-send-while-disconnected");
        C.Equal(logic.OnTimer(t.AddSeconds(30)).Count, 0, "command.timer-silent-while-disconnected");
        DriveLive(logic, t.AddSeconds(31));
        C.Equal(logic.PendingOf("switch.demo_plug"), PendingKind.None, "command.reconnect-snapshot-confirms-target");

        var heart = NewLogic();
        DriveLive(heart, t);
        C.True(!heart.OnTimer(t.AddSeconds(19)).Any(IsPing), "ha.no-early-ping");
        var ping = heart.OnTimer(t.AddSeconds(20)).Single();
        var pingId = JsonDocument.Parse(ping).RootElement.GetProperty("id").GetInt32();
        C.Equal(JsonDocument.Parse(ping).RootElement.GetProperty("type").GetString(), "ping", "ha.heartbeat");
        heart.OnTimer(t.AddSeconds(30));
        C.Equal(heart.Phase, ConnPhase.Disconnected, "ha.missed-pong-reconnect-state");
        C.True(heart.ConsumeTransportFault(), "ha.missed-pong-fault");
        C.True(!heart.ControlsEnabled, "ha.controls-off-after-missed-pong");

        var steady = NewLogic();
        DriveLive(steady, t);
        var livePing = steady.OnTimer(t.AddSeconds(20)).Single();
        var liveId = JsonDocument.Parse(livePing).RootElement.GetProperty("id").GetInt32();
        steady.OnMessage("{\"id\":" + liveId + ",\"type\":\"pong\"}", t.AddSeconds(21));
        steady.OnTimer(t.AddSeconds(30));
        C.Equal(steady.Phase, ConnPhase.Live, "ha.pong-keeps-session");
        C.Equal(JsonDocument.Parse(steady.OnTimer(t.AddSeconds(40)).Single()).RootElement.GetProperty("type").GetString(), "ping", "ha.next-heartbeat");

        logic.OnMessage("{\"type\":\"auth_invalid\"}", t);
        var closed = NewLogic();
        closed.BeginSession(t);
        closed.OnMessage("{\"type\":\"auth_required\"}", t);
        closed.OnMessage("{\"type\":\"auth_invalid\"}", t);
        C.Equal(closed.Phase, ConnPhase.Disconnected, "ha.auth-invalid");
        _ = pingId;
    }

    static void DemoPace()
    {
        var t = T0();
        var quick = new Lab();
        quick.Handshake(t);
        C.Equal(quick.Logic.DisplayOf("sensor.demo_temp"), "22.5 °C", "ha.sensor-unit");
        C.Equal(quick.Logic.Presence, PresenceKind.Unknown, "demo.initial-presence-unknown");
        quick.Broker.Pace = ConfirmPace.Quick;
        C.True(quick.Logic.TryCommand("light.demo_ceiling", true, t, out var quickJson), "demo.quick-command-accepted");
        C.Equal(quick.Logic.PendingOf("light.demo_ceiling"), PendingKind.TurningOn, "demo.quick-pending");
        quick.Drain(quick.Broker.Accept(quickJson!, t), t);
        C.Equal(quick.Logic.StateOf("light.demo_ceiling"), "off", "demo.quick-waits");
        quick.Drain(quick.Broker.Poll(t.AddMilliseconds(200)), t.AddMilliseconds(200));
        C.Equal(quick.Logic.StateOf("light.demo_ceiling"), "on", "demo.quick-confirms");
        C.Equal(quick.Logic.PendingOf("light.demo_ceiling"), PendingKind.None, "demo.quick-clears");
        C.True(!quick.Logic.OnTimer(t.AddSeconds(5)).Any(IsCall), "demo.quick-no-retry");

        var slow = new Lab();
        slow.Handshake(t);
        slow.Broker.Pace = ConfirmPace.Slow;
        C.True(slow.Logic.TryCommand("light.demo_ceiling", true, t, out var slowJson), "demo.slow-command-accepted");
        slow.Drain(slow.Broker.Accept(slowJson!, t), t);
        C.True(!slow.Logic.OnTimer(t.AddSeconds(5)).Any(IsCall), "demo.slow-no-auto-retry");
        C.Equal(slow.Logic.PendingOf("light.demo_ceiling"), PendingKind.Unconfirmed, "demo.slow-unconfirmed");
        C.Equal(slow.Logic.StateOf("light.demo_ceiling"), "off", "demo.slow-still-waiting-at-5s");
        slow.Drain(slow.Broker.Poll(t.AddSeconds(6)), t.AddSeconds(6));
        C.Equal(slow.Logic.StateOf("light.demo_ceiling"), "on", "demo.slow-confirms-without-retry");
        C.Equal(slow.Logic.PendingOf("light.demo_ceiling"), PendingKind.None, "demo.slow-clears");

        var dropped = new Lab();
        dropped.Handshake(t);
        dropped.Broker.Pace = ConfirmPace.Slow;
        C.True(dropped.Logic.TryCommand("light.demo_ceiling", true, t, out var droppedJson), "demo.disconnect-command-accepted");
        dropped.Drain(dropped.Broker.Accept(droppedJson!, t), t);
        dropped.Broker.Disconnect();
        dropped.Logic.OnTransportClosed(t.AddSeconds(1));
        C.True(!dropped.Logic.TryCommand("light.demo_ceiling", true, t.AddSeconds(2), out _), "demo.no-send-while-dropped");
        C.Equal(dropped.Logic.OnTimer(t.AddSeconds(8)).Count, 0, "demo.timer-silent-while-dropped");
        dropped.Drain(dropped.Broker.Poll(t.AddSeconds(8)), t.AddSeconds(8));
        dropped.Logic.BeginSession(t.AddSeconds(8));
        dropped.Drain(dropped.Broker.Start(t.AddSeconds(8)), t.AddSeconds(8));
        C.Equal(dropped.Logic.StateOf("light.demo_ceiling"), "off", "demo.disconnect-drops-late-confirm");
        _ = droppedJson;
    }

    static void PresenceReconnect()
    {
        var t = T0();
        var lab = new Lab();
        var director = new PresenceDirector();
        lab.Handshake(t);
        director.Sample(lab.Logic.Presence, lab.Logic.Phase == ConnPhase.Live, t);
        C.True(director.Shield, "reconnect.black-until-home");
        lab.Drain(lab.Broker.SetPresence("home", t), t);
        director.Sample(lab.Logic.Presence, true, t);
        C.Equal(lab.Logic.Presence, PresenceKind.Home, "reconnect.snapshot-home");
        C.True(director.ConsumeArrivedHome(), "reconnect.home-album");
        C.Equal(director.ConsumeCommand(), DisplayCommand.TurnOn, "reconnect.home-on");

        lab.Drain(lab.Broker.SetPresence("not_home", t), t);
        director.Sample(lab.Logic.Presence, true, t);
        director.Sample(lab.Logic.Presence, true, t.AddMinutes(4));
        C.Equal(director.AwayAccumulated, TimeSpan.FromMinutes(4), "reconnect.away-four");
        lab.Broker.Disconnect();
        lab.Logic.OnTransportClosed(t.AddMinutes(4));
        director.Sample(lab.Logic.Presence, false, t.AddMinutes(4));
        director.Sample(lab.Logic.Presence, false, t.AddMinutes(34));
        C.Equal(director.AwayAccumulated, TimeSpan.FromMinutes(4), "reconnect.gap-not-counted");
        lab.Logic.BeginSession(t.AddMinutes(34));
        lab.Drain(lab.Broker.Start(t.AddMinutes(34)), t.AddMinutes(34));
        C.Equal(lab.Logic.Phase, ConnPhase.Live, "reconnect.live-after-snapshot");
        C.Equal(lab.Logic.Presence, PresenceKind.Away, "reconnect.snapshot-away");
        C.True(lab.Logic.ControlsEnabled, "reconnect.controls-after-snapshot");
        director.Sample(lab.Logic.Presence, true, t.AddMinutes(34));
        C.Equal(director.AwayAccumulated, TimeSpan.FromMinutes(4), "reconnect.resume-accumulation");
        director.Sample(lab.Logic.Presence, true, t.AddMinutes(40));
        C.Equal(director.AwayAccumulated, TimeSpan.FromMinutes(10), "reconnect.reaches-ten");
        C.True(director.Shield && director.MonitorLatched, "reconnect.shield-then-off");
        C.Equal(director.ConsumeCommand(), DisplayCommand.TurnOff, "reconnect.display-off");

        var wake = new Lab();
        var back = new PresenceDirector();
        wake.Handshake(t);
        wake.Drain(wake.Broker.SetPresence("home", t), t);
        back.Sample(PresenceKind.Home, true, t);
        back.ConsumeCommand();
        back.ConsumeArrivedHome();
        wake.Drain(wake.Broker.SetPresence("not_home", t), t);
        back.Sample(PresenceKind.Away, true, t);
        back.Sample(PresenceKind.Away, true, t.AddMinutes(4));
        wake.Broker.Disconnect();
        wake.Logic.OnTransportClosed(t.AddMinutes(4));
        back.Sample(PresenceKind.Away, false, t.AddMinutes(4));
        wake.Broker.SetPresence("home", t.AddMinutes(20));
        C.True(!wake.Logic.TryCommand("light.demo_ceiling", true, t.AddMinutes(19), out _), "reconnect.command-only-when-live");
        wake.Logic.BeginSession(t.AddMinutes(20));
        wake.Drain(wake.Broker.Start(t.AddMinutes(20)), t.AddMinutes(20));
        C.Equal(wake.Logic.Presence, PresenceKind.Home, "reconnect.home-snapshot");
        back.Sample(wake.Logic.Presence, wake.Logic.Phase == ConnPhase.Live, t.AddMinutes(20));
        C.True(!back.Shield, "reconnect.home-clears-shield");
        C.Equal(back.AwayAccumulated, TimeSpan.Zero, "reconnect.home-resets-away");
        C.Equal(back.ConsumeCommand(), DisplayCommand.TurnOn, "reconnect.home-wakes-display");
        C.True(back.ConsumeArrivedHome(), "reconnect.home-shows-album");
    }

    static void Config()
    {
        var root = RepoRoot();
        var exampleText = File.ReadAllText(Path.Combine(root, "frame.example.json"));
        C.True(exampleText.Contains("ha.example") && exampleText.Contains("immich.example"), "config.example-hosts");
        C.True(!exampleText.Contains("eyJ"), "config.example-has-no-jwt");
        var loaded = ConfigLoader.Load(Path.Combine(root, "frame.example.json"), null, _ => null);
        C.True(loaded.Demo.Enabled, "config.demo-on");
        C.True(!loaded.Album.Crop, "config.fit-by-default");
        C.Equal(loaded.Album.IntervalSeconds, 30, "config.interval-30");
        C.Equal(loaded.HomeAssistant.PresenceHome, "home", "config.presence-home");
        C.Equal(loaded.HomeAssistant.PresenceAway, "not_home", "config.presence-away");
        C.Equal(loaded.HomeAssistant.Entities.Count, 3, "config.three-entities");
        C.Equal(loaded.HomeAssistant.Entities[0].Name, "客厅主灯", "config.chinese-name");
        C.True(!loaded.Immich.Enabled, "config.immich-off");
        C.Equal(loaded.HaToken, null, "config.public-has-no-token");

        var dir = Scratch("check-config");
        File.WriteAllText(Path.Combine(dir, "frame.json"), """
        {
          "homeAssistantToken": "secret-from-public",
          "album": { "folder": "Photos", "crop": true, "intervalSeconds": 0 },
          "homeAssistant": {
            "url": "ws://ha.example:8123/api/websocket",
            "tokenEnv": "HA_TOKEN",
            "token": "also-public",
            "presenceEntityId": "person.demo",
            "presenceHome": "home",
            "presenceAway": "not_home",
            "entities": [
              { "entityId": "light.demo_ceiling", "name": "客厅主灯", "kind": "light" },
              { "entityId": "light.demo_ceiling", "name": "重复", "kind": "light" }
            ]
          },
          "demo": { "enabled": false }
        }
        """);
        File.WriteAllText(Path.Combine(dir, "frame.private.json"), """
        { "homeAssistantToken": "from-file", "immichApiKey": "immich-file" }
        """);
        var merged = ConfigLoader.Load(
            Path.Combine(dir, "frame.json"),
            Path.Combine(dir, "frame.private.json"),
            name => name == "HA_TOKEN" ? "from-env" : "immich-env");
        C.Equal(merged.HaToken, "from-file", "config.private-token-wins");
        C.Equal(merged.ImmichApiKey, "immich-file", "config.private-immich-key");
        C.Equal(merged.HomeAssistant.Entities.Count, 1, "config.duplicate-entity-skipped");
        C.True(merged.Album.Crop, "config.crop-option");
        C.Equal(merged.Album.IntervalSeconds, 30, "config.invalid-interval-defaults-30");
        C.True(!merged.Demo.Enabled, "config.demo-can-disable");

        File.WriteAllText(Path.Combine(dir, "frame.private.json"), """
        { "homeAssistantToken": "replace-me", "immichApiKey": "replace-me" }
        """);
        var envOnly = ConfigLoader.Load(
            Path.Combine(dir, "frame.json"),
            Path.Combine(dir, "frame.private.json"),
            name => name == "HA_TOKEN" ? "from-env" : null);
        C.Equal(envOnly.HaToken, "from-env", "config.env-token");
        C.Equal(ConfigLoader.Clean("replace-me"), null, "config.placeholder-ignored");
    }

    static void Immich()
    {
        using var album = ImmichSync.BuildAlbumRequest("https://immich.example/", "album-1", "demo-key");
        using var thumb = ImmichSync.BuildThumbRequest("https://immich.example", "asset-1", "demo-key");
        C.Equal(album.Method.Method, "GET", "immich.album-get");
        C.Equal(album.RequestUri!.ToString(), "https://immich.example/api/albums/album-1", "immich.album-url");
        C.Equal(thumb.RequestUri!.ToString(), "https://immich.example/api/assets/asset-1/thumbnail?size=preview", "immich.thumb-url");
        C.Equal(string.Join(",", album.Headers.GetValues("x-api-key")), "demo-key", "immich.album-key");
        C.Equal(string.Join(",", thumb.Headers.GetValues("x-api-key")), "demo-key", "immich.thumb-key");
        var parsed = ImmichSync.ParseAlbum("""
        {"assets":[
          {"id":"asset-1","type":"IMAGE","originalMimeType":"image/jpeg"},
          {"id":"asset-2","type":"VIDEO","originalMimeType":"video/mp4"}
        ]}
        """);
        C.Equal(parsed.Count, 1, "immich.skip-video");
        C.Equal(parsed[0].Id, "asset-1", "immich.image-id");

        var root = Scratch("check-immich");
        var cache = Path.Combine(root, "cache");
        var local = Path.Combine(root, "local");
        Directory.CreateDirectory(cache);
        Directory.CreateDirectory(local);
        File.WriteAllBytes(Path.Combine(cache, "stale.jpg"), new byte[] { 9 });
        File.WriteAllBytes(Path.Combine(local, "local.jpg"), new byte[] { 7 });
        var bytes = new byte[] { 0xff, 0xd8, 0xff, 0xe0, 0, 0, 0, 0 };
        var handler = new RecordingHandler
        {
            Handle = request =>
            {
                var uri = request.RequestUri!.AbsolutePath;
                if (uri.Contains("/api/albums/", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                    {
                        Content = new StringContent("""
                        {"assets":[
                          {"id":"asset-1","type":"IMAGE"},
                          {"id":"asset-2","type":"VIDEO"}
                        ]}
                        """, Encoding.UTF8, "application/json")
                    };
                }
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(bytes)
                };
            }
        };
        using (var http = new HttpClient(handler))
        {
            var updated = ImmichSync.SyncAsync(http, "https://immich.example", "album-1", "demo-key", cache, local, CancellationToken.None)
                .GetAwaiter().GetResult();
            C.Equal(updated.Status, ImmichStatus.Updated, "immich.updated");
            C.Equal(updated.Files.Count, 1, "immich.one-file");
            C.True(updated.Files[0].EndsWith("asset-1.jpg", StringComparison.Ordinal), "immich.cached-name");
            C.True(File.ReadAllBytes(updated.Files[0]).AsSpan().SequenceEqual(bytes), "immich.cached-bytes");
            C.True(!File.Exists(Path.Combine(cache, "stale.jpg")), "immich.stale-removed");
            C.True(!File.Exists(updated.Files[0] + ".partial"), "immich.partial-removed");
            C.Equal(handler.Seen.Count, 2, "immich.two-requests");
            foreach (var seen in handler.Seen)
            {
                C.Equal(seen.Method, "GET", "immich.method-" + seen.Uri);
                C.Equal(seen.Key, "demo-key", "immich.key-" + seen.Uri);
                var allowed = seen.Uri.EndsWith("/api/albums/album-1", StringComparison.Ordinal)
                    || seen.Uri.Contains("/api/assets/asset-1/thumbnail?size=preview", StringComparison.Ordinal);
                C.True(allowed, "immich.allowed-url " + seen.Uri);
            }
        }

        handler.Handle = _ => throw new HttpRequestException("offline");
        handler.Seen.Clear();
        using (var http = new HttpClient(handler))
        {
            var kept = ImmichSync.SyncAsync(http, "https://immich.example", "album-1", "demo-key", cache, local, CancellationToken.None)
                .GetAwaiter().GetResult();
            C.Equal(kept.Status, ImmichStatus.KeptCache, "immich.cache-fallback");
            C.True(kept.Files.Any(path => path.EndsWith("asset-1.jpg", StringComparison.Ordinal)), "immich.cache-file");
        }

        using (var http = new HttpClient(new RecordingHandler
        {
            Handle = _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            }
        }))
        {
            var malformed = ImmichSync.SyncAsync(http, "https://immich.example", "album-1", "demo-key", cache, local, CancellationToken.None)
                .GetAwaiter().GetResult();
            C.Equal(malformed.Status, ImmichStatus.KeptCache, "immich.malformed-album-keeps-cache");
            C.Equal(malformed.Files.Count, 1, "immich.malformed-album-keeps-preview");
        }

        var emptyCache = Path.Combine(root, "empty-cache");
        Directory.CreateDirectory(emptyCache);
        using (var http = new HttpClient(handler))
        {
            var localFallback = ImmichSync.SyncAsync(http, "https://immich.example", "album-1", "demo-key", emptyCache, local, CancellationToken.None)
                .GetAwaiter().GetResult();
            C.Equal(localFallback.Status, ImmichStatus.FellBackToLocal, "immich.local-fallback");
            C.True(localFallback.Files.Any(path => path.EndsWith("local.jpg", StringComparison.Ordinal)), "immich.local-file");
        }

        var emptyAlbum = new RecordingHandler
        {
            Handle = _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{\"assets\":[]}", Encoding.UTF8, "application/json")
            }
        };
        using (var http = new HttpClient(emptyAlbum))
        {
            var empty = ImmichSync.SyncAsync(http, "https://immich.example", "album-1", "demo-key", Path.Combine(root, "empty-album"), local, CancellationToken.None)
                .GetAwaiter().GetResult();
            C.Equal(empty.Status, ImmichStatus.Empty, "immich.empty-album");
            C.Equal(empty.Files.Count, 1, "immich.empty-uses-local");
        }

        var atomic = Path.Combine(root, "atomic.jpg");
        ImmichSync.AtomicSave(atomic, new byte[] { 1, 1 });
        ImmichSync.AtomicSave(atomic, new byte[] { 2, 2, 2 });
        C.True(File.ReadAllBytes(atomic).AsSpan().SequenceEqual(new byte[] { 2, 2, 2 }), "immich.atomic-replace");
        C.True(!Directory.EnumerateFiles(root, "*.partial").Any(), "immich.atomic-no-partial");

        var v3Cache = Path.Combine(root, "v3-cache");
        var v3Calls = 0;
        var v3Handler = new RecordingHandler
        {
            Handle = request =>
            {
                v3Calls++;
                var uri = request.RequestUri!.AbsolutePath;
                if (uri.EndsWith("/api/albums/album-v3", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                    {
                        Content = new StringContent("""
                        {"id":"album-v3","albumName":"v3","assetCount":2}
                        """, Encoding.UTF8, "application/json")
                    };
                }
                if (uri.EndsWith("/api/search/metadata", StringComparison.Ordinal))
                {
                    var reqBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                    if (reqBody.Contains("\"page\":1", StringComparison.Ordinal))
                    {
                        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                        {
                            Content = new StringContent("""
                            {"assets":{"items":[{"id":"asset-1","type":"IMAGE"}],"nextPage":"2"}}
                            """, Encoding.UTF8, "application/json")
                        };
                    }
                    return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                    {
                        Content = new StringContent("""
                        {"assets":{"items":[{"id":"asset-2","type":"IMAGE"}],"nextPage":null}}
                        """, Encoding.UTF8, "application/json")
                    };
                }
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(bytes)
                };
            }
        };
        using (var http = new HttpClient(v3Handler))
        {
            var v3 = ImmichSync.SyncAsync(http, "https://immich.example", "album-v3", "demo-key", v3Cache, local, CancellationToken.None)
                .GetAwaiter().GetResult();
            C.Equal(v3.Status, ImmichStatus.Updated, "immich.v3-updated");
            C.Equal(v3.Files.Count, 2, "immich.v3-two-files");
            C.True(v3.Files.Any(path => path.EndsWith("asset-1.jpg", StringComparison.Ordinal)), "immich.v3-asset1");
            C.True(v3.Files.Any(path => path.EndsWith("asset-2.jpg", StringComparison.Ordinal)), "immich.v3-asset2");
            C.Equal(v3Calls, 5, "immich.v3-calls");
        }

        var liveAssets = ImmichSync.ParseAlbum("""
        {"assets":[
          {"id":"img-1","type":"IMAGE","livePhotoVideoId":"vid-1"}
        ]}
        """);
        C.Equal(liveAssets.Count, 1, "immich.live-count");
        C.Equal(liveAssets[0].LivePhotoVideoId, "vid-1", "immich.live-vid-id");

        var albums = ImmichSync.ParseAlbumsList("""
        [
          {"id":"00000000-0000-0000-0000-000000000001","albumName":"Big","assetCount":500},
          {"id":"00000000-0000-0000-0000-000000000002","albumName":"Empty","assetCount":0},
          {"id":"00000000-0000-0000-0000-000000000003","albumName":"Small","assetCount":25}
        ]
        """);
        C.Equal(albums.Count, 2, "immich.albums-nonempty");
        C.Equal(albums[0].Name, "Small", "immich.albums-sorted");

        var coverEntities = new List<EntityConfig>
        {
            new("cover.living", "客厅窗帘", EntityKind.Cover)
        };
        var infoList = new List<EntityConfig>
        {
            new("weather.home", "天气", EntityKind.Sensor)
        };
        var coverLogic = new HaClientLogic(coverEntities, "person.demo", "home", "not_home", "tok", infoList);
        coverLogic.BeginSession(T0());
        coverLogic.OnMessage("{\"type\":\"auth_required\"}", T0());
        var subMsg = coverLogic.OnMessage("{\"type\":\"auth_ok\"}", T0()).Single();
        var subId = JsonDocument.Parse(subMsg).RootElement.GetProperty("id").GetInt32();
        var getMsg = coverLogic.OnMessage($"{{\"id\":{subId},\"type\":\"result\",\"success\":true}}", T0()).Single();
        var getId = JsonDocument.Parse(getMsg).RootElement.GetProperty("id").GetInt32();
        coverLogic.OnMessage("{\"id\":" + getId + ",\"type\":\"result\",\"success\":true,\"result\":["
            + "{\"entity_id\":\"cover.living\",\"state\":\"closed\",\"attributes\":{\"current_position\":0}},"
            + "{\"entity_id\":\"weather.home\",\"state\":\"partlycloudy\",\"attributes\":{\"temperature\":13.9,\"humidity\":57}}"
            + "]}", T0());
        C.True(coverLogic.TryCommand("cover.living", true, T0(), out var openJson) && openJson != null, "cover.open-accepted");
        var openCall = ReadCall(openJson!);
        C.Equal(openCall.Domain, "cover", "cover.domain");
        C.Equal(openCall.Service, "open_cover", "cover.service");
        C.Equal(coverLogic.AttributeOf("weather.home", "temperature"), "13.9", "info.attr-temp");

        var cfgFile = Path.Combine(root, "save-frame.json");
        File.WriteAllText(cfgFile, """
        {
          "album": { "folder": "Photos", "crop": true },
          "immich": { "enabled": false, "albumId": "00000000-0000-0000-0000-000000000000" }
        }
        """);
        C.True(ConfigLoader.SaveImmichSelection(cfgFile, "11111111-2222-3333-4444-555555555555"), "config.save-album-ok");
        var reloaded = ConfigLoader.Load(cfgFile, null, null);
        C.True(reloaded.Immich.Enabled, "config.save-album-enabled");
        C.Equal(reloaded.Immich.AlbumId, "11111111-2222-3333-4444-555555555555", "config.save-album-id");
    }

    static void ProjectShape()
    {
        var root = RepoRoot();
        var wpf = File.ReadAllText(Path.Combine(root, "src", "SurfaceFrame", "SurfaceFrame.csproj"));
        C.True(wpf.Contains("<EnableWindowsTargeting>true</EnableWindowsTargeting>", StringComparison.Ordinal), "project.windows-targeting");
        C.True(wpf.Contains("<UseWPF>true</UseWPF>", StringComparison.Ordinal), "project.wpf");
        C.True(wpf.Contains("net8.0-windows", StringComparison.Ordinal), "project.tfm");
        C.True(wpf.Contains("<PlatformTarget>x64</PlatformTarget>", StringComparison.Ordinal), "project.x64");
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories))
            C.True(!File.ReadAllText(file).Contains("PackageReference", StringComparison.Ordinal), "project.no-nuget " + Path.GetFileName(file));

        var xaml = File.ReadAllText(Path.Combine(root, "src", "SurfaceFrame", "MainWindow.xaml"));
        C.True(xaml.Contains("WindowStyle=\"None\"", StringComparison.Ordinal), "project.borderless");
        C.True(xaml.Contains("Topmost=\"True\"", StringComparison.Ordinal), "project.topmost");
        C.True(xaml.Contains("ResizeMode=\"NoResize\"", StringComparison.Ordinal), "project.no-resize");
        C.True(xaml.Contains("相册是空的", StringComparison.Ordinal), "project.empty-hint");

        var window = File.ReadAllText(Path.Combine(root, "src", "SurfaceFrame", "MainWindow.xaml.cs"));
        C.True(window.Contains("PhotoImage.Stretch = _config.Album.Crop ? Stretch.UniformToFill : Stretch.Uniform;", StringComparison.Ordinal), "project.fit-or-crop");
        var powerOff = MethodBody(window, "BlackShieldThenMonitorOff");
        C.True(powerOff.IndexOf("Visibility.Visible", StringComparison.Ordinal) >= 0
            && powerOff.IndexOf("Visibility.Visible", StringComparison.Ordinal) < powerOff.IndexOf("MonitorOff", StringComparison.Ordinal),
            "project.shield-before-power-off");
        C.True(powerOff.IndexOf("KeepSystemAwake(false)", StringComparison.Ordinal) >= 0
            && powerOff.IndexOf("KeepSystemAwake(false)", StringComparison.Ordinal) < powerOff.IndexOf("MonitorOff", StringComparison.Ordinal),
            "project.awake-without-display-before-off");
        var mouseDown = MethodBody(window, "OnPreviewMouseLeftButtonDown");
        var mouseUp = MethodBody(window, "OnPreviewMouseLeftButtonUp");
        C.True(!mouseDown.Contains("_runtime.Press", StringComparison.Ordinal)
            && !mouseUp.Contains("_runtime.Press", StringComparison.Ordinal), "project.mouse-does-not-press");

        var power = File.ReadAllText(Path.Combine(root, "src", "SurfaceFrame", "DisplayPower.cs"));
        foreach (var banned in new[] { "SetMonitorBrightness", "WmiMonitorBrightness", "SetDeviceGammaRamp", "IOCTL_VIDEO", "GetDeviceGammaRamp" })
            C.True(!power.Contains(banned, StringComparison.Ordinal), "project.no-" + banned);
        C.True(power.Contains("SC_MONITORPOWER", StringComparison.Ordinal), "project.monitor-power");
        C.True(power.Contains("ES_SYSTEM_REQUIRED", StringComparison.Ordinal) && power.Contains("ES_DISPLAY_REQUIRED", StringComparison.Ordinal), "project.stay-awake");

        var runtime = File.ReadAllText(Path.Combine(root, "src", "SurfaceFrame", "HaRuntime.cs"));
        var start = runtime.IndexOf("void Start()", StringComparison.Ordinal);
        var demo = runtime.IndexOf("if (_demo != null)", start, StringComparison.Ordinal);
        var run = runtime.IndexOf("Task.Run", start, StringComparison.Ordinal);
        C.True(demo >= 0 && run > demo, "project.demo-returns-before-socket");
        C.Equal(CountOf(runtime, "new ClientWebSocket"), 1, "project.one-socket-construction");
    }

    static UserAction Swipe(GestureRecognizer gestures, DateTime time, params int[] distances)
    {
        for (var i = 0; i < distances.Length; i++)
        {
            gestures.TouchDown(i, 400 + i * 30, 100, time.AddMilliseconds(i * 20));
            gestures.TouchMove(i, 400 + i * 30 + distances[i] / 2, 105);
        }
        var action = UserAction.None;
        for (var i = 0; i < distances.Length; i++)
        {
            action = gestures.TouchUp(i, 400 + i * 30 + distances[i], 110, time.AddMilliseconds(100 + i * 20));
            if (i + 1 < distances.Length)
                C.Equal(action, UserAction.None, "gesture.wait-for-all-fingers");
        }
        return action;
    }

    static void V5HomeKitUtility()
    {
        C.True(PointerPolicy.IsCard("scene:evening"), "v5.scene-card");
        C.True(PointerPolicy.TryParseScene("scene:evening", out var scene) && scene == "evening", "v5.scene-parse");

        var combo = new EntityConfig("cover.left", "客厅窗帘", EntityKind.Cover, "客厅", null, new[] { "cover.left", "cover.right" });
        var logic = new HaClientLogic(new[] { combo }, "person.demo", "home", "not_home", "token");
        var now = T0();
        logic.BeginSession(now);
        var _ = logic.OnMessage("{\"type\":\"auth_required\"}", now);
        var sub = logic.OnMessage("{\"type\":\"auth_ok\"}", now).Single();
        var subId = JsonDocument.Parse(sub).RootElement.GetProperty("id").GetInt32();
        var get = logic.OnMessage(Ok(subId), now).Single();
        var getId = JsonDocument.Parse(get).RootElement.GetProperty("id").GetInt32();
        logic.OnMessage(States(getId,
            ("cover.left", "open", "2026-01-01T00:00:00+00:00"),
            ("cover.right", "open", "2026-01-01T00:00:00+00:00"),
            ("person.demo", "home", "2026-01-01T00:00:00+00:00")), now);

        C.True(logic.TryCommand("cover.left", false, now, out var cmdJson) && cmdJson != null, "v5.combo-cmd");
        using (var doc = JsonDocument.Parse(cmdJson!))
        {
            var targetIds = doc.RootElement.GetProperty("target").GetProperty("entity_id").EnumerateArray().Select(x => x.GetString()).ToArray();
            C.Equal(targetIds.Length, 2, "v5.combo-target-count");
            C.Equal(targetIds[1], "cover.right", "v5.combo-target-second");
        }

        C.True(logic.TryCallService("light", "turn_off", new[] { "light.a", "light.b" }, out var svcJson) && svcJson != null, "v5.scene-service");
        using (var doc2 = JsonDocument.Parse(svcJson!))
        {
            C.Equal(doc2.RootElement.GetProperty("service").GetString(), "turn_off", "v5.scene-service-name");
            C.Equal(doc2.RootElement.GetProperty("target").GetProperty("entity_id").GetArrayLength(), 2, "v5.scene-service-targets");
        }

        var summary = UtilityChartData.ParseMonth(
            "{\"2026-11-01\": 5.2, \"2026-11-24\": 8.4}",
            "{\"2026-11-01\": null, \"2026-11-24\": 4.1}",
            "{\"2026-11-01\": 10.0, \"2026-11-24\": 14.0}",
            "{\"2026-11-24\": 1.2}");
        C.Equal(summary.Days.Count, 2, "v5.utility-days");
        C.Equal(summary.Days[0].DayLabel, "1", "v5.utility-day1");
        C.Equal(summary.Days[1].DayLabel, "24", "v5.utility-day24");
        C.True(Math.Abs(summary.Elec7dKwh - 24.0) < 0.01, "v5.utility-elec-7d");
        C.True(Math.Abs(summary.Gas7dM3 - 1.2) < 0.01, "v5.utility-gas-7d");
        C.True(summary.PeakTotalCost > 12.5, "v5.utility-peak");
    }

    static void V6RandomAndPin()
    {
        using (var req = ImmichSync.BuildRandomSearchRequest("https://immich.example/", "alb-1", "key-1", 60))
        {
            C.Equal(req.Method, HttpMethod.Post, "v6.random-method");
            C.True(req.RequestUri!.AbsolutePath.EndsWith("/api/search/random", StringComparison.Ordinal), "v6.random-path");
            C.Equal(string.Join(",", req.Headers.GetValues("x-api-key")), "key-1", "v6.random-key");
            var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            C.True(body.Contains("\"albumIds\":[\"alb-1\"]", StringComparison.Ordinal)
                && body.Contains("\"type\":\"IMAGE\"", StringComparison.Ordinal)
                && body.Contains("\"size\":60", StringComparison.Ordinal), "v6.random-body");
        }

        var root = Scratch("v6-random");
        var cache = Path.Combine(root, "cache");
        var local = Path.Combine(root, "local");
        Directory.CreateDirectory(local);
        var jpg = new byte[] { 0xff, 0xd8, 0xff, 0xe0, 0, 16, 74, 70, 73, 70, 0, 1 };
        File.WriteAllBytes(Path.Combine(local, "local.jpg"), jpg);

        var randHandler = new RecordingHandler
        {
            Handle = request =>
            {
                var uri = request.RequestUri!.AbsolutePath;
                if (uri.EndsWith("/api/search/random", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                    {
                        Content = new StringContent("""
                        [
                          {"id":"b-second","type":"IMAGE"},
                          {"id":"missing-404","type":"IMAGE"},
                          {"id":"a-first","type":"IMAGE","livePhotoVideoId":"vid-a"}
                        ]
                        """, Encoding.UTF8, "application/json")
                    };
                }
                if (uri.Contains("/missing-404/", StringComparison.Ordinal))
                    return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(jpg) };
            }
        };
        using (var http = new HttpClient(randHandler))
        {
            var res = ImmichSync.SyncAsync(http, "https://immich.example", "alb-1", "key-1", cache, local, CancellationToken.None, 60, null, randomSample: true)
                .GetAwaiter().GetResult();
            C.Equal(res.Status, ImmichStatus.Updated, "v6.random-updated");
            C.Equal(res.Files.Count, 2, "v6.random-skip-404");
            C.True(res.Files[0].EndsWith("b-second.jpg", StringComparison.Ordinal), "v6.random-order-first");
            C.True(res.Files[1].EndsWith("a-first.jpg", StringComparison.Ordinal), "v6.random-order-second");
        }

        using (var http = new HttpClient(new RecordingHandler { Handle = _ => throw new HttpRequestException("offline") }))
        {
            var offline = ImmichSync.SyncAsync(http, "https://immich.example", "alb-1", "key-1", cache, local, CancellationToken.None, 60, null, randomSample: true)
                .GetAwaiter().GetResult();
            C.Equal(offline.Status, ImmichStatus.KeptCache, "v6.random-offline-cache");
            C.Equal(offline.Files.Count, 2, "v6.random-offline-count");
        }

        var items = new[] { "a", "b", "c", "d", "e" };
        var shuffled = SlideDeck.Shuffle(items, new Random(42));
        C.Equal(shuffled.Count, items.Length, "v6.shuffle-count");
        C.True(shuffled.OrderBy(x => x).SequenceEqual(items), "v6.shuffle-same-elements");
        var deck = new SlideDeck();
        deck.Reset(new[] { new PhotoSlide("1.jpg"), new PhotoSlide("2.jpg") });
        deck.Go(1);
        C.Equal(deck.CurrentIndex, 0, "v6.deck-index-0");
        deck.Go(1);
        C.Equal(deck.CurrentIndex, 1, "v6.deck-index-1");

        var pinCfgPath = Path.Combine(root, "pin-frame.json");
        File.WriteAllText(pinCfgPath, "{\"album\":{\"folder\":\"Photos\",\"pin\":\"2468\"}}");
        var pinCfg = ConfigLoader.Load(pinCfgPath, null, null);
        C.Equal(pinCfg.Album.Pin, "2468", "v6.config-pin");

        var noPinPath = Path.Combine(root, "nopin-frame.json");
        File.WriteAllText(noPinPath, "{\"album\":{\"folder\":\"Photos\"}}");
        var noPinCfg = ConfigLoader.Load(noPinPath, null, null);
        C.True(noPinCfg.Album.Pin == null, "v6.config-no-pin");

        C.True(PointerPolicy.TryParseAlbum("album:pin:1", out var pinAct) && pinAct == "pin:1", "v6.policy-pin-tag");
    }

    static void V7PhotoMeta()
    {
        C.Equal(PhotoMetaFormatter.FormatDate("2026-04-04T09:34:08+08:00"), "2026.04.04", "v7.date-iso");
        C.True(PhotoMetaFormatter.FormatDate("") == null, "v7.date-empty");

        C.Equal(PhotoMetaFormatter.FormatCity("China", "Beijing", "Haidian", 39.96, 116.30), "北京", "v7.city-beijing-gps");
        C.Equal(PhotoMetaFormatter.FormatCity("China", "Shanghai", "Pudong", 31.22, 121.54), "上海", "v7.city-shanghai-gps");
        C.Equal(PhotoMetaFormatter.FormatCity("China", "Hainan", "Jiyang", 18.25, 109.51), "三亚", "v7.city-sanya-gps");
        C.Equal(PhotoMetaFormatter.FormatCity("China", "Zhejiang", "Xihu", 30.26, 120.13), "杭州", "v7.city-hangzhou-gps");
        C.Equal(PhotoMetaFormatter.FormatCity("Japan", "Tokyo", "Shibuya", 35.66, 139.70), "东京", "v7.city-tokyo-gps");
        C.Equal(PhotoMetaFormatter.FormatCity("Japan", "Yamanashi", "Narusawa", 35.51, 138.75), "富士山", "v7.city-fuji-gps");
        C.Equal(PhotoMetaFormatter.FormatCity("Norway", "Troms", "Tromsø", 69.65, 18.95), "特罗姆瑟", "v7.city-tromso");
        C.Equal(PhotoMetaFormatter.FormatCity("United Arab Emirates", "Abu Dhabi", "Abu Dhabi", 24.45, 54.38), "阿布扎比", "v7.city-abudhabi");
        C.True(PhotoMetaFormatter.FormatCity(null, null, null, null, null) == null, "v7.city-empty");

        C.Equal(PhotoMetaFormatter.FormatDevice("SONY", "ILCE-7M3"), "Sony A7M3", "v7.dev-sony-a7m3");
        C.Equal(PhotoMetaFormatter.FormatDevice("SONY", "ILCE-7RM5"), "Sony A7R5", "v7.dev-sony-a7r5");
        C.Equal(PhotoMetaFormatter.FormatDevice("Apple", "iPhone 17 Pro Max"), "iPhone 17 Pro Max", "v7.dev-iphone");
        C.Equal(PhotoMetaFormatter.FormatDevice("FUJIFILM", "X-S10"), "Fujifilm X-S10", "v7.dev-fuji");
        C.True(PhotoMetaFormatter.FormatDevice(null, null) == null, "v7.dev-empty");

        var parsed = ImmichSync.ParseRandomAssets("""
        [
          {
            "id": "asset-meta-1",
            "type": "IMAGE",
            "localDateTime": "2026-04-04T09:34:08.000Z",
            "exifInfo": {
              "city": "Shibuya",
              "state": "Tokyo",
              "country": "Japan",
              "latitude": 35.66,
              "longitude": 139.70,
              "make": "SONY",
              "model": "ILCE-7M3"
            }
          }
        ]
        """);
        C.Equal(parsed.Count, 1, "v7.parse-count");
        C.Equal(parsed[0].Metadata?.Date, "2026.04.04", "v7.parse-date");
        C.Equal(parsed[0].Metadata?.City, "东京", "v7.parse-city");
        C.Equal(parsed[0].Metadata?.Device, "Sony A7M3", "v7.parse-device");

        var dir = Scratch("v7-meta-map");
        ImmichSync.SaveMetaMap(dir, new Dictionary<string, PhotoMetadata> { ["asset-meta-1"] = parsed[0].Metadata! });
        var loaded = ImmichSync.LoadMetaMap(dir);
        C.Equal(loaded["asset-meta-1"].City, "东京", "v7.metamap-roundtrip");

        var openCfg = ConfigLoader.Load(Path.Combine(RepoRoot(), "frame.example.json"), null, null);
        C.True(openCfg.HomeAssistant.Scenes is { Count: >= 1 }, "v8.example-scenes");
        C.True(openCfg.HomeAssistant.Dashboard != null, "v8.example-dashboard");
        C.True(openCfg.HomeAssistant.AllTrackedInfoEntities().Any(e => e.EntityId == "weather.demo_home"), "v8.tracked-dashboard");
    }

    static bool CanShow(string path) => !Path.GetFileName(path).Contains("bad", StringComparison.OrdinalIgnoreCase);

    static string? Name(string? path) => path == null ? null : Path.GetFileName(path);

    static DateTime T0() => new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    static List<EntityConfig> DemoEntities() => new()
    {
        new("light.demo_ceiling", "客厅主灯", EntityKind.Light),
        new("switch.demo_plug", "插座", EntityKind.Switch),
        new("sensor.demo_temp", "温度", EntityKind.Sensor)
    };

    static HaClientLogic NewLogic()
        => new(DemoEntities(), "person.demo", "home", "not_home", "placeholder-token");

    static void DriveLive(HaClientLogic logic, DateTime now)
    {
        logic.BeginSession(now);
        var auth = logic.OnMessage("{\"type\":\"auth_required\"}", now);
        _ = auth;
        var subscribe = logic.OnMessage("{\"type\":\"auth_ok\"}", now).Single();
        var subscribeId = JsonDocument.Parse(subscribe).RootElement.GetProperty("id").GetInt32();
        var get = logic.OnMessage(Ok(subscribeId), now).Single();
        var getId = JsonDocument.Parse(get).RootElement.GetProperty("id").GetInt32();
        logic.OnMessage(States(getId,
            ("light.demo_ceiling", "off", "2026-01-01T00:00:00+00:00"),
            ("switch.demo_plug", "off", "2026-01-01T00:00:00+00:00"),
            ("sensor.demo_temp", "22.5", "2026-01-01T00:00:00+00:00"),
            ("person.demo", "home", "2026-01-01T00:00:00+00:00")), now);
        if (logic.Phase != ConnPhase.Live)
            throw new InvalidOperationException(logic.Phase.ToString());
    }

    static string Ok(int id) => "{\"id\":" + id + ",\"type\":\"result\",\"success\":true,\"result\":null}";

    static string Changed(string entityId, string state, string stamp)
        => "{\"type\":\"event\",\"event\":{\"event_type\":\"state_changed\",\"time_fired\":\"" + stamp
            + "\",\"data\":{\"entity_id\":\"" + entityId + "\",\"new_state\":{\"entity_id\":\"" + entityId
            + "\",\"state\":\"" + state + "\",\"last_changed\":\"" + stamp + "\",\"attributes\":{}}}}}";

    static string States(int id, params (string Id, string State, string Stamp)[] items)
    {
        var parts = items.Select(item => "{\"entity_id\":\"" + item.Id + "\",\"state\":\"" + item.State
            + "\",\"last_changed\":\"" + item.Stamp + "\",\"attributes\":{}}");
        return "{\"id\":" + id + ",\"type\":\"result\",\"success\":true,\"result\":[" + string.Join(",", parts) + "]}";
    }

    static bool IsCall(string json) => json.Contains("\"call_service\"", StringComparison.Ordinal);

    static bool IsPing(string json) => json.Contains("\"ping\"", StringComparison.Ordinal);

    static (int Id, string Domain, string Service, string Entity) ReadCall(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.GetProperty("type").GetString() != "call_service")
            throw new InvalidOperationException(json);
        var entity = root.GetProperty("target").GetProperty("entity_id").GetString()!;
        return (
            root.GetProperty("id").GetInt32(),
            root.GetProperty("domain").GetString()!,
            root.GetProperty("service").GetString()!,
            entity);
    }

    static string Scratch(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, name);
        if (Directory.Exists(path))
            Directory.Delete(path, true);
        Directory.CreateDirectory(path);
        return path;
    }

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "frame.example.json")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("frame.example.json");
    }

    static string MethodBody(string source, string name)
    {
        var start = source.IndexOf("void " + name, StringComparison.Ordinal);
        if (start < 0)
            throw new InvalidOperationException(name);
        var brace = source.IndexOf('{', start);
        var depth = 0;
        for (var i = brace; i < source.Length; i++)
        {
            if (source[i] == '{')
                depth++;
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                    return source.Substring(brace, i - brace + 1);
            }
        }
        throw new InvalidOperationException(name);
    }

    static int CountOf(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }

    sealed class Lab
    {
        public HaClientLogic Logic { get; }
        public DemoBroker Broker { get; }

        public Lab()
        {
            var entities = DemoEntities();
            Logic = new HaClientLogic(entities, "person.demo", "home", "not_home", "placeholder-token");
            Broker = new DemoBroker(entities, "person.demo", "unknown");
        }

        public void Handshake(DateTime now)
        {
            Logic.BeginSession(now);
            Drain(Broker.Start(now), now);
            if (Logic.Phase != ConnPhase.Live)
                throw new InvalidOperationException("handshake " + Logic.Phase);
        }

        public void Drain(IEnumerable<string> seed, DateTime now)
        {
            var queue = new Queue<string>(seed);
            var guard = 0;
            while (queue.Count > 0 && guard++ < 40)
            {
                var message = queue.Dequeue();
                foreach (var outgoing in Logic.OnMessage(message, now))
                    foreach (var response in Broker.Accept(outgoing, now))
                        queue.Enqueue(response);
            }
        }
    }

    sealed class RecordingHandler : HttpMessageHandler
    {
        public List<(string Method, string Uri, string? Key)> Seen { get; } = new();
        public Func<HttpRequestMessage, HttpResponseMessage> Handle { get; set; }
            = _ => throw new HttpRequestException("unset");

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? key = null;
            if (request.Headers.TryGetValues("x-api-key", out var values))
                key = string.Join(",", values);
            Seen.Add((request.Method.Method, request.RequestUri?.ToString() ?? "", key));
            return Task.FromResult(Handle(request));
        }
    }

    sealed class Check
    {
        public int Failed { get; private set; }

        public void Run(string name, Action body)
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                Fail(name + " exception " + ex);
            }
        }

        public void True(bool condition, string name)
        {
            if (condition)
                Console.WriteLine("PASS " + name);
            else
                Fail(name);
        }

        public void Equal<T>(T actual, T expected, string name)
        {
            if (EqualityComparer<T>.Default.Equals(actual, expected))
                Console.WriteLine("PASS " + name);
            else
                Fail(name + " expected=" + expected + " actual=" + actual);
        }

        void Fail(string name)
        {
            Console.WriteLine("FAIL " + name);
            Failed++;
        }
    }
}
