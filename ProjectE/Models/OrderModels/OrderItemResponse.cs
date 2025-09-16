using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;

namespace ProjectE.Models.OrderModels
{

    /// <summary>
    /// Represents an individual item within a customer's order.
    /// </summary>
    public class OrderItemResponse
    {
        /// <summary>Unique identifier of the item.</summary>
        [Required]
        [Range(1, int.MaxValue, ErrorMessage = "ItemId must be a positive number.")]
        public int MenuItemId { get; set; }

        /// <summary>Name of the item.</summary>
        [Required]
        [StringLength(100)]
        public string MenuItemName { get; set; }

        public decimal Price { get; set; }

        /// <summary>Quantity of the item ordered.</summary>
        [Required]
        [Range(1, int.MaxValue, ErrorMessage = "Quantity must be at least 1.")]
        public int Quantity { get; set; }


        /// <summary>Notes for any customizations to the meal (e.g., add/remove ingredients as per customer request).</summary>
        public string? Notes { get; set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="OrderItemResponse"/> class.
        /// </summary>
        public OrderItemResponse() { 
        
        }

    }
}