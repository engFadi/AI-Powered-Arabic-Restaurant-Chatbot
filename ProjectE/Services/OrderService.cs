using Microsoft.EntityFrameworkCore;
using ProjectE.Data;
using ProjectE.Exceptions;
using ProjectE.Models.Auth;
using ProjectE.Models.DTOs;
using ProjectE.Models.Entities;
using ProjectE.Models.Enums;
using ProjectE.Models.OrderModels;
using System.Collections.Generic;
using System.Linq;
using static Microsoft.EntityFrameworkCore.DbLoggerCategory;

namespace ProjectE.Services
{
    /// <summary>
    /// Service responsible for managing orders including creation, retrieval, updating, and deletion.
    /// </summary>
    public class OrderService : IOrderService
    {

        private readonly ApplicationDbContext _context;

        public OrderService(ApplicationDbContext context)
        {
            _context = context;
        }

        /// /// <summary>
        /// Retrieves all orders for the currently authenticated user.
        /// </summary>
        /// <param name="userId">The unique identifier of the current user.</param>
        /// <param name="take">Optional. The number of most recent orders to retrieve.</param>
        /// <returns>A collection of the user's orders.</returns>
        public async Task<IEnumerable<OrderResponse>> GetOrdersByUserId(string userId, int? take = null)
        {
            var internalUserId = await _context.Users
            .Where(u => u.UserId == userId)
            .Select(u => u.Id)
            .FirstOrDefaultAsync();

            if (internalUserId == 0)
                throw new UnauthorizedAccessException("User not found.");

            IQueryable<OrderEntity> query = _context.Orders
                .Where(o => o.UserId == internalUserId)
                .Include(o => o.Items)
                    .ThenInclude(i => i.MenuItem)
                .Include(o => o.User);

            query = query.OrderByDescending(o => o.CreatedAt);

            if (take.HasValue && take.Value > 0)
            {
                query = query.Take(take.Value);
            }
            var orders = await query.ToListAsync();

            return orders.Select(MapToOrderResponse);
        }

        /// <summary>
        /// Creates a new order based on the provided ChatOrderRequest.
        /// </summary>
        /// <param name="model">The order request containing items and customer details.</param>
        /// <returns>Returns the created order with calculated total price.</returns>
        public async Task<OrderResponse> CreateOrder(string userId,ChatOrderRequest request)
        {
            // Collect all requested menu item IDs.
            var menuItemIds = request.Items.Select(i => i.MenuItemId).ToList();

            // Check duplicates first
            var duplicateIds = menuItemIds
                .GroupBy(id => id)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();

            if (duplicateIds.Any())
            {
                throw new DuplicateMenuItemException(
                    $"Duplicate menu item(s) in request: {string.Join(",", duplicateIds)}"
                );
            }

            if (request.Items == null || !request.Items.Any())
            {
                throw new EmptyOrderItemsException("Order must contain at least one item.");
            }

            // Retrieve all requested menu items in a single query.
            var menuItems = await _context.MenuItems
                .Where(m => menuItemIds.Contains(m.Id)) // 
                .ToListAsync();

            // Ensure all menu items exist.
            if (menuItems.Count != request.Items.Count)
            {
                var missingIds = string.Join(",", menuItemIds.Except(menuItems.Select(m => m.Id)));
                throw new MenuItemNotFoundException($"Menu item(s) not found: {missingIds}");
            }


            // Ensure all menu items are available.
            if (menuItems.Any(m => !m.IsAvailable)) {
                var unavailable = string.Join(",", menuItems.Where(m => !m.IsAvailable).Select(m => m.Id));
                throw new MenuItemUnavailableException($"Menu item(s) not available: {unavailable}");
            }

            var user = await _context.Users.FirstOrDefaultAsync(u => u.UserId == userId);
            if (user == null) throw new UnauthorizedAccessException("User not found.");

            // Ensure all quantities are valid
            if (request.Items.Any(i => i.Quantity <= 0))
                throw new InvalidQuantityException("Quantity must be greater than zero.");

            // Create new order entity.
            var order = new OrderEntity
            {
                UserId = user.Id, 
                CreatedAt = DateTime.UtcNow,
                CustomerName = string.IsNullOrWhiteSpace(request.CustomerName) ? user.UserName : request.CustomerName,
                PhoneNumber = string.IsNullOrWhiteSpace(request.PhoneNumber) ? user.PhoneNumber : request.PhoneNumber,
                DeliveryAddress = request.DeliveryAddress,
                Status = OrderStatus.Pending.ToString(),
                // Ensure Notes is never null because DB column is non-nullable in current schema
                Notes = string.IsNullOrWhiteSpace(request.Notes) ? string.Empty : request.Notes.Trim(),
                Items = request.Items.Select(i => new OrderItemEntity
                {
                    MenuItemId = i.MenuItemId,
                    Quantity = i.Quantity,
                    Notes = string.IsNullOrWhiteSpace(i.Notes) ? string.Empty : i.Notes.Trim()
                }).ToList()
            };

            try
            {
                await _context.Orders.AddAsync(order);
                await _context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.InnerException?.Message);
                throw;
            }

