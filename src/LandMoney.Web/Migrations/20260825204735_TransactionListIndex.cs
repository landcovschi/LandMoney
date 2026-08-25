using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LandMoney.Web.Migrations
{
    /// <inheritdoc />
    public partial class TransactionListIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_transactions_occurred_at_created_at",
                table: "transactions",
                columns: new[] { "occurred_at", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_transactions_occurred_at_created_at",
                table: "transactions");
        }
    }
}
