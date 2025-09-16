using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ProjectE.Models.Entities
{
    public class FeedBackEntity : BaseEntity
    {
        [Required]
        public int UserId { get; set; }

        [ForeignKey("UserId")]
        public UserEntity User { get; set; }

        public int? OrderId { get; set; }

        [ForeignKey(nameof(OrderId))]
        public OrderEntity? Order { get; set; } // Nullable navigation property

        [Range(1, 5)]
        public int Rating { get; set; }

        [StringLength(1000)]
        public string Comment { get; set; }

        [Required]
        public DateTime DateSubmitted { get; set; } = DateTime.UtcNow;

    }
}
