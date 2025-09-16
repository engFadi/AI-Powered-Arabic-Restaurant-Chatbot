namespace ProjectE.Models.ChatBot
{
    /// <summary>
    /// Represents a bot reply, optionally associated with a tool keyword.
    /// </summary>
    public class BotReply
    {
        /// <summary>
        /// The bot's message text.
        /// </summary>
        public string Text { get; set; }

        /// <summary>
        /// Keyword indicating a tool/action (e.g., add_to_order, submit, reserve, cancel_order).
        /// Null if not a tool call.
        /// </summary>
        public string Keyword { get; set; }

        /// <summary>
        /// strongly-typed payload sent by the model’s tool call
        /// </summary>
        public BotPayloadDto Payload { get; set; }

        /// <summary>
        /// keep the raw arguments for full debugging
        /// </summary>
        public string RawToolArguments { get; set; }

        public BotReply() { }

        public BotReply(string text, string keyword = null)
        {
            Text = text;
            Keyword = keyword;
        }


    }
}
