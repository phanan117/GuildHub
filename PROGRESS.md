# GuildHub — Tiến độ dự án & Tài liệu chuyển giao

> **Cập nhật lần cuối:** 26/11/2026  
> **Trạng thái tổng quan:**
> - ✅ **Giai đoạn 0 (Discord Bot nền tảng):** Hoàn thành & đang vận hành thực tế (`guildhub-bot`, `guildhub-bot-demo`).
> - ✅ **Giai đoạn 1 (Web Quản trị Bang hội — Nền tảng, Dashboard, Thành viên & Cấu hình Server/Hệ phái):** Hoàn thành 100%, build `Release` sạch (`0 Warning, 0 Error`).
> - ✅ **Giai đoạn 2 (Lịch hoạt động — Schedule & Phân tích bang hội / Bảng chuyên cần):** Hoàn thành 100% (Phần 2A `/schedule` + Bot tự mở form, Phần 2B `/analytics` + xuất Excel `.xlsx`, kèm cấu hình SuperAdmin test đa server), build `Release` sạch (`0 Warning, 0 Error`) & đang chạy thực tế trên Docker (`guildhub-bot`, `guildhub-web`).
> - ⏳ **Giai đoạn 3 (Đội hình bang chiến 60vs60 — Lineup Builder kéo-thả):** Sẵn sàng triển khai.

---

## 1. Tóm tắt các tính năng đã hoàn thành ở Giai đoạn 1

### 1.1. Tách lõi dùng chung (`src/GuildHub.Core`)
Toàn bộ Domain, Enums, Metadata, EF Core `DbContext` và Migrations dùng chung cho cả `GuildHub.Bot` và `GuildHub.Web`:
- **Entities (8 bảng PostgreSQL hiện tại):**
  - `src/GuildHub.Core/Domain/Entities/WarEvent.cs` — Sự kiện bang chiến / giao hữu tạo từ Bot hoặc tự động từ Lịch hoạt động (GĐ 2).
  - `src/GuildHub.Core/Domain/Entities/EventSignup.cs` — Danh sách đăng ký tham gia (`Confirmed`, `Late`, `Bench`, `Absent`, `NotParticipating`).
  - `src/GuildHub.Core/Domain/Entities/ClassEmoji.cs`, `UiEmoji.cs` — Emoji Discord tự động đồng bộ từ thư mục `Icon/`.
  - `src/GuildHub.Core/Domain/Entities/GuildConfig.cs` — Cấu hình theo từng Server Discord (`MemberRoleId`, `MemberRoleName`, lịch sử sync gần nhất).
  - `src/GuildHub.Core/Domain/Entities/GuildMember.cs` — Hồ sơ thành viên bang (Discord ID, username, display name, nickname ingame, avatar, hệ phái, trạng thái role/remove).
  - `src/GuildHub.Core/Domain/Entities/GuildClassConfig.cs` — Cấu hình hệ phái theo từng Server (tên hiển thị, vai trò Tank/DPS/Healer, mã màu HEX, file icon riêng, Role Discord tương ứng khi Link, hỗ trợ hệ phái tùy chỉnh `IsCustom`).
  - `src/GuildHub.Core/Domain/Entities/ScheduledEvent.cs` *(mới ở GĐ 2)* — Lịch hoạt động bang hội, cấu hình tự mở form Discord và liên kết `CreatedWarEventId -> war_events`.
- **Metadata & DbContext:**
  - `src/GuildHub.Core/Metadata/ClassMetadata.cs` — Metadata mặc định cho 8 hệ phái gốc + 13 UI icons; hỗ trợ `TryGet(...)` an toàn cho hệ phái tùy chỉnh.
  - `src/GuildHub.Core/Data/GuildHubDbContext.cs` — Ánh xạ đầy đủ 8 bảng PostgreSQL với `ulong <-> long` ValueConverter.

### 1.2. Xác thực Discord OAuth2 & Đồng bộ 3 lớp
- **OAuth2 (`src/GuildHub.Web/Auth/DiscordOAuthEndpoints.cs`, `WebDiscordOptions.cs`):**
  - Chỉ cho phép tài khoản có quyền `Administrator` hoặc `ManageGuild` trên các server Bot đang tham gia (`prompt=none`, hỗ trợ đổi tài khoản `?switch=true`, DataProtection persist tại `./keys-web`, Cookie 30 ngày).
  - *(Mới ở GĐ 2)* Hỗ trợ danh sách **SuperAdmin** qua cấu hình `DiscordWeb:SuperAdminUserIds` (biến môi trường `DISCORD_WEB_SUPER_ADMIN_IDS` trong `docker-compose.yml`, mặc định test hiện tại là `347609569595359243`) để truy cập toàn bộ server mà Bot đang tham gia khi kiểm thử; có thể đặt rỗng trong `.env` khi không cần dùng.
