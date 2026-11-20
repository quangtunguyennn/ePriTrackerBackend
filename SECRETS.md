# Cấu hình bí mật (secrets)

`appsettings.json` nằm trên GitHub nên **không chứa bí mật**. Ba giá trị sau được đặt ngoài repo:

| Khoá | Dùng cho | Thiếu thì sao |
|---|---|---|
| `JwtSettings:SecretKey` | Ký token đăng nhập (≥ 32 byte) | **Backend không khởi động**, báo lỗi kèm hướng dẫn |
| `YouTubeApiKey` | Video review ở trang chi tiết | Không có video review, phần còn lại vẫn chạy |
| `SmtpSettings:Password` | Gửi email thông báo (Gmail app password) | Không gửi email (thông báo vẫn được ghi vào DB) |

Test `CommittedAppSettings_ContainNoSecrets` sẽ báo đỏ nếu ai đó dán lại bí mật vào `appsettings.json`.

## Máy dev (User Secrets)

Chạy trong thư mục `ePriTrackerBackend/` (thư mục có file `.csproj`):

```bash
dotnet user-secrets set "JwtSettings:SecretKey" "<chuỗi ngẫu nhiên, ít nhất 32 ký tự>"
dotnet user-secrets set "YouTubeApiKey" "<YouTube Data API v3 key>"
dotnet user-secrets set "SmtpSettings:Password" "<Gmail app password>"
dotnet user-secrets list
```

Tạo chuỗi ngẫu nhiên cho JWT (PowerShell):

```powershell
[Convert]::ToBase64String((1..48 | ForEach-Object { Get-Random -Maximum 256 }))
```

User Secrets chỉ được đọc khi `ASPNETCORE_ENVIRONMENT=Development` (mặc định của mọi profile trong `launchSettings.json`, cả IIS Express). Giá trị lưu tại `%APPDATA%\Microsoft\UserSecrets\928ce124-4681-44de-914f-6070de5dc811\secrets.json` – ngoài repo.

Mỗi máy có JWT key riêng là bình thường: token tạo trên máy này không dùng được ở máy khác.

## Server / máy demo (biến môi trường)

Dấu `:` đổi thành `__`:

```
JwtSettings__SecretKey=...
YouTubeApiKey=...
SmtpSettings__Password=...
```

## Thời hạn đăng nhập

`JwtSettings:DurationInMinutes` trong `appsettings.json` (mặc định 60, giới hạn 5 – 1440 phút).

## Thu hồi các key cũ đã lộ trên GitHub

Các giá trị cũ vẫn còn trong lịch sử Git, nên **phải thu hồi và tạo mới** (chủ tài khoản thực hiện):

1. **Gmail app password**: https://myaccount.google.com/apppasswords → xoá mật khẩu ứng dụng cũ → tạo mới → `dotnet user-secrets set "SmtpSettings:Password" "..."`.
2. **YouTube API key**: Google Cloud Console → APIs & Services → Credentials → xoá hoặc "Regenerate key"; nên giới hạn key chỉ cho YouTube Data API v3 → đặt lại bằng `user-secrets`.
3. **JWT key**: chỉ cần đặt chuỗi mới (đã đổi trên máy Khôi). Mọi token cũ tự hết hiệu lực.

Không cần viết lại lịch sử Git: key cũ đã thu hồi thì không còn dùng được.
