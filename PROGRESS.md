# GuildHub — Tiến độ dự án & Tài liệu chuyển giao

> **Cập nhật lần cuối:** 26/11/2026  
> **Trạng thái tổng quan:**
> - ✅ **Giai đoạn 0 (Discord Bot nền tảng):** Hoàn thành & đang vận hành thực tế (`guildhub-bot`, `guildhub-bot-demo`).
> - ✅ **Giai đoạn 1 (Web Quản trị Bang hội — Nền tảng, Dashboard, Thành viên & Cấu hình Server/Hệ phái):** Hoàn thành 100%, đã build `Release` sạch (`0 Warning, 0 Error`).
> - ⏳ **Giai đoạn 2 (Lịch hoạt động — Schedule & Phân tích bang hội / Bảng chuyên cần):** Sẵn sàng triển khai.
> - ⏳ **Giai đoạn 3 (Đội hình bang chiến 60vs60 — Lineup Builder kéo-thả):** Chưa triển khai.

---

## 1. Các tính năng và file chính đã hoàn thành ở Giai đoạn 1

### 1.1. Tách lõi dùng chung (`src/GuildHub.Core`)
Toàn bộ Domain, Enums, Metadata, EF Core `DbContext` và Migrations đã được tách thành thư viện dùng chung cho cả `GuildHub.Bot` và `GuildHub.Web`:
- **Entities:**
  - `src/GuildHub.Core/Domain/Entities/WarEvent.cs` — Sự kiện bang chiến / giao hữu tạo từ Bot (hoặc từ Schedule ở GĐ 2).
  - `src/GuildHub.Core/Domain/Entities/EventSignup.cs` — Danh sách đăng ký tham gia sự kiện (`Đi`, `Dự bị`, `Bận`).
  - `src/GuildHub.Core/Domain/Entities/ClassEmoji.cs`, `UiEmoji.cs` — Emoji Discord tự động đồng bộ từ thư mục `Icon/`.
  - `src/GuildHub.Core/Domain/Entities/GuildConfig.cs` — Cấu hình theo từng Server Discord (`MemberRoleId`, `MemberRoleName`, lịch sử sync gần nhất).
  - `src/GuildHub.Core/Domain/Entities/GuildMember.cs` — Hồ sơ thành viên bang (Discord ID, username, display name, nickname ingame, avatar, hệ phái, trạng thái role/remove).
  - `src/GuildHub.Core/Domain/Entities/GuildClassConfig.cs` — Cấu hình hệ phái theo từng Server (tên hiển thị, vai trò Tank/DPS/Healer, mã màu HEX, file icon riêng, Role Discord tương ứng khi Link, hỗ trợ hệ phái tùy chỉnh `IsCustom`).
- **Metadata & DbContext:**
  - `src/GuildHub.Core/Metadata/ClassMetadata.cs` — Metadata mặc định cho 8 hệ phái gốc (Tố Vấn, Thiết Y, Thần Tương, Cửu Linh, Huyết Hà, Toái Mộng, Long Ngâm, Huyền Cơ) + 13 UI icons; hỗ trợ `TryGet(...)` an toàn cho hệ phái tùy chỉnh.
  - `src/GuildHub.Core/Data/GuildHubDbContext.cs` — Ánh xạ đầy đủ 7 bảng PostgreSQL với `ulong <-> long` ValueConverter.

### 1.2. Xác thực Discord OAuth2 (Chỉ Admin / Manage Server)
- `src/GuildHub.Web/Auth/DiscordOAuthEndpoints.cs` & `DiscordAuthModels.cs`
- `src/GuildHub.Web/Components/Pages/Login.razor` & `Components/Shared/RedirectToLogin.razor`
- **Tính năng đã làm:**
  - Đăng nhập qua Discord OAuth2 (`identify guilds`), đối chiếu chỉ cho phép tài khoản có quyền `Administrator` hoặc `ManageGuild` trên các server mà Bot đang tham gia.
  - Sử dụng `prompt=none` cho lần đăng nhập sau để không phải bấm **Authorize** lại nếu đã cấp quyền; có tùy chọn **Đổi tài khoản Discord** (`?switch=true` → `prompt=consent`).
  - Lưu khóa DataProtection ra ổ đĩa (`/app/keys` mount `./keys-web`) + Cookie 30 ngày (`SlidingExpiration`) để giữ phiên đăng nhập ổn định kể cả khi rebuild container Web.

