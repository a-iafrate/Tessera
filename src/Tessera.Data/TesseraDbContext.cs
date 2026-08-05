using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Tessera.Core.Spaces;
using DomainUser = Tessera.Core.Users.User;
using ChannelIdentity = Tessera.Core.Users.ChannelIdentity;

namespace Tessera.Data;

public sealed class TesseraDbContext(DbContextOptions<TesseraDbContext> options)
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<DomainUser> DomainUsers => Set<DomainUser>();

    public DbSet<ChannelIdentity> ChannelIdentities => Set<ChannelIdentity>();

    public DbSet<Space> Spaces => Set<Space>();

    public DbSet<Membership> Memberships => Set<Membership>();

    public DbSet<MembershipPermission> MembershipPermissions => Set<MembershipPermission>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfigurationsFromAssembly(typeof(TesseraDbContext).Assembly);
    }
}
