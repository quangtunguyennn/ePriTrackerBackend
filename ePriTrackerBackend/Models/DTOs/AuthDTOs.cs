namespace ePriTrackerBackend.Models.DTOs
{
    /// <summary>Kết quả đăng nhập thành công. Controller trả về JSON { token, userRoles } như trước.</summary>
    public class LoginResultDTO
    {
        public string Token { get; set; }
        public List<string> Roles { get; set; } = new List<string>();
    }

    /// <summary>Response của GET /api/auth/me – tên trường giữ nguyên như anonymous object cũ.</summary>
    public class CurrentUserDTO
    {
        public string UserName { get; set; }
        public string UserEmail { get; set; }
        public List<string> Roles { get; set; } = new List<string>();
    }
}
