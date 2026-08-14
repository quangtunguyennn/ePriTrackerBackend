using System.ComponentModel.DataAnnotations;

namespace ePriTrackerBackend.Models.Entities
{
    public class User
    {
        [Key]
        public Guid UserId { get; set; }
        [Required]
        [StringLength(50)]
        public string FirstName { get; set; }
        [StringLength(50)]
        [Required]
        public string LastName { get; set; }
        [StringLength(255)]
        [Required]
        public string Email { get; set; }
        [Required]
        public string Password { get; set; }
        [StringLength(20)]
      
        public bool? IsActive { get; set; }
        public DateTime? CreatedAt { get; set; }

    }
}
