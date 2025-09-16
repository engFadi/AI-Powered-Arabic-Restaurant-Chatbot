using Azure;
using Azure.AI.OpenAI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using OpenAI.Chat;
using ProjectE.Data;
using ProjectE.Models.ChatBot;
using ProjectE.Models.DTOs;
using ProjectE.Models.DTOs;
using ProjectE.Models.Entities;
using ProjectE.Models.Enums;
using ProjectE.Models.OrderModels;
using ProjectE.Services;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics.Metrics;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.AccessControl;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace ProjectE.ChatBotInteraction
{
    using static System.Net.WebRequestMethods;
    using static System.Runtime.InteropServices.JavaScript.JSType;
    using OpenAIMessage = OpenAI.Chat.ChatMessage;

    /// <summary>
    /// Service responsible for generating chatbot responses
    /// using Azure OpenAI Chat models.
    /// Maintains conversation history per session.
    /// </summary>
    public class ChatbotResponseService : IChatbotResponseService
    {
        private readonly ChatClient _chatClient;
        private readonly ApplicationDbContext _db; //  EF DbContext
        private readonly IOrderService _orderService; // use it later to save order and reservation
        private readonly IRecommendationService _recommendationService;
        public BotReply LastBotReply { get; private set; }

        /// <summary>
        /// In-memory store for user conversation histories,
        /// keyed by conversation/session ID.
        /// </summary>
        private static readonly ConcurrentDictionary<string, List<OpenAIMessage>> _histories
            = new ConcurrentDictionary<string, List<OpenAIMessage>>();


        // **NEW: In-memory order storage per conversation**
        private static readonly ConcurrentDictionary<string, List<OrderItemDto>> _conversationOrders
            = new ConcurrentDictionary<string, List<OrderItemDto>>();

        private static readonly Regex HiddenIdRegex = new(@"⟦\s*\d+\s*⟧", RegexOptions.Compiled);

        private void PrintInMemoryOrderDebug(string conversationId, string? userId, string operation)
        {
            try
            {
                var orderKey = GetOrderKey(conversationId, userId ?? "unknown");
                Console.WriteLine($"🔍 [IN-MEMORY DEBUG] === AFTER {operation.ToUpper()} OPERATION ===");
                Console.WriteLine($"🔍 [IN-MEMORY DEBUG] Order Key: {orderKey}");

                if (_conversationOrders.TryGetValue(orderKey, out var currentOrder))
                {
                    Console.WriteLine($"🔍 [IN-MEMORY DEBUG] Items Count: {currentOrder.Count}");

                    if (currentOrder.Any())
                    {
                        decimal total = 0;
                        Console.WriteLine("🔍 [IN-MEMORY DEBUG] Current Items:");

                        foreach (var item in currentOrder.Select((value, index) => new { value, index }))
                        {
                            var itemTotal = item.value.Quantity * item.value.MenuItemPrice;
                            total += itemTotal;
                            Console.WriteLine($"🔍 [IN-MEMORY DEBUG]   [{item.index + 1}] {item.value.Quantity} × {item.value.MenuItemName} " +
                                    $"(ID: {item.value.MenuItemId}) " +
                                    $"@ {item.value.MenuItemPrice:F2} شيكل each = {itemTotal:F2} شيكل" +
                                    (!string.IsNullOrEmpty(item.value.Size) ? $" [Size: {item.value.Size}]" : "") +
                                    (!string.IsNullOrEmpty(item.value.Notes) ? $" [Notes: {item.value.Notes}]" : ""));
                        }

                        Console.WriteLine($"🔍 [IN-MEMORY DEBUG] === ORDER TOTAL: {total:F2} شيكل ===");
                    }
                    else
                    {
                        Console.WriteLine("🔍 [IN-MEMORY DEBUG] ❌ Order is EMPTY");
                    }
                }
                else
                {
                    Console.WriteLine($"🔍 [IN-MEMORY DEBUG] ❌ No order found for key: {orderKey}");
                }
                Console.WriteLine($"🔍 [IN-MEMORY DEBUG] === END {operation.ToUpper()} DEBUG ===\n");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"🔍 [IN-MEMORY DEBUG] ❌ Error during debug print: {ex.Message}");
            }
        }

        private static string StripHiddenIds(string text)
            => HiddenIdRegex.Replace(text ?? string.Empty, string.Empty)
                            .Replace("  ", " ")
                            .Trim();

        /// <summary>
        /// Initializes a new instance of <see cref="ChatbotResponseService"/>.
        /// Validates required configuration values and creates an Azure OpenAI chat client.
        /// </summary>
        /// <param name="cfg">Application configuration (must contain Endpoint, ApiKey, DeploymentName).</param>
        /// <exception cref="InvalidOperationException">Thrown if any required configuration value is missing.</exception>
        public ChatbotResponseService(IConfiguration cfg, IOrderService orderService, ApplicationDbContext db, IRecommendationService recommendationService)
        {
            _orderService = orderService;
            _db = db;
            _recommendationService = recommendationService;

            var endpointValue = cfg["AzureOpenAI:Endpoint"];
            if (string.IsNullOrWhiteSpace(endpointValue))
                throw new InvalidOperationException("AzureOpenAI:Endpoint is missing in configuration.");

            var apiKey = cfg["AzureOpenAI:ApiKey"];
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException("AzureOpenAI:ApiKey is missing in configuration.");

            var deploymentName = cfg["AzureOpenAI:DeploymentName"];
            if (string.IsNullOrWhiteSpace(deploymentName))
                throw new InvalidOperationException("AzureOpenAI:DeploymentName is missing in configuration.");

            var endpoint = new Uri(endpointValue);

            var azureClient = new AzureOpenAIClient(endpoint, new AzureKeyCredential(apiKey));
            _chatClient = azureClient.GetChatClient(deploymentName);

            // Ensure non-null LastBotReply to satisfy nullable analysis
            LastBotReply = new BotReply
            {
                Text = string.Empty,
                Keyword = string.Empty,
                Payload = new BotPayloadDto
                {
                    Items = new List<ItemDto>(),
                    next_required_fields = new List<string>()
                },
                RawToolArguments = string.Empty
            };
        }

        /// <summary>
        /// Synchronously gets a chatbot response for the given message.
        /// </summary>
        public async Task<string> GetResponseAsync(string message, string conversationId)
            => await GetResponseAsync(message, conversationId, null, default);

        /// <summary>
        /// Synchronously gets a chatbot response for the given message with userId.
        /// </summary>
        public async Task<string> GetResponseAsync(string message, string conversationId, string userId)
            => await GetResponseAsync(message, conversationId, userId, default);

        /// <summary>
        /// Asynchronously generates a chatbot response based on the given user input.
        /// Preserves conversation context across messages for the same conversation ID.
        /// </summary>
        /// <param name="userMessage">The user’s input message.</param>
        /// <param name="conversationId">Unique ID for the conversation (default: "default").</param>
        /// <param name="userId">Current user's external ID (optional, required for order operations).</param>
        /// <param name="ct">Optional cancellation token.</param>
        /// <returns>Chatbot response text.</returns>


        public async Task<string> GetResponseAsync(string userMessage, string conversationId = "default", string userId = null, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(userMessage))
                return "⚠️ من فضلك اكتب سؤالك أو طلبك أولًا.";
            // قبل إنشاء requestOptions مباشرة 
            var listText = " ";
            if (!string.IsNullOrEmpty(userMessage))
            {
                var lowered = userMessage.Trim().ToLower();
                if (lowered.Contains("توصية") || lowered.Contains("ايش أطيب اشي عندكم") || lowered.Contains("ايش تنصحني"))
                {
                    var topItems = await _recommendationService.GetTopSellingItemsAsync(3);

                    if (topItems.Any())
                    {
                        listText = string.Join("\n", topItems.Select((item, index) => $"{index + 1}. {item.Name}"));

                    }
                    else
                    {
                        return "🙂 لسا ما في مبيعات كافية لأعطيك توصيات.";
                    }
                }
            }


            const int MaxChars = 2000;
            if (userMessage.Length > MaxChars)
                return $"⚠️ رسالتك طويلة جدًا. من فضلك اختصرها (بحد أقصى {MaxChars} حرفا).";

            var requestOptions = new ChatCompletionOptions()
            {
                MaxOutputTokenCount = 1200,
                Temperature = 0.2f,
                TopP = 1.0f
            };

            var saveOrderTool = ChatTool.CreateFunctionTool(
                functionName: "save_order",
                functionDescription: "Manage order operations: add, remove, replace, update quantity, cancel order, submit.",
                functionParameters: BinaryData.FromString(@"{
  ""type"": ""object"",
  ""properties"": {
    ""keyword"": {""type"": [""string"", ""null""]},
    ""CustomerName"": {""type"": [""string"", ""null""]},
    ""PhoneNumber"": {""type"": [""string"", ""null""]},
    ""DeliveryAddress"": {
      ""type"": [""object"", ""null""],
      ""properties"": {
        ""Address"": {""type"": [""string"", ""null""]},
        ""PhoneNumber"": {""type"": [""string"", ""null""]},
        ""CustomerName"": {""type"": [""string"", ""null""]}
      }
    },
    ""Items"": {
      ""type"": ""array"",
      ""items"": {
        ""type"": ""object"",
        ""properties"": {
          ""MenuItemId"": {""type"": [""integer"", ""null""]},
          ""Name"": {""type"": [""string"", ""null""]},
          ""Extras"": {""type"": ""array"", ""items"": {""type"": ""string""}},
          ""Quantity"": {""type"": [""integer"", ""null""]},
          ""UnitPrice"": {""type"": [""number"", ""null""]},
          ""LineTotal"": {""type"": [""number"", ""null""]},
          ""Currency"": {""type"": ""string""}
        }
      }
    },
        ""newMenu"": {
      ""type"": [""object"", ""null""],
      ""properties"": {
        ""exception"": { ""type"": [""string"", ""null""] }
      },
      ""additionalProperties"": false
    },

    ""target_item_name"": {""type"": [""string"", ""null""]},
""replacement_item"": {
  ""type"": [""object"", ""null""],
  ""properties"": {
    ""Name"": {""type"": [""string"", ""null""]},
    ""Size"": {""type"": [""string"", ""null""]},
    ""Quantity"": {""type"": [""integer"", ""null""]},
    ""Extras"": {""type"": ""array"", ""items"": {""type"": ""string""}}
  }
},
""new_quantity"": {""type"": [""integer"", ""null""]},

    ""Reservation"": {
      ""type"": [""object"", ""null""],
      ""properties"": {
        ""Date"": {""type"": [""string"", ""null""]},
        ""Time"": {""type"": [""string"", ""null""]},
        ""PartySize"": {""type"": [""integer"", ""null""]},
        ""CustomerName"": {""type"": [""string"", ""null""]}
      }
    },
    ""Totals"": {
      ""type"": [""object"", ""null""],
      ""properties"": {
        ""Subtotal"": {""type"": [""number"", ""null""]},
        ""Currency"": {""type"": ""string""}
      }
    },
    ""next_required_fields"": {""type"": ""array"", ""items"": {""type"": ""string""}}
  }
}")
            );

            requestOptions.Tools.Add(saveOrderTool);
            var menuText = BuildMenuPrompt();
            // fetch real user info
            string userContext = await BuildUserContextAsync(userId);
            // Retrieve or initialize conversation history
            var history = _histories.GetOrAdd(conversationId, _ =>
            {
                var init = new List<OpenAIMessage>();

                init.Add(new SystemChatMessage($@"[Short introduction: AI-powered restaurant assistant chatbot that speaks only Arabic]
 
***
 
# Rules of Operation
 
1. **Conversation Scope**
   - Respond **only in Arabic**.
   - You may answer only about:
     - The restaurant menu and prices
     - Orders (adding, editing, submitting, canceling)
     - Reservations
     - **Restaurant information** (opening hours(اوقات الدوام), name, location, contact info provided in this prompt)
     - **Working Hours Questions**: When users ask ""أيمتى بتفتحوا؟"", ""شو ساعات الدوام؟"", ""متى بتشتغلوا؟"", respond with: ""نحن مفتوحين من الساعة 10:00 صباحاً حتى 11:00 مساءً يومياً، ومغلقين يوم الجمعة. 😊""
     - **Location Questions**: When users ask ""وين مكانكم؟"", ""شو عنوانكم؟"", ""وين موقعكم؟"", respond with: ""موقعنا في رام الله - مدينة روابي. 📍""
     - **Restaurant information** (opening hours(اوقات الدوام), name, location, contact info provided in this prompt)
   - If the user asks about anything else about the Restaurant, do **not call any tool**.
     Instead, reply politely in Arabic with a short, friendly message.
 
2. **Source of Information**
   - Do not invent or provide any information from yourself.
   - All details (menu, descriptions, prices, reservations, user info, restaurant info) come **only from this prompt**.
 
3. **Menu Display**
   - **Intent detection**
     - **Full menu intent**: user asks for the full menu (e.g., ""القائمة/المنيو""، ""اعرض كل الأصناف""، ""بدي الأسعار""، ""اعرض قائمة الطعام كاملة"") **without** include/exclude conditions.
       - **Action**: Show the **full menu directly**. Do **NOT call save_order** for this step.
       - **Response format**: Display all menu items using this format:
         ```
         Name ⟦Id⟧ Description (Category) — Price شيكل
         ```
     - **Specific/filtered menu intent**: user asks to include/exclude an ingredient across the menu (e.g., ""الأصناف بدون بصل/من غير بصل/no onion/without onion"" أو ""الأصناف اللي فيها طماطم/with tomato"").
       - **Action**:
         1) Call `save_order` with `keyword: """"specific""""` and set `newMenu.exception` as:
            - Exclude case (بدون/من غير/no/without): `newMenu.exception = """"لا <ingredient>""""` (e.g., `""""لا بصل""""`).
            - Include case (فيها/تحتوي/with): `newMenu.exception = """"<ingredient>""""` (e.g., `""""طماطم""""`).
         2) Show the filtered items in the same format.
         3) If no items match: apologize that nothing matches the requested condition.
   - **Always show items in this format** for any displayed list (full or filtered):
     ```
     Name ⟦Id⟧ Description (Category) — Price شيكل
     ```
   - **Important**: For simple menu requests like ""القائمة"" or ""اعرض قائمة الطعام"", just display the menu directly without calling any tools.
     - The description comes from `<Description>`: **use the text inside** but **never display < >**.
     - Always include **Name, Id, Description, Category, and Price** for each shown item. Do not omit any fields.
   - **Note**: If the user names a specific item and asks “بدون …/no …”, this is **customization** (not menu filtering). Handle via keyword: `""add_to_order""` and Extras.
 
4. **Structured Data (ToolCall: save_order)**
   - Never display JSON to the user.
   - Use `save_order` **only** for structured actions in the order/reservation flow.  
   - The field `keyword` is **mandatory and must never be empty**.  
   - Allowed values for `keyword` are:  
     - `""add_to_order""` → **when adding a new item or modifying an item. This includes ALL of these scenarios:**
        * **Adding new items**: ""بدي كباب""، ""ضيف بطاطا""، ""أطلب سيزر""
        * **Adding MORE quantity (CRITICAL)**: ""بدي كمان واحد كباب""، ""ضيف كمان 3 ساندويش فلافل""، ""زيد وحدة ثانية من السيزر""، ""كمان 2 كولا""
        * **Customizing items**: ""بدي سيزر بدون خس""، ""كباب بدون بصل""
        * **Keywords that MUST trigger add_to_order**: ""بدي""، ""ضيف""، ""كمان""، ""زيد""، ""أطلب""، ""أريد""، ""واحد ثاني""، ""وحدة ثانية""
        * **CRITICAL**: The phrase ""بدي كمان"" (I want more) ALWAYS means add_to_order, regardless of whether the item already exists in the order.
        * **CRITICAL**: When user says ""كمان"" with any item name, you MUST call save_order with add_to_order keyword.
     - `""remove_item""` → **when removing an item from current order. This includes ALL of these scenarios:**
        * **Direct removal requests**: ""احذف الكباب""، ""شيل البطاطا من الطلب""، ""امحي السيزر""
        * **Cancel specific items**: ""ألغي الكولا""، ""ما بدي الدوناتس""، ""لا تحطلي أجنحة دجاج""
        * **Remove with quantities**: ""احذف وحدة من الكباب""، ""شيل 2 من البطاطا""
        * **Keywords that MUST trigger remove_item**: ""احذف""، ""شيل""، ""امحي""، ""ألغي""، ""ما بدي""، ""لا تحطلي""، ""remove""، ""delete""
        * **CRITICAL**: Always use `target_item_name` field to specify which item to remove.
     - `""show_summary""` → when the user asks to see their current order summary/draft.
     - `""replace_item""` → **when replacing one item with another. This includes:**
        * **Simple replacement**: ""بدل الدوناتس بسيزر""، ""غير الكولا لعصير برتقال""
        * **Replacement with quantity**: ""بدل 2 دوناتس بـ 3 سيزر""، ""غير الكولا الواحدة لـ 2 عصير برتقال""
        * **CRITICAL**: Always use `target_item_name` field for the item to replace
        * **CRITICAL**: Always use `replacement_item` object with `Name` and optionally `Quantity`
        * **Quantity logic**: If no quantity specified in replacement, preserve the original item's quantity
     - `""update_quantity""` → when **changing quantity** of existing item. Use this for phrases like:
       * ""خلي [الصنف] [عدد] بدل [عدد]"" (make [item] [number] instead of [number])
       * ""غير [الصنف] لـ [عدد]"" (change [item] to [number])  
       * ""زيد [الصنف] لـ [عدد]"" (increase [item] to [number])
       * ""خلي الكمية [عدد]"" (make the quantity [number])
       * Any request to modify the quantity of an **existing** item in the order
     - `""submit""` → only after showing a full order summary and receiving confirmation.  
     - `""reserve""` → when creating or confirming a reservation.  
     - `""cancel_order""` → when canceling the current order.
     - `""get_order_history""` → when the user asks to see their previous orders.
     - `""specific""` → **when the user asks for a filtered/specific menu**, e.g., ""الأصناف بدون بصل"" or ""الأصناف اللي فيها طماطم"".  
   - If none of these cases applies, **do not call `save_order`**. Just reply normally in Arabic.  
   - Field `Extras` must include any modifications (e.g., `""بدون بصل""`, `""إضافة جبنة""`).  
   - **Field `newMenu.exception` must always be included when `keyword = ""specific""`.**  
     - Example: ""بدي الأصناف اللي ما فيها بصل"" → `newMenu.exception = ""لا بصل""`  
     - Example: ""الأصناف التي تحتوي على طماطم"" → `newMenu.exception = ""طماطم""`  
   - For all other keywords, `newMenu` must be `null` or omitted.  
   - If some required details are missing (Quantity, Date, PartySize, etc.), put their names in `next_required_fields` and naturally ask the user about them.  
   - Never repeat completed fields inside `next_required_fields`.  
 
5. **Ingredient Handling — Filtering vs. Customization**
   - There are **three different intents** now:  
     1) **Menu filtering (keyword: specific)**: user asks to *show/list* items with or without an ingredient (e.g., “بدي الأصناف اللي ما فيها بصل”, “اعرض الأصناف اللي فيها طماطم”).  
        - Action: call `save_order` with `keyword: ""specific""`.  
        - Put the filter condition inside `newMenu.exception`.  
        - Search the `<Description>` of each item (case-insensitive; Arabic/English variants).  
        - Exclude or include items based on the condition.  
        - Show the results in full format ` Description (Category) — Price شيكل`.  
        - If none match:  
          > ""عذرًا، ما لقيت أصناف مطابقة لشرطك بالنسبة لـ [المكوّن].""  
 
6. **Conversation Flow**
   - Always start with: **""""مرحباً {{USER_NAME}}! أنا مساعد YallaEat 😊\nكيف أقدر أساعدك اليوم؟ (قائمة الطعام، الأسعار، التوصيل، الحجوزات…)""""** where {{USER_NAME}} is the Name from the User Context. If Name is ""Guest User"" or ""Unknown User"", just say ""مرحباً! أنا مساعد YallaEat 😊\nكيف أقدر أساعدك اليوم؟ (قائمة الطعام، الأسعار، التوصيل، الحجوزات…)"" without the name. **IMPORTANT: Always include the user's name regardless of language (Arabic, English, or any other language) - names should be used as-is.**
   - If the user mentions a reservation, switch directly to reservation questions.
   - For each item, ask only the missing details (size, flavor, quantity, extras).
   - After adding an item, provide a short summary (name + quantity + partial price) and ask if they want to add more.
   - At any time, if the user asks about their current order, respond with a full summary and total price.
   - If the user asks to see their previous orders (e.g., ""شو كانت طلباتي؟"", ""اعرضلي آخر طلب"", ""طلباتي السابقة""), you must immediately call the `save_order` tool with `keyword: ""get_order_history""`. Do not ask any questions, just execute the tool.
   - For reservations, ask about (Date, Time, PartySize, Notes), then call `""reserve""`.
   - If the user asks to remove an item (e.g., ""احذف الكباب""، ""شيل البطاطا""، ""ما بدي السيزر""), you must immediately call the `save_order` tool with `keyword: ""remove_item""` and set `target_item_name` to the item name. Do not ask for confirmation.
   - For cancellations, immediately call `""cancel_order""` and confirm the cancellation to the user.
   - If the user asks about their current order (e.g., ""شو في طلبي؟"", ""اعرضلي الطلب الحالي"", ""ملخص الطلب""), you must immediately call the `save_order` tool with `keyword: ""show_summary""`. Do not ask any questions, just execute the tool.
 
### Address and Submission Workflow
   **Handling Delivery Questions:**
    -If the user asks a general question about delivery prices(e.g., ""شو أسعار التوصيل عندكم؟?"", ""لوين بتوصلوا؟""):
    -You must answer by rephrasing the information in a friendly, natural way.Do not just copy the list.
    -**Good response example:***""بالتأكيد! أسعار التوصيل عنا بسيطة: لبيرزيت 7 شيكل، لرام الله 12 شيكل، ولأهلنا في روابي التوصيل مجاني 😊. حالياً هاي هي المناطق اللي بنغطيها."" *
 
   **Initiating Final Submission:**
    -When the user asks to finalize the order (e.g., """"أكدلي""""، """"ابعت الطلب""""، """"بدّي أرسل""""):
    -First, display a clear summary of the items and the subtotal.
    -Second, ask for the delivery address in detail.You can say something like: *""تمام، لوين بتحب نوصل الطلب؟ من فضلك زودني بالمدينة والحي أو الشارع."" *

   **Address Collection Rules:**
    -**CRITICAL RULE**: If the user provides ONLY a city name (e.g., ""رام الله"", ""بيرزيت"", ""روابي""), you **MUST ALWAYS** ask for more details.
    -**Examples of incomplete addresses that require follow-up:**
      * ""رام الله"" → Ask for street/neighborhood
      * ""بيرزيت"" → Ask for street/neighborhood  
      * ""روابي"" → Ask for street/neighborhood
      * ""ramallah"" → Ask for street/neighborhood
    -**How to ask for details**: Use the city name the user provided in your follow-up question.
      * **Example:** If user says ""رام الله"", respond: *""تمام، ممكن تفاصيل أكتر في رام الله؟ أي شارع أو حي تحديداً؟""*
      * **Example:** If user says ""بيرزيت"", respond: *""تمام، ممكن تفاصيل أكتر في بيرزيت؟ أي شارع أو حي؟""*
      * **Example:** If user says ""روابي"", respond: *""تمام، ممكن تفاصيل أكتر في روابي؟ أي شارع أو حي؟""*

    - **RESERVATION HANDLING:**
     - **New Reservation Request**: When user says ""بدي أعمل حجز طاولة جديد"" or ""حجز جديد"" or ""أريد حجز آخر"", treat this as a completely fresh reservation request. Ask for all details from scratch: date, time, and party size.
     - **Fresh Start**: Do NOT reference previous reservations when handling new reservation requests. Always ask: ""تمام! كم شخص للحجز الجديد؟ وأي تاريخ ووقت تفضل؟""
     - **Standard Reservation Flow**: For regular reservation requests (""بدي حجز طاولة"", ""أريد حجز""), ask about (Date, Time, PartySize), then call `""reserve""`.
     - **Availability Check**: Always check against the database. The restaurant has 7 tables maximum per time slot.
   
   **Address Completion:**
    -After the user provides additional details (e.g., ""شارع الإرسال"", ""حي الماصيون"", ""قرب المسجد""), you **MUST combine** it with the original city name.
    -**Examples of complete address formation:**
      * Original: ""رام الله"" + Details: ""شارع الإرسال"" = Final: **""رام الله، شارع الإرسال""**
      * Original: ""بيرزيت"" + Details: ""قرب الجامعة"" = Final: **""بيرزيت، قرب الجامعة""**
      * Original: ""روابي"" + Details: ""حي الريحان"" = Final: **""روابي، حي الريحان""**
    -**Goal**: The final address sent to the submit tool must contain: **City + Specific Location Details**

   **When NOT to ask for details:**
    -If the user provides a complete address from the start (e.g., ""رام الله، شارع الإرسال"", ""بيرزيت قرب الجامعة""), accept it as complete.
    -If the address already contains specific details like street names, neighborhoods, or landmarks.

   **Handling Out-of-Zone Addresses:**
    - If the provided address is outside the supported delivery zones(any area other than Rawabi, Birzeit, or Ramallah), you **must** follow this specific two - step process:
    -**First, **politely apologize and clearly state that delivery is not available for their chosen area.
    - **Second,**immediately ask the user if they would like to provide a different address within the supported zones or if they prefer to cancel the order.
    - Don't forget to ask for details, and never accept a single-word address.
    - Wait for the user's response to either provide a new address or cancel.
    - **Never call the tool with `keyword: ""submit""` unless you have a complete and valid delivery address from the user.**
    

7. **Prices (fixed)**  
   {menuText}  

    {userContext}
- Always address the user by their name from the user context (Arabic or English as-is).
- For the first message in a conversation, use the welcome format: ""أهلًا [Name]! شو بتحب تطلب اليوم؟"" If the user is a guest, use ""أهلًا! شو بتحب تطلب اليوم؟""


9. Restaurant Information:
   - Name: ""YallaEat""
   - Working Hours: 10:00AM - 11:00 PM  (closed on Friday)
   - Current Date & Time: {DateTime.Now:yyyy-MM-dd HH:mm}
   - Today is: {DateTime.Now.ToString("dddd", new System.Globalization.CultureInfo("ar-SA"))}
   - If the current time is outside working hours, you must inform the user that the restaurant is closed now and provide them with the opening hours.
   - Location: Ramallah – Rawabi City

10.Reservation Information:
   - Minimum reservation: 1 person
   - Maximum reservation: 10 people
   - **CRITICAL VALIDATION RULES**: 
     * **FRIDAY RESTRICTION**: The restaurant is CLOSED on Fridays. If user requests ANY Friday reservation (regardless of date), immediately reject with: ""⚠️ عذراً، المطعم مغلق يوم الجمعة. يرجى اختيار يوم آخر للحجز.""
     * **PARTY SIZE LIMIT**: If user requests more than 10 people, immediately reject with: ""⚠️ عذراً، الحد الأقصى للحجز هو 10 أشخاص والحد الأدنى شخص واحد. يرجى تعديل عدد الأشخاص.""
     * **PAST DATE RESTRICTION**: If user requests a past date, immediately reject with: ""⚠️ لا يمكن حجز طاولة في تاريخ سابق. يرجى اختيار تاريخ في المستقبل.""
   - **DO NOT CALL `reserve` TOOL** if any of the above validations fail. Just respond with the appropriate error message.
   - Use the keyword """"reserve"""" for reservation actions ONLY when all validations pass.
   - **IMPORTANT**: The restaurant has exactly 7 tables. Check the database for existing reservations at the requested date/time.
   - **AVAILABILITY LOGIC**: If there are already 7 reservations for the same date and time in the database, reject the new reservation and ask the customer to choose another time.
   - **DUPLICATE PREVENTION**: Do NOT allow the same user to have multiple reservations for the exact same date/time combination.
   - When extracting a reservation date or time, always return it in ISO 8601 format (yyyy-MM-dd and HH:mm), even if the customer speaks in Arabic.

**Delivery Zones and Fees(Fixed):**
   -Rawabi: Free
   - Birzeit: 7 ILS
   - Ramallah: 12 ILS
   - Any other area: Not supported. You must politely apologize and inform the user that delivery is not available for their area.

11. **Reply Style**  
    - Replies must always be short, friendly, and natural, like a restaurant staff member.  
    - Never use technical symbols, JSON, or system-like responses.  
    - Never skip to the next step before the current one is answered.  
    - If the request is ambiguous, ask one simple clarifying question.  

11. **Handling Recommendations**
    - If the user asks for a recommendation (e.g., ""توصية"", ""ايش تنصحني"" , ""ايش أطيب اشي عندكم"", ), you must suggest a popular dish.
    - Use the following response style:
  - ""أكيد! بنصحك تجرب \n{listText}\n،  من الأطباق الأكثر مبيعًا عنا. شو رأيك تجرب منهم أو بتحب تطلب اشي ثاني ؟""
    - You should pick a popular item from the menu to replace \n{listText}\n)
***

# Behavior Examples (Arabic)

- **Example: Missing detail**  
  المستخدم: ""بدي كباب""  
  المساعد: ""تمام، كم سيخ بتحب من الكباب؟""  

- **Example: After adding item**  
  ""أضفت سيخين كباب مع صحن حمص. المجموع الحالي 30 شيكل. بتحب تضيف شي تاني؟""  

- **Example: Final submission**  
  ""جاهز أأكد وأبعت الطلب؟"" → then call `save_order` with `""submit""`.  

- **Example: Reservation**  
  ""كم شخص؟ وعلى أي تاريخ بتحب؟"" → after details, call `save_order` with `""reserve""`.  

- **Example: Cancel**  
  ""تم إلغاء طلبك. إذا بتحب نبلّش طلب جديد خبرني.""  

- **Example: Out-of-scope question**  
  المستخدم: ""شو رأيك بمباراة أمس؟""  
  المساعد: ""آسف، بقدر أساعدك فقط بخصوص الطلبات أو الحجوزات.""  

- **Example: Customization (Extras)**  
  المستخدم: ""بدي سيزر بدون خس""  
  المساعد: ""تمام! بتحب الكمية؟"" → (call `save_order` with `""add_to_order""`, `Items[0].Name=""سيزر""`, `Extras=[""بدون خس""]`)  

- **Example: Remove item from order**  
  المستخدم: """"احذف الكباب من الطلب""""  
  المساعد: → (call `save_order` with `""""remove_item""""`, `target_item_name=""""كباب""""`)

- **Example: Remove with different phrasing**  
  المستخدم: """"شيل البطاطا""""، """"ما بدي السيزر""""، """"ألغي الكولا""""  
  المساعد: → (call `save_order` with `""""remove_item""""`, `target_item_name=""[item name]""`)

- **Example: Remove specific quantity (treat as full removal)**  
  المستخدم: """"احذف وحدة من الكباب""""  
  المساعد: → (call `save_order` with `""""remove_item""""`, `target_item_name=""""كباب""""`)

**Keywords that MUST trigger show_summary**: 
""اعرض طلبي""، ""شو في طلبي""، ""ملخص الطلب""، ""شو طلبت""، ""اعرضلي الطلب الحالي""، ""طلبي الحالي""، ""شو عندي بالطلب""، ""وين وصل طلبي""، ""summary""، ""current order""

- **Example: Adding more of existing item (MUST use add_to_order)**  
  المستخدم: """"بدي كمان واحد كباب""""  
  المساعد: → (call `save_order` with `""""add_to_order""""`, `Items[0].Name=""""كباب""""`, `Quantity=1`)

- **Example: Adding more with different phrasing**  
  المستخدم: """"كمان 2 كولا""""، """"زيد وحدة ثانية من السيزر""""  
  المساعد: → (call `save_order` with `""""add_to_order""""`, with appropriate item and quantity)

- **Example: Various ""more"" phrases that MUST trigger add_to_order**  
  المستخدم: """"واحد ثاني من الكباب""""، """"وحدة ثانية سيزر""""، """"كمان واحد""""  
  المساعد: → (call `save_order` with `""""add_to_order""""`)

- **Example: Filtering (specific with newMenu)**  
  المستخدم: ""اعرض الأصناف اللي لا تحتوي بصل""  
  → (call `save_order` with `""specific""`, `newMenu.exception=""لا بصل""`)  
  المستخدم: ""اعرض الأصناف اللي فيها بندوره""  
  → (call `save_order` with `""specific""`, `newMenu.exception=""بندوره""`)  

- **Example: Show current order summary**  
  المستخدم: ""اعرضلي طلبي الحالي"" أو ""شو في عندي بالطلب؟""  
  المساعد: → (call `save_order` with `""show_summary""`)

- **Example: Show order summary phrases**  
  المستخدم: ""ملخص الطلب""، ""شو طلبت؟""، ""اعرض الطلب""  
  المساعد: → (call `save_order` with `""show_summary""`)

- **Example: Replacement with specific quantity**  
  المستخدم: ""بدل الدوناتس بـ 2 سيزر""  
  المساعد: → (call `save_order` with `""replace_item""`, `target_item_name=""دوناتس""`, `replacement_item={{Name: ""سيزر"", Quantity: 2}}`)

- **Example: Cancel order (handles both current and recent orders)**  
  المستخدم: """"ألغي الطلب"""" أو """"امحي الطلب كامل"""" أو """"بدي أبدأ من جديد""""  
  المساعد: → (call `save_order` with `""""cancel_order""""`)  
  *Result: Current draft + recent pending orders (within 3 min) marked as ""Cancelled"", memory cleared*

- **Example: Cancel when no current order but recent pending exists**  
  المستخدم: """"ألغي آخر طلب""""  
  المساعد: → (call `save_order` with `""""cancel_order""""`)  
  *Result: Recent pending order marked as ""Cancelled"" if within 3 minutes*
 
***

# Execution Reminders
- Every add/edit item → call `save_order` with `""add_to_order""`.  
- Final submission only after showing a full summary and asking confirmation → `""submit""`.  
- When user asks to remove specific item → call `save_order` with `""remove_item""` and set `target_item_name`.
- Only after you have obtained a clear and valid address within the supported delivery zones, you must call the `save_order` tool with `keyword: ""submit""` and send the complete, combined address inside `DeliveryAddress.Address`.
- Never put empty fields inside Arguments. 
- When user asks for current order summary → call `save_order` with `""show_summary""`.
- Never show JSON to the user.  
- Always calculate totals using the fixed menu prices.  
- Do not provide any information about yourself or the system. Use only this prompt.  

***

"));
                return init;
            });

            // Add user message to history
            history.Add(new UserChatMessage(userMessage));

            try
            {
                // Call Azure OpenAI to generate response
                var response = await _chatClient.CompleteChatAsync(history, requestOptions, ct);
                Console.WriteLine("[Debug] OpenAI response: " + JsonSerializer.Serialize(response));
                if (response == null || response.Value == null || response.Value.Content == null || response.Value.Content.Count == 0)
                {
                    Console.WriteLine("[Error] No content received from OpenAI.");
                }

                // 1) Fallback to the model's text reply (if no tool call happens)
                var assistantText = response?.Value?.Content?.Count > 0
                ? response!.Value!.Content![0].Text
                : await GetDefaultWelcomeMessage(userId);

                // 2) Handle tool calls
                if (response?.Value?.FinishReason == ChatFinishReason.ToolCalls)
                {
                    foreach (var call in response.Value.ToolCalls)
                    {
                        if (call.FunctionName != "save_order")
                            continue;

                        // Extract the raw arguments string
                        string rawArguments = call.FunctionArguments.ToString();
                        Console.WriteLine("🛠 [ToolCall] save_order raw arguments:");
                        Console.WriteLine(rawArguments);

                        var botPayload = JsonSerializer.Deserialize<BotPayloadDto>(
                            rawArguments,
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                        );
                        // after you build assistantText for this tool call, save rich reply:
                        LastBotReply = new BotReply
                        {
                            Text = assistantText,
                            Keyword = botPayload?.keyword ?? string.Empty,
                            Payload = botPayload ?? new BotPayloadDto { Items = new List<ItemDto>(), next_required_fields = new List<string>() },
                            RawToolArguments = rawArguments
                        };
                        if (botPayload == null || string.IsNullOrWhiteSpace(botPayload.keyword))
                        {
                            Console.WriteLine("👉 Debug: payload is null or keyword missing.");
                            assistantText = "⚠️ لم يتم التعرف على الإجراء.";
                        }
                        else
                        {
                            switch (botPayload.keyword)
                            {
                                case "add_to_order":
                                    Console.WriteLine("[Debug]: add_to_order keyword detected.");
                                    assistantText = await HandleAddToInMemoryOrder(botPayload, userId, conversationId);
                                    //if (botPayload.Items != null && botPayload.Items.Any())
                                    //{
                                    //    try
                                    //    {
                                    //        // Map items to internal DTOs with IDs and other data
                                    //        var internalItems = await MapItemsToDtos(botPayload.Items);

                                    //        if (internalItems.Any())
                                    //        {
                                    //            // Convert to the expected OrderItemDto format
                                    //            var orderItems = ConvertToOrderItemDtos(internalItems);

                                    //            // Add to user's order through OrderService
                                    //            if (!string.IsNullOrWhiteSpace(userId))
                                    //            {
                                    //                await _orderService.AddItemsToDraftOrder(userId, orderItems);
                                    //                Console.WriteLine($"[Debug]: Items added to draft order successfully");
                                    //            }

                                    //            // Generate summary for response
                                    //            var summary = string.Join(Environment.NewLine, botPayload.Items.Select(i =>
                                    //            {
                                    //                var qty = i.Quantity ?? 0;
                                    //                var size = string.IsNullOrWhiteSpace(i.Size) ? "" : $" ({i.Size})";
                                    //                return $"{(qty > 0 ? $"{qty} × " : "")}{i.Name}{size}";
                                    //            }));

                                    //            assistantText = $"🍽️ أضفت الأصناف التالية لطلبك:\n{summary}\n\nهل تريد إضافة شيء آخر أم أؤكد الطلب؟";
                                    //        }
                                    //        else
                                    //        {
                                    //            Console.WriteLine("[Warning] No valid items could be mapped");
                                    //            assistantText = "⚠️ عذراً، لم أتمكن من العثور على الأصناف المطلوبة. هل يمكنك اختيار صنف آخر من القائمة؟";
                                    //        }
                                    //    }
                                    //    catch (Exception ex)
                                    //    {
                                    //        Console.WriteLine($"[Error]: Failed to add items to order: {ex.Message}");
                                    //        assistantText = "⚠️ عذراً، حدثت مشكلة أثناء إضافة الصنف. هل يمكنك إعادة المحاولة؟";
                                    //    }
                                    //}
                                    break;

                                case "remove_item":
                                    Console.WriteLine("[Debug]: remove_item keyword detected.");
                                    assistantText = await HandleRemoveItem(botPayload, userId, conversationId);
                                    break;
                                case "replace_item":
                                    Console.WriteLine("[Debug]: replace_item keyword detected.");
                                    assistantText = await HandleReplaceItem(botPayload, userId, conversationId);
                                    break;
                                case "update_quantity":
                                    Console.WriteLine("[Debug]: update_quantity keyword detected.");
                                    assistantText = await HandleUpdateQuantity(botPayload, userId, conversationId);
                                    break;
                                case "submit":
                                    Console.WriteLine($"Debug: submit keyword detected (items count = {botPayload.Items?.Count ?? 0})");
                                    assistantText = await HandleSubmitOrder(botPayload, userId, conversationId);
                                    //assistantText = "✅ تم إرسال طلبك. شكراً!";

                                    //if (botPayload.DeliveryAddress != null && !string.IsNullOrWhiteSpace(botPayload.DeliveryAddress.Address) && !string.IsNullOrWhiteSpace(userId))
                                    //{
                                    //    try
                                    //    {
                                    //        var address = botPayload.DeliveryAddress.Address;

                                    //        var confirmedOrder = await _orderService.ConfirmOrder(userId, address);

                                    //        assistantText = $"✅ تم تأكيد طلبك بنجاح!\n" +
                                    //                      $"سيتم توصيله إلى: {confirmedOrder.DeliveryAddress}\n" +
                                    //                      $"المجموع الإجمالي (مع التوصيل): {confirmedOrder.TotalPrice:F2} شيكل.\n" +
                                    //                      $"شكراً لطلبك من YallaEat!";
                                    //    }
                                    //    catch (Exception ex)
                                    //    {
                                    //        Console.WriteLine($"[Error]: Failed to confirm order: {ex.Message}");
                                    //        assistantText = $"⚠️ عذراً، حدثت مشكلة أثناء تأكيد طلبك: {ex.Message}.";
                                    //    }
                                    //}
                                    //else
                                    //{
                                    //    assistantText = "من فضلك، أحتاج إلى عنوان التوصيل أولاً لتأكيد الطلب. لوين بتحب نوصل الطلب؟";
                                    //}
                                    break;

                                case "reserve":
                                    Console.WriteLine("Debug: reserve keyword detected.");
                                    if (botPayload.Reservation != null)
                                    {
                                        try
                                        {
                                            DateTime date = DateTime.Parse(s: botPayload.Reservation.Date);
                                            TimeSpan time = TimeSpan.Parse(s: botPayload.Reservation.Time);
                                            int partySize = botPayload.Reservation.PartySize ?? 1;

                                            // ✅ Validate party size
                                            if (partySize < 1 || partySize > 10)
                                            {
                                                assistantText = "⚠️ عذراً، الحد الأقصى للحجز هو 10 أشخاص والحد الأدنى شخص واحد. يرجى تعديل عدد الأشخاص.";
                                                break;
                                            }

                                            // ✅ Check if it's Friday (restaurant closed)
                                            if (date.DayOfWeek == DayOfWeek.Friday)
                                            {
                                                assistantText = "⚠️ عذراً، المطعم مغلق يوم الجمعة. يرجى اختيار يوم آخر للحجز.";
                                                break;
                                            }

                                            // ✅ Check if date is in the past
                                            if (date.Date < DateTime.Today)
                                            {
                                                assistantText = "⚠️ لا يمكن حجز طاولة في تاريخ سابق. يرجى اختيار تاريخ في المستقبل.";
                                                break;
                                            }

                                            var user = await _db.Users.FirstOrDefaultAsync(u => u.UserId == userId);
                                            if (user == null)
                                            {
                                                assistantText = "⚠️ يجب أن تكون مسجلاً للدخول لإجراء حجز.";
                                                break;
                                            }

                                            // ✅ Check total table availability first (7 tables max)
                                            int existingReservations = await _db.Reservations
                                                .CountAsync(r => r.Date == date && r.Time == time);

                                            Console.WriteLine($"[Debug] Reservation check: Date={date:yyyy-MM-dd}, Time={time}, Existing reservations={existingReservations}");

                                            if (existingReservations >= 7)
                                            {
                                                assistantText = "❌ عذراً، جميع الطاولات محجوزة في هذا الموعد. حاول وقت آخر.";
                                            }
                                            else
                                            {
                                                // ✅ Check if user already has a reservation for this EXACT date/time
                                                var existingUserReservation = await _db.Reservations
                                                    .FirstOrDefaultAsync(r => r.CustomerName == user.UserName && r.Date == date && r.Time == time);

                                                if (existingUserReservation != null)
                                                {
                                                    // ✅ STRICT PREVENTION: Don't allow duplicate reservations
                                                    assistantText = $"⚠️ لديك حجز مسبق في نفس الموعد ({date:yyyy-MM-dd} الساعة {time}).\n" +
                                                                  $"لا يمكن حجز أكثر من طاولة واحدة في نفس الوقت.\n" +
                                                                  $"يرجى اختيار وقت مختلف للحجز الجديد.";
                                                }
                                                else
                                                {
                                                    // ✅ NEW RESERVATION: Create the reservation
                                                    var reservation = new ReservationEntity
                                                    {
                                                        CustomerName = user.UserName,
                                                        Date = date,
                                                        Time = time,
                                                        PartySize = partySize
                                                    };

                                                    _db.Reservations.Add(reservation);
                                                    await _db.SaveChangesAsync();

                                                    Console.WriteLine($"[Debug] Created new reservation: ID={reservation.Id}, Customer={reservation.CustomerName}, Date={date:yyyy-MM-dd}, Time={time}, PartySize={reservation.PartySize}");

                                                    assistantText = $"✅ تم حجز طاولة باسم {reservation.CustomerName} بتاريخ {date:yyyy-MM-dd} ⏰ الساعة {time} لعدد {reservation.PartySize} أشخاص.";
                                                }
                                            }
                                        }
                                        catch (Exception ex)
                                        {
                                            Console.WriteLine($"[Error] Reservation failed: {ex.Message}");
                                            assistantText = "⚠️ لم أستطع تسجيل الحجز. تأكد من كتابة التاريخ والوقت بشكل صحيح.";
                                        }
                                    }
                                    else
                                    {
                                        assistantText = "⚠️ لم أستطع التعرف على تفاصيل الحجز. ممكن تعطيني التاريخ والوقت وعدد الأشخاص؟";
                                    }
                                    break;

                                case "cancel_order":
                                    Console.WriteLine("Debug: cancel_order keyword detected.");
                                    assistantText = await HandleCancelOrder(botPayload, userId, conversationId);
                                    break;

                                case "show_summary":
                                    Console.WriteLine("[Debug]: show_summary keyword detected.");
                                    assistantText = await HandleShowSummary(botPayload, userId, conversationId);
                                    break;

                                case "get_order_history":
                                    Console.WriteLine("Debug: get_order_history keyword detected.");

                                    if (string.IsNullOrWhiteSpace(userId))
                                    {
                                        assistantText = "عذراً، يجب أن تكون مسجلاً للدخول لعرض طلباتك السابقة.";
                                        break;
                                    }

                                    try
                                    {
                                        var previousOrders = await _orderService.GetOrdersByUserId(userId, 3);
                                        if (previousOrders == null || !previousOrders.Any())
                                        {
                                            assistantText = "ليس لديك أي طلبات سابقة في سجلاتنا.";
                                        }
                                        else
                                        {
                                            var responseBuilder = new System.Text.StringBuilder();
                                            responseBuilder.AppendLine("📋 هذه هي آخر طلباتك:");

                                            foreach (var order in previousOrders)
                                            {
                                                responseBuilder.AppendLine($"\n--------------------");
                                                responseBuilder.AppendLine($"**طلب رقم: #{order.OrderId}** (بتاريخ: {order.CreatedAt:yyyy-MM-dd})");
                                                responseBuilder.AppendLine($"*الحالة: {order.Status.ToString()}*");

                                                foreach (var item in order.Items)
                                                {
                                                    responseBuilder.AppendLine($"- {item.Quantity} × {item.MenuItemName}");
                                                }
                                                responseBuilder.AppendLine($"**المجموع الإجمالي: {order.TotalPrice:F2} شيكل**");
                                            }
                                            assistantText = responseBuilder.ToString();
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        Console.WriteLine($"[Error]: Failed to get previous orders: {ex.Message}");
                                        assistantText = "⚠️ عذراً، حدثت مشكلة أثناء جلب سجل طلباتك.";
                                    }
                                    break;

                                case "specific":
                                    switch (botPayload.newMenu?.exception)
                                    {
                                        case "لا بصل":
                                            assistantText =
                                                "كباب (لحم، خبز عربي، صلصة طحينة )(وجبات رئيسية) — 22.00 شيكل\r\n" +
                                                "بطاطا (فرايز بطاطا مقرمشة) (مقبلات) — 12.00 شيكل\r\n" +
                                                "أجنحة دجاج (أجنحة دجاج مقلية, صلصة باربكيو )(مقبلات) — 23.00 شيكل\r\n" +
                                                "كولا  500 ml (مشروبات) — 5.00 شيكل\r\n" +
                                                "عصير برتقال  500 ml (مشروبات) — 5.00 شيكل\r\n" +
                                                "قهوة (قهوة عربية) (مشروبات) — 5.00 شيكل\r\n" +
                                                "تشيز كيك (كيك كريمي, قاعدة بسكويت، صوص الفراولة أو الشوكولاتة.) (حلويات) — 10.00 شيكل\r\n" +
                                                "دوناتس (عجينة مغطاة بالسكر و الشوكولاتة) (حلويات) — 10.00 شيكل\r\n" +
                                                "ساندويش فلافل (فلافل, طحينة, بندورة, مخلل, خيار,طحينية) (ساندويش) — 8.00 شيكل\r\n" +
                                                "سيزر (خس، دجاج مشوي، بارميزان، وصوص سيزر) (سلطة) — 15.00 شيكل";
                                            break;

                                        case "بصل":
                                            assistantText =
                                                "كلاسيك بيف برجر (جبنة , لحم , خس، بندورة، بصل) (وجبات رئيسية) — 22.00 شيكل\r\n" +
                                                "تبولة (بقدونس، برغل، بندورة, خيار، بصل، ليمون, زيت زيتون) (سلطة) — 12.00 شيكل\r\n";
                                            break;

                                        case "لا خس":
                                            assistantText =
                                                "كباب (لحم، خبز عربي، صلصة طحينة) (وجبات رئيسية) — 22.00 شيكل\r\n" +
                                                "بطاطا (فرايز بطاطا مقرمشة) (مقبلات) — 12.00 شيكل\r\n" +
                                                "أجنحة (دجاج أجنحة دجاج مقلية, صلصة باربكيو) (مقبلات) — 23.00 شيكل\r\n" +
                                                "كولا  500 ml (مشروبات) — 5.00 شيكل\r\n" +
                                                "عصير برتقال  500 ml (مشروبات) — 5.00 شيكل\r\n" +
                                                "قهوة (قهوة عربية) (مشروبات) — 5.00 شيكل\r\n" +
                                                "تشيز (كيك كيك كريمي, قاعدة بسكويت، صوص الفراولة أو الشوكولاتة.) (حلويات) — 10.00 شيكل\r\n" +
                                                "دوناتس (عجينة مغطاة بالسكر و الشوكولاتة) (حلويات) — 10.00 شيكل\r\n" +
                                                "ساندويش فلافل (فلافل, طحينة, بندورة, مخلل, خيار,طحينية) (ساندويش) — 8.00 شيكل" +
                                                "تبولة (بقدونس، برغل، بندورة, خيار، بصل، ليمون, زيت زيتون) (سلطة) — 12.00 شيك";
                                            break;

                                        case "خس":
                                            assistantText =
                                                "كلاسيك بيف برجر (جبنة , لحم , خس، بندورة، بصل) (وجبات رئيسية) — 22.00 شيكل\r\n" +
                                                "سيزر (خس، دجاج مشوي، بارميزان، وصوص سيزر) (سلطة) — 15.00 شيكل\r\n";
                                            break;

                                        case "بندوره":
                                            assistantText =
                                                "كلاسيك بيف برجر (جبنة , لحم , خس، بندورة، بصل) (وجبات رئيسية) — 22.00 شيكل\r\n" +
                                                "تبولة (بقدونس، برغل، بندورة, خيار، بصل، ليمون, زيت زيتون) (سلطة) — 12.00 شيك";
                                            break;

                                        case "لا بندوره":
                                            assistantText =
                                                "كباب (لحم، خبز عربي، صلصة طحينة) (وجبات رئيسية) — 22.00 شيكل\r\n" +
                                                "بطاطا (فرايز بطاطا مقرمشة) (مقبلات) — 12.00 شيكل\r\n" +
                                                "أجنحة دجاج (أجنحة دجاج مقلية, صلصة باربكيو) (مقبلات) — 23.00 شيكل\r\n" +
                                                "كولا  500 ml (مشروبات) — 5.00 شيكل\r\n" +
                                                "عصير برتقال  500 ml (مشروبات) — 5.00 شيكل\r\n" +
                                                "قهوة (قهوة عربية) (مشروبات) — 5.00 شيكل\r\n" +
                                                "تشيز كيك (كيك كريمي, قاعدة بسكويت، صوص الفراولة أو الشوكولاتة.) (حلويات) — 10.00 شيكل\r\n" +
                                                "دوناتس (عجينة مغطاة بالسكر و الشوكولاتة) (حلويات) — 10.00 شيكل\r\n" +
                                                "سيزر (خس، دجاج مشوي، بارميزان، وصوص سيزر) (سلطة) — 15.00 شيكل\r\n";
                                            break;

                                        default:
                                            assistantText = "تصفية حسب المكوّن المطلوب";
                                            break;
                                    }
                                    break;

                                default:
                                    Console.WriteLine($"Debug: unknown keyword = {botPayload.keyword}");
                                    assistantText = " لم أفهم الإجراء المطلوب.";
                                    break;
                            }
                        }
                    }
                }

                // Clean hidden IDs from text before showing to user
                assistantText = StripHiddenIds(assistantText);
                assistantText = (assistantText ?? string.Empty).Trim();
                // Save assistant reply to history
                history.Add(new AssistantChatMessage(assistantText));

                if (LastBotReply == null)
                {
                    LastBotReply = new BotReply
                    {
                        Text = assistantText,
                        Keyword = string.Empty,
                        Payload = new BotPayloadDto
                        {
                            Items = new List<ItemDto>(),
                            next_required_fields = new List<string>()
                        },
                        RawToolArguments = string.Empty
                    };
                }
                else
                {
                    LastBotReply.Text = assistantText;
                }

                return assistantText;
            }
            catch (OperationCanceledException)
            {
                return "⏳ تم إلغاء الطلب. جرّب مرة أخرى إذا استمر التأخير.";
            }
            catch (RequestFailedException ex) when (ex.Status == 401 || ex.Status == 403)
            {
                return "❌ تعذّر الاتصال بخدمة الذكاء الاصطناعي (صلاحيات غير كافية). يرجى إبلاغ الدعم.";
            }
            catch (RequestFailedException ex) when (ex.Status == 429)
            {
                return "⚠️ هناك ضغط مرتفع حاليًا. من فضلك أعد المحاولة بعد لحظات.";
            }
            catch (RequestFailedException)
            {
                return "❌ حدث خطأ بالخادم أثناء معالجة طلبك. يرجى المحاولة لاحقًا.";
            }
            catch (Exception)
            {
                return "❌ حدث خطأ غير متوقع. إذا تكرر الأمر، يرجى إبلاغ الدعم.";
            }
        }

        private string BuildMenuPrompt()
        {
            var items = _db.MenuItems.Where(m => m.IsAvailable).ToList();
            if (!items.Any())
                return "لا يوجد أصناف متاحة حالياً.";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("📋 القائمة الحالية:");

            foreach (var item in items)
            {
                sb.AppendLine($"{item.Name} ⟦{item.Id}⟧ <{item.Description}> ({item.Category}) — {item.Price} شيكل");
            }

            return sb.ToString();
        }


        /// <summary>
        /// Builds the user context section dynamically based on the logged-in user's information.
        /// </summary>
        /// <param name="userId">The current user's ID</param>
        /// <returns>Formatted user context text for the system prompt</returns>
        private async Task<string> BuildUserContextAsync(string? userId)
        {
            // If no user is logged in, use fallback context
            if (string.IsNullOrWhiteSpace(userId))
            {
                return @"8. *User Context (Guest User)*  
                            - Name: ""Guest User""  
                            - Phone: Not provided  
                            - Address: Not provided";
            }

            try
            {
                // Fetch the current user from database
                var user = await _db.Users.FirstOrDefaultAsync(u => u.UserId == userId);

                if (user == null)
                {
                    Console.WriteLine($"[Warning] User with ID {userId} not found in database");
                    return @"8. *User Context (Unknown User)*  
                               - Name: ""Unknown User""  
                               - Phone: Not provided  
                               - Address: Not provided";
                }

                // Build dynamic user context with real data
                return $@"8. *User Context (Current User)*  
                            - Name: ""{user.UserName}""  
                            - Phone: {user.PhoneNumber}  
                            - Email: {user.Email}  
                            - Address: {GetUserDefaultAddress(user)}";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Error] Failed to fetch user context: {ex.Message}");
                return @"8. *User Context (Error)*  
                            - Name: ""System User""  
                            - Phone: Not available  
                            - Address: Not available";
            }
        }

        /// <summary>
        /// Gets a default address for the user (you can customize this logic)
        /// </summary>
        /// <param name="user">The user entity</param>
        /// <returns>A default address string</returns>
        private string GetUserDefaultAddress(UserEntity user)
        {
            // You can implement logic here to:
            // 1. Get the user's last used address from their order history
            // 2. Use a stored default address if you add that to UserEntity
            // 3. Return a city-based default

            // For now, let's get the last delivery address from their orders
            try
            {
                var lastOrder = _db.Orders
                    .Where(o => o.UserId == user.Id)
                    .OrderByDescending(o => o.CreatedAt)
                    .FirstOrDefault();

                if (lastOrder != null && !string.IsNullOrWhiteSpace(lastOrder.DeliveryAddress) &&
                    lastOrder.DeliveryAddress != "Not Provided")
                {
                    return lastOrder.DeliveryAddress;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Warning] Could not fetch user's last address: {ex.Message}");
            }

            // Fallback to a generic address
            return "Address not provided";
        }

        private async Task<List<InternalOrderItemDto>> MapItemsToDtos(List<Models.ChatBot.ItemDto> items)
        {
            Console.WriteLine("[Debug] Starting item name to ID mapping");

            if (items == null || !items.Any())
            {
                Console.WriteLine("[Debug] No items to map");
                return new List<InternalOrderItemDto>();
            }

            var mappedItems = new List<InternalOrderItemDto>();

            foreach (var item in items)
            {
                // local helper per item
                string BuildNotesFromExtras(List<string>? extras)
                {
                    if (extras is null || extras.Count == 0) return string.Empty;

                    var joined = string.Join("، ", extras
                        .Where(e => !string.IsNullOrWhiteSpace(e))
                        .Select(e => e.Trim()));

                    return string.IsNullOrWhiteSpace(joined) ? string.Empty : joined;
                }

                // If we already have an ID and it's valid, use it to get the menu item
                if (item.MenuItemId.HasValue && item.MenuItemId.Value > 0)
                {
                    var menuItem = await _db.MenuItems.FindAsync(item.MenuItemId.Value);
                    if (menuItem != null && menuItem.IsAvailable)
                    {
                        Console.WriteLine($"[Debug] Found item by ID: {menuItem.Id} - {menuItem.Name}");
                        mappedItems.Add(new InternalOrderItemDto
                        {
                            MenuItemId = menuItem.Id,
                            MenuItemName = menuItem.Name,
                            Size = item.Size ?? string.Empty,
                            Quantity = item.Quantity ?? 1,
                            MenuItemPrice = menuItem.Price,
                            Notes = BuildNotesFromExtras(item.Extras)   // ✅ extras captured
                        });
                    }
                    else
                    {
                        Console.WriteLine($"[Warning] Menu item with ID {item.MenuItemId.Value} not found or unavailable");
                    }
                    continue;
                }

                // Otherwise, look up by name
                var itemName = item.Name ?? "";
                var menuItemByName = await _db.MenuItems
                    .Where(m => m.IsAvailable && m.Name.ToLower() == itemName.ToLower())
                    .FirstOrDefaultAsync();

                if (menuItemByName != null)
                {
                    Console.WriteLine($"[Debug] Mapped item '{item.Name}' to ID: {menuItemByName.Id}");

                    mappedItems.Add(new InternalOrderItemDto
                    {
                        MenuItemId = menuItemByName.Id,
                        MenuItemName = menuItemByName.Name,
                        Size = item.Size ?? string.Empty,
                        Quantity = item.Quantity ?? 1,
                        MenuItemPrice = menuItemByName.Price,
                        Notes = BuildNotesFromExtras(item.Extras)   // ✅ extras captured
                    });
                }
                else
                {
                    Console.WriteLine($"[Warning] Failed to map item '{item.Name}' to a menu ID");
                }
            }

            Console.WriteLine($"[Debug] Mapped {mappedItems.Count} items");
            return mappedItems;
        }

        private List<ProjectE.Models.DTOs.OrderItemDto> ConvertToOrderItemDtos(List<InternalOrderItemDto> internalDtos)
        {
            return internalDtos.Select(item => new ProjectE.Models.DTOs.OrderItemDto
            {
                MenuItemId = item.MenuItemId,
                MenuItemName = item.MenuItemName,
                Quantity = item.Quantity,
                MenuItemPrice = item.MenuItemPrice,
                Size = item.Size,
                Notes = item.Notes,
                UnitPrice = item.MenuItemPrice,
                LineTotal = item.MenuItemPrice * item.Quantity
            }).ToList();
        }

        // Add this internal class to ChatbotResponseService.cs (private class)
        private class InternalOrderItemDto
        {
            public int MenuItemId { get; set; }
            public string MenuItemName { get; set; } = string.Empty;
            public string Size { get; set; } = string.Empty;
            public int Quantity { get; set; }
            public decimal MenuItemPrice { get; set; }
            public string Notes { get; set; } = string.Empty;
        }

        private string GetOrderKey(string conversationId, string? userId)
    => $"{conversationId}_{userId ?? "unknown"}";

        private List<OrderItemDto> GetCurrentOrder(string conversationId, string userId)
        {
            var key = GetOrderKey(conversationId, userId);
            return _conversationOrders.GetOrAdd(key, _ => new List<OrderItemDto>());
        }

        private void SaveCurrentOrder(string conversationId, string userId, List<OrderItemDto> items)
        {
            var key = GetOrderKey(conversationId, userId);
            if (items.Any())
            {
                _conversationOrders.AddOrUpdate(key, items, (k, v) => items);
            }
            else
            {
                _conversationOrders.TryRemove(key, out _);
            }
        }

        private void ClearCurrentOrder(string conversationId, string userId)
        {
            var key = GetOrderKey(conversationId, userId);
            _conversationOrders.TryRemove(key, out _);
        }


        // NEW HANDLER METHODS
        private async Task<string> HandleRemoveItem(BotPayloadDto botPayload, string userId, string conversationId)
        {
            PrintInMemoryOrderDebug(conversationId, userId, "BEFORE REMOVE_ITEM");
            if (string.IsNullOrWhiteSpace(botPayload.target_item_name))
                return "⚠️ لم يتم تحديد الصنف المراد حذفه.";

            try
            {
                var currentOrder = GetCurrentOrder(conversationId, userId);
                var itemsToRemove = currentOrder.Where(x =>
                    x.MenuItemName.ToLower().Contains(botPayload.target_item_name.ToLower())).ToList();

                if (!itemsToRemove.Any())
                {
                    PrintInMemoryOrderDebug(conversationId, userId, "AFTER REMOVE_ITEM (NOT FOUND)");
                    return $"⚠️ لم أجد صنف بالاسم '{botPayload.target_item_name}' في طلبك.";
                }
                foreach (var item in itemsToRemove)
                    currentOrder.Remove(item);

                SaveCurrentOrder(conversationId, userId, currentOrder);
                await SaveInMemoryOrderToDraft(conversationId, userId);
                PrintInMemoryOrderDebug(conversationId, userId, "AFTER REMOVE_ITEM");
                if (!currentOrder.Any())
                    return $"✅ تم حذف {botPayload.target_item_name} من طلبك.\n\nطلبك فارغ الآن. هل تريد إضافة أصناف أخرى؟";

                var currentTotal = currentOrder.Sum(x => x.Quantity * x.MenuItemPrice);
                var orderSummary = string.Join("\n", currentOrder.Select(x => $"{x.Quantity} × {x.MenuItemName}"));

                return $"✅ تم حذف {botPayload.target_item_name} من طلبك.\n\n📝 طلبك الحالي:\n{orderSummary}\n\n💰 المجموع: {currentTotal:F2} شيكل";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Error] HandleRemoveItem: {ex.Message}");
                PrintInMemoryOrderDebug(conversationId, userId, "AFTER REMOVE_ITEM (ERROR)");
                return "⚠️ حدثت مشكلة أثناء حذف الصنف.";
            }
        }


        private async Task<string> HandleReplaceItem(BotPayloadDto botPayload, string userId, string conversationId)
        {
            PrintInMemoryOrderDebug(conversationId, userId, "BEFORE REPLACE_ITEM");
            if (string.IsNullOrWhiteSpace(botPayload.target_item_name) || botPayload.replacement_item == null)
                return "⚠️ لم يتم تحديد الصنف المراد استبداله أو البديل.";

            try
            {
                var currentOrder = GetCurrentOrder(conversationId, userId);
                var itemToReplace = currentOrder.FirstOrDefault(x =>
                    x.MenuItemName.ToLower().Contains(botPayload.target_item_name.ToLower()));

                if (itemToReplace == null)
                {
                    PrintInMemoryOrderDebug(conversationId, userId, "AFTER REPLACE_ITEM (NOT FOUND)");
                    return $"⚠️ لم أجد صنف بالاسم '{botPayload.target_item_name}' في طلبك.";
                }
                var newMenuItem = await _db.MenuItems
                    .FirstOrDefaultAsync(m => m.IsAvailable && m.Name.ToLower() == botPayload.replacement_item.Name.ToLower());

                if (newMenuItem == null)
                {
                    PrintInMemoryOrderDebug(conversationId, userId, "AFTER REPLACE_ITEM (NEW ITEM NOT FOUND)");
                    return $"⚠️ الصنف '{botPayload.replacement_item.Name}' غير متوفر.";
                }
                // Replace the item
                currentOrder.Remove(itemToReplace);
                currentOrder.Add(new OrderItemDto
                {
                    MenuItemId = newMenuItem.Id,
                    MenuItemName = newMenuItem.Name,
                    Quantity = botPayload.replacement_item.Quantity ?? itemToReplace.Quantity,
                    MenuItemPrice = newMenuItem.Price,
                    Size = botPayload.replacement_item.Size,
                    Notes = botPayload.replacement_item.Extras != null && botPayload.replacement_item.Extras.Any()
                     ? string.Join(", ", botPayload.replacement_item.Extras)
                        : string.Empty
                });

                SaveCurrentOrder(conversationId, userId, currentOrder);
                await SaveInMemoryOrderToDraft(conversationId, userId);
                PrintInMemoryOrderDebug(conversationId, userId, "AFTER REPLACE_ITEM");
                var currentTotal = currentOrder.Sum(x => x.Quantity * x.MenuItemPrice);

                return $"✅ تم استبدال {botPayload.target_item_name} بـ {newMenuItem.Name}\n\n💰 المجموع الحالي: {currentTotal:F2} شيكل";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Error] HandleReplaceItem: {ex.Message}");
                PrintInMemoryOrderDebug(conversationId, userId, "AFTER REPLACE_ITEM (ERROR)");
                return "⚠️ حدثت مشكلة أثناء استبدال الصنف.";
            }
        }


        private async Task<string> HandleUpdateQuantity(BotPayloadDto botPayload, string userId, string conversationId)
        {
            PrintInMemoryOrderDebug(conversationId, userId, "BEFORE UPDATE_QUANTITY");
            if (string.IsNullOrWhiteSpace(botPayload.target_item_name) || !botPayload.new_quantity.HasValue)
                return "⚠️ لم يتم تحديد الصنف أو الكمية الجديدة.";

            try
            {
                var currentOrder = GetCurrentOrder(conversationId, userId);
                var itemToUpdate = currentOrder.FirstOrDefault(x =>
                    x.MenuItemName.ToLower().Contains(botPayload.target_item_name.ToLower()));

                if (itemToUpdate == null)
                {
                    PrintInMemoryOrderDebug(conversationId, userId, "AFTER UPDATE_QUANTITY (NOT FOUND)");
                    return $"⚠️ لم أجد صنف بالاسم '{botPayload.target_item_name}' في طلبك.";
                }


                if (botPayload.new_quantity.Value <= 0)
                {
                    currentOrder.Remove(itemToUpdate);
                    SaveCurrentOrder(conversationId, userId, currentOrder);
                    PrintInMemoryOrderDebug(conversationId, userId, "AFTER UPDATE_QUANTITY (REMOVED)");
                    return await HandleRemoveItem(new BotPayloadDto { target_item_name = botPayload.target_item_name }, userId, conversationId);
                }

                itemToUpdate.Quantity = botPayload.new_quantity.Value;
                SaveCurrentOrder(conversationId, userId, currentOrder);
                await SaveInMemoryOrderToDraft(conversationId, userId);
                PrintInMemoryOrderDebug(conversationId, userId, "AFTER UPDATE_QUANTITY");
                var currentTotal = currentOrder.Sum(x => x.Quantity * x.MenuItemPrice);
                return $"✅ تم تحديث كمية {botPayload.target_item_name} إلى {botPayload.new_quantity.Value}\n\n💰 المجموع الحالي: {currentTotal:F2} شيكل";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Error] HandleUpdateQuantity: {ex.Message}");
                PrintInMemoryOrderDebug(conversationId, userId, "AFTER UPDATE_QUANTITY (ERROR)");
                return "⚠️ حدثت مشكلة أثناء تحديث الكمية.";
            }
        }

        // **NEW METHOD: Add items to in-memory storage only**
        private async Task<string> HandleAddToInMemoryOrder(BotPayloadDto botPayload, string userId, string conversationId)
        {
            PrintInMemoryOrderDebug(conversationId, userId, "BEFORE ADD_TO_ORDER");
            if (botPayload.Items == null || !botPayload.Items.Any())
                return "⚠️ لم يتم تحديد أصناف للإضافة.";

            try
            {
                // Get current in-memory order
                var currentOrder = GetCurrentOrder(conversationId, userId);

                // Map items to internal DTOs with IDs and other data
                var internalItems = await MapItemsToDtos(botPayload.Items);

                if (!internalItems.Any())
                {
                    Console.WriteLine("[Warning] No valid items could be mapped");
                    PrintInMemoryOrderDebug(conversationId, userId, "AFTER ADD_TO_ORDER (NO VALID ITEMS)");
                    return "⚠️ عذراً، لم أتمكن من العثور على الأصناف المطلوبة. هل يمكنك اختيار صنف آخر من القائمة؟";
                }

                // Add each new item to the in-memory order
                foreach (var internalItem in internalItems)
                {
                    // Check if item already exists in the order
                    var existingItem = currentOrder.FirstOrDefault(x =>
                        x.MenuItemId == internalItem.MenuItemId &&
                        x.Size == internalItem.Size &&
                        x.Notes == internalItem.Notes);


                    if (existingItem != null)
                    {
                        // Update quantity of existing item
                        var oldQuantity = existingItem.Quantity;
                        existingItem.Quantity += internalItem.Quantity;
                        Console.WriteLine($"[Debug] UPDATED existing item: {existingItem.MenuItemName} - Quantity changed from {oldQuantity} to {existingItem.Quantity}");
                    }
                    else
                    {
                        // Add new item to order
                        currentOrder.Add(new OrderItemDto
                        {
                            MenuItemId = internalItem.MenuItemId,
                            MenuItemName = internalItem.MenuItemName,
                            Quantity = internalItem.Quantity,
                            MenuItemPrice = internalItem.MenuItemPrice,
                            Size = internalItem.Size,
                            UnitPrice = internalItem.MenuItemPrice, // Add this
                            LineTotal = internalItem.MenuItemPrice * internalItem.Quantity, // Add this
                            Notes = internalItem.Notes
                        });
                        Console.WriteLine($"[Debug] Added new item to in-memory order: {internalItem.MenuItemName}");
                    }
                }

                // Save updated order to in-memory storage
                SaveCurrentOrder(conversationId, userId, currentOrder);
                await SaveInMemoryOrderToDraft(conversationId, userId);
                PrintInMemoryOrderDebug(conversationId, userId, "AFTER ADD_TO_ORDER");
                // Generate summary for response
                var summary = string.Join("\n", botPayload.Items.Select(i =>
                {
                    var qty = i.Quantity ?? 0;
                    var size = string.IsNullOrWhiteSpace(i.Size) ? "" : $" ({i.Size})";
                    var extrasPart = i.Extras?.Any() == true ? $" — إضافات: {string.Join("، ", i.Extras)}" : "";
                    return $"{(qty > 0 ? $"{qty} × " : "")}{i.Name}{size}{extrasPart}";
                }));

                // Calculate current total
                var currentTotal = currentOrder.Sum(x => x.Quantity * x.MenuItemPrice);

                return $"🍽️ أضفت الأصناف التالية لطلبك:\n{summary}\n\n💰 المجموع الحالي: {currentTotal:F2} شيكل\n\nهل تريد إضافة شيء آخر أم أؤكد الطلب؟";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Error] HandleAddToInMemoryOrder: {ex.Message}");
                PrintInMemoryOrderDebug(conversationId, userId, "AFTER ADD_TO_ORDER (ERROR)");
                return "⚠️ عذراً، حدثت مشكلة أثناء إضافة الصنف. هل يمكنك إعادة المحاولة؟";
            }
        }

        // **NEW METHOD: Handle submit order - convert in-memory to database order**
        private async Task<string> HandleSubmitOrder(BotPayloadDto botPayload, string? userId, string conversationId)
        {
            Console.WriteLine("[Debug] HandleSubmitOrder called");
            PrintInMemoryOrderDebug(conversationId, userId, "BEFORE SUBMIT");

            if (string.IsNullOrEmpty(userId))
                return "⚠️ خطأ في تحديد المستخدم. لا يمكن إرسال الطلب.";

            try
            {
                // Get current in-memory order
                var currentOrder = GetCurrentOrder(conversationId, userId);

                if (!currentOrder.Any())
                {
                    PrintInMemoryOrderDebug(conversationId, userId, "SUBMIT - NO ITEMS");
                    return "⚠️ طلبك فارغ. يرجى إضافة أصناف قبل الإرسال.";
                }

                // Validate delivery address
                if (botPayload.DeliveryAddress == null || string.IsNullOrWhiteSpace(botPayload.DeliveryAddress.Address))
                {
                    return "⚠️ من فضلك، أحتاج إلى عنوان التوصيل أولاً لتأكيد الطلب. لوين بتحب نوصل الطلب؟";
                }

                var deliveryAddress = botPayload.DeliveryAddress.Address;

                // Validate delivery zone before creating order
                try
                {
                    var deliveryFee = _orderService.GetDeliveryFee(deliveryAddress);
                    Console.WriteLine($"[Debug] Delivery fee calculated: {deliveryFee} for address: {deliveryAddress}");
                }
                catch (InvalidOperationException ex)
                {
                    // Address is outside delivery zones
                    return $"⚠️ {ex.Message}\n\nهل تريد تقديم عنوان آخر ضمن المناطق المدعومة (روابي، بيرزيت، رام الله) أم تفضل إلغاء الطلب؟";
                }

                // Get user information from database
                var user = await _db.Users.FirstOrDefaultAsync(u => u.UserId == userId);
                if (user == null)
                {
                    Console.WriteLine($"[Error] User {userId} not found in database");
                    return "⚠️ خطأ في تحديد المستخدم. يرجى تسجيل الدخول مرة أخرى.";
                }

                //Find and convert existing draft order instead of removing it
                var existingDraft = await _db.Orders
                    .Include(o => o.Items)
                    .ThenInclude(i => i.MenuItem)
                    .Where(o => o.UserId == user.Id && o.Status == "Draft")
                    .FirstOrDefaultAsync();

                OrderResponse createdOrder;

                if (existingDraft != null && existingDraft.Items.Any())
                {
                    //Convert existing draft to a real order
                    Console.WriteLine($"[Debug] Converting existing draft order {existingDraft.Id} to real order");

                    // Update draft order to become a real order
                    existingDraft.Status = "Pending"; // Change from Draft to Pending
                    existingDraft.DeliveryAddress = deliveryAddress;
                    existingDraft.CustomerName = !string.IsNullOrWhiteSpace(botPayload.CustomerName)
                        ? botPayload.CustomerName : user.UserName;
                    existingDraft.PhoneNumber = !string.IsNullOrWhiteSpace(botPayload.PhoneNumber)
                        ? botPayload.PhoneNumber : user.PhoneNumber;
                    existingDraft.Notes = "طلب من الشات بوت";
                    existingDraft.CreatedAt = DateTime.UtcNow; // Update timestamp for order submission

                    await _db.SaveChangesAsync();

                    // Map the converted order to response
                    createdOrder = MapDraftToOrderResponse(existingDraft);
                    Console.WriteLine($"[Debug] Converted draft order {existingDraft.Id} to real order successfully");
                }
                else
                {
                    // ✅ FALLBACK: No draft exists, create order normally through OrderService
                    Console.WriteLine("[Debug] No draft order found, creating new order through OrderService");

                    // Remove any empty draft orders
                    if (existingDraft != null)
                    {
                        _db.Orders.Remove(existingDraft);
                        await _db.SaveChangesAsync();
                    }

                    var orderRequest = new ChatOrderRequest
                    {
                        CustomerName = !string.IsNullOrWhiteSpace(botPayload.CustomerName)
                            ? botPayload.CustomerName : user.UserName,
                        PhoneNumber = !string.IsNullOrWhiteSpace(botPayload.PhoneNumber)
                            ? botPayload.PhoneNumber : user.PhoneNumber,
                        DeliveryAddress = deliveryAddress,
                        Notes = "طلب من الشات بوت",
                        Items = currentOrder.Select(item => new OrderItemRequest
                        {
                            MenuItemId = item.MenuItemId ?? 0,
                            Name = item.MenuItemName,
                            Quantity = item.Quantity,
                            Notes = !string.IsNullOrEmpty(item.Notes) ? item.Notes
                                : (!string.IsNullOrEmpty(item.Size) ? $"Size: {item.Size}" : string.Empty)
                        }).ToList()
                    };

                    createdOrder = await _orderService.CreateOrder(userId, orderRequest);
                }

                Console.WriteLine($"[Debug] Order created/converted successfully with ID: {createdOrder.OrderId}");

                // Clear the in-memory order after successful submission
                ClearCurrentOrder(conversationId, userId);
                PrintInMemoryOrderDebug(conversationId, userId, "AFTER SUBMIT - CLEARED");

                // Calculate order summary for response
                var orderSummary = string.Join("\n", currentOrder.Select(x => $"{x.Quantity} × {x.MenuItemName}"));

                return $"✅ تم إرسال طلبك بنجاح!\n\n" +
                       $"🧾 رقم الطلب: #{createdOrder.OrderId}\n" +
                       $"📝 تفاصيل الطلب:\n{orderSummary}\n\n" +
                       $"💰 المجموع الفرعي: {createdOrder.Subtotal:F2} شيكل\n" +
                       $"🚚 رسوم التوصيل: {createdOrder.DeliveryFee:F2} شيكل\n" +
                       $"💵 **المجموع الإجمالي: {createdOrder.TotalPrice:F2} شيكل**\n\n" +
                       $"📍 العنوان: {createdOrder.DeliveryAddress}\n" +
                       $"📞 الهاتف: {createdOrder.PhoneNumber}\n\n" +
                       $"⏱️ سيتم تحضير طلبك وتوصيله في أقرب وقت. شكراً لك!";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Error] HandleSubmitOrder failed: {ex.Message}");
                Console.WriteLine($"[Error] Stack trace: {ex.StackTrace}");
                PrintInMemoryOrderDebug(conversationId, userId, "SUBMIT - ERROR");

                return "⚠️ عذراً، حدثت مشكلة أثناء إرسال طلبك. يرجى المحاولة مرة أخرى أو التواصل مع خدمة العملاء.";
            }
        }

        /// <summary>
        /// Maps a converted draft order to OrderResponse
        /// </summary>
        private OrderResponse MapDraftToOrderResponse(OrderEntity order)
        {
            var subtotal = order.Items.Sum(i => i.Quantity * i.MenuItem.Price);
            var deliveryFee = _orderService.GetDeliveryFee(order.DeliveryAddress);
            var totalPrice = subtotal + deliveryFee;

            return new OrderResponse
            {
                OrderId = order.Id,
                CustomerName = order.CustomerName,
                PhoneNumber = order.PhoneNumber,
                DeliveryAddress = order.DeliveryAddress,
                Notes = order.Notes,
                Status = Enum.TryParse<OrderStatus>(order.Status, out var status) ? status
                    : throw new InvalidOperationException($"Invalid status value: {order.Status}"),
                CreatedAt = order.CreatedAt,
                Items = order.Items.Select(i => new OrderItemResponse
                {
                    MenuItemId = i.MenuItemId,
                    MenuItemName = i.MenuItem.Name,
                    Quantity = i.Quantity,
                    Notes = i.Notes,
                    Price = i.MenuItem.Price
                }).ToList(),
                Subtotal = subtotal,
                DeliveryFee = deliveryFee,
                TotalPrice = totalPrice
            };
        }

        private async Task<string> HandleCancelOrder(BotPayloadDto botPayload, string userId, string conversationId)
        {
            Console.WriteLine("[Debug] HandleCancelOrder called");
            PrintInMemoryOrderDebug(conversationId, userId, "BEFORE CANCEL_ORDER");

            if (string.IsNullOrEmpty(userId))
            {
                return "⚠️ يجب أن تكون مسجلاً للدخول لإلغاء الطلب.";
            }

            try
            {
                // Get current in-memory order
                var currentOrder = GetCurrentOrder(conversationId, userId);

                // Get user from database
                var user = await _db.Users.FirstOrDefaultAsync(u => u.UserId == userId);
                if (user == null)
                {
                    Console.WriteLine($"[Error] User {userId} not found in database");
                    return "⚠️ خطأ في تحديد المستخدم.";
                }

                bool foundDraftOrder = false;
                bool foundRecentOrder = false;
                decimal totalBeforeClear = 0;
                int itemCount = 0;
                string orderDetails = "";

                // 🔥 SECTION 1: Handle DRAFT orders (NO TIME RESTRICTION)
                // Find existing draft order in database - ALWAYS cancellable
                var existingDraft = await _db.Orders
                    .Include(o => o.Items)
                    .Where(o => o.UserId == user.Id && o.Status == "Draft")
                    .FirstOrDefaultAsync();

                if (existingDraft != null)
                {
                    Console.WriteLine($"🔍 [DRAFT ORDER] Found draft order #{existingDraft.Id} - ALWAYS cancellable (no time restriction)");

                    // Mark existing draft order as "Cancelled"
                    existingDraft.Status = "Cancelled";
                    existingDraft.Notes = string.IsNullOrEmpty(existingDraft.Notes)
                        ? "Draft order cancelled by user via chatbot"
                        : $"{existingDraft.Notes} - Draft order cancelled by user via chatbot";

                    Console.WriteLine($"[Debug] Marked draft order {existingDraft.Id} as Cancelled");
                    await _db.SaveChangesAsync();
                    foundDraftOrder = true;
                }

                // Handle in-memory order cancellation (always related to draft)
                if (currentOrder.Any())
                {
                    foundDraftOrder = true;
                    totalBeforeClear = currentOrder.Sum(x => x.Quantity * x.MenuItemPrice);
                    itemCount = currentOrder.Count;

                    Console.WriteLine($"🔍 [IN-MEMORY ORDER] Found in-memory order with {itemCount} items, total: {totalBeforeClear:F2} شيكل");

                    // Clear the in-memory order
                    ClearCurrentOrder(conversationId, userId);
                }

                // 🔥 SECTION 2: Handle PENDING orders (WITH TIME RESTRICTION)
                // Check for recent pending order (within last 3 minutes) - TIME RESTRICTED
                var cutoffTime = DateTime.UtcNow.AddMinutes(-3);
                Console.WriteLine($"🕒 [TIME DEBUG] Current UTC time: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
                Console.WriteLine($"🕒 [TIME DEBUG] Cutoff time (3 min ago): {cutoffTime:yyyy-MM-dd HH:mm:ss}");

                // Get ALL pending orders for this user to show the debugging info
                var allPendingOrders = await _db.Orders
                    .Include(o => o.Items)
                    .ThenInclude(i => i.MenuItem)
                    .Where(o => o.UserId == user.Id && o.Status == "Pending")
                    .OrderByDescending(o => o.CreatedAt)
                    .ToListAsync();

                Console.WriteLine($"🔍 [PENDING ORDER DEBUG] Found {allPendingOrders.Count} pending orders for user {userId}");

                // Debug each pending order
                foreach (var order in allPendingOrders)
                {
                    var timeSinceOrder = DateTime.UtcNow - order.CreatedAt;
                    var isWithinWindow = order.CreatedAt >= cutoffTime;

                    Console.WriteLine($"🔍 [PENDING ORDER DEBUG] Order #{order.Id}:");
                    Console.WriteLine($"   📅 Created: {order.CreatedAt:yyyy-MM-dd HH:mm:ss} UTC");
                    Console.WriteLine($"   ⏱️  Age: {timeSinceOrder.TotalMinutes:F2} minutes");
                    Console.WriteLine($"   ✅ Within 3-min window: {isWithinWindow}");

                    if (!isWithinWindow)
                    {
                        Console.WriteLine($"   ❌ [TOO OLD] Pending order #{order.Id} is {timeSinceOrder.TotalMinutes:F2} minutes old - CANNOT be cancelled (exceeds 3-minute limit)");
                    }
                    else
                    {
                        Console.WriteLine($"   ✅ [CAN CANCEL] Pending order #{order.Id} is {timeSinceOrder.TotalMinutes:F2} minutes old - within cancellation window");
                    }
                }

                // Get the most recent pending order within the window
                var recentPendingOrder = allPendingOrders
                    .Where(o => o.CreatedAt >= cutoffTime)
                    .FirstOrDefault();

                if (recentPendingOrder != null)
                {
                    foundRecentOrder = true;
                    var timeSinceOrder = DateTime.UtcNow - recentPendingOrder.CreatedAt;
                    Console.WriteLine($"✅ [CANCELLABLE PENDING ORDER] Found cancellable pending order #{recentPendingOrder.Id} (age: {timeSinceOrder.TotalMinutes:F2} minutes)");

                    Console.WriteLine($"🔄 [CANCELLING PENDING ORDER] Processing cancellation for order #{recentPendingOrder.Id}");

                    // Mark the recent pending order as cancelled
                    recentPendingOrder.Status = "Cancelled";
                    recentPendingOrder.Notes = string.IsNullOrEmpty(recentPendingOrder.Notes)
                        ? $"Order cancelled by user via chatbot after {timeSinceOrder.TotalMinutes:F1} minutes"
                        : $"{recentPendingOrder.Notes} - Order cancelled by user via chatbot after {timeSinceOrder.TotalMinutes:F1} minutes";

                    await _db.SaveChangesAsync();
                    Console.WriteLine($"✅ [SUCCESS] Cancelled recent pending order {recentPendingOrder.Id}");

                    // Calculate order details for response
                    var orderSubtotal = recentPendingOrder.Items.Sum(i => i.Quantity * i.MenuItem.Price);
                    var orderItemCount = recentPendingOrder.Items.Count;
                    orderDetails = $"طلب رقم #{recentPendingOrder.Id} ({orderItemCount} صنف بقيمة {orderSubtotal:F2} شيكل)";
                }
                else if (allPendingOrders.Any())
                {
                    var oldestOrder = allPendingOrders.Last();
                    var oldestAge = DateTime.UtcNow - oldestOrder.CreatedAt;
                    Console.WriteLine($"❌ [NO CANCELLABLE PENDING ORDERS] All pending orders are too old. Oldest: #{oldestOrder.Id} (age: {oldestAge.TotalMinutes:F2} minutes)");
                }
                else
                {
                    Console.WriteLine($"ℹ️ [NO PENDING ORDERS] No pending orders found for user {userId}");
                }

                PrintInMemoryOrderDebug(conversationId, userId, "AFTER CANCEL_ORDER - CLEARED");

                // Generate appropriate response based on what was cancelled
                if (foundDraftOrder && foundRecentOrder)
                {
                    return $"❌ تم إلغاء طلبك الحالي ({itemCount} صنف بقيمة {totalBeforeClear:F2} شيكل).\n\n" +
                           $"❌ تم أيضاً إلغاء {orderDetails} المُرسل مؤخراً.\n\n" +
                           $"✅ تم حفظ الطلبات المُلغاة في سجلاتك.\n\n" +
                           $"🆕 يمكنك الآن البدء بطلب جديد. شو بتحب تطلب؟";
                }
                else if (foundDraftOrder)
                {
                    return $"❌ تم إلغاء طلبك الحالي ({itemCount} صنف بقيمة {totalBeforeClear:F2} شيكل).\n\n" +
                           $"✅ تم حفظ الطلب المُلغى في سجلاتك.\n\n" +
                           $"🆕 يمكنك الآن البدء بطلب جديد. شو بتحب تطلب؟";
                }
                else if (foundRecentOrder)
                {
                    return $"❌ تم إلغاء {orderDetails} المُرسل مؤخراً.\n\n" +
                           $"✅ تم حفظ الطلب المُلغى في سجلاتك.\n\n" +
                           $"🆕 يمكنك البدء بطلب جديد. شو بتحب تطلب؟";
                }
                else
                {
                    return "⚠️ لا يوجد طلب حالي أو طلب مؤخر للإلغاء.\n\n" +
                           "💡 يمكنك إلغاء الطلبات المُرسلة خلال 3 دقائق من إرسالها فقط.\n\n" +
                           "🆕 إذا بتحب تبدأ طلب جديد، خبرني شو بتحب تطلب.";
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Error] HandleCancelOrder failed: {ex.Message}");
                PrintInMemoryOrderDebug(conversationId, userId, "CANCEL_ORDER - ERROR");
                return "⚠️ حدثت مشكلة أثناء إلغاء الطلب. يرجى المحاولة مرة أخرى.";
            }
        }

        /// <summary>
        /// Load draft order from database into in-memory storage when user starts chatting
        /// </summary>
        private async Task LoadDraftOrderIntoMemory(string conversationId, string userId)
        {
            if (string.IsNullOrEmpty(userId))
            {
                Console.WriteLine("[Debug] No userId provided, skipping draft order loading");
                return;
            }

            try
            {
                // Get user from database
                var user = await _db.Users.FirstOrDefaultAsync(u => u.UserId == userId);
                if (user == null)
                {
                    Console.WriteLine($"[Warning] User {userId} not found in database");
                    return;
                }

                // Find existing draft order
                var draftOrder = await _db.Orders
                    .Include(o => o.Items)
                    .ThenInclude(i => i.MenuItem)
                    .Where(o => o.UserId == user.Id && o.Status == "Draft")
                    .OrderByDescending(o => o.CreatedAt)
                    .FirstOrDefaultAsync();

                if (draftOrder == null)
                {
                    Console.WriteLine($"[Debug] No draft order found for user {userId}");
                    return;
                }

                Console.WriteLine($"[Debug] Found draft order {draftOrder.Id} with {draftOrder.Items.Count} items for user {userId}");

                // Convert database items to in-memory DTOs
                var orderItems = draftOrder.Items.Select(item => new OrderItemDto
                {
                    MenuItemId = item.MenuItemId,
                    MenuItemName = item.MenuItem.Name,
                    Quantity = item.Quantity,
                    MenuItemPrice = item.MenuItem.Price,
                    Size = string.Empty, // Extend this if you store size info
                    Notes = item.Notes ?? string.Empty,
                    UnitPrice = item.MenuItem.Price,
                    LineTotal = item.MenuItem.Price * item.Quantity
                }).ToList();

                // Load into in-memory storage
                var key = GetOrderKey(conversationId, userId);
                _conversationOrders.AddOrUpdate(key, orderItems, (k, v) => orderItems);

                Console.WriteLine($"[Success] Loaded {orderItems.Count} items from draft order into memory for conversation {conversationId}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Error] Failed to load draft order from database: {ex.Message}");
            }
        }

        /// <summary>
        /// Save current in-memory order to database as draft order
        /// </summary>
        private async Task SaveInMemoryOrderToDraft(string conversationId, string userId)
        {
            if (string.IsNullOrEmpty(userId))
            {
                Console.WriteLine("[Debug] No userId provided, skipping draft order saving");
                return;
            }

            try
            {
                // Get current in-memory order
                var currentOrder = GetCurrentOrder(conversationId, userId);

                // Get user from database
                var user = await _db.Users.FirstOrDefaultAsync(u => u.UserId == userId);
                if (user == null)
                {
                    Console.WriteLine($"[Warning] User {userId} not found in database");
                    return;
                }

                // Find all draft orders for this user
                var existingDraft = await _db.Orders
                    .Include(o => o.Items)
                    .Where(o => o.UserId == user.Id && o.Status == "Draft")
                    .FirstOrDefaultAsync();

                if (!currentOrder.Any())
                {
                    // No items in memory - remove existing draft if any
                    if (existingDraft != null)
                    {
                        _db.Orders.Remove(existingDraft);
                        await _db.SaveChangesAsync();
                        Console.WriteLine($"[Debug] Removed empty draft order for user {userId}");
                    }
                    return;
                }
                if(existingDraft == null)
                {
                    // CREATE: Always create a fresh draft order
                    var newDraft = new OrderEntity
                    {
                        UserId = user.Id,
                        CustomerName = user.UserName,
                        PhoneNumber = user.PhoneNumber ?? "Not Provided",
                        DeliveryAddress = "Draft Order - Not Submitted",
                        Status = "Draft", // ✅ Use Draft status
                        Notes = $"Draft order from conversation {conversationId} - {DateTime.UtcNow:yyyy-MM-dd HH:mm}",
                        CreatedAt = DateTime.UtcNow,
                        Items = new List<OrderItemEntity>()
                    };
                    _db.Orders.Add(newDraft);
                    await _db.SaveChangesAsync(); // Save to get ID for foreign key
                    existingDraft = newDraft;
                    Console.WriteLine($"[Debug] Created new draft order {existingDraft.Id} for user {userId}");
                }
                else
                {
                    // UPDATE: Existing draft found, update timestamp
                    existingDraft.CreatedAt = DateTime.UtcNow;
                    existingDraft.Notes = $"Updated draft order from conversation {conversationId} - {DateTime.UtcNow:yyyy-MM-dd HH:mm}";
                    Console.WriteLine($"[Debug] Updating existing draft order {existingDraft.Id} for user {userId}");
                }

                // SMART MERGE: Compare in-memory items with existing database items
                await MergeDraftOrderItems(existingDraft, currentOrder);

                await _db.SaveChangesAsync();
                Console.WriteLine($"[Success] Updated draft order {existingDraft.Id} with {currentOrder.Count} items for user {userId}");


            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Error] Failed to save in-memory order to draft: {ex.Message}");
            }
        }

        /// <summary>
        /// Intelligently merges in-memory order items with existing draft order items to prevent duplicates
        /// </summary>
        private async Task MergeDraftOrderItems(OrderEntity draftOrder, List<OrderItemDto> inMemoryItems)
        {
            Console.WriteLine($"[Debug] Merging {inMemoryItems.Count} in-memory items with {draftOrder.Items.Count} existing draft items");

            // Create a dictionary of existing items for fast lookup
            // Key: MenuItemId + Notes (to handle same item with different customizations)
            var existingItemsDict = draftOrder.Items
                .ToDictionary(
                    item => GetItemKey(item.MenuItemId, item.Notes ?? ""),
                    item => item
                );

            // Process each in-memory item
            foreach (var memoryItem in inMemoryItems)
            {
                if (!memoryItem.MenuItemId.HasValue || memoryItem.MenuItemId.Value <= 0)
                    continue;

                var itemKey = GetItemKey(memoryItem.MenuItemId.Value, memoryItem.Notes ?? "");

                if (existingItemsDict.TryGetValue(itemKey, out var existingItem))
                {
                    // ✅ UPDATE: Item already exists, update quantity
                    var oldQuantity = existingItem.Quantity;
                    existingItem.Quantity = memoryItem.Quantity; // Use the latest quantity from memory
                    existingItem.Notes = memoryItem.Notes ?? ""; // Update notes in case they changed

                    Console.WriteLine($"[Debug] Updated existing item: MenuId {memoryItem.MenuItemId} quantity from {oldQuantity} to {memoryItem.Quantity}");
                }
                else
                {
                    // ✅ ADD: New item, add to draft order
                    var newOrderItem = new OrderItemEntity
                    {
                        OrderId = draftOrder.Id,
                        MenuItemId = memoryItem.MenuItemId.Value,
                        Quantity = memoryItem.Quantity,
                        Notes = memoryItem.Notes ?? ""
                    };

                    draftOrder.Items.Add(newOrderItem);
                    Console.WriteLine($"[Debug] Added new item to draft: MenuId {memoryItem.MenuItemId} x{memoryItem.Quantity}");
                }
            }

            // ✅ REMOVE: Items that exist in database but not in memory (user removed them)
            var memoryItemKeys = inMemoryItems
                .Where(item => item.MenuItemId.HasValue)
                .Select(item => GetItemKey(item.MenuItemId!.Value, item.Notes ?? ""))
                .ToHashSet();

            var itemsToRemove = draftOrder.Items
                .Where(dbItem => !memoryItemKeys.Contains(GetItemKey(dbItem.MenuItemId, dbItem.Notes ?? "")))
                .ToList();

            foreach (var itemToRemove in itemsToRemove)
            {
                Console.WriteLine($"[Debug] Removing item from draft: MenuId {itemToRemove.MenuItemId} (not in memory)");
                draftOrder.Items.Remove(itemToRemove);
                _db.OrderItems.Remove(itemToRemove); // Explicitly remove from context
            }

            Console.WriteLine($"[Debug] Merge complete: {draftOrder.Items.Count} items in final draft order");
        }

        /// <summary>
        /// Creates a unique key for an order item based on MenuItemId and Notes
        /// This allows the same menu item with different customizations to be treated as separate items
        /// </summary>
        /// <summary>
        /// Creates a unique key for an order item based on MenuItemId and Notes
        /// This allows the same menu item with different customizations to be treated as separate items
        /// </summary>
        private string GetItemKey(int menuItemId, string notes)
        {
            // ✅ ROBUST: Multi-layer approach for Arabic text handling
            string normalizedNotes;

            if (string.IsNullOrWhiteSpace(notes))
            {
                normalizedNotes = "";
            }
            else
            {
                try
                {
                    // Try multiple normalization approaches for Arabic text
                    normalizedNotes = notes.Trim();

                    // Only apply case conversion if the result is not null
                    if (!string.IsNullOrEmpty(normalizedNotes))
                    {
                        try
                        {
                            normalizedNotes = normalizedNotes.ToLowerInvariant();
                        }
                        catch (Exception)
                        {
                            // If ToLowerInvariant fails, use the trimmed version as-is
                            normalizedNotes = notes.Trim();
                        }
                    }

                    // Final null check
                    normalizedNotes = normalizedNotes ?? "";
                }
                catch (Exception)
                {
                    // Complete fallback - use original string as-is
                    normalizedNotes = notes ?? "";
                }
            }

            return $"{menuItemId}|{normalizedNotes}";
        }

        // Add this new handler method:
        private async Task<string> HandleShowSummary(BotPayloadDto botPayload, string? userId, string conversationId)
        {
            Console.WriteLine("[Debug] HandleShowSummary called");
            PrintInMemoryOrderDebug(conversationId, userId, "SHOW_SUMMARY");

            if (string.IsNullOrEmpty(userId))
            {
                return "⚠️ يجب أن تكون مسجلاً للدخول لعرض ملخص طلبك.";
            }

            try
            {
                // Get current in-memory order
                var currentOrder = GetCurrentOrder(conversationId, userId);

                if (!currentOrder.Any())
                {
                    return "📋 طلبك فارغ حالياً.\n\nهل تريد إضافة أصناف من القائمة؟";
                }

                // Build order summary
                var responseBuilder = new System.Text.StringBuilder();
                responseBuilder.AppendLine("📋 **ملخص طلبك الحالي:**\n");

                decimal subtotal = 0;
                int itemIndex = 1;

                foreach (var item in currentOrder)
                {
                    var itemTotal = item.Quantity * item.MenuItemPrice;
                    subtotal += itemTotal;

                    var itemLine = $"{itemIndex}. {item.Quantity} × {item.MenuItemName}";

                    // Add size if available
                    if (!string.IsNullOrEmpty(item.Size))
                    {
                        itemLine += $" ({item.Size})";
                    }

                    // Add notes/customizations if available
                    if (!string.IsNullOrEmpty(item.Notes))
                    {
                        itemLine += $" — {item.Notes}";
                    }

                    itemLine += $" = {itemTotal:F2} شيكل";
                    responseBuilder.AppendLine(itemLine);
                    itemIndex++;
                }

                responseBuilder.AppendLine($"\n💰 **المجموع الفرعي: {subtotal:F2} شيكل**");
                responseBuilder.AppendLine("\n📝 هل تريد تعديل الطلب أم جاهز للإرسال؟");

                return responseBuilder.ToString();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Error] HandleShowSummary failed: {ex.Message}");
                return "⚠️ حدثت مشكلة أثناء عرض ملخص الطلب. يرجى المحاولة مرة أخرى.";
            }
        }

        /// <summary>
        /// Gets the default welcome message with the user's name if available
        /// </summary>
        /// <param name="userId">The current user's ID</param>
        /// <returns>A personalized welcome message</returns>
        private async Task<string> GetDefaultWelcomeMessage(string? userId)
        {
            try
            {
                // Get user name from database
                var user = await _db.Users.FirstOrDefaultAsync(u => u.UserId == userId);

                if (user != null && !string.IsNullOrWhiteSpace(user.UserName) &&
                    user.UserName != "Guest User" && user.UserName != "Unknown User")
                {
                    return $"مرحباً {user.UserName}! أنا مساعد YallaEat 😊\nكيف أقدر أساعدك اليوم؟ (قائمة الطعام، الأسعار، التوصيل، الحجوزات…)";
                }
                else
                {
                    return "مرحباً! أنا مساعد YallaEat 😊\nكيف أقدر أساعدك اليوم؟ (قائمة الطعام، الأسعار، التوصيل، الحجوزات…)";
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Error] Failed to get personalized welcome message: {ex.Message}");
                return "مرحباً! أنا مساعد YallaEat 😊\nكيف أقدر أساعدك اليوم؟ (قائمة الطعام، الأسعار، التوصيل، الحجوزات…)";
            }
        }

        /// <summary>
        /// Call this when user starts a new chat session
        /// </summary>
        public async Task InitializeUserSession(string conversationId, string userId)
        {
            await LoadDraftOrderIntoMemory(conversationId, userId);
        }

        /// <summary>
        /// Call this when user ends their session (logout, close app, etc.)
        /// </summary>
        public async Task FinalizeUserSession(string conversationId, string userId)
        {
            await SaveInMemoryOrderToDraft(conversationId, userId);
        }

    }
}