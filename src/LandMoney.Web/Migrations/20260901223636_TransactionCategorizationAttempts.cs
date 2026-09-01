using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LandMoney.Web.Migrations
{
    /// <inheritdoc />
    public partial class TransactionCategorizationAttempts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "categorization_attempts",
                table: "transactions",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "categorization_attempts",
                table: "transactions");
        }
    }
}
