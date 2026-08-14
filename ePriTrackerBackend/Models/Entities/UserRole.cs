using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ePriTrackerBackend.Models.Entities
{
    public class UserRole
    {
        [Key]
        public Guid Id { get; set; }
        [Required]
        public Guid UserId { get; set; }
        [JsonIgnore]
        public User? User { get; set; }
        [Required]
        public int RoleId { get; set; }
        [JsonIgnore]
        public Role? Role { get; set; }
    }
}
