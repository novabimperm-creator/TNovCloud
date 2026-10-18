using Microsoft.EntityFrameworkCore;

namespace TNovCloud.Api.Data;

public sealed class CloudDbContext(DbContextOptions<CloudDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Folder> Folders => Set<Folder>();
    public DbSet<StoredFile> Files => Set<StoredFile>();
    public DbSet<FileVersion> FileVersions => Set<FileVersion>();
    public DbSet<FileAuditEvent> FileAuditEvents => Set<FileAuditEvent>();
    public DbSet<ApprovalProcess> Processes => Set<ApprovalProcess>();
    public DbSet<ApprovalTask> Tasks => Set<ApprovalTask>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<User>(e =>
        {
            e.HasIndex(x => x.Login).IsUnique();
            e.Property(x => x.Login).HasMaxLength(100).IsRequired();
            e.Property(x => x.DisplayName).HasMaxLength(200).IsRequired();
        });

        model.Entity<Folder>(e =>
        {
            e.HasOne(x => x.Parent).WithMany(x => x.Children).HasForeignKey(x => x.ParentId);
            e.HasIndex(x => new { x.ParentId, x.Name }).IsUnique()
                .HasFilter("\"DeletedAt\" IS NULL");
            e.Property(x => x.Name).HasMaxLength(255).IsRequired();
            e.Property(x => x.Revision).IsConcurrencyToken();
        });

        model.Entity<StoredFile>(e =>
        {
            e.HasOne(x => x.Folder).WithMany(x => x.Files).HasForeignKey(x => x.FolderId);
            e.HasOne(x => x.CheckedOutBy).WithMany().HasForeignKey(x => x.CheckedOutById);
            e.HasIndex(x => new { x.FolderId, x.Name }).IsUnique()
                .HasFilter("\"DeletedAt\" IS NULL");
            e.Property(x => x.Name).HasMaxLength(255).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Revision).IsConcurrencyToken();
        });

        model.Entity<FileVersion>(e =>
        {
            e.HasOne(x => x.File).WithMany(x => x.Versions).HasForeignKey(x => x.FileId);
            e.HasOne(x => x.CreatedBy).WithMany().HasForeignKey(x => x.CreatedById);
            e.HasIndex(x => new { x.FileId, x.Version }).IsUnique();
        });

        model.Entity<FileAuditEvent>(e =>
        {
            e.HasOne(x => x.Actor).WithMany().HasForeignKey(x => x.ActorId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.EntryId, x.CreatedAt });
            e.Property(x => x.Action).HasMaxLength(32).IsRequired();
            e.Property(x => x.Path).HasMaxLength(2048).IsRequired();
            e.Property(x => x.PreviousPath).HasMaxLength(2048);
            e.Property(x => x.Details).HasMaxLength(2000);
        });

        model.Entity<ApprovalProcess>(e =>
        {
            e.HasOne(x => x.File).WithMany().HasForeignKey(x => x.FileId);
            e.HasOne(x => x.Initiator).WithMany().HasForeignKey(x => x.InitiatorId);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Title).HasMaxLength(300).IsRequired();
        });

        model.Entity<ApprovalTask>(e =>
        {
            e.HasOne(x => x.Process).WithMany(x => x.Tasks).HasForeignKey(x => x.ProcessId);
            e.HasOne(x => x.Assignee).WithMany().HasForeignKey(x => x.AssigneeId);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
        });
    }
}
