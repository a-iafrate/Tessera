namespace Tessera.Core.Reminders;

public class Reminder
{
    public Guid Id { get; set; }
    public Guid SpaceId { get; set; }
    public string Text { get; set; } = null!;
    public DateTimeOffset DueAt { get; set; }
    public string TimeZoneId { get; set; } = null!;
    public RecurrenceRule? Recurrence { get; set; }
    public Guid CreatedByUserId { get; set; }
    public Guid? AssignedToUserId { get; set; }
    public bool IsCompleted { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? NotifiedAt { get; set; }
}
