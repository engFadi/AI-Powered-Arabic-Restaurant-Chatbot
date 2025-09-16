using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProjectE.Data;
using ProjectE.Models.DTOs;
using System.Linq;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Security.Claims; 
namespace ProjectE.Controllers.Api
{
    [ApiController]
    [Route("api/orders")]
    [Authorize] 
    public class OrderApiController : ControllerBase
    {
        private readonly ApplicationDbContext _context;

        public OrderApiController(ApplicationDbContext context)
        {
            _context = context;
        }

        public class StatusUpdateModel
        {
            public required string NewStatus { get; set; }
        }

        // --- ADMIN ENDPOINTS ---

        [HttpGet]
        [Authorize(Roles = "Admin")] 
        public async Task<IActionResult> GetOrders([FromQuery] string status, [FromQuery] string sortBy)
        {
            var query = _context.Orders.AsQueryable();

            //EXCLUDE DRAFT ORDERS from admin dashboard
            query = query.Where(o => o.Status != "Draft");

            if (!string.IsNullOrEmpty(status) && status.ToLower() != "all")
            {
                query = query.Where(o => o.Status == status);
            }

            query = sortBy?.ToLower() switch
            {
                "date_asc" => query.OrderBy(o => o.CreatedAt),
                "status" => query.OrderBy(o => o.Status),
                _ => query.OrderByDescending(o => o.CreatedAt),
            };

            var orderSummaries = await query
                .Select(o => new OrderSummaryDto
                {
                    Id = o.Id,
                    CustomerName = o.CustomerName,
                    Status = o.Status,
                    CreatedAt = o.CreatedAt
                })
                .ToListAsync();

            return Ok(orderSummaries);
        }

        [HttpGet("{id}")]
        [Authorize(Roles = "Admin")] // This endpoint is specifically for Admins
        public async Task<IActionResult> GetOrderDetails(int id)
        {
            // ... (this code remains the same)
            var order = await _context.Orders
                .Include(o => o.Items)
                .ThenInclude(i => i.MenuItem)
                .FirstOrDefaultAsync(o => o.Id == id);

            if (order == null)
            {
                return NotFound();
            }

            var orderDetailsDto = new OrderDetailsDto
            {
                Id = order.Id,
                CustomerName = order.CustomerName,
                PhoneNumber = order.PhoneNumber,
                DeliveryAddress = order.DeliveryAddress,
                Notes = order.Notes,
                Status = order.Status,
                Items = order.Items.Select(item => new OrderItemDto
                {
                    Quantity = item.Quantity,
                    MenuItemName = item.MenuItem != null ? item.MenuItem.Name : "Archived Item",
                    MenuItemPrice = item.MenuItem != null ? item.MenuItem.Price : 0,
                    Notes = item.Notes != null ? item.Notes : string.Empty
                }).ToList()
            };

            return Ok(orderDetailsDto);
        }

        [HttpPut("{id}/status")]
        [Authorize(Roles = "Admin")] 
        public async Task<IActionResult> UpdateOrderStatus(int id, [FromBody] StatusUpdateModel model)
        {
            var order = await _context.Orders.FindAsync(id);

            if (order == null)
            {
                return NotFound();
            }

            order.Status = model.NewStatus;
            await _context.SaveChangesAsync();

            return Ok(new { message = $"Order #{id} status updated successfully." });
        }

        // --- CUSTOMER ENDPOINT ---

        [HttpGet("my/latest")] // RESTful route for the user's latest order
        [Authorize(Roles = "Customer")] // This endpoint is specifically for Customers
        public async Task<IActionResult> GetMyLatestOrder()
        {
            // Get the user's string-based unique ID from the token
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdClaim))
                return Unauthorized(new { message = "User ID not found in token" });

            var user = await _context.Users.FirstOrDefaultAsync(u => u.UserId == userIdClaim);
            if (user == null)
                return NotFound(new { message = "User not found." });

            // Now use the correct integer ID (user.Id) to find the order
            var order = await _context.Orders
                .Where(o => o.UserId == user.Id) // Use the integer ID for the query
                .OrderByDescending(o => o.CreatedAt)
                .Include(o => o.Items)
                .ThenInclude(i => i.MenuItem)
                .FirstOrDefaultAsync();

            if (order == null)
                return NotFound(new { message = "No orders found for this customer" });

            // calculate the total with the delivery fee
            var subtotal = order.Items.Sum(i => i.Quantity * i.MenuItem.Price);

            // Calculate delivery fee using the same logic as OrderService
            var deliveryFee = CalculateDeliveryFee(order.DeliveryAddress);
            var totalPrice = subtotal + deliveryFee;

            // Map to DTO and return
            var orderDto = new OrderDetailsDto
            {
                Id = order.Id,
                CustomerName = order.CustomerName,
                PhoneNumber = order.PhoneNumber,
                DeliveryAddress = order.DeliveryAddress,
                Notes = order.Notes,
                Status = order.Status,
                Items = order.Items.Select(item => new OrderItemDto
                {
                    Quantity = item.Quantity,
                    MenuItemName = item.MenuItem != null ? item.MenuItem.Name : "Archived Item",
                    MenuItemPrice = item.MenuItem != null ? item.MenuItem.Price : 0,
                    Notes = item.Notes != null ? item.Notes : string.Empty
                }).ToList(),
                Subtotal = subtotal,
                DeliveryFee = deliveryFee,
                TotalPrice = totalPrice
            };

            return Ok(orderDto);
        }

        /// <summary>
        /// Calculate delivery fee - same logic as OrderService.GetDeliveryFee
        /// </summary>
        private decimal CalculateDeliveryFee(string deliveryAddress)
        {
            if (string.IsNullOrWhiteSpace(deliveryAddress) ||
                deliveryAddress.Trim().Equals("Not Provided", StringComparison.OrdinalIgnoreCase))
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
                return 0m; // Default to no fee for unknown addresses
            }
        }
    }
}