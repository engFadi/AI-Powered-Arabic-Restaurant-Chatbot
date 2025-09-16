using ProjectE.Models.DTOs;
using ProjectE.Models.Entities;
using ProjectE.Models.OrderModels;
using System.Collections.Generic;

namespace ProjectE.Services
{
    /// <summary>
    /// Defines the contract for order-related operations such as creation, retrieval, updating, and deletion.
    /// </summary>
    public interface IOrderService
    {
        /// <summary>
        /// Creates a new order in the system.
        /// </summary>
        /// <param name="newOrder">The order to be created.</param>
        /// <returns>The newly created order.</returns>
        Task<OrderResponse> CreateOrder(string userId, ChatOrderRequest model);

        /// <summary>
        /// Retrieves all existing orders.
        /// </summary>
        /// <param name="userId">The unique identifier of the current user.</param>
        /// <param name="take">Optional. The number of most recent orders to retrieve.</param>
        /// <returns>A collection of the user's orders.</returns>
        Task<IEnumerable<OrderResponse>> GetOrdersByUserId(string userId, int? take = null);

        /// <summary>
        /// Cancels an existing order for the authenticated user.
        /// </summary>
        /// <param name="userId">The unique identifier of the current user (from authentication context).</param>
        /// <param name="orderId">The ID of the order to cancel.</param>
        Task<bool> CancelOrderByUser(string userId, int orderId);

        /// <summary>
        /// Adds items to the current draft order for a user.
        /// </summary>
        /// <param name="userId">The user's ID</param>
        /// <param name="items">The items to add to the order</param>
        /// <returns>The updated order</returns>
        Task<OrderResponse> AddItemsToDraftOrder(string userId, List<OrderItemDto> items);

        Task<OrderResponse> ConfirmOrder(string userId, string deliveryAddress);

        decimal GetDeliveryFee(string deliveryAddress);

    }

}