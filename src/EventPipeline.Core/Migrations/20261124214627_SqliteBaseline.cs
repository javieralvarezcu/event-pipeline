using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventPipeline.Core.Migrations
{
    /// <inheritdoc />
    public partial class SqliteBaseline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AppSettings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: true),
                    IsSecret = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppSettings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "AppUsers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Username = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    PasswordHash = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    MustChangePassword = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastLoginAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppUsers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DataProtectionKeys",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FriendlyName = table.Column<string>(type: "TEXT", nullable: true),
                    Xml = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataProtectionKeys", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DeepSeekCallLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Operation = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    ContextSummary = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    SystemPrompt = table.Column<string>(type: "TEXT", nullable: false),
                    UserPrompt = table.Column<string>(type: "TEXT", nullable: false),
                    ResponseContent = table.Column<string>(type: "TEXT", nullable: true),
                    Model = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    MaxTokens = table.Column<int>(type: "INTEGER", nullable: false),
                    Temperature = table.Column<double>(type: "REAL", nullable: false),
                    PromptTokens = table.Column<int>(type: "INTEGER", nullable: false),
                    CompletionTokens = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalTokens = table.Column<int>(type: "INTEGER", nullable: false),
                    TokensEstimated = table.Column<bool>(type: "INTEGER", nullable: false),
                    Attempt = table.Column<int>(type: "INTEGER", nullable: false),
                    Succeeded = table.Column<bool>(type: "INTEGER", nullable: false),
                    HttpStatusCode = table.Column<int>(type: "INTEGER", nullable: true),
                    FinishReason = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    DurationMs = table.Column<long>(type: "INTEGER", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeepSeekCallLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EventRecords",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    EventUniqueId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    EventDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    EventDateDescription = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Summary = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    IsRecurrent = table.Column<bool>(type: "INTEGER", nullable: false),
                    RecurrenceType = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    RecurrenceDaysOfWeek = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    RecurrenceStartDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RecurrenceEndDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Account = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    PostId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Caption = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    PostDatetime = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Url = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    ImageUrl = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Excluded = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventRecords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IgAccounts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Username = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ProfileUrl = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastScrapedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IgAccounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "JobRunLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    JobRunId = table.Column<int>(type: "INTEGER", nullable: false),
                    JobName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    LoggedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Message = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobRunLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "JobRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    JobName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    TriggerType = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SummaryJson = table.Column<string>(type: "TEXT", nullable: true),
                    Error = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MuxoEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ExternalId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Date = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Venue = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Link = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Categories = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Price = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MuxoEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Posts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Url = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Account = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    PostId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Caption = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    PostDatetime = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ImageUrl = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    IsEvent = table.Column<bool>(type: "INTEGER", nullable: false),
                    AnalysisJson = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AnalyzedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Posts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CrossMatches",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    EventUniqueId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    MuxoEventId = table.Column<int>(type: "INTEGER", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CrossMatches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CrossMatches_MuxoEvents_MuxoEventId",
                        column: x => x.MuxoEventId,
                        principalTable: "MuxoEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AppUsers_Username",
                table: "AppUsers",
                column: "Username",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CrossMatches_EventUniqueId",
                table: "CrossMatches",
                column: "EventUniqueId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CrossMatches_MuxoEventId",
                table: "CrossMatches",
                column: "MuxoEventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeepSeekCallLogs_Operation",
                table: "DeepSeekCallLogs",
                column: "Operation");

            migrationBuilder.CreateIndex(
                name: "IX_DeepSeekCallLogs_StartedAtUtc",
                table: "DeepSeekCallLogs",
                column: "StartedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_EventRecords_Account",
                table: "EventRecords",
                column: "Account");

            migrationBuilder.CreateIndex(
                name: "IX_EventRecords_CreatedAt",
                table: "EventRecords",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_EventRecords_EventDate",
                table: "EventRecords",
                column: "EventDate");

            migrationBuilder.CreateIndex(
                name: "IX_EventRecords_EventUniqueId",
                table: "EventRecords",
                column: "EventUniqueId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EventRecords_PostId",
                table: "EventRecords",
                column: "PostId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IgAccounts_Username",
                table: "IgAccounts",
                column: "Username",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JobRunLogs_JobRunId",
                table: "JobRunLogs",
                column: "JobRunId");

            migrationBuilder.CreateIndex(
                name: "IX_JobRunLogs_LoggedAtUtc",
                table: "JobRunLogs",
                column: "LoggedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_JobRuns_JobName_StartedAtUtc",
                table: "JobRuns",
                columns: new[] { "JobName", "StartedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MuxoEvents_ExternalId",
                table: "MuxoEvents",
                column: "ExternalId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Posts_Url",
                table: "Posts",
                column: "Url",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppSettings");

            migrationBuilder.DropTable(
                name: "AppUsers");

            migrationBuilder.DropTable(
                name: "CrossMatches");

            migrationBuilder.DropTable(
                name: "DataProtectionKeys");

            migrationBuilder.DropTable(
                name: "DeepSeekCallLogs");

            migrationBuilder.DropTable(
                name: "EventRecords");

            migrationBuilder.DropTable(
                name: "IgAccounts");

            migrationBuilder.DropTable(
                name: "JobRunLogs");

            migrationBuilder.DropTable(
                name: "JobRuns");

            migrationBuilder.DropTable(
                name: "Posts");

            migrationBuilder.DropTable(
                name: "MuxoEvents");
        }
    }
}
