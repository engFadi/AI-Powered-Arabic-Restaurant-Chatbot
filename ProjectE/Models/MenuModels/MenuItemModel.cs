using System.ComponentModel.DataAnnotations;

namespace ProjectE.Models.MenuModels
{
    /// <summary>
    /// Represents a single item in the restaurant menu.
    /// </summary>
    public class MenuItem
    {
        /// <summary>
        /// Gets or sets the unique identifier of the menu item.
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// Gets or sets the name of the menu item (e.g., "Chicken Shawarma").
        /// </summary>
        [Required(ErrorMessage = "Name is required.")]
        [StringLength(100, ErrorMessage = "Name cannot exceed 100 characters.")]
        [Display(Name = "Menu Item Name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the price of the menu item in local currency (₪).
        /// </summary>
        [Required(ErrorMessage = "Price is required.")]
        [Range(0.01, 1000, ErrorMessage = "Price must be between 0.01 and 1000.")]
        public decimal Price { get; set; }

        /// <summary>
        /// Gets or sets the category of the menu item (e.g., Salad, Sandwich, Drink).
        /// </summary>
        [Required(ErrorMessage = "Category is required.")]
        public String Category { get; set; }

        /// <summary>
        /// Gets or sets a short description of the menu item (e.g., ingredients or preparation).
        /// </summary>
        [StringLength(250, ErrorMessage = "Description cannot exceed 250 characters.")]
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets whether the menu item is currently available to customers.
        /// </summary>
        public bool IsAvailable { get; set; }
    }
}
