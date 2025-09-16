using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualBasic;
using ProjectE.ChatBotInteraction;
using ProjectE.Data;
using ProjectE.Models;
using ProjectE.Models.Auth;
using ProjectE.Models.ChatBot;
using ProjectE.Models.Entities;
using ProjectE.Services;
using System;
using System.Collections.Generic;

namespace ProjectE.Controllers
{
    /// <summary>
    /// Handles chatbot-related API requests.
    /// </summary>
    /// 
    [ApiController]
    [Route("api/[controller]")]
    public class ChatController : Controller
    {
        private readonly IChatbotResponseService _chatService;
        private readonly ApplicationDbContext _dbContext;
        private readonly IOrderService _orderService;

        [HttpGet]

        public IActionResult chatbot()
        {
            return View();
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ChatController"/> class.
        /// </summary>
        public ChatController(IChatbotResponseService chatService, ApplicationDbContext dbContext, IOrderService orderService)
        {
            _chatService = chatService;
            _dbContext = dbContext;
            _orderService = orderService;
        }
        /// <summary>
        /// Accepts a real user message and returns both the user's message and the bot's reply.
        /// </summary>
        /// Pseudocode:
        /// - Validate request (user message, user id, message text).
        /// - Resolve bot user and real user from DB; map userId to int.
        /// - Resolve or create conversation by conversationId.
        /// - Ask chat service for bot response.
        /// - Safely read optional LastBotReply (nullable) to avoid CS8600 warning.
        /// - If tool keyword is add_to_order, safely access payload items and add to order via service.
        /// - Persist user and bot messages to DB.
        /// - Return both messages in response.
        [HttpPost]
        public async Task<IActionResult> ChatWithBot([FromBody] ProjectE.Models.ChatBot.ChatMessage userMessage)
        {
            if (userMessage == null) return BadRequest("User message is required.");
            if (string.IsNullOrWhiteSpace(userMessage.UserId)) return BadRequest("User ID is required.");
            if (string.IsNullOrWhiteSpace(userMessage.MessageText)) return BadRequest("Message text is required.");
            Console.WriteLine("Received message from userId: " + userMessage.UserId);

            var botUser = await _dbContext.Users.FirstOrDefaultAsync(u => u.UserId == "Bot");
            if (botUser == null)
            {
                return BadRequest("Bot user not found in DB. Please insert it first.");
            }

            var userEntity = await _dbContext.Users
                .FirstOrDefaultAsync(u => u.UserId == userMessage.UserId);

            if (userEntity == null)
            {
                return BadRequest("Invalid UserId");
            }

            int userIdInt = userEntity.Id;
            Console.WriteLine("[Debug] Resolved user integer ID: " + userIdInt);

            var conversationId = string.IsNullOrWhiteSpace(userMessage.ConversationId)
                ? Guid.NewGuid().ToString()
                : userMessage.ConversationId;
            Console.WriteLine("Using ConversationId: " + conversationId);

            var conversation = await _dbContext.Conversations
                .FirstOrDefaultAsync(c => c.ConversationId == conversationId);
            if (conversation == null)
            {
                conversation = new ConversationsEntity
                {
                    ConversationId = conversationId,
                    UserId = userMessage.UserId,
                    StartedAt = DateTime.UtcNow,
                    Status = "Active"
                };
                _dbContext.Conversations.Add(conversation);
                await _dbContext.SaveChangesAsync();
                Console.WriteLine("Created new conversation in DB: " + conversationId);
            }
            else
            {
                Console.WriteLine("Using existing conversation from DB: " + conversationId);
            }

            var botText = await _chatService.GetResponseAsync(userMessage.MessageText, conversationId, userMessage.UserId);

            // Fix CS8600 by making the variable nullable since the RHS may be null.
            BotReply? botReplyObj = (_chatService as ChatbotResponseService)?.LastBotReply;

            string botReply = !string.IsNullOrWhiteSpace(botReplyObj?.Text)
                        ? botReplyObj!.Text!
                        : (!string.IsNullOrWhiteSpace(botText) ? botText : "لم يتم استلام رد.");
            Console.WriteLine($"[Debug] Returning bot reply: '{botReply}' (LastBotReply.Text='{botReplyObj?.Text}', botText='{botText}')");
            //// Safely access payload and items with null-conditional operators.
            //if (botReplyObj?.Keyword == "add_to_order" && botReplyObj?.Payload?.Items != null)
            //{
            //    Console.WriteLine("[Debug] ChatController detected add_to_order keyword");
            //    try
            //    {
            //        var orderItems = new List<Models.DTOs.OrderItemDto>();

            //        // We asserted not null above; use local variable for clarity.
            //        var payloadItems = botReplyObj.Payload!.Items!;
            //        foreach (var item in payloadItems)
            //        {
            //            if (string.IsNullOrEmpty(item.Name))
            //                continue;

            //            var menuItem = await _dbContext.MenuItems
            //                .FirstOrDefaultAsync(m => m.IsAvailable && m.Name.ToLower() == item.Name.ToLower());

            //            if (menuItem != null)
            //            {
            //                orderItems.Add(new Models.DTOs.OrderItemDto
            //                {
            //                    MenuItemName = menuItem.Name,
            //                    Quantity = item.Quantity ?? 1,
            //                    MenuItemPrice = menuItem.Price,
            //                     Notes = item.Extras != null ? string.Join(", ", item.Extras) : null

            //                });

            //                Console.WriteLine($"[Debug] Added item: {menuItem.Name}, Price: {menuItem.Price}, Quantity: {item.Quantity ?? 1}");
            //            }
            //            else
            //            {
            //                Console.WriteLine($"[Warning] Menu item '{item.Name}' not found");
            //            }
            //        }

            //        //if (orderItems.Any())
            //        //{
            //        //    await _orderService.AddItemsToDraftOrder(userMessage.UserId, orderItems);
            //        //    Console.WriteLine("[Debug] Successfully added items to draft order");
            //        //}
            //        //else
            //        //{
            //        //    Console.WriteLine("[Warning] No valid items to add to order");
            //        //}
            //    }
            //    catch (Exception ex)
            //    {
            //        Console.WriteLine($"[Error] Failed to add items to order: {ex.Message}");
            //    }
            //}

            var userChatEntity = new ChatMessageEntity
            {
                MessageId = Guid.NewGuid().ToString(),
                ConversationId = conversationId,
                Content = userMessage.MessageText,
                UserId = userIdInt,
                SentAt = DateTime.UtcNow
            };

            if (!string.IsNullOrWhiteSpace(botReply) && botUser != null)
            {
                var botChatEntity = new ChatMessageEntity
                {
                    MessageId = Guid.NewGuid().ToString(),
                    ConversationId = conversationId,
                    Content = botReply,
                    UserId = botUser.Id,
                    SentAt = DateTime.UtcNow
                };

                await _dbContext.ChatMessages.AddRangeAsync(userChatEntity, botChatEntity);
            }
            else
            {
                await _dbContext.ChatMessages.AddAsync(userChatEntity);
            }

            await _dbContext.SaveChangesAsync();

            return Ok(new[]
            {
                new {
                    sender = 0,
                    messageText = userMessage.MessageText,
                    conversationId = conversationId
                },
                new {
                    sender = 1,
                    messageText = botReply,
                    conversationId = conversationId
                }
            });
        }

        // Replace the problematic methods in your ChatController with these corrected versions:

        [HttpPost("start-session")]
        public async Task<IActionResult> StartChatSession([FromBody] StartChatRequest request)
        {
            if (string.IsNullOrEmpty(request.ConversationId))
                return BadRequest("ConversationId is required");

            try
            {
                // ✅ Use _chatService instead of _chatbotService
                await _chatService.InitializeUserSession(request.ConversationId, request.UserId);

                return Ok(new
                {
                    message = "Chat session initialized",
                    conversationId = request.ConversationId
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        [HttpPost("end-session")]
        public async Task<IActionResult> EndChatSession([FromBody] EndChatRequest request)
        {
            if (string.IsNullOrEmpty(request.ConversationId))
                return BadRequest("ConversationId is required");

            try
            {
                // ✅ Use _chatService instead of _chatbotService
                await _chatService.FinalizeUserSession(request.ConversationId, request.UserId);

                return Ok(new { message = "Chat session finalized" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        // Keep the request classes at the end of the ChatController class
        public class StartChatRequest
        {
            public string ConversationId { get; set; } = string.Empty;
            public string? UserId { get; set; }
        }

        public class EndChatRequest
        {
            public string ConversationId { get; set; } = string.Empty;
            public string? UserId { get; set; }
        }
    }
}