### 1.3. Cơ chế đồng bộ Discord 3 lớp (Real-time + Định kỳ + Thủ công)
- **Lớp 1 — Real-time Gateway Events trên Bot:** `src/GuildHub.Bot/Services/MemberRealtimeSyncService.cs`
  - Lắng nghe `GuildMemberUpdated`, `UserJoined`, `UserLeft` trực tiếp từ Discord Gateway để tự động cập nhật `guild_members` (Role bang, Nickname server, Avatar, DisplayName, Username, tự map hệ phái khi có Role hệ phái, và tự cập nhật lại trạng thái khi rời server / mất Role).
  - Khi thành viên chọn hệ phái trên form Bot (`EventInteractionModule.cs`), tự động đồng bộ `GameClass` về hồ sơ `guild_members` nếu thành viên chưa link phái.
- **Lớp 2 — Snapshot đối soát định kỳ 30 phút:** `src/GuildHub.Bot/Services/PeriodicMemberSyncHostedService.cs`
  - Quét lại danh sách thành viên server định kỳ mỗi 30 phút để vá mọi sự kiện có thể bị sót khi Bot mất kết nối tạm thời.
- **Lớp 3 — Nút Sync Discord thủ công trên Web:** `src/GuildHub.Web/Services/DiscordSyncService.cs` & `MainLayout.razor`
  - Chạy on-demand qua REST API với cơ chế **cooldown 15 giây** tránh spam rate-limit Discord, hiển thị thời gian đồng bộ và số thành viên gần nhất ở góc trên bên phải.

### 1.4. Giao diện & Các trang Web chính (`src/GuildHub.Web`)
- **Shell & Điều hướng:**
  - `src/GuildHub.Web/Components/Layout/MainLayout.razor` — Sidebar icon gọn (Tổng quan, Phân tích, Lịch hoạt động, Thành viên, Cấu hình, Đội hình), chuyển đổi Server (khi Admin quản lý nhiều server), tìm kiếm nhanh, nút Sync Discord & Đăng xuất.
  - `src/GuildHub.Web/Components/Shared/SearchSelect.razor` — Component dropdown tìm kiếm kiểu **Select2** viết thuần Blazor + Tailwind CSS (hỗ trợ tìm kiếm tiếng Việt không dấu, Avatar tròn, Icon hệ phái, chấm màu Role, nút xóa nhanh). **Đã thay thế 100% thẻ `<select>` trên toàn bộ dự án.**
- **Trang Tổng quan (`/`) — `Home.razor` + `DashboardService.cs`:**
  - 4 thẻ KPI: Tổng thành viên, Đã link phái, Chưa link phái, Số sự kiện/tuần.
  - 3 biểu đồ SVG Donut (`DonutChart.razor`): Phân bố môn phái (dùng màu cấu hình theo từng hệ phái), Phân bố vai trò (Tank / DPS / Healer), Trạng thái thành viên.
  - 3 vòng tròn tỷ lệ (`RingGauge.razor`): Tỷ lệ Tank, DPS, Healer.
  - Bảng thành viên tích hợp bên dưới, bấm vào KPI có thể lọc nhanh.
- **Trang Quản lý thành viên (`/members`) — `Members.razor` + `MemberDirectoryPanel.razor` + `MemberManagementService.cs`:**
  - Bộ lọc tự động áp dụng ngay khi gõ/chọn (debounce 250ms cho ô tìm kiếm, `<SearchSelect>` cho Phái, Trạng thái, Role Discord, Số dòng).
  - Sửa **Nickname** trực tiếp trên bảng → đồng bộ 2 chiều lên Discord (`Manage Nicknames`) & DB.
  - **Liên kết / Đổi / Hủy liên kết môn phái** trực tiếp trên từng dòng → tự động gán/gỡ **Role hệ phái** tương ứng trên server Discord.
  - **Remove** → xác nhận và gỡ trực tiếp **Role bang** trên Discord + chuyển hồ sơ sang mục *Đã remove*; hỗ trợ **Khôi phục** (gán lại Role bang trên Discord) hoặc **Xóa hẳn**.
  - Modal **Thêm thành viên mới**: chọn trực tiếp thành viên từ server Discord chưa có Role bang qua `<SearchSelect>` (có avatar + `@username`), nhập Nickname (tùy chọn), chọn Môn phái → tự gán Role bang, Role hệ phái và đổi Nick trên Discord.
