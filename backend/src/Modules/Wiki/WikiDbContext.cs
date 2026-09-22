using Microsoft.EntityFrameworkCore;
using Aictiq.Modules.Wiki.Domain;
using Aictiq.Modules.Wiki.Endpoints;
using Aictiq.SharedKernel;
using Aictiq.SharedKernel.Events;
using Aictiq.SharedKernel.Persistence;
using Aictiq.SharedKernel.Tenancy;

namespace Aictiq.Modules.Wiki;

public sealed class WikiDbContext(DbContextOptions<WikiDbContext> options,
    IDomainEventDispatcher? dispatcher = null, ICurrentTenant? currentTenant = null)
    : ModuleDbContext(options, dispatcher, currentTenant)
{
    protected override string Schema => "wiki";
    public DbSet<WikiPage> Pages => Set<WikiPage>();
    public DbSet<WikiPageRevision> PageRevisions => Set<WikiPageRevision>();
    public DbSet<WikiPageItemLink> PageItemLinks => Set<WikiPageItemLink>();
    public DbSet<WikiPagePermission> PagePermissions => Set<WikiPagePermission>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<WikiPage>(b =>
        {
            b.ToTable("pages");
            b.Property(x => x.Slug).HasMaxLength(160);
            b.Property(x => x.Title).HasMaxLength(500);
            b.Property(x => x.CreatedBy).HasMaxLength(64);
            b.Property(x => x.Version).IsRowVersion();
            b.Property(x => x.Search).HasColumnType("tsvector").HasComputedColumnSql("setweight(to_tsvector('english', coalesce(title, '')), 'A') || setweight(to_tsvector('simple', coalesce(title, '')), 'A')", stored: true);
            b.HasIndex(x => new { x.ProjectId, x.ParentId, x.Position });
            b.HasIndex(x => new { x.ProjectId, x.UpdatedAt });
            // A subpage cannot outlive its parent: deleting a page deletes everything below it.
            b.HasOne<WikiPage>().WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<WikiPageRevision>(b =>
        {
            b.ToTable("page_revisions");
            b.Property(x => x.ContentMarkdown).HasMaxLength(WikiPageEndpoints.MaxMarkdownLength);
            b.Property(x => x.ContentHtml).HasMaxLength(WikiPageEndpoints.MaxHtmlLength);
            b.Property(x => x.AuthorId).HasMaxLength(64);
            b.Property(x => x.Summary).HasMaxLength(500);
            b.HasIndex(x => new { x.PageId, x.Number }).IsUnique().HasDatabaseName("ux_page_revisions_number");
            b.HasOne<WikiPage>().WithMany().HasForeignKey(x => x.PageId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<WikiPageItemLink>(b =>
        {
            b.ToTable("page_item_links"); b.Ignore(x => x.Id); b.HasKey(x => new { x.PageId, x.ItemId });
            b.HasIndex(x => x.ItemId);
            b.HasOne<WikiPage>().WithMany().HasForeignKey(x => x.PageId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<WikiPagePermission>(b =>
        {
            b.ToTable("page_permissions");
            b.Property(x => x.SubjectId).HasMaxLength(64);
            b.HasIndex(x => new { x.PageId, x.SubjectKind, x.SubjectId }).IsUnique();
            b.HasIndex(x => x.PageId);
            b.HasOne<WikiPage>().WithMany().HasForeignKey(x => x.PageId).OnDelete(DeleteBehavior.Cascade);
            b.ToTable(table => table.HasCheckConstraint("ck_page_permissions_subject", "(subject_kind <> 2 OR subject_id IN ('guest', 'member', 'admin'))"));
        });
    }
}
