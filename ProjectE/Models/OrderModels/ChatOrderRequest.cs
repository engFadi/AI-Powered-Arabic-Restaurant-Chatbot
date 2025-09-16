using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;

namespace ProjectE.Models.OrderModels
{

    /// <summary>
    /// Represents the data received from a customer through the chat bot to place an order.
    /// This is a simplified version of an order without status or timestamps.
    /// </summary>
    public class ChatOrderRequest
    {
        /// <summary>Full name of the customer placing the order.</summary>
        [Required(ErrorMessage = "Customer name is required.")]
        [StringLength(100, MinimumLength = 2, ErrorMessage = "Customer name must be between 2 and 100 characters.")]
        public string? CustomerName { get; set; }

        /// <summary>Customer's phone number.</summary>
        [Required(ErrorMessage = "Phone number is required.")]
        [RegularExpression(@"^05\d{8}$", ErrorMessage = "Phone number must be 10 digits and start with '05'.")]
        public string? PhoneNumber { get; set; }

        /// <summary>List of food items included in the order.</summary>
        [Required]
        [MinLength(1, ErrorMessage = "Order must contain at least one item.")]
        //[ValidateComplexType]
        public List<OrderItemRequest> Items { get; set; }

        /// <summary>Delivery address for the order.</summary>
        [Required(ErrorMessage = "Delivery address is required")]
        [StringLength(250, MinimumLength = 5, ErrorMessage = "Address must be between 5 and 250 characters.")]
        public string DeliveryAddress { get; set; }

        /// <summary>Optional notes provided by the customer.</summary>
        [MaxLength(500, ErrorMessage = "Notes cannot exceed 500 characters.")]
        public string? Notes { get; set; }

        /// <summary>
        /// Initializes a new instance of the Order class with default values.
        /// </summary>
        public ChatOrderRequest()
        {
            Items = new List<OrderItemRequest>();
        }
    }

}