- **Trang Cấu hình Server & Hệ phái (`/settings`) — `Settings.razor`:**
  - Cấu hình **Role thành viên Discord** đại diện cho bang (dùng để lọc sync & gỡ/gán khi Remove/Restore/Add).
  - Bảng **Cấu hình Hệ phái** full-width: upload icon riêng từng hệ phái (`.png`/`.webp` ≤ 512KB), chọn màu hiển thị (color picker + mã HEX), đổi tên hệ phái, chọn vai trò (`Tank`/`DPS`/`Healer`), gán **Role Discord tương ứng khi Link**, nút **Thêm hệ phái mới** (custom class) và nút xóa cho hệ phái tùy chỉnh.
- **Các trang giữ chỗ cho GĐ 2 & 3:** `Schedule.razor`, `Analytics.razor`, `Lineup.razor`.

---

## 2. Các quyết định kỹ thuật & cấu trúc DB quan trọng cần giữ nguyên

### 2.1. Kiến trúc & Công nghệ cốt lõi
- **Runtime:** `.NET 8` (`GuildHub.Core`, `GuildHub.Bot`, `GuildHub.Web`), PostgreSQL 18 chạy native trên host Windows (`port 5432`), Docker containers kết nối qua `host.docker.internal:5432` (`extra_hosts: host.docker.internal:host-gateway`).
- **Docker Compose (`docker-compose.yml`):**
  - `bot` (`guildhub-bot`) → DB `guildhub`, mount `./logs` và `./Icon:ro`.
  - `web` (`guildhub-web`, port `5080:8080`) → DB `guildhub`, mount `./logs-web`, `./keys-web`, `./Icon` (read-write để upload icon hệ phái).
  - `bot-demo` (`guildhub-bot-demo`, profile `demo`) → DB riêng `guildhub_demo` để demo không ảnh hưởng bang thật.
- **Serilog:** Lưu log xoay vòng 30 ngày tách riêng cho Bot (`./logs`) và Web (`./logs-web`).

### 2.2. Quy ước Database & EF Core Migrations (Rất quan trọng)
- **KHÔNG BAO GIỜ drop/reset DB `guildhub`** vì đang chứa dữ liệu đăng ký bang chiến thực tế. Mọi thay đổi schema ở GĐ 2 & 3 phải dùng **additive EF Core migration**.
- **Nơi chứa Migrations:** Tất cả migrations nằm trong `src/GuildHub.Core/Data/Migrations/` (`b => b.MigrationsAssembly("GuildHub.Core")`), tự động apply khi Bot hoặc Web khởi động (`AutoMigrateDatabase: true`).
- **Lệnh tạo Migration mới:**
  ```powershell
  dotnet ef migrations add <TenMigration> --project src/GuildHub.Core --startup-project src/GuildHub.Bot --output-dir Data/Migrations
  ```
- **7 Migrations hiện có (giữ nguyên thứ tự):**
  1. `20261125085902_InitialCreate`
  2. `20261125092200_AddEventCloseTimeAndSignupOptions`
  3. `20261125094417_AddUiEmojis`
  4. `20261125104731_AddOnlineReminderSent`
  5. `20261126043141_AddGuildMembersAndGuildConfigs`
  6. `20261126050745_AddGuildClassConfigs`
  7. `20261126062838_AddClassColorAndCustomClasses`
- **Quy ước bảng & cột PostgreSQL:**
  - `snake_case` cho toàn bộ tên bảng và cột.
  - Discord Snowflakes (`ulong` / `ulong?`) luôn lưu kiểu `bigint` (`long` / `long?`) qua `unchecked` ValueConverter.
  - Index unique một phần trên `guild_members`: `(guild_id, discord_user_id) WHERE discord_user_id IS NOT NULL`.

