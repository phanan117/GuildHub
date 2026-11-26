using GuildHub.Core.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace GuildHub.Core.Data;

public sealed class GuildHubDbContext(DbContextOptions<GuildHubDbContext> options) : DbContext(options)
{
    public DbSet<WarEvent> WarEvents => Set<WarEvent>();
    public DbSet<EventSignup> EventSignups => Set<EventSignup>();
    public DbSet<ClassEmoji> ClassEmojis => Set<ClassEmoji>();
    public DbSet<UiEmoji> UiEmojis => Set<UiEmoji>();
    public DbSet<GuildConfig> GuildConfigs => Set<GuildConfig>();
    public DbSet<GuildMember> GuildMembers => Set<GuildMember>();
    public DbSet<GuildClassConfig> GuildClassConfigs => Set<GuildClassConfig>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var ulongConverter = new ValueConverter<ulong, long>(
            v => unchecked((long)v),
            v => unchecked((ulong)v));

        var nullableUlongConverter = new ValueConverter<ulong?, long?>(
            v => v.HasValue ? unchecked((long)v.Value) : null,
            v => v.HasValue ? unchecked((ulong)v.Value) : null);

        modelBuilder.Entity<WarEvent>(entity =>
        {
            entity.ToTable("war_events");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.GuildId).HasColumnName("guild_id").HasConversion(ulongConverter);
            entity.Property(e => e.ChannelId).HasColumnName("channel_id").HasConversion(ulongConverter);
            entity.Property(e => e.MessageId).HasColumnName("message_id").HasConversion(ulongConverter);
            entity.Property(e => e.Title).HasColumnName("title").HasMaxLength(200).IsRequired();
            entity.Property(e => e.Description).HasColumnName("description").HasMaxLength(1500);
            entity.Property(e => e.EventType).HasColumnName("event_type").HasConversion<int>();
            entity.Property(e => e.EventTimeUtc).HasColumnName("event_time_utc");
            entity.Property(e => e.CloseTimeUtc).HasColumnName("close_time_utc");
            entity.Property(e => e.MentionRoleId).HasColumnName("mention_role_id").HasConversion(nullableUlongConverter);
            entity.Property(e => e.LeaderDiscordId).HasColumnName("leader_discord_id").HasConversion(ulongConverter);
            entity.Property(e => e.IsClosed).HasColumnName("is_closed").HasDefaultValue(false);
            entity.Property(e => e.OnlineReminderSent).HasColumnName("online_reminder_sent").HasDefaultValue(false);
            entity.Property(e => e.NextSignupOrder).HasColumnName("next_signup_order").HasDefaultValue(1);
            entity.Property(e => e.CreatedAtUtc).HasColumnName("created_at_utc");

            entity.HasIndex(e => new { e.GuildId, e.CreatedAtUtc });
            entity.HasIndex(e => e.MessageId);
            entity.HasIndex(e => new { e.IsClosed, e.CloseTimeUtc });
            entity.HasIndex(e => new { e.OnlineReminderSent, e.EventTimeUtc });
        });

        modelBuilder.Entity<EventSignup>(entity =>
        {
            entity.ToTable("event_signups");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.WarEventId).HasColumnName("war_event_id");
            entity.Property(e => e.DiscordUserId).HasColumnName("discord_user_id").HasConversion(ulongConverter);
            entity.Property(e => e.DisplayName).HasColumnName("display_name").HasMaxLength(100).IsRequired();
            entity.Property(e => e.GameClass).HasColumnName("game_class").HasConversion<int?>();
            entity.Property(e => e.Status).HasColumnName("status").HasConversion<int>();
            entity.Property(e => e.SignupOrder).HasColumnName("signup_order");
            entity.Property(e => e.Note).HasColumnName("note").HasMaxLength(300);
            entity.Property(e => e.OptionsEnabled).HasColumnName("options_enabled").HasDefaultValue(true);
            entity.Property(e => e.SignedUpAtUtc).HasColumnName("signed_up_at_utc");
            entity.Property(e => e.UpdatedAtUtc).HasColumnName("updated_at_utc");

            entity.HasOne(e => e.WarEvent)
                .WithMany(w => w.Signups)
                .HasForeignKey(e => e.WarEventId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => new { e.WarEventId, e.DiscordUserId }).IsUnique();
            entity.HasIndex(e => new { e.WarEventId, e.Status, e.SignupOrder });
        });

        modelBuilder.Entity<ClassEmoji>(entity =>
        {
            entity.ToTable("class_emojis");
            entity.HasKey(e => e.GameClass);

            entity.Property(e => e.GameClass).HasColumnName("game_class").HasConversion<int>();
            entity.Property(e => e.EmojiName).HasColumnName("emoji_name").HasMaxLength(64).IsRequired();
            entity.Property(e => e.EmojiId).HasColumnName("emoji_id").HasConversion(ulongConverter);
            entity.Property(e => e.FileSha256).HasColumnName("file_sha256").HasMaxLength(64).IsRequired();
            entity.Property(e => e.SyncedAtUtc).HasColumnName("synced_at_utc");
        });

        modelBuilder.Entity<UiEmoji>(entity =>
        {
            entity.ToTable("ui_emojis");
            entity.HasKey(e => e.UiIcon);

            entity.Property(e => e.UiIcon).HasColumnName("ui_icon").HasConversion<int>();
            entity.Property(e => e.EmojiName).HasColumnName("emoji_name").HasMaxLength(64).IsRequired();
            entity.Property(e => e.EmojiId).HasColumnName("emoji_id").HasConversion(ulongConverter);
            entity.Property(e => e.FileSha256).HasColumnName("file_sha256").HasMaxLength(64).IsRequired();
            entity.Property(e => e.SyncedAtUtc).HasColumnName("synced_at_utc");
        });

        modelBuilder.Entity<GuildConfig>(entity =>
        {
            entity.ToTable("guild_configs");
            entity.HasKey(e => e.GuildId);

            entity.Property(e => e.GuildId).HasColumnName("guild_id").HasConversion(ulongConverter);
            entity.Property(e => e.GuildName).HasColumnName("guild_name").HasMaxLength(150).IsRequired();
            entity.Property(e => e.IconUrl).HasColumnName("icon_url").HasMaxLength(300);
            entity.Property(e => e.MemberRoleId).HasColumnName("member_role_id").HasConversion(nullableUlongConverter);
            entity.Property(e => e.MemberRoleName).HasColumnName("member_role_name").HasMaxLength(100);
            entity.Property(e => e.LastSyncedAtUtc).HasColumnName("last_synced_at_utc");
            entity.Property(e => e.LastSyncedMemberCount).HasColumnName("last_synced_member_count").HasDefaultValue(0);
            entity.Property(e => e.UpdatedAtUtc).HasColumnName("updated_at_utc");
        });

        modelBuilder.Entity<GuildMember>(entity =>
        {
            entity.ToTable("guild_members");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.GuildId).HasColumnName("guild_id").HasConversion(ulongConverter);
            entity.Property(e => e.DiscordUserId).HasColumnName("discord_user_id").HasConversion(nullableUlongConverter);
            entity.Property(e => e.DiscordUsername).HasColumnName("discord_username").HasMaxLength(100).IsRequired();
            entity.Property(e => e.DisplayName).HasColumnName("display_name").HasMaxLength(100).IsRequired();
            entity.Property(e => e.IngameNickname).HasColumnName("ingame_nickname").HasMaxLength(100);
            entity.Property(e => e.AvatarUrl).HasColumnName("avatar_url").HasMaxLength(300);
            entity.Property(e => e.GameClass).HasColumnName("game_class").HasConversion<int?>();
            entity.Property(e => e.HasGuildRole).HasColumnName("has_guild_role").HasDefaultValue(true);
            entity.Property(e => e.IsRemoved).HasColumnName("is_removed").HasDefaultValue(false);
            entity.Property(e => e.IsExternal).HasColumnName("is_external").HasDefaultValue(false);
            entity.Property(e => e.RemovedAtUtc).HasColumnName("removed_at_utc");
            entity.Property(e => e.CreatedAtUtc).HasColumnName("created_at_utc");
            entity.Property(e => e.UpdatedAtUtc).HasColumnName("updated_at_utc");

            entity.HasIndex(e => new { e.GuildId, e.DiscordUserId })
                .IsUnique()
                .HasFilter("discord_user_id IS NOT NULL");
            entity.HasIndex(e => new { e.GuildId, e.IsRemoved });
            entity.HasIndex(e => new { e.GuildId, e.GameClass });
        });

        modelBuilder.Entity<GuildClassConfig>(entity =>
        {
            entity.ToTable("guild_class_configs");
            entity.HasKey(e => new { e.GuildId, e.GameClass });

            entity.Property(e => e.GuildId).HasColumnName("guild_id").HasConversion(ulongConverter);
            entity.Property(e => e.GameClass).HasColumnName("game_class").HasConversion<int>();
            entity.Property(e => e.DisplayName).HasColumnName("display_name").HasMaxLength(80).IsRequired();
            entity.Property(e => e.Role).HasColumnName("role").HasConversion<int>().HasDefaultValue(Core.Domain.Enums.ClassRole.Dps);
            entity.Property(e => e.HexColor).HasColumnName("hex_color").HasMaxLength(16).HasDefaultValue("#6366F1").IsRequired();
            entity.Property(e => e.IconFileName).HasColumnName("icon_file_name").HasMaxLength(120).IsRequired();
            entity.Property(e => e.DiscordRoleId).HasColumnName("discord_role_id").HasConversion(nullableUlongConverter);
            entity.Property(e => e.DiscordRoleName).HasColumnName("discord_role_name").HasMaxLength(100);
            entity.Property(e => e.SortOrder).HasColumnName("sort_order").HasDefaultValue(0);
            entity.Property(e => e.IsCustom).HasColumnName("is_custom").HasDefaultValue(false);
            entity.Property(e => e.UpdatedAtUtc).HasColumnName("updated_at_utc");
        });
    }
}