            // Load related data (user and menu items)
            await _context.Entry(order).Collection(o => o.Items).LoadAsync();
            await _context.Entry(order).Reference(o => o.User).LoadAsync();

            foreach (var item in order.Items)
            {
                await _context.Entry(item).Reference(i => i.MenuItem).LoadAsync();
            }

            // Use the mapping method to build the response
            return MapToOrderResponse(order);
        }

        /// <summary>
        /// Cancels an existing order for the authenticated user.
        /// </summary>
        /// <param name="userId">The unique identifier of the current user (from authentication context).</param>
        /// <param name="orderId">The ID of the order to cancel.</param>
        /// <returns>
        /// Returns true if the order was successfully canceled; 
        /// otherwise, throws exceptions depending on the error case:
        /// </returns>
        public async Task<bool> CancelOrderByUser(string userId, int orderId)
        {
            var internalUserId = await _context.Users
                .Where(u => u.UserId == userId)
                .Select(u => u.Id)
                .FirstOrDefaultAsync();

            if (internalUserId == 0)
                throw new UnauthorizedAccessException("User not found.");

            var order = await _context.Orders
                .FirstOrDefaultAsync(o => o.Id == orderId && o.UserId == internalUserId);

            if (order == null)
                throw new KeyNotFoundException("Order not found.");

            // Customer cancels if order is pending or confirmed only
            if (order.Status != OrderStatus.Pending.ToString() )
                throw new InvalidOperationException("You cannot cancel the order once it is in Processing or later.");

            _context.Orders.Remove(order);
            await _context.SaveChangesAsync();

            return true;
        }

        /// <summary>
        /// Adds items to a user's draft order. Creates a new draft order if one doesn't exist.
        /// </summary>
        /// <param name="userId">The user's ID</param>
        /// <param name="items">The items to add to the order</param>
        /// <returns>The updated order</returns>
        public async Task<OrderResponse> AddItemsToDraftOrder(string userId, List<OrderItemDto> items)
        {
            Console.WriteLine($"[Debug] Adding items to draft order for user {userId}");

            if (items == null || !items.Any())
            {
                Console.WriteLine("[Debug] No items provided to add");
                throw new ArgumentException("No items provided to add to the order.");
            }

            // Get internal user ID
            var user = await _context.Users.FirstOrDefaultAsync(u => u.UserId == userId);
            if (user == null)
            {
                Console.WriteLine($"[Error] User with ID {userId} not found");
                throw new UnauthorizedAccessException("User not found.");
            }

            int internalUserId = user.Id;
            Console.WriteLine($"[Debug] Mapped user {userId} to internal ID {internalUserId}");

            // Check if user has a draft order
            var draftOrder = await _context.Orders
                .Include(o => o.Items)
                    .ThenInclude(i => i.MenuItem)
                .FirstOrDefaultAsync(o => o.UserId == internalUserId && o.Status == OrderStatus.Pending.ToString());

            // Create a new draft order if one doesn't exist
            if (draftOrder == null)
            {
                Console.WriteLine("[Debug] No draft order found, creating new draft order");
                draftOrder = new OrderEntity
                {
                    UserId = internalUserId,
                    CustomerName = user.UserName,
                    PhoneNumber = user.PhoneNumber,
                    CreatedAt = DateTime.UtcNow,
                    Status = OrderStatus.Pending.ToString(),
                    Items = new List<OrderItemEntity>(),
                    DeliveryAddress = "Not Provided",
                    // Ensure non-null Notes to satisfy NOT NULL constraint
                    Notes = string.Empty
                };

                _context.Orders.Add(draftOrder);
                await _context.SaveChangesAsync();
                Console.WriteLine($"[Debug] Created new draft order with ID {draftOrder.Id}");
            }
            else
            {
                Console.WriteLine($"[Debug] Found existing draft order with ID {draftOrder.Id}");
            }

            // Helper to normalize notes: null/whitespace -> "", trim, case-insensitive compare
            static string NormalizeNotes(string? notes)
                => string.IsNullOrWhiteSpace(notes) ? string.Empty : notes.Trim();

            foreach (var item in items)
            {
                // Guard quantity (optional)
                var qty = item.Quantity <= 0 ? 1 : item.Quantity;

                // Find the menu item by name (case-insensitive)
                var menuItem = await _context.MenuItems
                    .FirstOrDefaultAsync(m => m.IsAvailable &&
                                              m.Name.ToLower() == item.MenuItemName.ToLower());

                if (menuItem == null)
                {
                    Console.WriteLine($"[Warning] Menu item '{item.MenuItemName}' not found or unavailable, skipping");
                    continue;
                }

                // Normalize incoming notes once
                var newNotes = NormalizeNotes(item.Notes);

                // Look for an existing line with the SAME MenuItemId AND SAME normalized notes
                var existingItem = draftOrder.Items.FirstOrDefault(i =>
                    i.MenuItemId == menuItem.Id &&
                    string.Equals(NormalizeNotes(i.Notes), newNotes, StringComparison.OrdinalIgnoreCase)
                );

                if (existingItem != null)
                {
                    // Same item + same notes -> increase quantity
                    var oldQty = existingItem.Quantity;
                    existingItem.Quantity = oldQty+ qty;
                    // Keep normalized notes (ensures stored as "" or trimmed)
                    existingItem.Notes = newNotes;

                    Console.WriteLine($"[Debug] Increased quantity for item (ID {existingItem.Id}) " +
                                      $"from {oldQty} to {existingItem.Quantity} (notes='{newNotes}')");
                }
                else
                {
                    // Different notes (or no matching line) -> add a new line item
                    var orderItem = new OrderItemEntity
                    {
                        OrderId = draftOrder.Id,
                        MenuItemId = menuItem.Id,
                        Quantity = qty,
                        Notes = newNotes
                    };

                    draftOrder.Items.Add(orderItem);
                    Console.WriteLine($"[Debug] Added new item '{menuItem.Name}' x{qty} (notes='{newNotes}')");
                }
            }

            // Save changes
            await _context.SaveChangesAsync();
            Console.WriteLine($"[Debug] Saved order changes. Order now has {draftOrder.Items.Count} items");

            // Reload the order to ensure we have all the latest data
            await _context.Entry(draftOrder).ReloadAsync();
            await _context.Entry(draftOrder).Collection(o => o.Items).LoadAsync();

            foreach (var item in draftOrder.Items)
            {
                await _context.Entry(item).Reference(i => i.MenuItem).LoadAsync();
            }

            // Map to response model
            var orderResponse = MapToOrderResponse(draftOrder);

            Console.WriteLine($"[Debug] Returning order with {orderResponse.Items.Count} items and total price {orderResponse.TotalPrice}");
            return orderResponse;
        }