### 2.3. Các ràng buộc & bài học kỹ thuật bắt buộc tuân thủ
1. **Hệ phái tùy chỉnh (`IsCustom = true`):**
   - Các hệ phái thêm mới có ID số nguyên `9, 10, ...` lưu trong cột `integer game_class` và ép kiểu `(GameClass)id`.
   - **TUYỆT ĐỐI KHÔNG dùng `Enum.IsDefined(typeof(GameClass), cInt)`** khi đọc/validate `GameClass` — luôn kiểm tra `cInt > 0` hoặc tra trong `GuildClassConfigs` và dùng `ClassMetadata.TryGet(...)`.
2. **UI Consistency & UX:**
   - **Tất cả dropdown trong toàn bộ Web (bao gồm GĐ 2 & GĐ 3) bắt buộc dùng `<SearchSelect>`** (kiểu Select2 thuần Blazor + Tailwind, không cài jQuery Select2 để tránh xung đột render tree của Blazor).
   - **Không đặt các thông báo hướng dẫn setup kỹ thuật rườm rà trên UI** — giữ giao diện sạch, trực diện cho quản trị bang.
   - Tránh đặt `overflow-hidden` trên card/modal chứa `<SearchSelect>` để dropdown không bị cắt.
3. **Discord Bot & Discord API:**
   - `InteractionService` chạy với `RunMode.Sync` + `UseCompiledLambda = false` và offload bằng `Task.Run` để không bị dispose DI scope giữa chừng.
   - Bot sử dụng `GatewayIntents.Guilds | GatewayIntents.GuildMembers`; yêu cầu quyền `Manage Roles` (role Bot cao hơn Role bang & Role hệ phái), `Manage Nicknames`, `Mention Everyone`, `Use External Emojis`.
   - Embed Discord giới hạn 1024 ký tự/field và 6000 ký tự/embed — `EventMessageBuilder.cs` đã có auto-compact mode, cần giữ nguyên khi chỉnh sửa form.
   - Xuất Excel bằng `ClosedXML` (`EventExcelExporter.cs`) dùng độ rộng cột cố định, **không dùng `AdjustToContents()`** để chạy mượt trên Linux Docker không cần cài font hệ thống.
4. **An toàn thao tác file & bảo mật:**
   - Khi sửa file có tiếng Việt trên Windows, dùng tool `edit_file`/`write_file` hoặc UTF-8 rõ ràng (không dùng `Get-Content`/`Set-Content` mặc định của PowerShell 5.1 gây lỗi font).
   - Không bao giờ commit hay in các bí mật trong `.env` / `appsettings.json`.

---

## 3. Checklist chi tiết việc cần làm cho Giai đoạn 2 và Giai đoạn 3

### Giai đoạn 2 — Lịch hoạt động (Schedule) & Phân tích bang hội / Bảng chuyên cần

#### Phần 2A: Lịch hoạt động — Schedule (`/schedule`)
- [ ] **Thiết kế DB & Migration (`AddScheduledEvents`):**
  - Tạo entity `ScheduledEvent` (`scheduled_events`) trong `GuildHub.Core`:
    - `Id`, `GuildId`, `Title`, `Description`, `EventType` (Bang chiến / Giao hữu / Sự kiện khác), `ScheduledTimeUtc`, `CloseBeforeMinutes` (hoặc `CloseTimeUtc`).
    - Cấu hình tự động tạo form Discord: `AutoCreateForm` (`bool`), `CreateFormBeforeHours` (hoặc `AutoCreateAtUtc`), `TargetChannelId` (`ulong?`), `MentionRoleId` (`ulong?`), `CreatedWarEventId` (`long?` liên kết sang `war_events`), `FormCreatedAtUtc`, `IsCancelled`, `CreatedByDiscordId`.
  - Tạo EF Core migration mới (additive) và kiểm tra apply sạch.
