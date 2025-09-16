namespace ProjectE.Models.ChatBot
{
    public class DeliveryAddressDto
    {
        public string? Address { get; set; }
        public string? PhoneNumber { get; set; }
        public string? CustomerName { get; set; }
    }

    public class ItemDto
    {
        public int? MenuItemId { get; set; }
        public string? Name { get; set; }
        public string? Size { get; set; }
        public List<string>? Extras { get; set; } = new();
        public int? Quantity { get; set; }
        public decimal? UnitPrice { get; set; }
        public decimal? LineTotal { get; set; }
        public string Currency { get; set; } = "شيكل";
    }
    public class NewMenuDto
    {
        public string? exception { get; set; }
    }
    public class ReservationInnerDto
    {
        public string? Date { get; set; }    
        public string? Time { get; set; }
        public int? PartySize { get; set; }
        public string? CustomerName { get; set; }
    }

    public class TotalsDto
    {
        public decimal? Subtotal { get; set; }
        public string? Currency { get; set; }
    }

    public class BotPayloadDto
    {
        public string? keyword { get; set; }   // add_to_order | confirm_order | submit
        public string? intent { get; set; }    // order
        public string? CustomerName { get; set; }
        public string? PhoneNumber { get; set; }
        public DeliveryAddressDto? DeliveryAddress { get; set; }
        public List<ItemDto> Items { get; set; } = new();
        public TotalsDto? Totals { get; set; }
        public ReservationInnerDto? Reservation { get; set; }
        public List<string> next_required_fields { get; set; } = new();

        //new properties for items management
        public string? target_item_name { get; set; }  // For remove/replace operations
        public ItemDto? replacement_item { get; set; }  // For replace operations
        public int? new_quantity { get; set; }  // For quantity updates
        public List<ItemDto>? current_order_items { get; set; }  // Current order state

        public NewMenuDto? newMenu { get; set; }

    }
}
