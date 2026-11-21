# Cấu hình bí mật (secrets)

`appsettings.json` nằm trên GitHub nên **không chứa bí mật**. Ba giá trị sau được đặt ngoài repo:

| Khoá | Dùng cho | Thiếu thì sao |
|---|---|---|
| `JwtSettings:SecretKey` | Ký token đăng nhập (≥ 32 byte) | Máy dev: **tự sinh** lần chạy đầu. Server: **không khởi động**, báo lỗi kèm hướng dẫn |
| `YouTubeApiKey` | Video review ở trang chi tiết | Không có video review, phần còn lại vẫn chạy |
| `SmtpSettings:Password` | Gửi email thông báo (Gmail app password) | Không gửi email (thông báo vẫn được ghi vào DB) |

Test `CommittedAppSettings_ContainNoSecrets` sẽ báo đỏ nếu ai đó dán lại bí mật vào `appsettings.json`.

## Máy dev (User Secrets)

**Không cần làm gì để chạy được.** Lần chạy đầu ở môi trường Development (mọi profile trong `launchSettings.json`, cả IIS Express), backend thấy chưa có khoá JWT sẽ tự sinh 64 byte ngẫu nhiên và lưu vào User Secrets – các lần sau dùng lại khoá đó.

YouTube và email là tuỳ chọn. Muốn bật, chạy script ở thư mục gốc repo (ô nhập bị ẩn, Enter để bỏ qua):

```powershell
powershell -ExecutionPolicy Bypass -File .\setup-secrets.ps1
```

Đổi khoá JWT (mọi người dùng phải đăng nhập lại): xoá khoá rồi chạy lại backend để nó tự sinh khoá mới:

```powershell
dotnet user-secrets remove "JwtSettings:SecretKey" --project ePriTrackerBackend
```

Giá trị lưu tại `%APPDATA%\Microsoft\UserSecrets\928ce124-4681-44de-914f-6070de5dc811\secrets.json` – ngoài repo. Mỗi máy có JWT key riêng là bình thường: token tạo trên máy này không dùng được ở máy khác.

> Chạy lệnh/script trong terminal của chính bạn (Windows Terminal, Developer PowerShell). Công cụ chạy dạng ứng dụng đóng gói (MSIX, vd ứng dụng Claude desktop) bị Windows chuyển `%APPDATA%` sang thư mục ảo riêng → Visual Studio không thấy file.

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

1. **Gmail app password**: https://myaccount.google.com/apppasswords → xoá mật khẩu ứng dụng cũ → tạo mới → nhập bằng `setup-secrets.ps1`.
2. **YouTube API key**: Google Cloud Console → APIs & Services → Credentials → xoá hoặc "Regenerate key"; nên giới hạn key chỉ cho YouTube Data API v3 → nhập bằng `setup-secrets.ps1`.
3. **JWT key**: mỗi máy tự sinh (xem trên). Mọi token cũ tự hết hiệu lực.

Không cần viết lại lịch sử Git: key cũ đã thu hồi thì không còn dùng được.
