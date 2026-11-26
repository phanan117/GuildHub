# GuildHub — Discord Bot Quản Lý Đăng Ký Bang Chiến & Scrim

Bot Discord xây dựng bằng **.NET 8**, **Discord.Net** và **PostgreSQL (EF Core + Npgsql)** giúp quản lý đăng ký Scrim / Bang Chiến theo từng hệ phái với giao diện trực quan lấy cảm hứng từ Raid-Helper.

## Tính năng hiện có

- **Tạo form đăng ký (`/tao-form`)**: Nhập tiêu đề, thời gian bắt đầu online, thời gian đóng form (tự động đóng khi hết giờ) và role được tag thông báo.
- **Tự động tag triệu tập khi đến giờ online**: Khi đến `thoi_gian_online`, bot tự động gửi thẻ thông báo và tag toàn bộ thành viên đã đăng ký (Tham gia, Đi trễ, Dự bị — loại trừ Vắng mặt và Không tham gia).
- **Hiển thị danh sách theo hệ phái (3 cột)**: Đầu form hiển thị Leader, tổng số người tham gia, giờ online, giờ đóng form và thống kê **Tank / DPS / Healer**; bên dưới gom nhóm theo 8 hệ phái (**Tố Vấn, Thiết Y, Thần Tương, Cửu Linh, Huyết Hà, Toái Mộng, Long Ngâm, Huyền Cơ**).
- **Tự động đồng bộ toàn bộ 21 Icon**: Tự động đọc 8 icon hệ phái + 13 icon giao diện (`thamgia`, `lich`, `dongho`, `khoa`, `shut-down-line`, `tanker`, `dps`, `heal`, `dubi`, `vangmat`, `khongthamgia`, `edit`, `excel`) trong thư mục `Icon/` và upload lên **Discord Application Emojis** khi khởi động.
- **Menu chọn hệ phái (`Chọn class`)**: Chọn hệ phái trực tiếp từ dropdown kèm icon riêng.
- **Các nút trạng thái sau khi đăng ký**: `Dự bị (Bench)`, `Online trễ (Late)`, `Vắng (Absence)` — yêu cầu đã đăng ký hệ phái trước; bấm lại cùng trạng thái sẽ chuyển về Tham gia chính thức.
- **Thông báo xác nhận riêng tư (Ephemeral)**: Sau khi đăng ký/đổi trạng thái sẽ gửi thẻ xác nhận chỉ mình người thao tác thấy, cho phép **Đổi tên hiển thị**, **Thêm ghi chú** hoặc **Hủy đăng ký**.
- **Quản lý form cho Leader/Admin**: Nút `⚙️` trên form hoặc lệnh `/dong-form`, `/mo-form`, `/sync-icon`.

---

## Cài đặt & Chạy Bot

### 1. Khởi chạy PostgreSQL

Nếu bạn có cài Docker:

```bash
docker compose up -d
```

Hoặc dùng PostgreSQL cài sẵn trên máy và tạo database tên `guildhub`.

### 2. Tạo Discord Bot & Lấy Token

1. Truy cập [Discord Developer Portal](https://discord.com/developers/applications) → **New Application**.
2. Vào mục **Bot** → bấm **Reset Token** để lấy Bot Token.
3. Vào mục **OAuth2 → URL Generator**:
   - Scopes: chọn `bot` và `applications.commands`
   - Bot Permissions: chọn `View Channels`, `Send Messages`, `Embed Links`, `Read Message History`, `Use External Emojis`.
4. Dùng URL sinh ra để mời bot vào server Discord của bang.

### 3. Cấu hình dự án

Mở file `src/GuildHub.Bot/appsettings.json` (hoặc dùng `dotnet user-secrets`) và điền thông tin:

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Port=5432;Database=guildhub;Username=postgres;Password=postgres"
  },
  "DiscordBot": {
    "Token": "DÁN_BOT_TOKEN_VÀO_ĐÂY",
    "DevGuildId": 123456789012345678,
    "IconDirectory": "Icon",
    "AutoMigrateDatabase": true,
    "AutoSyncIcons": true
  }
}
```

> **Mẹo:** Điền `DevGuildId` (Server ID của bang) để Slash Command cập nhật **ngay lập tức**. Để `null` nếu muốn đăng ký Global cho mọi server (Discord có thể mất vài phút đến 1 giờ để đồng bộ).

Hoặc lưu token an toàn bằng User Secrets:

```bash
dotnet user-secrets --project src/GuildHub.Bot set "DiscordBot:Token" "YOUR_TOKEN"
dotnet user-secrets --project src/GuildHub.Bot set "DiscordBot:DevGuildId" "YOUR_GUILD_ID"
```

### 4. Chạy Bot

```bash
dotnet run --project src/GuildHub.Bot
```

Khi chạy lần đầu:
1. Bot tự động áp dụng EF Core Migration để tạo các bảng `war_events`, `event_signups`, `class_emojis` trong PostgreSQL.
2. Bot tự động đọc 8 file ảnh trong thư mục `Icon/` và upload thành Discord Application Emojis.
3. Bot đăng ký các Slash Command (`/tao-form`, `/dong-form`, `/mo-form`, `/sync-icon`).

---

## Danh sách lệnh

| Lệnh | Mô tả |
| :--- | :--- |
| `/tao-form` | Tạo form đăng ký (`tieu_de`, `thoi_gian_online`, `thoi_gian_dong`, `role`, tùy chọn `noi_dung`, `kenh`) |
| `/dong-form` | Đóng đăng ký của một sự kiện theo `event_id` |
| `/mo-form` | Mở lại đăng ký của một sự kiện theo `event_id` |
| `/sync-icon` | Đồng bộ lại các file ảnh trong thư mục `Icon/` lên Discord Application Emojis |

---

## Chạy Bot liên tục 24/7 bằng Docker

1. **Mở Docker Desktop** (vào *Settings → General* bật **Start Docker Desktop when you sign in to your computer** để bot tự chạy lại sau khi khởi động máy).
2. **Tạo file `.env`** từ mẫu `.env.example`:
   ```powershell
   Copy-Item .env.example .env
   ```
   Mở `.env` và điền `DISCORD_BOT_TOKEN` (để trống `DISCORD_DEV_GUILD_ID` nếu chạy Global nhiều server).
3. **Build và chạy nền (`restart: unless-stopped`):**
   ```powershell
   docker compose up -d --build
   ```
4. **Các lệnh quản lý hay dùng:**
   - Xem log realtime: `docker compose logs -f bot`
   - Dừng bot: `docker compose down`
   - Cập nhật sau khi sửa code: `docker compose up -d --build`
   - File log Serilog (tự xóa sau 30 ngày) nằm trực tiếp tại thư mục `logs/`.
   - Thư mục `Icon/` được mount trực tiếp vào container: bạn có thể thêm/sửa ảnh PNG rồi chạy `/sync-icon` trên Discord mà không cần build lại Docker image.
