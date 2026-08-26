using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LandMoney.Web.Migrations
{
    /// <inheritdoc />
    public partial class TransactionOwner : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_transactions_occurred_at_created_at",
                table: "transactions");

            migrationBuilder.AddColumn<string>(
                name: "owner_id",
                table: "transactions",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_transactions_owner_id_occurred_at_created_at",
                table: "transactions",
                columns: new[] { "owner_id", "occurred_at", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_transactions_owner_id_occurred_at_created_at",
                table: "transactions");

            migrationBuilder.DropColumn(
                name: "owner_id",
                table: "transactions");

            migrationBuilder.CreateIndex(
                name: "ix_transactions_occurred_at_created_at",
                table: "transactions",
                columns: new[] { "occurred_at", "created_at" });
        }
    }
}