        /// <summary>
        /// Confirms a user's draft order, assigns a delivery address, and moves it to the pending state for processing.
        /// </summary>
        /// <param name="userId">The unique identifier of the user confirming the order.</param>
        /// <param name="deliveryAddress">The delivery address for the order.</param>
        /// <returns>An <see cref="OrderResponse"/> object representing the confirmed order.</returns>
        public async Task<OrderResponse> ConfirmOrder(string userId, string deliveryAddress)
        {
            var user = await _context.Users.FirstOrDefaultAsync(u => u.UserId == userId);
            if (user == null)
            {
                throw new UnauthorizedAccessException("User not found.");
            }

            var draftOrder = await _context.Orders
                .Include(o => o.Items)
                    .ThenInclude(i => i.MenuItem)
                .FirstOrDefaultAsync(o => o.UserId == user.Id && o.Status == OrderStatus.Pending.ToString());

            if (draftOrder == null)
            {
                throw new InvalidOperationException("There is no order request to confirm.");
            }

            if (!draftOrder.Items.Any())
            {
                throw new InvalidOperationException("An empty order cannot be confirmed.");
            }

            draftOrder.DeliveryAddress = deliveryAddress;
            draftOrder.Status = OrderStatus.Pending.ToString(); 
            draftOrder.CreatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            return MapToOrderResponse(draftOrder);
        }

        /// <summary>
        /// Calculates the delivery fee based on the provided delivery address.
        /// The fee is determined by checking for specific keywords (city names) within the address string.
        /// </summary>
        /// <param name="deliveryAddress">The full delivery address provided by the customer.</param>
        /// <returns>The calculated delivery fee as a decimal value.</returns>
        public decimal GetDeliveryFee(string deliveryAddress)
        {

            if (string.IsNullOrWhiteSpace(deliveryAddress) || deliveryAddress.Trim().Equals("Not Provided", StringComparison.OrdinalIgnoreCase))
            {
                return 0m;
            }

            var normalized = deliveryAddress.Trim().ToLower();

            if (normalized.Contains("روابي") || normalized.Contains("rawabi"))
            {
                return 0m; // Free delivery
            }
            else if (normalized.Contains("بيرزيت") || normalized.Contains("birzeit"))
            {
                return 7m;
            }
            else if (normalized.Contains("رام الله") || normalized.Contains("ramallah"))
            {
                return 12m;
            }

            else
            {
                throw new InvalidOperationException(
                    $"Delivery is not supported for the specified location: {deliveryAddress}");
            }
        }

        /// <summary>
        /// Maps an OrderEntity to an OrderResponse DTO.
        /// </summary>
        /// <param name="order">The order entity to map.</param>
        /// <returns>Returns an OrderResponse DTO.</returns>
        private OrderResponse MapToOrderResponse(OrderEntity order)
        {
            var subtotal = order.Items.Sum(i => i.Quantity * i.MenuItem.Price);

            var deliveryFee = GetDeliveryFee(order.DeliveryAddress);

            var totalPrice = subtotal + deliveryFee;

            return new OrderResponse
            {
                OrderId = order.Id,
                CustomerName = order.CustomerName,
                PhoneNumber = order.PhoneNumber,
                DeliveryAddress = order.DeliveryAddress,
                Notes = order.Notes,
                Status = Enum.TryParse<OrderStatus>(order.Status, out var status) ? status
                 : throw new InvalidOperationException($"Invalid status value: {order.Status}."),
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
                TotalPrice = totalPrice,
            };

        }

    }
}

    
        
