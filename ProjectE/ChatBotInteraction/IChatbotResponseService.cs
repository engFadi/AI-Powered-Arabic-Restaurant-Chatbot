namespace ProjectE.ChatBotInteraction
{
    /// <summary>
    /// Defines the contract for chatbot response services.
    /// </summary>
    public interface IChatbotResponseService
    {
        /// <summary>
        /// Gets a chatbot response based on the user's message.
        /// </summary>
        /// <param name="message">The user message.</param>
        /// <returns>The chatbot's response.</returns>
        Task<string> GetResponseAsync(string message, string conversationId, string userId);
        Task InitializeUserSession(string conversationId, string userId);
        Task FinalizeUserSession(string conversationId, string userId);
    }
}
