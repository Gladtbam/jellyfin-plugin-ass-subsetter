using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using static Jellyfin.Plugin.AssSubsetter.Fonts.Persistence.FontDatabaseRows;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;

/// <summary>Private persistence model; each operation owns its context and read snapshot.</summary>
internal sealed class FontDbContext : DbContext
{
    internal FontDbContext(SqliteConnection connection, bool ownsConnection = false)
        : base(new DbContextOptionsBuilder<FontDbContext>()
            .UseSqlite(connection, ownsConnection)
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .Options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var schema_info = Configure<SchemaInfoRow>(modelBuilder, "schema_info", nameof(SchemaInfoRow.SingletonId));
        schema_info.Property(row => row.SingletonId).ValueGeneratedNever();
        schema_info.ToTable(table => table.HasCheckConstraint("ck_schema_info_1", "singleton_id = 1"));

        var scan_generation = Configure<ScanGenerationRow>(modelBuilder, "scan_generation", nameof(ScanGenerationRow.Id));

        var font_file = Configure<FontFileRow>(modelBuilder, "font_file", nameof(FontFileRow.Id));
        font_file.HasIndex(nameof(FontFileRow.GenerationId), nameof(FontFileRow.NormalizedPath)).IsUnique();
        font_file.HasOne<ScanGenerationRow>().WithMany().HasForeignKey(row => row.GenerationId).OnDelete(DeleteBehavior.Cascade);

        var font_face = Configure<FontFaceRow>(modelBuilder, "font_face", nameof(FontFaceRow.Id));
        font_face.HasIndex(nameof(FontFaceRow.FileId), nameof(FontFaceRow.FaceIndex)).IsUnique();
        font_face.HasOne<FontFileRow>().WithMany().HasForeignKey(row => row.FileId).OnDelete(DeleteBehavior.Cascade);

        var font_path_alias = Configure<FontPathAliasRow>(modelBuilder, "font_path_alias", nameof(FontPathAliasRow.Id));
        font_path_alias.HasIndex(nameof(FontPathAliasRow.FileId), nameof(FontPathAliasRow.NormalizedPath)).IsUnique();
        font_path_alias.HasOne<FontFileRow>().WithMany().HasForeignKey(row => row.FileId).OnDelete(DeleteBehavior.Cascade);

        var face_name = Configure<FaceNameRow>(modelBuilder, "face_name", nameof(FaceNameRow.Id));
        face_name.HasIndex(nameof(FaceNameRow.FaceId), nameof(FaceNameRow.Ordinal)).IsUnique();
        face_name.HasOne<FontFaceRow>().WithMany().HasForeignKey(row => row.FaceId).OnDelete(DeleteBehavior.Cascade);

        var face_name_evidence = Configure<FaceNameEvidenceRow>(modelBuilder, "face_name_evidence", nameof(FaceNameEvidenceRow.FaceId));
        face_name_evidence.Property(row => row.FaceId).ValueGeneratedNever();
        face_name_evidence.HasOne<FontFaceRow>().WithMany().HasForeignKey(row => row.FaceId).OnDelete(DeleteBehavior.Cascade);

        var font_axis = Configure<FontAxisRow>(modelBuilder, "font_axis", nameof(FontAxisRow.Id));
        font_axis.HasIndex(nameof(FontAxisRow.FaceId), nameof(FontAxisRow.Tag)).IsUnique();
        font_axis.HasOne<FontFaceRow>().WithMany().HasForeignKey(row => row.FaceId).OnDelete(DeleteBehavior.Cascade);

        var font_named_instance = Configure<FontNamedInstanceRow>(modelBuilder, "font_named_instance", nameof(FontNamedInstanceRow.Id));
        font_named_instance.HasOne<FontFaceRow>().WithMany().HasForeignKey(row => row.FaceId).OnDelete(DeleteBehavior.Cascade);

        var face_table = Configure<FaceTableRow>(modelBuilder, "face_table", nameof(FaceTableRow.Id));
        face_table.HasIndex(nameof(FaceTableRow.FaceId), nameof(FaceTableRow.Tag)).IsUnique();
        face_table.HasOne<FontFaceRow>().WithMany().HasForeignKey(row => row.FaceId).OnDelete(DeleteBehavior.Cascade);

        var face_coverage = Configure<FaceCoverageRow>(modelBuilder, "face_coverage", nameof(FaceCoverageRow.Id));
        face_coverage.HasIndex(nameof(FaceCoverageRow.FaceId), nameof(FaceCoverageRow.CoverageKind)).IsUnique();
        face_coverage.HasOne<FontFaceRow>().WithMany().HasForeignKey(row => row.FaceId).OnDelete(DeleteBehavior.Cascade);

        var font_deep_evidence = Configure<FontDeepEvidenceRow>(modelBuilder, "font_deep_evidence", nameof(FontDeepEvidenceRow.FileId));
        font_deep_evidence.Property(row => row.FileId).ValueGeneratedNever();
        font_deep_evidence.HasOne<FontFileRow>().WithMany().HasForeignKey(row => row.FileId).OnDelete(DeleteBehavior.Cascade);

        var analysis_batch = Configure<AnalysisBatchRow>(modelBuilder, "analysis_batch", nameof(AnalysisBatchRow.Id));
        analysis_batch.HasOne<ScanGenerationRow>().WithMany().HasForeignKey(row => row.GenerationId).OnDelete(DeleteBehavior.Restrict);
        analysis_batch.Property(row => row.CandidateCount).HasDefaultValue(0);
        analysis_batch.Property(row => row.ComparisonCount).HasDefaultValue(0);
        analysis_batch.Property(row => row.ReclaimableBytes).HasDefaultValue(0);
        analysis_batch.Property(row => row.SummaryJson).HasDefaultValue("{}");

        var font_relation = Configure<FontRelationRow>(modelBuilder, "font_relation", nameof(FontRelationRow.Id));
        font_relation.HasIndex(nameof(FontRelationRow.AnalysisBatchId), nameof(FontRelationRow.LeftFaceId), nameof(FontRelationRow.RightFaceId)).IsUnique();
        font_relation.HasOne<AnalysisBatchRow>().WithMany().HasForeignKey(row => row.AnalysisBatchId).OnDelete(DeleteBehavior.Cascade);
        font_relation.HasOne<FontFaceRow>().WithMany().HasForeignKey(row => row.LeftFaceId).OnDelete(DeleteBehavior.Restrict);
        font_relation.HasOne<FontFaceRow>().WithMany().HasForeignKey(row => row.RightFaceId).OnDelete(DeleteBehavior.Restrict);
        font_relation.ToTable(table => table.HasCheckConstraint("ck_font_relation_1", "left_face_id < right_face_id"));

        var duplicate_group = Configure<DuplicateGroupRow>(modelBuilder, "duplicate_group", nameof(DuplicateGroupRow.Id));
        duplicate_group.HasOne<AnalysisBatchRow>().WithMany().HasForeignKey(row => row.AnalysisBatchId).OnDelete(DeleteBehavior.Cascade);
        duplicate_group.HasOne<FontFaceRow>().WithMany().HasForeignKey(row => row.RetainedFaceId).OnDelete(DeleteBehavior.Restrict);

        var group_member = Configure<GroupMemberRow>(modelBuilder, "group_member", nameof(GroupMemberRow.Id));
        group_member.HasIndex(nameof(GroupMemberRow.DuplicateGroupId), nameof(GroupMemberRow.FaceId)).IsUnique();
        group_member.HasOne<DuplicateGroupRow>().WithMany().HasForeignKey(row => row.DuplicateGroupId).OnDelete(DeleteBehavior.Cascade);
        group_member.HasOne<FontFaceRow>().WithMany().HasForeignKey(row => row.FaceId).OnDelete(DeleteBehavior.Restrict);
        group_member.HasOne<FontFileRow>().WithMany().HasForeignKey(row => row.FileId).OnDelete(DeleteBehavior.Restrict);

        var operation_request = Configure<OperationRequestRow>(modelBuilder, "operation_request", nameof(OperationRequestRow.Id));
        operation_request.HasIndex(nameof(OperationRequestRow.IdempotencyKey)).IsUnique();
        operation_request.HasOne<AnalysisBatchRow>().WithMany().HasForeignKey(row => row.AnalysisBatchId).OnDelete(DeleteBehavior.Restrict);

        var maintenance_batch = Configure<MaintenanceBatchRow>(modelBuilder, "maintenance_batch", nameof(MaintenanceBatchRow.Id));
        maintenance_batch.HasIndex(nameof(MaintenanceBatchRow.OperationRequestId)).IsUnique();
        maintenance_batch.HasOne<OperationRequestRow>().WithMany().HasForeignKey(row => row.OperationRequestId).OnDelete(DeleteBehavior.Restrict);

        var quarantine_item = Configure<QuarantineItemRow>(modelBuilder, "quarantine_item", nameof(QuarantineItemRow.Id));
        quarantine_item.HasIndex(nameof(QuarantineItemRow.MaintenanceBatchId), nameof(QuarantineItemRow.FileId)).IsUnique();
        quarantine_item.HasOne<MaintenanceBatchRow>().WithMany().HasForeignKey(row => row.MaintenanceBatchId).OnDelete(DeleteBehavior.Cascade);

        var rename_item = Configure<RenameItemRow>(modelBuilder, "rename_item", nameof(RenameItemRow.Id));
        rename_item.HasIndex(nameof(RenameItemRow.MaintenanceBatchId), nameof(RenameItemRow.FileId)).IsUnique();
        rename_item.HasOne<MaintenanceBatchRow>().WithMany().HasForeignKey(row => row.MaintenanceBatchId).OnDelete(DeleteBehavior.Cascade);

        var rename_candidate_state = Configure<RenameCandidateStateRow>(modelBuilder, "rename_candidate_state", nameof(RenameCandidateStateRow.SingletonId));
        rename_candidate_state.Property(row => row.SingletonId).ValueGeneratedNever();
        rename_candidate_state.ToTable(table => table.HasCheckConstraint("ck_rename_candidate_state_1", "singleton_id = 1"));

        var rename_candidate = Configure<RenameCandidateRow>(modelBuilder, "rename_candidate", nameof(RenameCandidateRow.FileId));
        rename_candidate.Property(row => row.FileId).ValueGeneratedNever();
        rename_candidate.HasOne<FontFileRow>().WithMany().HasForeignKey(row => row.FileId).OnDelete(DeleteBehavior.Cascade);

        var rename_request_item = Configure<RenameRequestItemRow>(modelBuilder, "rename_request_item", nameof(RenameRequestItemRow.Id));
        rename_request_item.HasIndex(nameof(RenameRequestItemRow.RequestId), nameof(RenameRequestItemRow.FileId)).IsUnique();
        rename_request_item.HasOne<OperationRequestRow>().WithMany().HasForeignKey(row => row.RequestId).OnDelete(DeleteBehavior.Cascade);

        var performance_sample = Configure<PerformanceSampleRow>(modelBuilder, "performance_sample", nameof(PerformanceSampleRow.Id));
        performance_sample.HasOne<ScanGenerationRow>().WithMany().HasForeignKey(row => row.GenerationId).OnDelete(DeleteBehavior.Cascade);

        font_file.HasIndex(nameof(FontFileRow.PhysicalIdentity), nameof(FontFileRow.Size), nameof(FontFileRow.ModifiedUtc)).HasDatabaseName("ix_font_file_physical_identity");
        rename_candidate.HasIndex(nameof(RenameCandidateRow.Category), nameof(RenameCandidateRow.OldPath), nameof(RenameCandidateRow.SourceKey)).HasDatabaseName("ix_rename_candidate_category_path");
        rename_candidate.HasIndex(nameof(RenameCandidateRow.SourceKey)).HasDatabaseName("ix_rename_candidate_source");
        rename_candidate.HasIndex(nameof(RenameCandidateRow.OldPath)).HasDatabaseName("ix_rename_candidate_old_path");
        rename_candidate.HasIndex(nameof(RenameCandidateRow.NewPath)).HasDatabaseName("ix_rename_candidate_new_path");
        rename_request_item.HasIndex(nameof(RenameRequestItemRow.PhysicalIdentity), nameof(RenameRequestItemRow.RequestId)).HasDatabaseName("ix_rename_request_item_identity");
        rename_request_item.HasIndex(nameof(RenameRequestItemRow.SourceKey), nameof(RenameRequestItemRow.RequestId)).HasDatabaseName("ix_rename_request_item_source").IsDescending(false, true);
        rename_request_item.HasIndex(nameof(RenameRequestItemRow.SourcePathKey), nameof(RenameRequestItemRow.RequestId)).HasDatabaseName("ix_rename_request_item_source_path");
        rename_request_item.HasIndex(nameof(RenameRequestItemRow.TargetPathKey), nameof(RenameRequestItemRow.RequestId)).HasDatabaseName("ix_rename_request_item_target_path");
        font_file.HasIndex(nameof(FontFileRow.GenerationId)).HasDatabaseName("ix_font_file_generation");
        font_file.HasIndex(nameof(FontFileRow.Sha256)).HasDatabaseName("ix_font_file_sha256");
        font_path_alias.HasIndex(nameof(FontPathAliasRow.NormalizedPath)).HasDatabaseName("ix_font_path_alias_normalized");
        font_face.HasIndex(nameof(FontFaceRow.StrictFingerprint)).HasDatabaseName("ix_font_face_strict_fingerprint");
        face_name.HasIndex(nameof(FaceNameRow.NormalizedName)).HasDatabaseName("ix_face_name_normalized");
        analysis_batch.HasIndex(nameof(AnalysisBatchRow.Status), nameof(AnalysisBatchRow.CompletedUtc)).HasDatabaseName("ix_analysis_batch_status");
        font_relation.HasIndex(nameof(FontRelationRow.AnalysisBatchId), nameof(FontRelationRow.RelationshipKind)).HasDatabaseName("ix_font_relation_kind");
        operation_request.HasIndex(nameof(OperationRequestRow.OperationKind), nameof(OperationRequestRow.Status), nameof(OperationRequestRow.CreatedUtc)).HasDatabaseName("ix_operation_request_status");
        quarantine_item.HasIndex(nameof(QuarantineItemRow.Status), nameof(QuarantineItemRow.ExpiresUtc)).HasDatabaseName("ix_quarantine_item_expiry");
        analysis_batch.HasIndex(nameof(AnalysisBatchRow.GenerationId)).HasDatabaseName("ix_analysis_batch_generation");
        font_named_instance.HasIndex(nameof(FontNamedInstanceRow.FaceId)).HasDatabaseName("ix_font_named_instance_face");
        font_relation.HasIndex(nameof(FontRelationRow.LeftFaceId)).HasDatabaseName("ix_font_relation_left_face");
        font_relation.HasIndex(nameof(FontRelationRow.RightFaceId)).HasDatabaseName("ix_font_relation_right_face");
        duplicate_group.HasIndex(nameof(DuplicateGroupRow.RetainedFaceId)).HasDatabaseName("ix_duplicate_group_retained_face");
        group_member.HasIndex(nameof(GroupMemberRow.FaceId)).HasDatabaseName("ix_group_member_face");
        group_member.HasIndex(nameof(GroupMemberRow.FileId)).HasDatabaseName("ix_group_member_file");
        rename_item.HasIndex(nameof(RenameItemRow.SourceKey), nameof(RenameItemRow.MaintenanceBatchId)).HasDatabaseName("ix_rename_item_source").IsDescending(false, true);
        rename_item.HasIndex(nameof(RenameItemRow.Category), nameof(RenameItemRow.OldPath), nameof(RenameItemRow.SourceKey)).HasDatabaseName("ix_rename_item_category_path");
        rename_item.HasIndex(nameof(RenameItemRow.FileId)).HasDatabaseName("ix_rename_item_file");
        performance_sample.HasIndex(nameof(PerformanceSampleRow.GenerationId)).HasDatabaseName("ix_performance_sample_generation");

        foreach (IMutableEntityType entity in modelBuilder.Model.GetEntityTypes())
        {
            foreach (IMutableProperty property in entity.GetProperties())
            {
                property.SetColumnName(ColumnName(property.Name));
            }
        }
    }

    private static EntityTypeBuilder<T> Configure<T>(ModelBuilder modelBuilder, string table, string key)
        where T : class
    {
        EntityTypeBuilder<T> entity = modelBuilder.Entity<T>();
        entity.ToTable(table);
        entity.HasKey(key);
        return entity;
    }

    private static string ColumnName(string property)
    {
        var result = new StringBuilder(property.Length + 4);
        for (int index = 0; index < property.Length; index++)
        {
            char character = property[index];
            if (index > 0 && char.IsUpper(character))
            {
                result.Append('_');
            }

            result.Append(char.ToLowerInvariant(character));
        }

        return result.ToString();
    }
}
