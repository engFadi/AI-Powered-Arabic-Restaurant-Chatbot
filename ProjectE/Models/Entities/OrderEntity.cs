namespace ProjectE.Models.Entities
{
    using System.ComponentModel.DataAnnotations;
    using System.ComponentModel.DataAnnotations.Schema;

    public class OrderEntity : BaseEntity
    {
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        // Customer info
        [Required]
        [StringLength(100)]
        public string CustomerName { get; set; }

        [Required]
        [StringLength(20)]
        public string PhoneNumber { get; set; }

        [Required]
        [StringLength(250)]
        public string DeliveryAddress { get; set; }

        [StringLength(500)]
        public string Notes { get; set; }

        [Required]
        [StringLength(50)]
        public string Status { get; set; } = "Pending"; // Default status

        // Relationship
        public int UserId { get; set; }

        [ForeignKey(nameof(UserId))]
        public UserEntity User { get; set; }

        // Always initialize the list to prevent null reference issues
        public List<OrderItemEntity> Items { get; set; } = new List<OrderItemEntity>();

        public ICollection<FeedBackEntity> Feedbacks { get; set; } = new List<FeedBackEntity>();
    }
}
