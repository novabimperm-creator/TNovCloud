using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TNovCloud.Api.Data;

#nullable disable

namespace TNovCloud.Api.Migrations;

[DbContext(typeof(CloudDbContext))]
[Migration("20260902091500_ServerFileApi")]
public sealed class ServerFileApi : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: "IX_Folders_ParentId_Name", table: "Folders");
        migrationBuilder.DropIndex(name: "IX_Files_FolderId_Name", table: "Files");

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "UpdatedAt",
            table: "Folders",
            type: "timestamp with time zone",
            nullable: false,
            defaultValue: DateTimeOffset.UnixEpoch);
        migrationBuilder.AddColumn<long>(
            name: "Revision",
            table: "Folders",
            type: "bigint",
            nullable: false,
            defaultValue: 1L);
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "DeletedAt",
            table: "Folders",
            type: "timestamp with time zone",
            nullable: true);
        migrationBuilder.AddColumn<Guid>(
            name: "DeletedById",
            table: "Folders",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "Revision",
            table: "Files",
            type: "bigint",
            nullable: false,
            defaultValue: 1L);
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "DeletedAt",
            table: "Files",
            type: "timestamp with time zone",
            nullable: true);
        migrationBuilder.AddColumn<Guid>(
            name: "DeletedById",
            table: "Files",
            type: "uuid",
            nullable: true);

        migrationBuilder.Sql("""UPDATE "Folders" SET "UpdatedAt" = "CreatedAt";""");

        migrationBuilder.CreateTable(
            name: "FileAuditEvents",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Action = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                IsFolder = table.Column<bool>(type: "boolean", nullable: false),
                EntryId = table.Column<Guid>(type: "uuid", nullable: false),
                Path = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                PreviousPath = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                Version = table.Column<int>(type: "integer", nullable: true),
                Revision = table.Column<long>(type: "bigint", nullable: false),
                ActorId = table.Column<Guid>(type: "uuid", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                Details = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_FileAuditEvents", x => x.Id);
                table.ForeignKey(
                    name: "FK_FileAuditEvents_Users_ActorId",
                    column: x => x.ActorId,
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_FileAuditEvents_ActorId",
            table: "FileAuditEvents",
            column: "ActorId");
        migrationBuilder.CreateIndex(
            name: "IX_FileAuditEvents_EntryId_CreatedAt",
            table: "FileAuditEvents",
            columns: new[] { "EntryId", "CreatedAt" });
        migrationBuilder.CreateIndex(
            name: "IX_Files_FolderId_Name",
            table: "Files",
            columns: new[] { "FolderId", "Name" },
            unique: true,
            filter: "\"DeletedAt\" IS NULL");
        migrationBuilder.CreateIndex(
            name: "IX_Folders_ParentId_Name",
            table: "Folders",
            columns: new[] { "ParentId", "Name" },
            unique: true,
            filter: "\"DeletedAt\" IS NULL");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "FileAuditEvents");
        migrationBuilder.DropIndex(name: "IX_Folders_ParentId_Name", table: "Folders");
        migrationBuilder.DropIndex(name: "IX_Files_FolderId_Name", table: "Files");

        migrationBuilder.DropColumn(name: "DeletedAt", table: "Files");
        migrationBuilder.DropColumn(name: "DeletedById", table: "Files");
        migrationBuilder.DropColumn(name: "Revision", table: "Files");
        migrationBuilder.DropColumn(name: "DeletedAt", table: "Folders");
        migrationBuilder.DropColumn(name: "DeletedById", table: "Folders");
        migrationBuilder.DropColumn(name: "Revision", table: "Folders");
        migrationBuilder.DropColumn(name: "UpdatedAt", table: "Folders");

        migrationBuilder.CreateIndex(
            name: "IX_Files_FolderId_Name",
            table: "Files",
            columns: new[] { "FolderId", "Name" },
            unique: true);
        migrationBuilder.CreateIndex(
            name: "IX_Folders_ParentId_Name",
            table: "Folders",
            columns: new[] { "ParentId", "Name" },
            unique: true);
    }
}
