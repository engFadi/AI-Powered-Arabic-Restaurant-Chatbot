using System;

namespace ProjectE.Models.Entities
{
    /// <summary>
    /// Represents a chat conversation session between a user and the bot.
    /// </summary>
    public class ConversationsEntity : BaseEntity
    {
        /// <summary>
        /// Unique identifier for this conversation.
        /// </summary>
        public string ConversationId { get; set; }

        /// <summary>
        /// The user who owns this conversation.
        /// </summary>
        public string UserId { get; set; }

        /// <summary>
        /// When the conversation started.
        /// </summary>
        public DateTime StartedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// When the conversation ended (null if still active).
        /// </summary>
        public DateTime? ClosedAt { get; set; }

        /// <summary>
        /// Status of the conversation (Active, Closed, Archived, etc.).
        /// </summary>
        public string Status { get; set; } = "Active";
    }
}
