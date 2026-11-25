# SurfaceFrame

A lightweight, native **.NET 8 WPF** digital photo frame and **Home Assistant** wall-dashboard designed for Windows touch tablets (tested on **Surface Go**, Windows 11 Pro x64, 3:2 landscape). Zero Electron, zero WebView, and **zero third-party NuGet dependencies**.

[中文说明](#中文说明)

---

## Highlights

- **Gallery Photo Frame (Screen 1)**
  - **Smart Full-Screen Framing**: Exact center-crop (`CenterFillPanel`) with automatic **2-up side-by-side portrait pairing** so vertical photos fill the 3:2 display naturally without awkward sidebars.
  - **Whole-Album Random Immich Sync**: Uses Immich's `POST /api/search/random` (with `withExif: true`) to continuously rotate fresh random batches across arbitrarily large albums, with atomic local disk caching for seamless offline fallback.
  - **Muted Live Photo Playback**: Automatically plays iOS Live Photos embedded in Immich via Windows Media Foundation (`mfreadwrite.dll`), muted and orientation-corrected.
  - **Minimalist Gallery EXIF Overlay**: Clean bottom-right vertical hairline badge showing localized city name, capture date (`YYYY.MM.DD`), and prettified camera model (`SONY ILCE-7M3` → `Sony A7M3`), collapsing empty fields cleanly.
  - **PIN-Protected Album Switcher**: Optional numeric touch keypad (`album.pin`) prevents accidental album changes on a shared wall/desk tablet.
- **HomeKit-Style Touch Control Panel (Screen 2 — 3-finger swipe left)**
  - Single-screen daylight-white layout (`1200×800` DIPs, zero scrolling).
  - Config-driven **Scene Pills** (`trigger`, `turn_off`, `toggle`), **4×3 Light Grid**, **Multi-Entity Combined Curtains** (`entityIds`), and **Switch Controls** with explicit state confirmation, 5-second timeout detection, and one-tap retry.
- **Bento Status & Energy Dashboard (Screen 3 — 3-finger swipe right)**
  - Fully config-driven Bento cards: Outdoor weather + indoor rooms, EV battery/range/climate, heating & AC, **monthly daily stacked electricity/gas cost bar chart**, and appliance/UPS device statuses.
- **Presence-Driven & Remote Display Control**
  - Watches one configured Home Assistant presence entity (`person.*` / `device_tracker.*`).
  - Optional remote Home Assistant helpers (`remoteLockEntityId` / `remoteAlbumEntityId`, e.g. `input_boolean.surface_frame_lock` & `input_select.surface_frame_album`) allow locking/waking the frame or switching Immich albums from Home Assistant, HomeKit, or AI assistants via HA MCP.
  - After 10 consecutive minutes away (pausing on `unknown`/`unavailable`/disconnect), covers the screen with a black shield and requests true Windows display power-off (`SC_MONITORPOWER`), keeping Windows awake/reachable over SSH/RustDesk; automatically wakes straight back to photos when home.
  - Leaves ambient-light auto-brightness to Windows native adaptive brightness (`ADAPTBRIGHT`).

## Controls & Gestures

| Input | Action |
| --- | --- |
| Tap left / right half (Photos) | Previous / Next slide |
| 3-finger swipe left | Photos → Control Panel (or Status → Photos) |
| 3-finger swipe right | Photos → Status Dashboard (or Control Panel → Photos) |
| Top-right album button | Open Album Switcher (prompts PIN keypad if `album.pin` is set) |
| `F12` | Toggle page (maintenance) |
| `Ctrl + Shift + M` | Temporary wake override while away |
| `Ctrl + Shift + X` | Exit application |

> Control and Status screens automatically return to Photos after 2 minutes of inactivity.

## Quick Start & Build

Requires **.NET 8 SDK** on Windows x64:

```powershell
# Run unit & invariant checks
dotnet run --project .\src\SurfaceFrame.Checks\SurfaceFrame.Checks.csproj -c Release

# Build self-contained win-x64 release ZIP in dist/
.\publish-win-x64.ps1
```

### Configuration

1. Copy `frame.example.json` to `frame.json` next to `SurfaceFrame.exe` (keep `demo.enabled: true` to explore the UI offline first).
2. Copy `frame.private.example.json` to `frame.private.json` and fill in your `homeAssistantToken` and `immichApiKey` (or set `HA_TOKEN` / `IMMICH_API_KEY` environment variables). **Never commit `frame.json` or `frame.private.json`.**
3. Set `demo.enabled: false`, configure your `homeAssistant.url`, `entities`, `scenes`, `dashboard`, and `immich` settings in `frame.json`.
   - Combine dual curtains into one card using `"entityIds": ["cover.left", "cover.right"]`.
   - Set `"album": { "pin": "1234" }` to lock album switching behind a touch PIN keypad (leave empty to disable).

## Windows Tablet Kiosk Tips (Surface Go)

- **Disable 3/4-finger system gestures**: Settings → Bluetooth & devices → Touch → turn off *Three- and four-finger touch gestures*.
- **Disable Edge Swipe**: In `gpedit.msc` → *Computer Configuration → Administrative Templates → Windows Components → Edge UI* → disable *Allow edge swipe*.
- **Power & Ambient Light**: Let SurfaceFrame control display sleep while plugged in, and enable Windows Adaptive Brightness:
  ```powershell
  powercfg /change standby-timeout-ac 0
  powercfg /change monitor-timeout-ac 0
  powercfg /setacvalueindex SCHEME_CURRENT SUB_VIDEO ADAPTBRIGHT 1
  powercfg /setdcvalueindex SCHEME_CURRENT SUB_VIDEO ADAPTBRIGHT 1
  powercfg /setactive SCHEME_CURRENT
  ```
- **Auto-start at logon**: Register an interactive Scheduled Task pointing to `%LOCALAPPDATA%\SurfaceFrame\SurfaceFrame.exe`.

---

## 中文说明

**SurfaceFrame** 是专为 Windows 触屏平板（在 **Surface Go** / Windows 11 Pro x64 横屏实测）打造的轻量级原生 **.NET 8 WPF** 电子相框与 **Home Assistant** 家居控制面板，**零 Electron、零浏览器内核、零第三方 NuGet 依赖**。

### 核心特性

1. **画廊级电子相框（主屏）**
   - 居中裁切满屏展示，竖图自动两张并排拼屏填满 3:2 屏幕。
   - 对接 **Immich** 原生 `/api/search/random` 接口，在所选相册的全部照片里持续随机抽取滚动缓冲，断网自动回退本地缓存。
   - 基于 Windows Media Foundation 自动静音播放 **Live Photo**，支持旋转方向校正。
   - 右下角画廊竖线风格 EXIF 角标：自动归并中文城市名、`YYYY.MM.DD` 日期与美化后的相机型号（如 `ILCE-7M3` → `Sony A7M3`），缺失字段自动隐藏。
   - 支持配置触控数字密码锁（`album.pin`），更改相册前需验证密码。
2. **HomeKit 风格触控面板（三指左滑）**
   - 单屏浅色无滚动布局，通过 `frame.json` 自定义顶部场景模式按钮、4×3 灯光卡片、多实体二合一窗帘（`entityIds`）与开关控制；带 5 秒确认超时与一键重试。
3. **Bento 状态与水电气走势屏（三指右滑）**
   - 全部卡片通过 `homeAssistant.dashboard` 配置驱动：室外天气与室内各房间温湿度、电动车电量/续航/温控、暖通地暖与空调、**本月电费/燃气每日堆叠柱状走势图**，以及扫地机/电视/UPS 等设备状态。
4. **离家真熄屏 / 回家自动唤亮**
   - 监听单个 Home Assistant 在家实体，确认离家满 10 分钟后先盖黑屏遮罩再调用 Win32 关闭显示器背光（系统保持在线可 SSH/远程），回家自动唤亮回相册；亮度由 Windows 光感自适应亮度接管。

### 配置与部署

1. 将 `frame.example.json` 复制为 `frame.json`，将 `frame.private.example.json` 复制为 `frame.private.json` 并填入 HA Token 与 Immich API Key（或使用环境变量 `HA_TOKEN` / `IMMICH_API_KEY`）。
2. 在装有 `.NET 8 SDK` 的 Windows x64 机器上运行 `.\publish-win-x64.ps1` 即可自动执行全部单元测试并生成免安装独立发布包（`dist/`）。
3. 推荐部署至 `%LOCALAPPDATA%\SurfaceFrame`，通过计划任务配置登录后自动启动。

## License

[MIT](LICENSE)
