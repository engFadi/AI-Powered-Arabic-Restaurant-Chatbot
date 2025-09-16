namespace ProjectE.Models.Entities
{
    using System.ComponentModel.DataAnnotations;

    public class MenuItemEntity : BaseEntity
    {
        [Required]
        [StringLength(200)]
        public string Name { get; set; }

        [Required]
        [Range(0.1, 9999)]
        public decimal Price { get; set; }

        [Required]
        [StringLength(100)]
        public string Category { get; set; }

        public bool IsAvailable { get; set; } = true; // Default to available
        public string? Description { get; set; }
    }
}
