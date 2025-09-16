using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ProjectE.Models.Entities
{
    public class ChatMessageEntity : BaseEntity
    {
        // so the ChatMessageEntity have the two keys for now 
        [Required]
        public String MessageId { get; set; }

        [Required]
        public String ConversationId { get; set; }

        [Required]
        public string Content { get; set; }

        public DateTime SentAt { get; set; } = DateTime.UtcNow;

      
        public int UserId { get; set; }

        [ForeignKey(nameof(UserId))]
        public UserEntity User { get; set; }
    }
}