- **Đồng bộ Discord 3 lớp:**
  - Lớp 1 Real-time Gateway: `src/GuildHub.Bot/Services/MemberRealtimeSyncService.cs`.
  - Lớp 2 Đối soát định kỳ 30 phút: `src/GuildHub.Bot/Services/PeriodicMemberSyncHostedService.cs`.
  - Lớp 3 Sync thủ công trên Web (cooldown 15s): `src/GuildHub.Web/Services/DiscordSyncService.cs`.

### 1.3. Các trang Web Giai đoạn 1 (`src/GuildHub.Web`)
- **Shell & Shared:** `MainLayout.razor`, `SearchSelect.razor` (thay thế 100% thẻ `<select>` trên toàn dự án), `DonutChart.razor`, `RingGauge.razor`.
- **Tổng quan (`/` — `Home.razor` + `DashboardService.cs`):** 4 KPI, 3 Donut charts, 3 Ring gauges, bảng thành viên lọc nhanh.
- **Thành viên (`/members` — `Members.razor` + `MemberDirectoryPanel.razor` + `MemberManagementService.cs`):** lọc debounce, đổi Nickname 2 chiều Discord, Link/đổi phái tự gán Role hệ phái, Remove/Khôi phục/Thêm thành viên từ Discord.
- **Cấu hình (`/settings` — `Settings.razor`):** chọn Role thành viên bang, bảng cấu hình hệ phái full-width (upload icon, chọn màu, đổi tên, vai trò, gán Role Discord, thêm/xóa hệ phái tùy chỉnh).
- **Trang giữ chỗ còn lại cho GĐ 3:** `Lineup.razor`.

---

## 2. Tổng kết những thay đổi mới đã hoàn thành ở Giai đoạn 2

### 2.1. Phần 2A — Lịch hoạt động (`/schedule`) & Bot tự động tạo form
- **Database & Migration:**
  - Thêm entity `src/GuildHub.Core/Domain/Entities/ScheduledEvent.cs` và ánh xạ bảng `scheduled_events` trong `GuildHubDbContext.cs` (FK `created_war_event_id -> war_events` với `OnDelete(SetNull)`, các index phục vụ lịch tháng & Bot polling).
  - Tạo & apply sạch additive migration #8: `src/GuildHub.Core/Data/Migrations/20261126073250_AddScheduledEvents.cs`.
- **Tích hợp Discord Bot (`src/GuildHub.Bot`):**
  - Cập nhật `src/GuildHub.Bot/Services/EventService.cs`: hỗ trợ truyền `EventType` (`GuildWar` / `Scrim` / `Other`), `CloseTimeUtc` nullable và hàm `DeleteEventIfNoMessageAsync` để dọn `WarEvent` rác nếu gửi tin nhắn Discord lỗi.
  - Thêm worker `src/GuildHub.Bot/Services/ScheduledFormHostedService.cs` (đăng ký trong `GuildHub.Bot/Program.cs`): kiểm tra mỗi 10 giây các lịch bật `AutoCreateForm = true` đã đến giờ mở form hoặc được bấm **"Tạo form ngay"** (`TriggerFormNow = true`), gọi chung `EventService` + `EventMessageBuilder` để đăng form lên kênh chỉ định, ping Role (hỗ trợ cả `@everyone`), cập nhật `CreatedWarEventId` & `FormCreatedAtUtc`, và ghi lại `LastFormError` nếu thiếu quyền kênh.
