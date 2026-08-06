namespace Tessera.Core.Expenses;

public class Budget
{
    public Guid Id { get; set; }
    public Guid SpaceId { get; set; }
    public Guid? CategoryId { get; set; }
    public decimal MonthlyLimit { get; set; }
    public int AlertThresholdPercent { get; set; } = 80;
    public DateOnly? LastAlertedFor { get; set; }
}
