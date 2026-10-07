using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventPipeline.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddJobRunLogs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "JobRunLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    JobRunId = table.Column<int>(type: "int", nullable: false),
                    JobName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    LoggedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Message = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobRunLogs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_JobRunLogs_JobRunId",
                table: "JobRunLogs",
                column: "JobRunId");

            migrationBuilder.CreateIndex(
                name: "IX_JobRunLogs_LoggedAtUtc",
                table: "JobRunLogs",
                column: "LoggedAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "JobRunLogs");
        }
    }
}
