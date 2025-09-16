namespace ProjectE.Models.Entities
{
    using System.ComponentModel.DataAnnotations;

    public class UserEntity : BaseEntity
    {
        [Required]
        public string UserId { get; set; }

        [Required]
        [StringLength(100)]
        public string UserName { get; set; }

        [Required]
        public string Password { get; set; }

        [Required]
        public string Role { get; set; }

        // New contact info
        [Required]
        [EmailAddress]
        public string Email { get; set; }

        [Required]
        [StringLength(20)]
        public string PhoneNumber { get; set; }

        // Navigation properties:

        public ICollection<ChatMessageEntity> ChatMessages { get; set; } = new List<ChatMessageEntity>();
        public ICollection<OrderEntity> Orders { get; set; } = new List<OrderEntity>();
        public ICollection<FeedBackEntity> Feedbacks { get; set; } = new List<FeedBackEntity>();
    }
}
