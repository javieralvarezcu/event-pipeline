using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventPipeline.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddEventExclusion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Excluded",
                table: "EventRecords",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Excluded",
                table: "EventRecords");
        }
    }
}
