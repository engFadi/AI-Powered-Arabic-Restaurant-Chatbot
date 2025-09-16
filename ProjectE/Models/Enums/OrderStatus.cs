using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;

namespace ProjectE.Models.Enums {

    /// <summary>
    /// Represents the possible statuses of an order 
    /// </summary>
    public enum OrderStatus
    {
        // for the unsubmitted chatbot orders 
        Draft,

        /// <summary>Order is placed but not yet confirmed.</summary>
        Pending,

        Submitted,

        /// <summary>Order is currently being prepared.</summary>
        Processing,

        /// <summary>Order is out for delivery to the customer.</summary>
        OutForDelivery,

        /// <summary>Order has been successfully delivered to the customer.</summary>
        Delivered,

        /// <summary>Order has been cancelled by the customer or restaurant.</summary>
        Cancelled
    }
}