- [ ] **Service quản lý lịch & tích hợp Bot:**
  - Tạo `ScheduleService` trong `GuildHub.Web/Services/` để CRUD lịch theo tháng/tuần, lấy danh sách kênh text & role của server qua `DiscordSyncService` (dùng `<SearchSelect>`).
  - Tạo Background Worker trên `GuildHub.Bot` (`ScheduledFormHostedService.cs`): định kỳ mỗi phút kiểm tra các `ScheduledEvent` bật `AutoCreateForm = true` đã tới giờ mở form & chưa có `CreatedWarEventId` → gọi chung `EventService` + `EventMessageBuilder` để tự đăng form đăng ký lên kênh Discord chỉ định, ping Role, rồi cập nhật lại `CreatedWarEventId` vào DB.
  - Hỗ trợ nút **"Tạo form ngay"** trên Web (tạo sự kiện ngay lập tức hoặc đặt cờ kích hoạt Bot đăng ngay).
- [ ] **Giao diện Lịch tháng (`src/GuildHub.Web/Components/Pages/Schedule.razor`):**
  - Lưới lịch tháng chuẩn **Thứ 2 → Chủ Nhật**, điều hướng Tháng trước / Hôm nay / Tháng sau.
  - Hiển thị chip sự kiện theo màu loại hoạt động (`Bang chiến`, `Giao hữu`, `Khác`), badge trạng thái form Discord (`Đã tạo form` / `Chờ tự tạo` / `Đã đóng`).
  - Modal Tạo / Sửa / Xóa lịch hoạt động (dùng `<SearchSelect>` cho Loại sự kiện, Kênh Discord đăng form, Role ping).
  - Drawer / Modal xem nhanh danh sách người đã đăng ký từ `war_events` / `event_signups` khi bấm vào sự kiện đã có form.

#### Phần 2B: Phân tích bang hội & Bảng chuyên cần (`/analytics`)
- [ ] **Service thống kê (`src/GuildHub.Web/Services/AnalyticsService.cs`):**
  - Bộ lọc khoảng thời gian: **30 ngày / 90 ngày / Toàn thời gian** + lọc theo loại sự kiện (`Tất cả` / `Bang chiến` / `Giao hữu`) dùng `<SearchSelect>`.
  - Truy vấn tổng hợp từ `war_events`, `event_signups`, `guild_members`, `guild_class_configs`:
    - KPI tổng quan: Tổng số buổi chiến, Tỷ lệ điểm danh trung bình, Tổng lượt đăng ký `Đi`, Số thành viên tích cực.
    - Biểu đồ cột theo tuần (số người tham gia qua từng tuần).
    - So sánh **Giao hữu (Scrim)** vs **Bang chiến (GuildWar)** (số buổi & trung bình quân số tham gia).
    - **Top 10 thành viên tích cực nhất** (kèm avatar, môn phái, số buổi `Đi` & tỷ lệ %).
    - Phân bố hệ phái thực chiến trung bình (Donut chart tái sử dụng `DonutChart.razor`).
    - **Bảng chuyên cần chi tiết từng thành viên:** Số buổi `Đi`, `Dự bị`, `Bận`, `Không đăng ký`, Tỷ lệ chuyên cần `%`, Lần tham gia gần nhất; hỗ trợ tìm kiếm, lọc phái (`<SearchSelect>`), sắp xếp theo cột và **xuất báo cáo Excel (`.xlsx`)**.
- [ ] **Giao diện Phân tích (`src/GuildHub.Web/Components/Pages/Analytics.razor`):**
  - Layout dashboard đồng bộ phong cách Tailwind của trang Tổng quan.
  - Bảng chuyên cần có phân trang, thanh tiến độ màu (xanh/vàng/đỏ theo ngưỡng `%` chuyên cần) và nút xuất Excel.

---

### Giai đoạn 3 — Đội hình bang chiến 60vs60 (Lineup Builder kéo-thả)

