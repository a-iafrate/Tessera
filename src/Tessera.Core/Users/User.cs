using Tessera.Core.Spaces;

namespace Tessera.Core.Users;

public class User
{
    public Guid Id { get; set; }
    public string Email { get; set; } = null!;
    public string? DisplayName { get; set; }
    public string PreferredCulture { get; set; } = "en";
    public string? TimeZoneId { get; set; }
    public Guid? DefaultSpaceId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public ICollection<ChannelIdentity> ChannelIdentities { get; set; } = [];
    public ICollection<Membership> Memberships { get; set; } = [];
}