- **Web Services & Giao diện (`src/GuildHub.Web`):**
  - Mở rộng `src/GuildHub.Web/Services/DiscordSyncService.cs`: thêm `GetGuildTextChannelsAsync` (lấy kênh Text & Thông báo nhóm theo Category) và tùy chọn `includeEveryone` cho `GetGuildRolesAsync`.
  - Tạo `src/GuildHub.Web/Services/ScheduleService.cs` (đăng ký trong `Program.cs`): CRUD lịch, kích hoạt **"Tạo form ngay"**, hiển thị đồng thời cả các `WarEvent` tạo trực tiếp từ Bot `/tao-form` lên lịch tháng, và truy vấn chi tiết đăng ký (có phân tích vai trò, môn phái tùy chỉnh).
  - Hoàn thiện `src/GuildHub.Web/Components/Pages/Schedule.razor`:
    - 4 thẻ KPI tháng, lưới lịch **Thứ 2 → Chủ Nhật** (GMT+7), điều hướng tháng, lọc `EventType`, nút `+` tạo nhanh ngay trên từng ô ngày.
    - Modal Tạo / Sửa lịch rộng rãi (`max-w-3xl`), dùng 100% `<SearchSelect>` bao gồm bộ chọn giờ **24h (`00h`–`23h` : `00p`–`59p`)** + 4 nút giờ cao điểm (`19:30`, `20:00`, `20:30`, `21:00`), xem trước giờ đóng form & giờ dự kiến tự đăng form.
    - Drawer chi tiết sự kiện: nút **"Tạo form ngay"** (tự polling cập nhật ngay khi Bot đăng xong), Sửa / Hủy / Khôi phục / Xóa, link nhảy thẳng tới tin nhắn Discord, và bảng danh sách thành viên đã đăng ký (tìm kiếm, lọc trạng thái, thống kê vai trò & môn phái).

### 2.2. Phần 2B — Phân tích bang hội & Bảng chuyên cần (`/analytics`)
- **Service & Xuất Excel (`src/GuildHub.Web`):**
  - Thêm gói `ClosedXML` `0.104.2` vào `src/GuildHub.Web/GuildHub.Web.csproj`.
  - Tạo `src/GuildHub.Web/Services/AnalyticsService.cs` (đăng ký trong `Program.cs`):
    - Lọc theo kỳ (**30 ngày / 90 ngày / Toàn thời gian**) và loại hoạt động (`Tất cả` / `Bang chiến` / `Giao hữu` / `Khác`).
    - Tính 4 KPI tổng quan, xu hướng quân số theo tuần (ISO Monday GMT+7, tối đa 12 tuần gần nhất), so sánh trực diện **Bang chiến vs Giao hữu (Scrim)**, **Top 10 thành viên tích cực nhất**, phân bố hệ phái thực chiến + tỷ lệ `Tank / DPS / Healer`, và bảng chuyên cần chi tiết từng thành viên.
    - Xuất báo cáo Excel `.xlsx` 2 sheet (`Bang chuyen can` + `Danh sach su kien`) với độ rộng cột cố định (**không dùng `AdjustToContents()`**).
  - Tạo endpoint có xác thực `src/GuildHub.Web/Endpoints/AnalyticsExportEndpoints.cs` (`GET /api/analytics/export-excel`, kiểm tra quyền `AuthorizedGuilds` theo phiên đăng nhập, đăng ký trong `Program.cs`).
- **Giao diện Dashboard (`src/GuildHub.Web/Components/Pages/Analytics.razor`):**
  - Đồng bộ phong cách Tailwind với `Home.razor`, 100% dropdown dùng `<SearchSelect>`.
  - Biểu đồ cột quân số theo tuần (chuyển đổi `TB / buổi` và `Tổng lượt đi`, tooltip chi tiết), thẻ so sánh Bang chiến vs Scrim, bảng Top 10, biểu đồ Donut hệ phái thực chiến tái sử dụng `DonutChart.razor`.
  - Bảng chuyên cần chi tiết: cột `Đi` (kèm phụ chú số buổi trễ), `Dự bị`, `Bận`, `Không ĐK`, thanh tiến độ màu theo ngưỡng `%` (`≥ 75%` xanh, `40–74%` vàng, `< 40%` đỏ), `Tham gia gần nhất`; hỗ trợ tìm kiếm, lọc môn phái, lọc mức chuyên cần, sắp xếp mọi cột, phân trang và nút **Xuất Excel (.xlsx)**.

---

## 3. Các quyết định kỹ thuật & bài học bắt buộc tuân thủ cho Giai đoạn 3

### 3.1. Kiến trúc & Database Migrations
- **Runtime:** `.NET 8`, PostgreSQL 18 native trên host Windows (`port 5432`), Docker containers `guildhub-bot` & `guildhub-web` (port `5080:8080`) kết nối qua `host.docker.internal:5432`.
- **KHÔNG BAO GIỜ drop/reset DB `guildhub`** — mọi thay đổi schema ở GĐ 3 bắt buộc dùng **additive EF Core migration**.
- **Lệnh tạo Migration mới:**
  ```powershell
  dotnet ef migrations add <TenMigration> --project src/GuildHub.Core --startup-project src/GuildHub.Bot --output-dir Data/Migrations
  ```
