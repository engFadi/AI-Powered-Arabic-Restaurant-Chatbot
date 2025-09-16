using System.ComponentModel.DataAnnotations;

namespace ProjectE.Models.Entities
{
    public class BaseEntity
    {
        [Key]
        public int Id { get; set; }
    }
}