- [ ] **Thiết kế DB & Migration (`AddWarLineups`):**
  - Tạo các entity trong `GuildHub.Core`:
    - `WarLineupPreset` (`war_lineup_presets`): `Id`, `GuildId`, `Name`, `WarEventId` (`long?` — gắn với 1 buổi đăng ký từ Bot để lấy trạng thái Đi/Dự bị/Bận), `OverallLeaderMemberId` (`long?` — 👑 Lead tổng), `Notes`, `CreatedAtUtc`, `UpdatedAtUtc`.
    - `WarLineupTeam` (`war_lineup_teams`): `Id`, `PresetId`, `TeamIndex` (1..10 cho Main Team, 11+ cho Sub Team), `TeamName` (VD: `Team 1`, `Team Công 1`, `Dự bị 1`), `IsSubTeam` (`bool`), `MaxSlots` (mặc định `6`), `TeamRoleTag` (tùy chọn: Công / Thủ / Cơ động / Dự bị).
    - `WarLineupSlot` (`war_lineup_slots`): `Id`, `TeamId`, `SlotIndex` (0..5), `GuildMemberId` (`long?`), `ExternalName` (`string?` — cho phép thêm nhanh `+ Ngoại binh` trong đội hình nếu cần), `ExternalClass` (`GameClass?`), `IsTeamLeader` (`bool` — ⭐ Lead team), `Note`.
  - Tạo EF Core migration mới (additive).
- [ ] **Service quản lý Đội hình (`src/GuildHub.Web/Services/LineupService.cs`):**
  - Quản lý Preset: Tạo mới (tự khởi tạo 10 Main Teams × 6 slots + 2 Sub Teams), **Nhân bản (Clone)** từ preset cũ, Đổi tên, Xóa, Gắn/đổi phiên `WarEvent` từ Bot.
  - Auto-save trạng thái kéo-thả (debounce hoặc lưu tức thì theo thao tác di chuyển slot / hoán đổi 2 thành viên / đổi Lead team / Lead tổng).
  - Tính toán thống kê đội hình real-time: Tổng quân số `X / 60`, tỷ lệ Tank / DPS / Healer của cả bang và từng Team, đếm số cảnh báo `(!)`.
  - Kiểm tra cảnh báo `(!)` khi gắn với `WarEvent`:
    - Thành viên trong đội hình nhưng đăng ký **Bận**, **Dự bị**, hoặc **Chưa đăng ký** ở buổi bang chiến đó.
    - Thành viên đã bị **Remove** hoặc **Mất role**.
  - Xuất đội hình: Xuất ảnh / Copy danh sách text đẹp để dán vào Discord (hoặc gửi trực tiếp vào kênh Discord qua Bot) + Xuất Excel đội hình.
- [ ] **Giao diện Lineup Builder (`/lineup`) — `src/GuildHub.Web/Components/Pages/Lineup.razor`:**
  - **Thanh công cụ trên cùng:** Chọn Preset (`<SearchSelect>`), nút `Tạo mới`, `Clone`, `Đổi tên`, `Xóa`, gắn `Phiên đăng ký Bot` (`<SearchSelect>`), thanh tổng hợp quân số (`Main: X/60`, Tank/DPS/Healer, cảnh báo), chế độ **Zoom / Toàn màn hình (Fullscreen)**, nút **Xuất Discord / Excel**.
  - **Khu vực chính (Lưới Đội hình):**
    - **10 Main Teams × 6 slots** (mỗi slot hiển thị icon môn phái, màu hệ phái, tên/nickname, huy hiệu `⭐ Lead team` / `👑 Lead tổng`, biểu tượng cảnh báo `(!)` kèm tooltip lý do).
    - Khu vực **Sub Teams (Dự bị)** bên dưới hoặc tab chuyển nhanh.
    - Hỗ trợ **Drag-and-Drop mượt mà** (HTML5 Drag & Drop / JS interop nhẹ cho Blazor): kéo từ cột nguồn vào slot, kéo đổi chỗ giữa 2 slot, kéo ra ngoài để bỏ khỏi team, click chuột phải hoặc nút nhanh để gán `⭐ Lead team` / `👑 Lead tổng`.
  - **Cột nguồn thành viên bên phải (Source Pool):**
    - Lọc theo trạng thái đăng ký của buổi chiến (`Đã đăng ký Đi`, `Dự bị`, `Tất cả thành viên bang`), lọc theo Môn phái (`<SearchSelect>`), ẩn/hiện người đã xếp vào team, ô tìm kiếm nhanh.
    - Nút **`+ Ngoại binh`** cho phép kéo-thả thêm nhanh slot khách mời vào đội hình khi thiếu người mà không ảnh hưởng danh sách thành viên chính thức của bang.