- **8 Migrations hiện có (giữ nguyên thứ tự):**
  1. `20261125085902_InitialCreate`
  2. `20261125092200_AddEventCloseTimeAndSignupOptions`
  3. `20261125094417_AddUiEmojis`
  4. `20261125104731_AddOnlineReminderSent`
  5. `20261126043141_AddGuildMembersAndGuildConfigs`
  6. `20261126050745_AddGuildClassConfigs`
  7. `20261126062838_AddClassColorAndCustomClasses`
  8. `20261126073250_AddScheduledEvents`
- **Quy ước PostgreSQL:** `snake_case` toàn bộ bảng/cột; Discord Snowflakes (`ulong` / `ulong?`) lưu kiểu `bigint` qua `unchecked` ValueConverter.

### 3.2. Các ràng buộc & bài học kỹ thuật quan trọng
1. **Hệ phái tùy chỉnh (`IsCustom = true`):**
   - Các hệ phái thêm mới có ID `9, 10, ...` lưu dạng `integer` và ép kiểu `(GameClass)id`.
   - **TUYỆT ĐỐI KHÔNG dùng `Enum.IsDefined(typeof(GameClass), cInt)`** — luôn kiểm tra `cInt > 0` và tra trong `MemberManagementService.GetResolvedClassConfigsAsync(guildId)` + `ClassMetadata.TryGet(...)`.
2. **Npgsql EF Core 8 với cột `ulong?` (`ValueConverter<ulong?, long?>`):**
   - **KHÔNG dùng `List<ulong>.Contains(m.DiscordUserId.Value)`** trong EF Core LINQ vì Npgsql ném lỗi `NpgsqlArrayConverter` (`UInt64` vs `Nullable<UInt64>`). Hãy query theo `GuildId` rồi đối chiếu bằng `HashSet<ulong>` trong bộ nhớ hoặc dùng `List<ulong?>`.
3. **UI Consistency, Razor & CSS:**
   - **100% dropdown dùng `<SearchSelect>`** (không dùng `<select>`, không dùng `<input type="time">` gây cắt AM/PM theo locale trình duyệt — dùng bộ chọn 24h nếu có nhập giờ).
   - Tránh đặt `overflow-hidden` trên card/modal chứa `<SearchSelect>` để dropdown không bị cắt; dùng `AlignRight="true"` khi `<SearchSelect>` nằm sát mép phải Drawer/Card.
   - Mọi phần trăm/số thập phân xuất vào inline CSS (`width`, `height`, SVG) phải format với `CultureInfo.InvariantCulture` để tránh dấu phẩy `,` tiếng Việt làm vỡ CSS.
   - Không đặt hướng dẫn setup kỹ thuật rườm rà trên UI.
4. **Xuất file & Tải về từ Web:**
   - Xuất Excel bằng `ClosedXML` dùng **độ rộng cột cố định**, không gọi `AdjustToContents()`.
   - Tải file từ Blazor Server nên dùng Minimal API endpoint có `.RequireAuthorization()` và đối chiếu `guildId` với claim `AuthorizedGuilds` (giống `/api/analytics/export-excel`).
5. **Discord Bot & An toàn file:**
   - `InteractionService` chạy `RunMode.Sync` + `UseCompiledLambda = false` và offload bằng `Task.Run`.
   - Sửa file có tiếng Việt trên Windows luôn dùng UTF-8 rõ ràng (`edit_file`/`write_file`); không bao giờ in/commit bí mật trong `.env`.

---

## 4. Checklist triển khai Giai đoạn 3 — Đội hình bang chiến 60vs60 (Lineup Builder kéo-thả)

