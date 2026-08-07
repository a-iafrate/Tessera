using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Tessera.Core.Channels;
using Tessera.Core.Conversations;
using Tessera.Core.Expenses;
using Tessera.Core.Reminders;
using Tessera.Core.Shopping;
using Tessera.Core.Spaces;
using DomainUser = Tessera.Core.Users.User;
using ChannelIdentity = Tessera.Core.Users.ChannelIdentity;
using LinkToken = Tessera.Core.Users.LinkToken;

namespace Tessera.Data;

public sealed class TesseraDbContext(DbContextOptions<TesseraDbContext> options)
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<DomainUser> DomainUsers => Set<DomainUser>();

    public DbSet<ChannelIdentity> ChannelIdentities => Set<ChannelIdentity>();

    public DbSet<Space> Spaces => Set<Space>();

    public DbSet<Membership> Memberships => Set<Membership>();

    public DbSet<MembershipPermission> MembershipPermissions => Set<MembershipPermission>();

    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();

    public DbSet<ShoppingList> ShoppingLists => Set<ShoppingList>();

    public DbSet<ShoppingItem> ShoppingItems => Set<ShoppingItem>();

    public DbSet<LinkToken> LinkTokens => Set<LinkToken>();

    public DbSet<Expense> Expenses => Set<Expense>();

    public DbSet<Category> Categories => Set<Category>();

    public DbSet<MerchantCategoryMapping> MerchantCategoryMappings => Set<MerchantCategoryMapping>();

    public DbSet<PendingExpenseConfirmation> PendingExpenseConfirmations => Set<PendingExpenseConfirmation>();

    public DbSet<Reminder> Reminders => Set<Reminder>();

    public DbSet<RecurringExpense> RecurringExpenses => Set<RecurringExpense>();

    public DbSet<Budget> Budgets => Set<Budget>();

    public DbSet<SubscriptionPlan> SubscriptionPlans => Set<SubscriptionPlan>();

    public DbSet<ConversationState> ConversationStates => Set<ConversationState>();

    public DbSet<InviteToken> InviteTokens => Set<InviteToken>();

    public DbSet<MembershipArchive> MembershipArchives => Set<MembershipArchive>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfigurationsFromAssembly(typeof(TesseraDbContext).Assembly);
    }
}
