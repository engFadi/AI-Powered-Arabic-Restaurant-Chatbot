using System.ComponentModel.DataAnnotations.Schema;

namespace ProjectE.Models.Entities
{
    public class OrderItemEntity : BaseEntity
    {
        
        public int OrderId { get; set; }

        [ForeignKey(nameof(OrderId))]
        public OrderEntity Order { get; set; }

        public int MenuItemId { get; set; }

        [ForeignKey(nameof(MenuItemId))]
        public MenuItemEntity MenuItem { get; set; }

        public int Quantity { get; set; }
        public string? Notes { get; set; }
    }
}
