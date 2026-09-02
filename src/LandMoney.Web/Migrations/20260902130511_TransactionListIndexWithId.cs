using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LandMoney.Web.Migrations
{
    /// <inheritdoc />
    public partial class TransactionListIndexWithId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_transactions_owner_id_occurred_at_created_at",
                table: "transactions");

            migrationBuilder.CreateIndex(
                name: "ix_transactions_owner_id_occurred_at_created_at_id",
                table: "transactions",
                columns: new[] { "owner_id", "occurred_at", "created_at", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_transactions_owner_id_occurred_at_created_at_id",
                table: "transactions");

            migrationBuilder.CreateIndex(
                name: "ix_transactions_owner_id_occurred_at_created_at",
                table: "transactions",
                columns: new[] { "owner_id", "occurred_at", "created_at" });
        }
    }
}
