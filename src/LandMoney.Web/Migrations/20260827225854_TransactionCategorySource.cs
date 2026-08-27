using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LandMoney.Web.Migrations
{
    /// <inheritdoc />
    // #59, and it had to land before the model adapter was switched on rather than
    // after -- see Transaction.CategorySource and CLAUDE.md's "Open decisions with
    // a deadline". A row categorised while two producers were running and no column
    // existed can never be asked which one wrote it.
    public partial class TransactionCategorySource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "category_source",
                table: "transactions",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            // **The backfill, which is a decision and not a formality.** #59 offered
            // the alternative -- leave every existing row null -- and asked for
            // whichever was chosen to be argued rather than done quietly.
            //
            // `rules` here is provably true, not merely defensible. Three facts, all
            // checkable in this repository rather than remembered: CreateTransactionRequest
            // has never carried a category field, so no client could have sent one;
            // CategorizerClient is the only writer, and it has only ever spoken to a
            // service whose one predictor was RulesPredictor; and CLAUDE.md forbade a
            // model call until a baseline was scored, which happened in #25 and #58
            // with no model involved. So every category now in this table came from
            // the rules, and writing that down loses nothing.
            //
            // Leaving them null would have said "the producer was never recorded",
            // which is a weaker statement than the evidence supports -- and it would
            // make `category_source IS NULL` mean two different things at once:
            // "written before the column existed" and "nothing wrote a category".
            // The WHERE clause is what keeps the second meaning clean. A row with no
            // category has no producer to name, so it keeps a null source, and the
            // invariant "a source exists exactly when a category does" holds for
            // every row in the table, old and new alike.
            //
            // Nothing to undo on the way down: Down drops the column and takes the
            // backfill with it.
            migrationBuilder.Sql(
                @"UPDATE transactions
                  SET category_source = 'rules'
                  WHERE category IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "category_source",
                table: "transactions");
        }
    }
}
