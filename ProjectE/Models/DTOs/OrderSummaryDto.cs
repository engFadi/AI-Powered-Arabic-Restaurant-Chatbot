using System;

namespace ProjectE.Models.DTOs
{
    public class OrderSummaryDto
    {
        public int Id { get; set; }
        public DateTime CreatedAt { get; set; }
        public string CustomerName { get; set; }
        public string Status { get; set; }
    }
}