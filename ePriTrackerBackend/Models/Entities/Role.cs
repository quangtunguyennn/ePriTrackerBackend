using System.ComponentModel.DataAnnotations;

namespace ePriTrackerBackend.Models.Entities
{
    public class Role
    {
        [Key]
        public int RoleId { get; set; }
        [Required]
        [StringLength(20)]
        public string RoleName { get; set; }
    }
}
