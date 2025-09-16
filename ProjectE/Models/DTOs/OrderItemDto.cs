namespace ProjectE.Models.DTOs
{
    public class OrderItemDto
    {
        public int Quantity { get; set; }
        public string MenuItemName { get; set; }
        public decimal MenuItemPrice { get; set; }
        // Add properties to match usage in OrderController
        public decimal LineTotal { get; set; }
        public int? MenuItemId { get; set; }
        public string? Size { get; set; }
        public decimal? UnitPrice { get; set; }
        public string? Notes { get; set; }

    }
}