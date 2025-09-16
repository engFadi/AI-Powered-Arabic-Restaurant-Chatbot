using System.Collections.Generic;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using System.ComponentModel.DataAnnotations;
using ProjectE.Models.Enums;

namespace ProjectE.Models.OrderModels
{
    /// <summary>
    /// Represents the response returned after an order has been created.
    /// </summary>
    public class OrderResponse
    {
        /// <summary>Unique identifier for the order.</summary>
        [Required]
        [Range(1, int.MaxValue, ErrorMessage = "OrderId must be a positive number.")]
        public int OrderId { get; set; }

        /// <summary>Full name of the customer who placed the order.</summary>
        [Required(ErrorMessage = "Customer name is required.")]
        [StringLength(100, MinimumLength = 2, ErrorMessage = "Customer name must be between 2 and 100 characters.")]
        public string CustomerName { get; set; }

        /// <summary>Customer's phone number.</summary>
        [Required(ErrorMessage = "Phone number is required.")]
        [RegularExpression(@"^05\d{8}$", ErrorMessage = "Phone number must be 10 digits and start with '05'.")]
        public string PhoneNumber { get; set; }

        /// <summary>List of food items included in the order.</summary>
        [Required]
        [MinLength(1, ErrorMessage = "Order must contain at least one item.")]
        public List<OrderItemResponse> Items { get; set; } = new();

        /// <summary>Optional notes or special instructions for the order.</summary>
        [MaxLength(500, ErrorMessage = "Notes cannot exceed 500 characters.")]
        public string? Notes { get; set; }

        /// <summary>Delivery address where the order should be sent.</summary>
        [Required(ErrorMessage = "Delivery address is required")]
        [StringLength(250, MinimumLength = 5, ErrorMessage = "Address must be between 5 and 250 characters.")]
        public string DeliveryAddress { get; set; }

        /// <summary>Total price of the order.
        /// The subtotal price of all items in the order (excluding delivery).
        /// Note: This value is calculated via query.        /// </summary>
        [Required]
        [Range(0, double.MaxValue, ErrorMessage = "Total price must be a non-negative value.")]
        public decimal Subtotal { get; set; }

        /// <summary>
        /// The fee for delivering the order.
        /// </summary>
        [Required]
        [Range(0, double.MaxValue, ErrorMessage = "Delivery fee must be a non-negative value.")]
        public decimal DeliveryFee { get; set; }

        /// <summary>
        /// The final total price of the order (Subtotal + DeliveryFee).
        /// This is a read-only property calculated automatically.
        /// </summary>
        public decimal TotalPrice { get; set; }

        /// <summary>Status of the order (e.g., Pending, Completed, Canceled).</summary>
        [Required(ErrorMessage = "Order status is required.")]
        public OrderStatus Status { get; set; }

        /// <summary>Date and time when the order was placed.</summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// Initializes a new instance of the OrderResponse class.
        /// </summary>
        public OrderResponse()
        {
            Items = new List<OrderItemResponse>();
        }

    }
}