- [ ] **3.1. Thiết kế DB & Migration (`AddWarLineups`):**
  - Tạo 3 entity trong `src/GuildHub.Core/Domain/Entities/` và cấu hình trong `GuildHubDbContext.cs`:
    - `WarLineupPreset` (`war_lineup_presets`): `Id`, `GuildId`, `Name`, `WarEventId` (`long?`, FK `SetNull` tới `war_events` để đối chiếu trạng thái Đi/Dự bị/Bận), `OverallLeaderMemberId` (`long?` — 👑 Lead tổng), `Notes`, `CreatedAtUtc`, `UpdatedAtUtc`.
    - `WarLineupTeam` (`war_lineup_teams`): `Id`, `PresetId` (FK `Cascade`), `TeamIndex` (`1..10` cho Main Team, `11+` cho Sub Team), `TeamName` (VD: `Team 1`, `Team Công 1`, `Dự bị 1`), `IsSubTeam` (`bool`), `MaxSlots` (mặc định `6`), `TeamRoleTag` (`Công` / `Thủ` / `Cơ động` / `Dự bị`).
    - `WarLineupSlot` (`war_lineup_slots`): `Id`, `TeamId` (FK `Cascade`), `SlotIndex` (`0..5`), `GuildMemberId` (`long?`, FK `SetNull` tới `guild_members`), `ExternalName` (`string?` — cho `+ Ngoại binh`), `ExternalClass` (`GameClass?`), `IsTeamLeader` (`bool` — ⭐ Lead team), `Note`.
  - Tạo additive EF Core migration `AddWarLineups` và apply sạch lên PostgreSQL.

- [ ] **3.2. Service quản lý Đội hình (`src/GuildHub.Web/Services/LineupService.cs`) & Export:**
  - Quản lý Preset: Tạo mới (tự khởi tạo 10 Main Teams × 6 slots + 2 Sub Teams), **Clone (Nhân bản)** từ preset cũ, Đổi tên, Xóa, Gắn/đổi phiên `WarEvent` từ Bot, sửa tên/tag từng Team.
  - Lưu tức thì mọi thao tác kéo-thả: xếp thành viên vào slot, hoán đổi 2 slot, bỏ khỏi team, gán `⭐ Lead team` / `👑 Lead tổng`, thêm nhanh `+ Ngoại binh`.
  - Thống kê real-time: Tổng quân số `Main: X / 60`, `Sub: Y`, tỷ lệ Tank / DPS / Healer của cả bang và từng Team, tổng số cảnh báo `(!)`.
  - Hệ thống cảnh báo `(!)` khi gắn `WarEvent`:
    - Thành viên trong đội hình đăng ký **Bận / Vắng / Không đi**, **Dự bị**, hoặc **Chưa đăng ký** ở buổi chiến đó.
    - Thành viên đã bị **Remove** hoặc **Mất role**.
  - Xuất đội hình:
    - Copy danh sách text định dạng đẹp dán vào Discord (hoặc gửi trực tiếp vào kênh Discord qua Bot).
    - Xuất Excel `.xlsx` đội hình (endpoint bảo vệ tương tự `/api/analytics/export-excel`, độ rộng cột cố định không dùng `AdjustToContents()`).

- [ ] **3.3. Giao diện Lineup Builder (`/lineup` — `src/GuildHub.Web/Components/Pages/Lineup.razor`):**
  - **Thanh công cụ trên cùng:** Chọn Preset (`<SearchSelect>`), nút `Tạo mới`, `Clone`, `Đổi tên`, `Xóa`, gắn `Phiên đăng ký Bot` (`<SearchSelect>`), thanh tổng hợp quân số (`Main: X/60`, Tank/DPS/Healer, cảnh báo), chế độ **Toàn màn hình (Fullscreen)**, nút **Copy Discord / Xuất Excel**.
  - **Lưới Đội hình (trái/giữa):**
    - **10 Main Teams × 6 slots** hiển thị icon môn phái, màu hệ phái, nickname/tên, huy hiệu `⭐ Lead team` / `👑 Lead tổng`, cảnh báo `(!)` kèm tooltip lý do, và nút đổi tên/tag team.
    - Khu vực **Sub Teams (Dự bị)** bên dưới hoặc tab chuyển nhanh.
    - **Drag-and-Drop mượt mà** (HTML5 Drag & Drop + JS interop nhẹ nếu cần): kéo từ Source Pool vào slot, kéo đổi chỗ giữa 2 slot, kéo ra ngoài hoặc bấm `×` để gỡ khỏi slot, nút nhanh đặt `⭐ Lead team` / `👑 Lead tổng`.
  - **Cột nguồn thành viên bên phải (Source Pool):**
    - Lọc theo trạng thái đăng ký (`Đã đăng ký Đi`, `Dự bị`, `Tất cả thành viên bang`), lọc Môn phái (`<SearchSelect>`), toggle ẩn/hiện người đã xếp vào team, ô tìm kiếm nhanh.
    - Nút **`+ Ngoại binh`** thêm nhanh khách mời (tên + môn phái qua `<SearchSelect>`) để kéo-thả vào đội hình mà không ảnh hưởng bảng thành viên chính thức.
