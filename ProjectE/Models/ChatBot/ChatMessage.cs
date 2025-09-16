using ProjectE.Models.Auth;
using System;

namespace ProjectE.Models.ChatBot
{
    /// <summary>
    /// Specifies the sender of a chat message.
    /// </summary>
    public enum SenderType
    {
        /// <summary>
        /// Message sent by the user.
        /// </summary>
        User,

        /// <summary>
        /// Message sent by the chatbot.
        /// </summary>
        Bot
    }

    /// <summary>
    /// Represents a message exchanged in the chatbot conversation.
    /// </summary>
    public class ChatMessage
    {
        /// <summary>
        /// Identifies who sent the message: User or Bot.
        /// </summary>
        public SenderType Sender { get; set; }

        /// <summary>
        /// string associated with the user who sent or received the message.
        /// </summary>
        public string UserId { get; set; }

        /// <summary>
        /// The content of the chat message.
        /// </summary>
        public string MessageText { get; set; }

        /// <summary>
        /// The timestamp when the message was created.
        /// </summary>
        public DateTime Timestamp { get; set; }

        /// <summary>
        /// A unique identifier for the message, useful for tracking or database storage.
        /// </summary>
        public Guid MessageId { get; set; }

        /// <summary>
        /// The conversation this message belongs to.
        /// </summary>
        public string ConversationId { get; set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="ChatMessage"/> class.
        /// </summary>
        /// <param name="sender">The sender of the message (User or Bot).</param>
        /// <param name="messageText">The content of the message.</param>
        /// <param name="userId">The Id of the user.</param>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="userId"/> is empty or <paramref name="messageText"/> is null/empty.
        /// </exception>
        public ChatMessage(SenderType sender, string messageText,string conversationId, string userId)
        {


            if (string.IsNullOrWhiteSpace(messageText))
                throw new ArgumentException("Message text cannot be null or empty.", nameof(messageText));
            if (string.IsNullOrWhiteSpace(conversationId))
                throw new ArgumentException("ConversationId cannot be null or empty.", nameof(conversationId));

            Sender = sender;
            MessageText = messageText;
            UserId = userId;
            MessageId = Guid.NewGuid();
            ConversationId = conversationId;
            Timestamp = DateTime.Now;
        }
    }
}