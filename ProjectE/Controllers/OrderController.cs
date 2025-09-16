using Azure.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProjectE.Models.ChatBot;
using ProjectE.Models.OrderModels;
using ProjectE.Services;
using System.Collections.Generic;
using System.Security.Claims;


namespace ProjectE.Controllers
{
    /// <summary>
    /// API controller responsible for handling HTTP requests related to orders.
    /// </summary>
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class OrderController : ControllerBase
    {
        private readonly IOrderService _orderService;

        public OrderController(IOrderService orderService)
        {
            _orderService = orderService;
        }

        /// <summary>
        /// Retrieves all orders for the currently authenticated user.
        /// </summary>
        // GET: api/order/my
        [HttpGet("my")]
        public async Task<ActionResult> GetMyOrders()
        {
            try
            {
                var userId = GetCurrentUserId();
                var orders = await _orderService.GetOrdersByUserId(userId);

                if (orders == null || !orders.Any())
                {
                    return NotFound(new { Message = "No orders found for this user." });
                }

                return Ok(orders);
            }
            catch (UnauthorizedAccessException ex)
            {
                return Unauthorized(new { Message = ex.Message });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = "An error occurred while retrieving orders.", Details = ex.Message });
            }
        }

        /// <summary>
        /// Creates a new order.
        /// </summary>
        /// <param name="newOrder">The order data received from the request body.</param>
        /// <returns>The created order with a status 201 Created.</returns>
        //POST: api/order
        [HttpPost()]
        public async Task<IActionResult> CreateOrder([FromBody] ChatOrderRequest model)
        {
            if (!ModelState.IsValid)
                return BadRequest(new { message = "Invalid request" });

            try
            {
                var userId = GetCurrentUserId();
                var createdOrder = await _orderService.CreateOrder(userId, model);

                // Return 201 Created with link to this action and the created order data
                return CreatedAtAction(
                    nameof(CreateOrder),
                    new { orderId = createdOrder.OrderId },
                    createdOrder
                );
            }
            catch (ArgumentException ex)
            {
                // If some items are unavailable
                return BadRequest(new { message = ex.Message });
            }
            catch (UnauthorizedAccessException ex)
            {
                return Unauthorized(new { message = ex.Message });
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new { message = ex.Message });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "An unexpected error occurred.", detail = ex.Message });
            }

        }

        /// <summary>
        /// Deletes (cancels) an order for the currently authenticated user.
        /// </summary>
        /// <param name="orderId">The ID of the order to cancel.</param>
        [HttpDelete("{orderId}")]
        public async Task<IActionResult> CancelOrder(int orderId)
        {
            try
            {
                var userId = GetCurrentUserId();
                var result = await _orderService.CancelOrderByUser(userId, orderId);

                if (result)
                    return Ok(new { Message = "Order cancelled successfully." });

                return BadRequest(new { Message = "Unable to cancel order." });
            }
            catch (UnauthorizedAccessException ex)
            {
                return Unauthorized(new { message = ex.Message });
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new { message = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "An unexpected error occurred.", detail = ex.Message });
            }
        }

        /// <summary>
        /// Adds items to the current draft order for the user (used when AI sends keyword == "add_to_order").
        /// </summary>
        [HttpPost("add-item")]
        public async Task<IActionResult> AddItemToOrder([FromBody] BotPayloadDto payload)
        {
            if (payload == null || payload.Items == null || !payload.Items.Any())
                return BadRequest(new { message = "No items provided." });

            try
            {
                var userId = GetCurrentUserId();

                var orderItems = payload.Items.Select(item => new ProjectE.Models.DTOs.OrderItemDto
                {
                    MenuItemId = item.MenuItemId,
                    MenuItemName = item.Name,
                    Size = item.Size,
                    Notes = item.Extras != null ? string.Join(", ", item.Extras) : null,
                    Quantity = item.Quantity ?? 0,
                    UnitPrice = item.UnitPrice,
                    LineTotal = item.LineTotal ?? 0m,
                }).ToList();

                // Delegate to order service
                var updatedOrder = await _orderService.AddItemsToDraftOrder(userId, orderItems);

                return Ok(new
                {
                    message = "Items added to your order successfully.",
                    order = updatedOrder
                });
            }
            catch (UnauthorizedAccessException ex)
            {
                return Unauthorized(new { message = ex.Message });
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new { message = ex.Message });
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "An unexpected error occurred.", detail = ex.Message });
            }
        }

        [HttpGet("delivery-fee")] 
        [AllowAnonymous] 
        public IActionResult TestDeliveryFee([FromQuery] string address)
        {
            if (string.IsNullOrWhiteSpace(address))
            {
                return BadRequest(new { Message = "Address query parameter is required." });
            }

            try
            {
                var fee = _orderService.GetDeliveryFee(address);

                return Ok(new
                {
                    Address = address,
                    DeliveryFee = fee
                });
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { Message = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return NotFound(new { Message = ex.Message });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = "An unexpected error occurred.", Details = ex.Message });
            }
        }



        /// <summary>
        /// Extracts the current user ID from JWT claims.
        /// </summary>
        /// <returns>User ID as string (GUID)</returns>
        private string GetCurrentUserId()
        {
            // Try common claim types; adjust if your token uses a different one (e.g., "UserId")
            var idClaim = User.FindFirst(ClaimTypes.NameIdentifier) ?? User.FindFirst("sub") ?? User.FindFirst("UserId");

            if (idClaim == null) throw new UnauthorizedAccessException("User ID claim not found.");

            return idClaim.Value;
        }

        /// <summary>
        /// Checks if the current user has the Admin role.
        /// </summary>
        /// <returns>True if user is admin, false otherwise.</returns>
        private bool IsAdmin() =>
          User.IsInRole("Admin") || User.Claims.Any(c => c.Type == ClaimTypes.Role && c.Value == "Admin");

    }


}