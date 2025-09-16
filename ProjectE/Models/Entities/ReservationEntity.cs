namespace ProjectE.Models.Entities
{
    public class ReservationEntity : BaseEntity
    {
        public string CustomerName { get; set; } = string.Empty;

        public DateTime Date { get; set; }
        public TimeSpan Time { get; set; }

        public int PartySize { get; set; }
    }
}
